using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zetian.Server;
using Zetian.Relay.Extensions;
using Zetian.Relay.Configuration;
using Zetian.AntiSpam.Extensions;
using BlazorSMTPForwarder.ServiceDefaults.Models;
using Azure.Data.Tables;
using System.Net;

namespace BlazorSMTPForwarderSrv.Services;

public class SmtpServerHostedService : IHostedService, IDisposable
{
    private SmtpServer? _smtpServer;
    private readonly ILogger<SmtpServerHostedService> _logger;
    private readonly SmtpServerConfiguration _config;
    private readonly TableServiceClient _tableServiceClient;
    private readonly ZetianMessageHandler _messageHandler;

    public SmtpServerHostedService(
        IOptions<SmtpServerConfiguration> configuration,
        IServiceProvider serviceProvider,
        ILogger<SmtpServerHostedService> logger,
        TableServiceClient tableServiceClient,
        ZetianMessageHandler messageHandler)
    {
        _logger = logger;
        _config = configuration.Value;
        _tableServiceClient = tableServiceClient;
        _messageHandler = messageHandler;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Configuring SMTP Server...");

        // 1. Load Settings from Azure Table
        var settings = await LoadSettingsAsync(cancellationToken);

        // 2. Build Server
        var ports = _config.Ports ?? new[] { 25, 587, 2525 };
        var builder = new SmtpServerBuilder()
            .ServerName(_config.ServerName ?? "localhost")
            .Port(ports.Length > 0 ? ports[0] : 25);

        _smtpServer = builder.Build();

        // 3. Configure Relay (SendGrid)
        if (!string.IsNullOrEmpty(settings.SendGridHost))
        {
            _smtpServer.EnableRelay(relayConfig =>
            {
                relayConfig.DefaultSmartHost = new SmartHostConfiguration
                {
                    Host = settings.SendGridHost,
                    Port = settings.SendGridPort > 0 ? settings.SendGridPort : 587,
                    Credentials = new NetworkCredential(settings.SendGridUser, settings.SendGridPass),
                    UseTls = true
                };
                relayConfig.MaxRetryCount = 3;
            });
        }

        // 4. Configure Anti-Spam
        _smtpServer.AddAntiSpam(spamBuilder =>
        {
            if (settings.EnableSpfCheck) spamBuilder.EnableSpf();
            if (settings.EnableDkimCheck) spamBuilder.EnableDkim();
            if (settings.EnableDmarcCheck) spamBuilder.EnableDmarc();
            
            if (settings.EnableSpamFiltering)
            {
                var rblDomain = !string.IsNullOrEmpty(settings.SpamhausKey) 
                    ? $"{settings.SpamhausKey}.zen.dq.spamhaus.net" 
                    : "zen.spamhaus.org";
                
                spamBuilder.EnableRbl(rblDomain);
            }
        });

        // 5. Wire up Message Handler
        _smtpServer.MessageReceived += async (s, e) => await _messageHandler.HandleMessageAsync(s, e);

        _logger.LogInformation("Starting SMTP Server...");
        await _smtpServer.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping SMTP Server...");
        if (_smtpServer != null)
        {
            await _smtpServer.StopAsync();
        }
    }

    public void Dispose()
    {
        _smtpServer?.Dispose();
    }

    private async Task<SmtpSettingsModel> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        var model = new SmtpSettingsModel();
        try
        {
            var table = _tableServiceClient.GetTableClient("SMTPSettings");
            await table.CreateIfNotExistsAsync(cancellationToken);
            var response = await table.GetEntityIfExistsAsync<TableEntity>("SmtpServer", "Current", cancellationToken: cancellationToken);

            TableEntity entity;
            bool needsUpdate = false;

            if (!response.HasValue)
            {
                entity = new TableEntity("SmtpServer", "Current");
                needsUpdate = true;
            }
            else
            {
                entity = response.Value;
            }

            // Helper to ensure property exists
            void EnsureProperty<T>(string key, T defaultValue)
            {
                if (!entity.ContainsKey(key))
                {
                    entity[key] = defaultValue;
                    needsUpdate = true;
                }
            }

            EnsureProperty("EnableSpamFiltering", false);
            EnsureProperty("SpamhausKey", "");
            EnsureProperty("EnableSpfCheck", false);
            EnsureProperty("EnableDkimCheck", false);
            EnsureProperty("EnableDmarcCheck", false);
            EnsureProperty("SendGridHost", "");
            EnsureProperty("SendGridPort", 587);
            EnsureProperty("SendGridUser", "");
            EnsureProperty("SendGridPass", "");

            if (needsUpdate)
            {
                await table.UpsertEntityAsync(entity, TableUpdateMode.Merge, cancellationToken);
                _logger.LogInformation("Initialized or updated SMTPSettings table with default values.");
            }

            model.EnableSpamFiltering = entity.GetBoolean("EnableSpamFiltering") ?? false;
            model.SpamhausKey = entity.GetString("SpamhausKey");
            model.EnableSpfCheck = entity.GetBoolean("EnableSpfCheck") ?? false;
            model.EnableDkimCheck = entity.GetBoolean("EnableDkimCheck") ?? false;
            model.EnableDmarcCheck = entity.GetBoolean("EnableDmarcCheck") ?? false;
            
            model.SendGridHost = entity.GetString("SendGridHost");
            model.SendGridPort = entity.GetInt32("SendGridPort") ?? 587;
            model.SendGridUser = entity.GetString("SendGridUser");
            model.SendGridPass = entity.GetString("SendGridPass");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings from Azure Table. Using defaults.");
        }
        return model;
    }

    private class SmtpSettingsModel
    {
        public bool EnableSpamFiltering { get; set; }
        public string? SpamhausKey { get; set; }
        public bool EnableSpfCheck { get; set; }
        public bool EnableDkimCheck { get; set; }
        public bool EnableDmarcCheck { get; set; }
        public string? SendGridHost { get; set; }
        public int SendGridPort { get; set; }
        public string? SendGridUser { get; set; }
        public string? SendGridPass { get; set; }
    }
}
