using Azure;
using Azure.Data.Tables;
using System;

namespace BlazorSMTPForwarder.ServiceDefaults.Models;

public class ServerLog : ITableEntity
{
    public string PartitionKey { get; set; } = default!;
    public string RowKey { get; set; } = default!;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string? Message { get; set; }
    public string? Level { get; set; } // Info, Warning, Error
    public string? Exception { get; set; }
    public string? Source { get; set; }
}
