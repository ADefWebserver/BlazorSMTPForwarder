using Azure.Data.Tables;
using Azure.Storage.Blobs;
using BlazorSMTPForwarder.ServiceDefaults.Models;
using BlazorSMTPForwarderSrv.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;
using SendGrid;
using SendGrid.Helpers.Mail;
using MimeKit;
using Zetian.Abstractions;
using Zetian.Models.EventArgs;
using Zetian.Relay.Configuration;
using Zetian.Relay.Enums;
using Zetian.Relay.Extensions;
using Zetian.Server;
using Zetian.Storage.AzureBlob;
using Zetian.Storage.AzureBlob.Configuration;

namespace BlazorSMTPForwarderSrv.Services;

public class ZetianMessageHandler
{
    private readonly ILogger<ZetianMessageHandler> _logger;
    private readonly TableStorageLogger _tableLogger;
    private readonly SmtpServerConfiguration _smtpServerConfiguration;
    private readonly BlobServiceClient _blobServiceClient;
    private SmtpSettingsModel _smtpServer = new SmtpSettingsModel();

    public ZetianMessageHandler(
        ILogger<ZetianMessageHandler> logger,
        TableStorageLogger tableLogger,
        SmtpServerConfiguration smtpServerConfiguration,
        BlobServiceClient blobServiceClient)
    {
        _logger = logger;
        _tableLogger = tableLogger;
        _smtpServerConfiguration = smtpServerConfiguration;
        _blobServiceClient = blobServiceClient;
    }

