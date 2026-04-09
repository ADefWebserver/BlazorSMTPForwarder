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
using System.Text.Json;

namespace BlazorSMTPForwarderSrv.Services;

public class SmtpServerHostedService : IHostedService, IDisposable
{
    private SmtpServer? _smtpServer;
    private readonly ILogger<SmtpServerHostedService> _logger;
    private readonly TableStorageLogger _tableLogger;
    private readonly TableServiceClient _tableServiceClient;
    private readonly ZetianMessageHandler _messageHandler;
    private readonly SmtpServerConfiguration _smtpServerConfiguration;

    public SmtpServerHostedService(
        IServiceProvider serviceProvider,
        ILogger<SmtpServerHostedService> logger,
        TableStorageLogger tableLogger,
        TableServiceClient tableServiceClient,
        ZetianMessageHandler messageHandler,
        SmtpServerConfiguration smtpServerConfiguration)
    {
        _logger = logger;
        _tableLogger = tableLogger;
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
                await _tableLogger.LogErrorAsync("Error in SMTP Server loop. Restarting in 5 seconds...", ex, nameof(SmtpServerHostedService));
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task RunServerInstanceAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Configuring SMTP Server...");
        await _tableLogger.LogInformationAsync("Configuring SMTP Server...", nameof(SmtpServerHostedService));

        // Load Settings from Azure Table
        var settings = await _smtpServerConfiguration.LoadSettingsAsync(stoppingToken);

        // Validate Settings
        if (string.IsNullOrEmpty(settings.ServerName))
        {
            var msg = "SMTP settings not set: ServerName is missing.";
            await _tableLogger.LogErrorAsync(msg, null, nameof(SmtpServerHostedService));
        }

        if (string.IsNullOrEmpty(settings.SendGridApiKey))
        {
            var msg = "Sendgrid Key not set.";
            await _tableLogger.LogErrorAsync(msg, null, nameof(SmtpServerHostedService));
        }

        if (settings.EnableSpamFiltering && string.IsNullOrEmpty(settings.SpamhausKey))
        {
            var msg = "SPAMHAUS enabled but SPAMHAUS key not set.";
            await _tableLogger.LogErrorAsync(msg, null, nameof(SmtpServerHostedService));
        }

        List<DomainConfiguration>? domains = null;
        if (!string.IsNullOrEmpty(settings.DomainsJson))
        {
            try
            {
                domains = JsonSerializer.Deserialize<List<DomainConfiguration>>(settings.DomainsJson);
            }
            catch
            {
                // Ignore deserialization error here
            }
        }

        if (domains == null || domains.Count == 0)
        {
            var msg = "No Domains have been configured.";
            await _tableLogger.LogErrorAsync(msg, null, nameof(SmtpServerHostedService));
        }
        else
        {
            foreach (var domain in domains)
            {
                if (domain.CatchAll.Type == CatchAllType.None && (domain.ForwardingRules == null || domain.ForwardingRules.Count == 0))
                {
                    var msg = $"Domain '{domain.DomainName}' has been configured, and is not a catch all, but, no email forwarding is specified.";
                    await _tableLogger.LogErrorAsync(msg, null, nameof(SmtpServerHostedService));
                }

                if (domain.CatchAll.Type == CatchAllType.Forward
                    && string.IsNullOrEmpty(domain.CatchAll.ForwardToEmail))
                {
                    var msg = $"Domain '{domain.DomainName}' has Catch-All set to Forward "
                            + "but no forward-to email is specified.";
                    await _tableLogger.LogErrorAsync(msg, null, nameof(SmtpServerHostedService));
                }
            }
        }

        // Build Server
        var builder = new SmtpServerBuilder()
            .ServerName(settings.ServerName ?? "localhost")
            .Port(25);

        _smtpServer = builder.Build();

        // Configure Anti-Spam
        _smtpServer.AddAntiSpam(spamBuilder =>
        {
            if (settings.EnableSpfCheck)
            {
                spamBuilder.EnableSpf();
                _logger.LogInformation("Anti-spam: SPF check enabled.");
            }
            if (settings.EnableDkimCheck)
            {
                spamBuilder.EnableDkim();
                _logger.LogInformation("Anti-spam: DKIM check enabled.");
            }
            if (settings.EnableDmarcCheck)
            {
                spamBuilder.EnableDmarc();
                _logger.LogInformation("Anti-spam: DMARC check enabled.");
            }

            if (settings.EnableSpamFiltering)
            {
                var rblDomain = !string.IsNullOrEmpty(settings.SpamhausKey)
                    ? $"{settings.SpamhausKey}.zen.dq.spamhaus.net"
                    : "zen.spamhaus.org";

                spamBuilder.EnableRbl(rblDomain);
                _logger.LogInformation(
                    "Anti-spam: RBL check enabled with domain {RblDomain}.",
                    rblDomain);
            }
            else
            {
                _logger.LogInformation(
                    "Anti-spam: Spam filtering (RBL) is disabled.");
            }
        });

        await _tableLogger.LogInformationAsync(
            $"Anti-spam configured: SPF={settings.EnableSpfCheck}, "
            + $"DKIM={settings.EnableDkimCheck}, "
            + $"DMARC={settings.EnableDmarcCheck}, "
            + $"RBL={settings.EnableSpamFiltering}",
            nameof(SmtpServerHostedService));

        // Wire up Message Handler
        _smtpServer.MessageReceived += async (s, e) => await _messageHandler.HandleMessageAsync(s, e);

        _smtpServer.SessionCompleted += async (s, e) =>
        {
            var ip = (e.Session.RemoteEndPoint as IPEndPoint)?.Address.ToString()
                     ?? "Unknown";

            if (e.Session.Properties.ContainsKey("SpamDetected"))
            {
                // Build a reason string from available session properties
                var reasons = new List<string>();
                if (e.Session.Properties.ContainsKey("SpfResult"))
                    reasons.Add($"SPF={e.Session.Properties["SpfResult"]}");
                if (e.Session.Properties.ContainsKey("DkimResult"))
                    reasons.Add($"DKIM={e.Session.Properties["DkimResult"]}");
                if (e.Session.Properties.ContainsKey("DmarcResult"))
                    reasons.Add($"DMARC={e.Session.Properties["DmarcResult"]}");
                if (e.Session.Properties.ContainsKey("RblResult"))
                    reasons.Add($"RBL={e.Session.Properties["RblResult"]}");

                var reasonStr = reasons.Count > 0
                    ? string.Join("; ", reasons)
                    : "Unknown (SpamDetected flag set but no detail properties)";

                _logger.LogWarning(
                    "Spam detected from {IP}. Reason: {Reason}", ip, reasonStr);

                var spamLog = new SpamLog
                {
                    PartitionKey = "Spam",
                    RowKey = (DateTime.MaxValue.Ticks - DateTime.UtcNow.Ticks)
                             .ToString("d19"),
                    Timestamp = DateTimeOffset.UtcNow,
                    SessionId = e.Session.Properties.TryGetValue("SessionId", out var sid)
                                ? sid?.ToString() : null,
                    IP = ip,
                    From = e.Session.Properties.TryGetValue("MailFrom", out var from)
                           ? from?.ToString() : null,
                    To = e.Session.Properties.TryGetValue("RcptTo", out var to)
                         ? to?.ToString() : null,
                    DetectionReason = reasonStr,
                };

                await _tableLogger.LogSpamAsync(spamLog);
                await _tableLogger.LogInformationAsync(
                    $"Spam logged from {ip}: {reasonStr}",
                    nameof(SmtpServerHostedService));
            }
            else
            {
                _logger.LogDebug(
                    "Session completed for {IP} - no spam detected.", ip);
            }
        };

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