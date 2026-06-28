using Azure.Data.Tables;
using BlazorSMTPForwarder.ServiceDefaults.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BlazorSMTPForwarder.Web.Services;

public class LogService
{
    private readonly TableClient _tableClient;
    private bool _tableEnsured;

    public LogService(TableServiceClient tableServiceClient)
    {
        _tableClient = tableServiceClient.GetTableClient("serverlogs");
    }

    private async Task EnsureTableAsync()
    {
        if (_tableEnsured) return;
        try
        {
            await _tableClient.CreateIfNotExistsAsync();
        }
        catch
        {
            // Table may already exist or storage may be temporarily unreachable
        }
        _tableEnsured = true;
    }

    public async Task<List<ServerLog>> GetAllLogsAsync()
    {
        await EnsureTableAsync();
        var logs = new List<ServerLog>();
        await foreach (var log in _tableClient.QueryAsync<ServerLog>())
        {
            logs.Add(log);
        }
        return logs;
    }

    public async Task<(List<ServerLog> Logs, string? ContinuationToken)> GetLogsAsync(int pageSize, string? afterRowKey)
    {
        await EnsureTableAsync();
        var logs = new List<ServerLog>();

        // Use RowKey cursor-based pagination instead of continuation tokens.
        // RowKey = (MaxTicks - UtcNow.Ticks) so entries are sorted newest-first ascending.
        // To get the next page, filter for RowKey > last seen RowKey (= older entries).
        string? filter = string.IsNullOrEmpty(afterRowKey)
            ? "PartitionKey eq 'Log'"
            : $"PartitionKey eq 'Log' and RowKey gt '{afterRowKey}'";

        await foreach (var log in _tableClient.QueryAsync<ServerLog>(filter: filter, maxPerPage: pageSize))
        {
            logs.Add(log);
            if (logs.Count >= pageSize) break;
        }

        // If we got a full page, there are likely more entries
        string? nextCursor = logs.Count >= pageSize ? logs[^1].RowKey : null;
        return (logs, nextCursor);
    }
}
