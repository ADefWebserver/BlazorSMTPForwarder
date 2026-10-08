using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zetian.Server;
using Zetian.Relay.Extensions;
using Zetian.Relay.Configuration;
using Zetian.AntiSpam.Builders;
using Zetian.AntiSpam.Checkers;
using Zetian.AntiSpam.Models;
using Zetian.Models.EventArgs;
using Zetian.Protocol;
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

    // Total spam score at or above which a message is rejected.
    private const double SpamRejectThreshold = 50;


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

        // Configure Anti-Spam.
        // NOTE: Zetian's AddAntiSpam() extension is intentionally NOT used. It hooks
        // MessageReceived with an async void handler, so e.Cancel is set only after the
        // server has already accepted the message (and our handler has forwarded it).
        // Instead we build the AntiSpamService ourselves and run it synchronously below.
        var antiSpamBuilder = new AntiSpamBuilder()
            .WithOptions(options => options.RejectThreshold = SpamRejectThreshold);

        // SPF/DKIM/DMARC use our own checker; Zetian's built-in ones produce false results.
        if (settings.EnableSpfCheck || settings.EnableDkimCheck || settings.EnableDmarcCheck)
        {
            antiSpamBuilder.AddChecker(new EmailAuthenticationChecker(
                settings.EnableSpfCheck, settings.EnableDkimCheck, settings.EnableDmarcCheck));
            _logger.LogInformation(
                "Anti-spam: email authentication enabled (SPF={Spf}, DKIM={Dkim}, DMARC={Dmarc}).",
                settings.EnableSpfCheck, settings.EnableDkimCheck, settings.EnableDmarcCheck);
        }

        if (settings.EnableSpamFiltering)
        {
            var rblDomain = !string.IsNullOrEmpty(settings.SpamhausKey)
                ? $"{settings.SpamhausKey}.zen.dq.spamhaus.net"
                : "zen.spamhaus.org";

            // A Spamhaus listing alone must reach the reject threshold (the library
            // default of 25 per listing never would). Only 127.0.0.x answers are real
            // listings; 127.255.255.x are Spamhaus error codes (e.g. public resolver
            // blocked, bad DQS key) and must not be treated as spam.
            antiSpamBuilder.EnableRbl(
                new[]
                {
                    new RblProvider
                    {
                        Name = "Spamhaus ZEN",
                        Zone = rblDomain,
                        IsEnabled = true,
                        ExpectedResponses = ["127.0.0."]
                    }
                },
                scorePerListing: 100);

            _logger.LogInformation(
                "Anti-spam: RBL check enabled with domain {RblDomain}.",
                rblDomain);

            if (string.IsNullOrEmpty(settings.SpamhausKey))
            {
                await _tableLogger.LogErrorAsync(
                    "Spam filtering is enabled without a Spamhaus DQS key. zen.spamhaus.org "
                    + "refuses queries from public/cloud DNS resolvers (such as Azure DNS), "
                    + "so RBL checks will likely never match. Set a Spamhaus DQS key.",
                    null, nameof(SmtpServerHostedService));
            }
        }
        else
        {
            _logger.LogInformation(
                "Anti-spam: Spam filtering (RBL) is disabled.");
        }

        var antiSpamService = antiSpamBuilder.Build();
        var antiSpamEnabled = antiSpamService.GetCheckers().Count > 0;

        await _tableLogger.LogInformationAsync(
            $"Anti-spam configured: SPF={settings.EnableSpfCheck}, "
            + $"DKIM={settings.EnableDkimCheck}, "
            + $"DMARC={settings.EnableDmarcCheck}, "
            + $"RBL={settings.EnableSpamFiltering}",
            nameof(SmtpServerHostedService));

        // Wire up Message Handler.
        // Zetian raises MessageReceived synchronously and checks e.Cancel as soon as the
        // handlers return, so the spam check must complete before this handler returns.
        _smtpServer.MessageReceived += (s, e) =>
        {
            if (antiSpamEnabled)
            {
                SpamCheckResult? spamResult = null;
                try
                {
                    spamResult = antiSpamService
                        .CheckMessageAsync(e.Message, e.Session, stoppingToken)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Anti-spam check failed. Accepting message.");
                }

                if (spamResult != null && spamResult.IsSpam)
                {
                    e.Cancel = true;
                    e.Response = new SmtpResponse(550, $"5.7.1 Message rejected as spam: {spamResult.Reason}");
                    _ = LogSpamAsync(e, spamResult);
                    return;
                }
            }

            _ = _messageHandler.HandleMessageAsync(s, e);
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

    private async Task LogSpamAsync(MessageEventArgs e, SpamCheckResult result)
    {
        try
        {
            var ip = (e.Session.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "Unknown";
            var reason = $"Score={result.Score:F0}; {result.Reason}";
            if (!string.IsNullOrWhiteSpace(result.Details))
            {
                reason += $" ({result.Details.Replace("\n", "; ")})";
            }

            _logger.LogWarning("Spam rejected from {IP}. Reason: {Reason}", ip, reason);

            await _tableLogger.LogSpamAsync(new SpamLog
            {
                PartitionKey = "Spam",
                RowKey = (DateTime.MaxValue.Ticks - DateTime.UtcNow.Ticks).ToString("d19"),
                Timestamp = DateTimeOffset.UtcNow,
                SessionId = e.Session.Id,
                TransactionId = e.Message.Id,
                IP = ip,
                From = e.Message.From?.Address,
                To = string.Join(", ", e.Message.Recipients.Select(r => r.Address)),
                Subject = e.Message.Subject,
                DetectionReason = reason,
            });
            await _tableLogger.LogInformationAsync(
                $"Spam rejected from {ip}: {reason}",
                nameof(SmtpServerHostedService));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log spam message.");
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