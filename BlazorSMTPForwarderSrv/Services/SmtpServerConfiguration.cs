using Azure.Data.Tables;
using Azure.Storage.Blobs;
using BlazorSMTPForwarderSrv.Models;
using DnsClient.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading;

namespace BlazorSMTPForwarderSrv.Services;

public class SmtpServerConfiguration
{
    public const string SectionName = "SmtpServer";

    private readonly ILogger<SmtpServerConfiguration> _logger;
    private readonly TableServiceClient _tableServiceClient;

    public SmtpServerConfiguration(
        ILogger<SmtpServerConfiguration> logger,
        TableServiceClient tableServiceClient)
    {
        _logger = logger;
        _tableServiceClient = tableServiceClient;
    }

    public async Task<SmtpSettingsModel> LoadSettingsAsync(CancellationToken cancellationToken = default)
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

            EnsureProperty("ServerName", "localhost");
            EnsureProperty("ServerPorts", "25");
            EnsureProperty("SpamhausKey", "");
            EnsureProperty("EnableSpfCheck", false);
            EnsureProperty("EnableDkimCheck", false);
            EnsureProperty("EnableDmarcCheck", false);
            EnsureProperty("SendGridApiKey", "");

            // Update the table if new properties were added
            if (needsUpdate)
            {
                await table.UpsertEntityAsync(entity, TableUpdateMode.Merge, cancellationToken);
                _logger.LogInformation("Initialized or updated SMTPSettings table with default values.");
            }

            model.ServerName = entity.GetString("ServerName");
            var portsString = entity.GetString("ServerPorts");
            if (!string.IsNullOrEmpty(portsString))
            {
                model.ServerPorts = portsString.Split(',').Select(p => int.Parse(p.Trim())).ToArray();
            }
            else
            {
                model.ServerPorts = new int[] { 25 };
            }

            model.EnableSpamFiltering = entity.GetBoolean("EnableSpamFiltering") ?? false;
            model.SpamhausKey = entity.GetString("SpamhausKey");
            model.EnableSpfCheck = entity.GetBoolean("EnableSpfCheck") ?? false;
            model.EnableDkimCheck = entity.GetBoolean("EnableDkimCheck") ?? false;
            model.EnableDmarcCheck = entity.GetBoolean("EnableDmarcCheck") ?? false;

            model.SendGridApiKey = entity.GetString("SendGridApiKey");
            model.DomainsJson = entity.GetString("DomainsJson");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings from Azure Table. Using defaults.");
        }
        return model;
    }
}
