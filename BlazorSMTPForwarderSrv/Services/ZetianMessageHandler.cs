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
    private readonly SmtpServerConfiguration _smtpServerConfiguration;
    private readonly BlobServiceClient _blobServiceClient;
    private SmtpSettingsModel _smtpServer = new SmtpSettingsModel();

    public ZetianMessageHandler(
        ILogger<ZetianMessageHandler> logger,
        SmtpServerConfiguration smtpServerConfiguration,
        BlobServiceClient blobServiceClient)
    {
        _logger = logger;
        _smtpServerConfiguration = smtpServerConfiguration;
        _blobServiceClient = blobServiceClient;
    }

    public async Task HandleMessageAsync(object sender, MessageEventArgs e)
    {
        _smtpServer = _smtpServerConfiguration.LoadSettingsAsync().GetAwaiter().GetResult();

        var server = sender as SmtpServer;
        if (server == null) return;

        var message = e.Message;
        var session = e.Session;

        _logger.LogInformation("Received message from {From} to {Recipients}", message.From, string.Join(", ", message.Recipients));

        bool hasLocal = message.Recipients.Any(r => IsLocalRecipient(r.Address));

        if (hasLocal)
        {
            try
            {
                if (!_smtpServer.DoNotSaveMessages)
                {
                    var container = _blobServiceClient.GetBlobContainerClient("email-messages");
                    await container.CreateIfNotExistsAsync();

                    var fileName = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}.eml";
                    var blobClient = container.GetBlobClient(fileName);

                    using var stream = new MemoryStream();
                    await message.SaveToStreamAsync(stream);
                    stream.Position = 0;

                    await blobClient.UploadAsync(stream);
                    _logger.LogInformation("Message saved to blob: {BlobName}", fileName);
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
                                            await ForwardMessageAsync(sendGridClient, mimeMessage, rule.DestinationEmail);
                                        }
                                    }
                                    else if (domainConfig.CatchAll.Type == CatchAllType.Forward && !string.IsNullOrEmpty(domainConfig.CatchAll.ForwardToEmail))
                                    {
                                         _logger.LogInformation("Catch-all forwarding message for {Recipient} to {Destination}", recipient.Address, domainConfig.CatchAll.ForwardToEmail);
                                         await ForwardMessageAsync(sendGridClient, mimeMessage, domainConfig.CatchAll.ForwardToEmail);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error forwarding message via SendGrid");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save message to blob storage.");
            }
        }
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
        var fromEmail = new EmailAddress("noreply@" + _smtpServer.ServerName, "Forwarder");
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

        var response = await client.SendEmailAsync(msg);
        if (response.StatusCode != HttpStatusCode.Accepted && response.StatusCode != HttpStatusCode.OK)
        {
             _logger.LogError("SendGrid failed with status code {StatusCode}", response.StatusCode);
             var body = await response.Body.ReadAsStringAsync();
             _logger.LogError("SendGrid response: {Response}", body);
        }
        else
        {
            _logger.LogInformation("Forwarded successfully to {Destination}", destination);
        }
    }
}