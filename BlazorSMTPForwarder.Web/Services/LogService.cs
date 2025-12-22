using Azure.Data.Tables;
using BlazorSMTPForwarder.ServiceDefaults.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BlazorSMTPForwarder.Web.Services;

public class LogService
{
    private readonly TableClient _tableClient;

    public LogService(TableServiceClient tableServiceClient)
    {
        _tableClient = tableServiceClient.GetTableClient("serverlogs");
        _tableClient.CreateIfNotExists();
    }

    public async Task<(List<ServerLog> Logs, string? ContinuationToken)> GetLogsAsync(int pageSize, string? continuationToken)
    {
        var logs = new List<ServerLog>();
        var query = _tableClient.QueryAsync<ServerLog>(maxPerPage: pageSize);

        var pages = query.AsPages(continuationToken, pageSize);

        await foreach (var page in pages)
        {
            logs.AddRange(page.Values);
            return (logs, page.ContinuationToken);
        }

        return (logs, null);
    }
}