    public async Task HandleMessageAsync(object? sender, MessageEventArgs e)
    {
        _smtpServer = _smtpServerConfiguration.LoadSettingsAsync().GetAwaiter().GetResult();

        var server = sender as SmtpServer;
        if (server == null) return;

        var message = e.Message;
        var session = e.Session;

        _logger.LogInformation("Received message from {From} to {Recipients}", message.From, string.Join(", ", message.Recipients));
        await _tableLogger.LogInformationAsync($"Received message from {message.From} to {string.Join(", ", message.Recipients)}", nameof(ZetianMessageHandler));

        bool hasLocal = message.Recipients.Any(r => IsLocalRecipient(r.Address));

        if (hasLocal)
        {
            try
            {
                if (!_smtpServer.DoNotSaveMessages)
                {
                    var container = _blobServiceClient.GetBlobContainerClient("email-messages");
                    await container.CreateIfNotExistsAsync();

                    using var stream = new MemoryStream();
                    await message.SaveToStreamAsync(stream);

                    foreach (var recipient in message.Recipients)
                    {
                        if (IsLocalRecipient(recipient.Address))
                        {
                            var address = recipient.Address;
                            var parts = address.Split('@');
                            if (parts.Length == 2)
                            {
                                var userName = parts[0];
                                var domain = parts[1];

                                var fileName = $"{domain}/{userName}/{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}.eml";
                                var blobClient = container.GetBlobClient(fileName);

                                stream.Position = 0;
                                
                                // Extract metadata
                                var metadata = new Dictionary<string, string>();
                                try
                                {
                                    var mimeMessage = MimeMessage.Load(stream);
                                    metadata["Subject"] = SanitizeHeader(mimeMessage.Subject ?? "(no subject)");
                                    metadata["From"] = SanitizeHeader(mimeMessage.From?.ToString() ?? "");
                                    metadata["RecipientUser"] = SanitizeHeader($"{userName}@{domain}");
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed to extract metadata for {BlobName}", fileName);
                                }

                                stream.Position = 0;
                                await blobClient.UploadAsync(stream, metadata: metadata);
                                _logger.LogInformation("Message saved to blob: {BlobName}", fileName);
                                await _tableLogger.LogInformationAsync($"Message saved to blob: {fileName}", nameof(ZetianMessageHandler));
                            }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(_smtpServer.SendGridApiKey) && !string.IsNullOrEmpty(_smtpServer.DomainsJson))
                {
                    try 
                    {
                        var domains = JsonSerializer.Deserialize<List<DomainConfiguration>>(_smtpServer.DomainsJson);
                        if (domains != null)
                        {
                            var sendGridClient = new SendGridClient(_smtpServer.SendGridApiKey);
                            
                            // Parse the message once
                            using var msgStream = new MemoryStream();
                            await message.SaveToStreamAsync(msgStream);
                            msgStream.Position = 0;
                            var mimeMessage = await MimeMessage.LoadAsync(msgStream);

                            foreach (var recipient in message.Recipients)
                            {
                                var domainConfig = domains.FirstOrDefault(d => recipient.Address.EndsWith("@" + d.DomainName, StringComparison.OrdinalIgnoreCase));
                                if (domainConfig != null)
                                {
                                    if (domainConfig.CatchAll.Type == CatchAllType.None)
                                    {
                                        var rule = domainConfig.ForwardingRules.FirstOrDefault(r => r.IncomingEmail.Equals(recipient.Address, StringComparison.OrdinalIgnoreCase));
                                        if (rule != null)
                                        {
                                            _logger.LogInformation("Forwarding message for {Recipient} to {Destination}", recipient.Address, rule.DestinationEmail);
                                            await _tableLogger.LogInformationAsync($"Forwarding message for {recipient.Address} to {rule.DestinationEmail}", nameof(ZetianMessageHandler));
                                            await ForwardMessageAsync(sendGridClient, mimeMessage, rule.DestinationEmail);
                                        }
                                    }
                                    else if (domainConfig.CatchAll.Type == CatchAllType.Forward && !string.IsNullOrEmpty(domainConfig.CatchAll.ForwardToEmail))
                                    {
                                         _logger.LogInformation("Catch-all forwarding message for {Recipient} to {Destination}", recipient.Address, domainConfig.CatchAll.ForwardToEmail);
                                         await _tableLogger.LogInformationAsync($"Catch-all forwarding message for {recipient.Address} to {domainConfig.CatchAll.ForwardToEmail}", nameof(ZetianMessageHandler));
                                         await ForwardMessageAsync(sendGridClient, mimeMessage, domainConfig.CatchAll.ForwardToEmail);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error forwarding message via SendGrid");
                        await _tableLogger.LogErrorAsync("Error forwarding message via SendGrid", ex, nameof(ZetianMessageHandler));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save message to blob storage.");
                await _tableLogger.LogErrorAsync("Failed to save message to blob storage.", ex, nameof(ZetianMessageHandler));
            }
        }
        else
        {
            _logger.LogInformation("No local recipients found. Message will not be processed.");
            await _tableLogger.LogInformationAsync("No local recipients found. Message will not be processed.", nameof(ZetianMessageHandler));
        }
    }

    public string SanitizeHeader(string input)
    {
        // Regex to replace anything that isn't standard ASCII (32-127)
        return System.Text.RegularExpressions.Regex.Replace(input, @"[^\u0020-\u007F]", string.Empty);
    }

    private bool IsLocalRecipient(string address)
    {
        if (string.IsNullOrWhiteSpace(_smtpServer.ServerName)) return true;

        if (address.EndsWith("@" + _smtpServer.ServerName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(_smtpServer.DomainsJson))
        {
            try
            {
                var domains = JsonSerializer.Deserialize<List<DomainConfiguration>>(_smtpServer.DomainsJson);
                if (domains != null)
                {
                    return domains.Any(d => address.EndsWith("@" + d.DomainName, StringComparison.OrdinalIgnoreCase));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing DomainsJson in IsLocalRecipient");
            }
        }

        return false;
    }

    private async Task ForwardMessageAsync(SendGridClient client, MimeMessage originalMessage, string destination)
    {
        var fromEmailAddress = !string.IsNullOrWhiteSpace(_smtpServer.SendGridFromEmail) 
            ? _smtpServer.SendGridFromEmail 
            : "noreply@" + _smtpServer.ServerName;

        var fromEmail = new EmailAddress(fromEmailAddress, "Forwarder");
        var toEmail = new EmailAddress(destination);
        var subject = (originalMessage.Subject ?? "No Subject");
        var plainTextContent = originalMessage.TextBody ?? "No text content";
        var htmlContent = originalMessage.HtmlBody ?? originalMessage.TextBody ?? "No content";

        var msg = MailHelper.CreateSingleEmail(fromEmail, toEmail, subject, plainTextContent, htmlContent);
        
        // Set Reply-To to original sender
        if (originalMessage.From.Count > 0)
        {
            var originalSender = originalMessage.From.Mailboxes.FirstOrDefault();
            if (originalSender != null)
            {
                msg.ReplyTo = new EmailAddress(originalSender.Address, originalSender.Name);
            }
        }

        // Attachments
        foreach (var attachment in originalMessage.Attachments)
        {
            if (attachment is MimePart part)
            {
                using var stream = new MemoryStream();
                await part.Content.DecodeToAsync(stream);
                var content = Convert.ToBase64String(stream.ToArray());
                msg.AddAttachment(part.FileName, content, part.ContentType.MimeType);
            }
        }

        // Inline images
        foreach (var part in originalMessage.BodyParts.OfType<MimePart>())
        {
            var contentId = part.ContentId;
            if (!string.IsNullOrEmpty(contentId))
            {
                var cleanContentId = contentId.Trim('<', '>');
                if (!string.IsNullOrEmpty(htmlContent) && htmlContent.Contains($"cid:{cleanContentId}"))
                {
                     using var stream = new MemoryStream();
                     await part.Content.DecodeToAsync(stream);
                     var content = Convert.ToBase64String(stream.ToArray());
                     msg.AddAttachment(part.FileName ?? "image", content, part.ContentType.MimeType, "inline", cleanContentId);
                }
            }
        }

        // Add original email as attachment
        using var originalEmailStream = new MemoryStream();
        await originalMessage.WriteToAsync(originalEmailStream);
        var originalEmailContent = Convert.ToBase64String(originalEmailStream.ToArray());
        msg.AddAttachment("original_message.eml", originalEmailContent, "message/rfc822", "attachment");

        var response = await client.SendEmailAsync(msg);
        if (response.StatusCode != HttpStatusCode.Accepted && response.StatusCode != HttpStatusCode.OK)
        {
             _logger.LogError("SendGrid failed with status code {StatusCode}", response.StatusCode);
             await _tableLogger.LogErrorAsync($"SendGrid failed with status code {response.StatusCode}", null, nameof(ZetianMessageHandler));
             var body = await response.Body.ReadAsStringAsync();
             _logger.LogError("SendGrid response: {Response}", body);
             await _tableLogger.LogErrorAsync($"SendGrid response: {body}", null, nameof(ZetianMessageHandler));
        }
        else
        {
            _logger.LogInformation("Forwarded successfully to {Destination}", destination);
            await _tableLogger.LogInformationAsync($"Forwarded successfully to {destination}", nameof(ZetianMessageHandler));
        }
    }
}