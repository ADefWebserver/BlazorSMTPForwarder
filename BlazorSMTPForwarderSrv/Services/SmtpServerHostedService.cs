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
    private readonly TableServiceClient _tableServiceClient;
    private readonly ZetianMessageHandler _messageHandler;
    private readonly SmtpServerConfiguration _smtpServerConfiguration;

    public SmtpServerHostedService(
        IServiceProvider serviceProvider,
        ILogger<SmtpServerHostedService> logger,
        TableServiceClient tableServiceClient,
        ZetianMessageHandler messageHandler,
        SmtpServerConfiguration smtpServerConfiguration)
    {
        _logger = logger;
        _tableServiceClient = tableServiceClient;
        _messageHandler = messageHandler;
        _smtpServerConfiguration = smtpServerConfiguration;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Configuring SMTP Server...");

        // 1. Load Settings from Azure Table
        var settings = await _smtpServerConfiguration.LoadSettingsAsync(cancellationToken);

        // 2. Build Server
        var builder = new SmtpServerBuilder()
            .ServerName(settings.ServerName)
            .Port(settings.ServerPorts[0]);

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
}