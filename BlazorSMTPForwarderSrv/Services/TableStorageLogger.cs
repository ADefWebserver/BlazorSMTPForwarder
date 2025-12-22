using Azure.Data.Tables;
using BlazorSMTPForwarder.ServiceDefaults.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace BlazorSMTPForwarderSrv.Services;

public class TableStorageLogger
{
    private readonly TableClient _tableClient;
    private readonly ILogger<TableStorageLogger> _logger;

    public TableStorageLogger(TableServiceClient tableServiceClient, ILogger<TableStorageLogger> logger)
    {
        _tableClient = tableServiceClient.GetTableClient("serverlogs");
        _tableClient.CreateIfNotExists();
        _logger = logger;
    }

    public async Task LogAsync(string message, string level = "Info", string? exception = null, string? source = null)
    {
        try
        {
            // Use a reverse chronological RowKey so newest logs are at the top (lexicographically)
            // Or just use standard Guid/Timestamp. 
            // For infinite scroll, usually we want newest first.
            // Azure Table Storage sorts by PartitionKey then RowKey.
            // Using (DateTime.MaxValue.Ticks - DateTime.UtcNow.Ticks) as RowKey ensures newest first.
            
            var rowKey = (DateTime.MaxValue.Ticks - DateTime.UtcNow.Ticks).ToString("d19");

            var log = new ServerLog
            {
                PartitionKey = "Log",
                RowKey = rowKey,
                Timestamp = DateTimeOffset.UtcNow,
                Message = message,
                Level = level,
                Exception = exception,
                Source = source
            };

            await _tableClient.AddEntityAsync(log);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write to table storage log.");
        }
    }

    public async Task LogInformationAsync(string message, string? source = null)
    {
        await LogAsync(message, "Info", null, source);
    }

    public async Task LogErrorAsync(string message, Exception? ex = null, string? source = null)
    {
        await LogAsync(message, "Error", ex?.ToString(), source);
    }
    
    public async Task LogWarningAsync(string message, string? source = null)
    {
        await LogAsync(message, "Warning", null, source);
    }
}
