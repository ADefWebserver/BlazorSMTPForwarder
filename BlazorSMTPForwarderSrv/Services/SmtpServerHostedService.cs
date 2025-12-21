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

    private Task? _executeTask;
    private CancellationTokenSource? _stopCts;
    private DateTimeOffset _lastRestartRequested = DateTimeOffset.MinValue;


    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _executeTask = Task.Run(() => ExecuteServerLoopAsync(_stopCts.Token), cancellationToken);
        return Task.CompletedTask;
    }

    private async Task ExecuteServerLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunServerInstanceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SMTP Server loop. Restarting in 5 seconds...");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task RunServerInstanceAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Configuring SMTP Server...");

        // Load Settings from Azure Table
        var settings = await _smtpServerConfiguration.LoadSettingsAsync(stoppingToken);

        // Build Server
        var builder = new SmtpServerBuilder()
            .ServerName(settings.ServerName ?? "localhost")
            .Port(25);

        _smtpServer = builder.Build();

        // Configure Anti-Spam
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

        // Wire up Message Handler
        _smtpServer.MessageReceived += async (s, e) => await _messageHandler.HandleMessageAsync(s, e);

        _logger.LogInformation("Starting SMTP Server...");
        
        // Start the server in a separate task because StartAsync blocks
        var serverTask = _smtpServer.StartAsync(stoppingToken);

        // Wait for restart signal or cancellation
        await WaitForRestartSignal(stoppingToken);

        _logger.LogInformation("Stopping SMTP Server instance...");
        await _smtpServer.StopAsync();
        
        try
        {
            await serverTask;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping SMTP server");
        }
        finally
        {
            _smtpServer.Dispose();
            _smtpServer = null;
        }
    }

    private async Task WaitForRestartSignal(CancellationToken stoppingToken)
    {
        var table = _tableServiceClient.GetTableClient("SMTPSettings");
        
        // Initialize last restart requested time if needed
        if (_lastRestartRequested == DateTimeOffset.MinValue)
        {
            var entity = await table.GetEntityIfExistsAsync<TableEntity>("SmtpServer", "Current", cancellationToken: stoppingToken);

            if (entity.HasValue && entity.Value != null && entity.Value.TryGetValue("RestartRequested", out var val) && val is DateTimeOffset dt)
            {
                _lastRestartRequested = dt;
            }
            else
            {
                _lastRestartRequested = DateTimeOffset.UtcNow;
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(5000, stoppingToken);

            try 
            {
                var entity = await table.GetEntityIfExistsAsync<TableEntity>("SmtpServer", "Current", cancellationToken: stoppingToken);
                if (entity.HasValue && entity.Value != null && entity.Value.TryGetValue("RestartRequested", out var val) && val is DateTimeOffset requestedAt)
                {
                    if (requestedAt > _lastRestartRequested)
                    {
                        _lastRestartRequested = requestedAt;
                        _logger.LogInformation("Restart signal received. Reloading configuration...");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check for restart signal");
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping SMTP Server Service...");
        if (_stopCts != null)
        {
            _stopCts.Cancel();
        }

        if (_executeTask != null)
        {
            await Task.WhenAny(_executeTask, Task.Delay(Timeout.Infinite, cancellationToken));
        }
    }

    public void Dispose()
    {
        _smtpServer?.Dispose();
        _stopCts?.Dispose();
    }        
}