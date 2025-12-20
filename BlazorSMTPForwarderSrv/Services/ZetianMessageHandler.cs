using BlazorSMTPForwarder.ServiceDefaults.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zetian.Server;
using Zetian.Storage.AzureBlob;
using Zetian.Storage.AzureBlob.Configuration;
using Zetian.Relay.Extensions;
using Zetian.Relay.Enums;
using Azure.Storage.Blobs;
using Azure.Data.Tables;
using Zetian.Models.EventArgs;
using Microsoft.Extensions.Configuration;

namespace BlazorSMTPForwarderSrv.Services;

public class ZetianMessageHandler
{
    private readonly ILogger<ZetianMessageHandler> _logger;
    private readonly SmtpServerConfiguration _smtpServerConfiguration;
    private readonly BlobServiceClient _blobServiceClient;

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
        var server = sender as SmtpServer;
        if (server == null) return;

        var message = e.Message;
        var session = e.Session;

        _logger.LogInformation("Received message from {From} to {Recipients}", message.From, string.Join(", ", message.Recipients));

        bool hasLocal = message.Recipients.Any(r => IsLocalRecipient(r.Address));
        bool hasRemote = message.Recipients.Any(r => !IsLocalRecipient(r.Address));

        if (hasLocal)
        {
            _logger.LogInformation("Storing message locally.");
            // Use Zetian's AzureBlobMessageStore if possible, or manual upload.
            // Since we have BlobServiceClient injected, we can use it directly to ensure it works with Aspire.
            // Zetian's AzureBlobMessageStore might require a connection string which we might not have in raw form if using Managed Identity.

            try
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save message to blob storage.");
            }
        }

        if (hasRemote)
        {
            _logger.LogInformation("Relaying message.");
            await server.QueueForRelayAsync(message, session, RelayPriority.Normal);
        }
    }

    private bool IsLocalRecipient(string address)
    {
        var Settings = _smtpServerConfiguration.LoadSettingsAsync().GetAwaiter().GetResult();

        if (string.IsNullOrWhiteSpace(Settings.ServerName)) return true;
        return address.EndsWith("@" + Settings.ServerName, StringComparison.OrdinalIgnoreCase);
    }
}
