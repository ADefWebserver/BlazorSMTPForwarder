using BlazorSMTPForwarder.ServiceDefaults.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Cryptography;
using SmtpServer;
using SmtpServer.Protocol;
using SmtpServer.Storage;
using System.Buffers;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System.Text;
using Azure.Data.Tables;
using Azure;
using DnsClient;
using System.Security.Cryptography;

namespace BlazorSMTPForwarderSrv.Services;

/// <summary>
/// Message store implementation that saves email messages to Azure Blob Storage and relays outgoing mail
/// </summary>
public class DefaultMessageStore : MessageStore
{
    private readonly ILogger<DefaultMessageStore> _logger;
    private readonly SmtpServerConfiguration _configuration;
    private readonly BlobServiceClient _blobServiceClient;
    private readonly TableServiceClient _tableServiceClient;
    private BlobContainerClient? _containerClient;
    private TableClient? _spamLogTableClient;
    private bool _containerEnsured;
    private bool _tableEnsured;

    // Cache for settings loaded from Table Storage
    private DateTime _lastSettingsLoad = DateTime.MinValue;
    private readonly TimeSpan _settingsRefreshInterval = TimeSpan.FromMinutes(1);
    
    // In-memory settings (refreshed from Table)
    private bool _enableSpamFiltering;
    private string? _spamhausKey;
    private bool _enableSpfCheck;
    private bool _enableDkimCheck;
    private bool _enableDmarcCheck;
    private bool _enableDkimSigning;
    private string? _dkimPrivateKey;
    private string? _dkimSelector;
    private string? _dkimDomain;

    public DefaultMessageStore(
        ILogger<DefaultMessageStore> logger,
        IOptionsMonitor<SmtpServerConfiguration> configuration,
        BlobServiceClient blobServiceClient,
        TableServiceClient tableServiceClient)
    {
        _logger = logger;
        _configuration = configuration.CurrentValue;
        _blobServiceClient = blobServiceClient;
        _tableServiceClient = tableServiceClient;
    }

    private async Task EnsureResourcesAsync(CancellationToken cancellationToken)
    {
        if (!_containerEnsured)
        {
            try
            {
                _containerClient = _blobServiceClient.GetBlobContainerClient("email-messages");
                await _containerClient.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
                _containerEnsured = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ensure blob container exists");
            }
        }

        if (!_tableEnsured)
        {
            try
            {
                _spamLogTableClient = _tableServiceClient.GetTableClient("spamlogs");
                await _spamLogTableClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
                _tableEnsured = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ensure spam log table exists");
            }
        }

        await RefreshSettingsAsync(cancellationToken);
    }

    private async Task RefreshSettingsAsync(CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - _lastSettingsLoad < _settingsRefreshInterval) return;

