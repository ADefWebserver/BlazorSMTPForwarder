using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpServer;
using SmtpServer.ComponentModel;
using BlazorSMTPForwarder.ServiceDefaults.Models;

namespace BlazorSMTPForwarderSrv.Services;

public class SmtpServerHostedService : IHostedService
{
    private readonly SmtpServer.SmtpServer _smtpServer;
    private readonly ILogger<SmtpServerHostedService> _logger;

    public SmtpServerHostedService(
        IOptions<SmtpServerConfiguration> configuration,
        IServiceProvider serviceProvider,
        ILogger<SmtpServerHostedService> logger)
    {
        _logger = logger;
        var config = configuration.Value;

        var optionsBuilder = new SmtpServerOptionsBuilder()
            .ServerName(config.ServerName ?? "localhost")
            .Port(config.Ports ?? new[] { 25, 587, 2525 })
            .Build();

        _smtpServer = new SmtpServer.SmtpServer(optionsBuilder, serviceProvider);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting SMTP Server...");
        return _smtpServer.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping SMTP Server...");
        _smtpServer.Shutdown();
        return Task.CompletedTask;
    }
}