        try
        {
            var table = _tableServiceClient.GetTableClient("SMTPSettings");
            var response = await table.GetEntityIfExistsAsync<TableEntity>("SmtpServer", "Current", cancellationToken: cancellationToken);
            
            if (response.HasValue)
            {
                var entity = response.Value;
                _enableSpamFiltering = entity.GetBoolean("EnableSpamFiltering") ?? false;
                _spamhausKey = entity.GetString("SpamhausKey");
                _enableSpfCheck = entity.GetBoolean("EnableSpfCheck") ?? false;
                _enableDkimCheck = entity.GetBoolean("EnableDkimCheck") ?? false;
                _enableDmarcCheck = entity.GetBoolean("EnableDmarcCheck") ?? false;
                _enableDkimSigning = entity.GetBoolean("EnableDkimSigning") ?? false;
                _dkimPrivateKey = entity.GetString("DkimPrivateKey");
                _dkimSelector = entity.GetString("DkimSelector");
                _dkimDomain = entity.GetString("DkimDomain");
            }
            _lastSettingsLoad = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh settings from Table Storage");
        }
    }

    public override async Task<SmtpResponse> SaveAsync(
        ISessionContext context,
        IMessageTransaction transaction,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken)
    {
        await EnsureResourcesAsync(cancellationToken);

        var sessionId = context.Properties.ContainsKey("SessionId") ? context.Properties["SessionId"]?.ToString() ?? "unknown" : "unknown";
        var transactionId = Guid.NewGuid().ToString("N");
        var ip = context.Properties.ContainsKey("RemoteEndPoint") ? context.Properties["RemoteEndPoint"]?.ToString() : "unknown";

        try
        {
            using var stream = new MemoryStream();
            var position = buffer.GetPosition(0);
            while (buffer.TryGet(ref position, out var memory))
            {
                await stream.WriteAsync(memory, cancellationToken);
            }
            stream.Position = 0;

            var message = await MimeMessage.LoadAsync(stream, cancellationToken);
            var from = message.From.ToString();
            var subject = message.Subject;

            // 1. Spamhaus Check (if enabled)
            if (_enableSpamFiltering && !string.IsNullOrWhiteSpace(_spamhausKey) && !string.IsNullOrWhiteSpace(ip))
            {
                // In a real implementation, you would query the Spamhaus DQS DNS here.
                // For this example, we'll skip the actual DNS query logic to keep it simple,
                // but this is where you'd check the IP against the Zen list.
            }

            // 2. SPF Check
            if (_enableSpfCheck)
            {
                // Basic SPF check logic would go here using a library or DNS lookup
            }

            // 3. DKIM Check
            if (_enableDkimCheck)
            {
                // MimeKit can verify DKIM signatures
                // var locator = new DnsClientDkimPublicKeyLocator();
                // var verifier = new DkimVerifier(locator);
                // await verifier.VerifyAsync(message, cancellationToken);
            }

            // 4. DMARC Check
            if (_enableDmarcCheck)
            {
                // DMARC logic
            }

            // Handle relay vs local storage
            var recipients = transaction.To.Select(t => t.User).ToList();
            
            foreach (var recipient in recipients)
            {
                // Check if local
                if (IsLocalRecipient(recipient))
                {
                    await SaveToBlobAsync(message, recipient, sessionId, transactionId, ip, cancellationToken);
                }
                else
                {
                    // Relay
                    await RelayMessageAsync(message, recipient, cancellationToken);
                }
            }

            return SmtpResponse.Ok;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message");
            return SmtpResponse.TransactionFailed;
        }
    }

    private bool IsLocalRecipient(string address)
    {
        // Simple check: if the domain matches our configured domain
        if (string.IsNullOrWhiteSpace(_configuration.Domain)) return true; // Accept all if no domain configured
        return address.EndsWith("@" + _configuration.Domain, StringComparison.OrdinalIgnoreCase);
    }

    private async Task SaveToBlobAsync(MimeMessage message, string recipient, string sessionId, string transactionId, string? ip, CancellationToken cancellationToken)
    {
        if (_containerClient == null) return;

        var fileName = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}.eml";
        // Folder structure: recipient/filename
        var blobPath = $"{recipient}/{fileName}";
        var blobClient = _containerClient.GetBlobClient(blobPath);

        using var stream = new MemoryStream();
        
        // Add custom headers for metadata
        message.Headers.Add("X-SMTP-Server-Received", DateTime.UtcNow.ToString("O"));
        message.Headers.Add("X-SMTP-Server-SessionId", sessionId);
        message.Headers.Add("X-SMTP-Server-TransactionId", transactionId);
        message.Headers.Add("X-SMTP-Server-Recipient-User", recipient);
        if (ip != null) message.Headers.Add("X-SMTP-Server-IP", ip);

        await message.WriteToAsync(stream, cancellationToken);
        stream.Position = 0;

        var metadata = new Dictionary<string, string>
        {
            { "Subject", message.Subject ?? "(no subject)" },
            { "From", message.From.ToString() },
            { "RecipientUser", recipient },
            { "SessionId", sessionId },
            { "TransactionId", transactionId }
        };

        await blobClient.UploadAsync(stream, new BlobUploadOptions { Metadata = metadata }, cancellationToken);
        _logger.LogInformation("Saved message for {Recipient} to {BlobPath}", recipient, blobPath);
    }

    private async Task RelayMessageAsync(MimeMessage message, string recipient, CancellationToken cancellationToken)
    {
        // DKIM Signing if enabled
        if (_enableDkimSigning && !string.IsNullOrWhiteSpace(_dkimPrivateKey) && !string.IsNullOrWhiteSpace(_dkimSelector) && !string.IsNullOrWhiteSpace(_dkimDomain))
        {
            try
            {
                // Sign the message
                var headers = new[] { HeaderId.From, HeaderId.Subject, HeaderId.Date, HeaderId.To };
                using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(_dkimPrivateKey));
                var signer = new DkimSigner(
                    keyStream,
                    _dkimDomain,
                    _dkimSelector,
                    DkimSignatureAlgorithm.RsaSha256)
                {
                    HeaderCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
                    BodyCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
                    AgentOrUserIdentifier = "@" + _dkimDomain,
                    QueryMethod = "dns/txt",
                };

                message.Prepare(EncodingConstraint.SevenBit);
                signer.Sign(message, headers);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sign message with DKIM");
            }
        }

        // Relay logic (simplified)
        // In a real scenario, you'd look up MX records for the recipient domain and send via SMTP client
        _logger.LogInformation("Relaying message to {Recipient} (Not fully implemented in this sample)", recipient);
        
        // Example using MailKit SmtpClient to relay (commented out as it requires valid external SMTP)
        /*
        using var client = new MailKit.Net.Smtp.SmtpClient();
        await client.ConnectAsync("smtp.example.com", 587, SecureSocketOptions.StartTls, cancellationToken);
        await client.AuthenticateAsync("user", "pass", cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
        */
    }

    private async Task LogSpamAsync(string sessionId, string transactionId, string from, string to, string subject, string blobPath, string ip)
    {
        if (_spamLogTableClient == null) return;
        try
        {
            var entity = new TableEntity
            {
                PartitionKey = DateTime.UtcNow.ToString("yyyy-MM-dd"),
                RowKey = $"{DateTime.UtcNow:HHmmss.fff}_{Guid.NewGuid():N}",
                ["Timestamp"] = DateTime.UtcNow,
                ["SessionId"] = sessionId,
                ["TransactionId"] = transactionId,
                ["From"] = from,
                ["To"] = to,
                ["Subject"] = subject,
                ["BlobPath"] = blobPath,
                ["IP"] = ip
            };
            await _spamLogTableClient.AddEntityAsync(entity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log spam entry");
        }
    } 
}
