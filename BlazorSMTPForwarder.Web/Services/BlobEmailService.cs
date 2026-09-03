using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using BlazorSMTPForwarder.ServiceDefaults.Models;

namespace BlazorSMTPForwarder.Web.Services;

/// <summary>
/// Reads messages saved by SMTP service from Azure Blob Storage. Messages are saved as EML files with
/// extra X-SMTP-Server-* headers that we parse for metadata.
/// </summary>
public class BlobEmailService
{
    public sealed record BulkDeleteResult(IReadOnlyList<string> Deleted, IReadOnlyList<DeleteFailure> Failed);
    public sealed record DeleteFailure(string BlobName, string Reason);

    private readonly BlobServiceClient _blobServiceClient;
    private readonly ILogger<BlobEmailService> _logger;
    private readonly string _containerName = "email-messages";
    private BlobContainerClient? _containerClient;
    private bool _containerEnsured;

    public BlobEmailService(BlobServiceClient blobServiceClient, IConfiguration configuration, ILogger<BlobEmailService> logger)
    {
        _blobServiceClient = blobServiceClient;
        _logger = logger;
    }

    private async Task<BlobContainerClient> GetOrCreateContainerAsync(CancellationToken ct)
    {
        _containerClient ??= _blobServiceClient.GetBlobContainerClient(_containerName);

        if (!_containerEnsured)
        {
            try
            {
                await _containerClient.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
            }
            catch (RequestFailedException ex)
            {
                _logger.LogWarning(ex, "Ensure container failed for {Container}", _containerName);
            }
            _containerEnsured = true;
        }
        return _containerClient;
    }

    public async Task<IReadOnlyList<string>> GetRecipientFoldersAsync(CancellationToken ct = default)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var container = await GetOrCreateContainerAsync(ct);
            await foreach (var blob in container.GetBlobsAsync(traits: BlobTraits.None, states: BlobStates.None, prefix: null, cancellationToken: ct))
            {
                var name = blob.Name;
                var parts = name.Split('/');
                if (parts.Length >= 3)
                {
                    // New structure: Domain/User/File.eml
                    results.Add($"{parts[0]}/{parts[1]}");
                }
                else if (parts.Length == 2)
                {
                    // Old structure: User/File.eml
                    var folder = parts[0];
                    if (!string.Equals(folder, ".$logs", StringComparison.Ordinal))
                        results.Add(folder);
                }
            }
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
        {
            // Container missing; return empty and log once
            _logger.LogInformation("Blob container '{Container}' not found yet. Returning empty list.", _containerName);
        }
        catch (RequestFailedException ex)
        {
            // Any other storage issues (auth, DNS, etc.): log and return empty
            _logger.LogWarning(ex, "Listing recipient folders failed for container '{Container}'. Returning empty list.", _containerName);
        }
        catch (InvalidOperationException)
        {
            // Configuration missing; already logged above
        }
        return results.OrderBy(s => s).ToList();
    }

    public async Task<IReadOnlyList<EmailListItem>> ListEmailsAsync(string? recipientFolder, CancellationToken ct = default)
    {
        var items = new List<EmailListItem>();
        try
        {
            var container = await GetOrCreateContainerAsync(ct);
            var prefix = string.IsNullOrWhiteSpace(recipientFolder) ? null : recipientFolder.Trim('/') + "/";

            await foreach (var blob in container.GetBlobsAsync(prefix: prefix, traits: BlobTraits.Metadata, states: BlobStates.None, cancellationToken: ct))
            {
                if (!blob.Name.EndsWith(".eml", StringComparison.OrdinalIgnoreCase))
                    continue;

                var metaRecipient = blob.Metadata.TryGetValue("RecipientUser", out var recipient) ? recipient : recipientFolder;

                if (string.IsNullOrEmpty(metaRecipient))
                {
                    var parts = blob.Name.Split('/');
                    if (parts.Length >= 3)
                    {
                        metaRecipient = $"{parts[1]}@{parts[0]}";
                    }
                    else if (parts.Length == 2)
                    {
                        metaRecipient = parts[0];
                    }
                }

                var received = blob.Properties.CreatedOn ?? blob.Properties.LastModified ?? DateTimeOffset.UtcNow;
                var size = blob.Properties.ContentLength ?? 0;

                var subject = blob.Metadata.TryGetValue("Subject", out var subj) ? subj : null;
                var from = blob.Metadata.TryGetValue("From", out var f) ? f : null;
                var isRead = blob.Metadata.TryGetValue("IsRead", out var readValue)
                    && bool.TryParse(readValue, out var parsedRead) && parsedRead;
                var hasAttachments = blob.Metadata.TryGetValue("HasAttachments", out var attachmentValue)
                    && bool.TryParse(attachmentValue, out var parsedAttachments) && parsedAttachments;

                // If metadata is missing (legacy emails), try to fetch from blob content
                if (subject == null || from == null)
                {
                    try 
                    {
                        var blobClient = container.GetBlobClient(blob.Name);
                        // Download first 4KB to get headers
                        var downloadResult = await blobClient.DownloadAsync(new HttpRange(0, 4096), cancellationToken: ct);
                        using var reader = new StreamReader(downloadResult.Value.Content);
                        var headerText = await reader.ReadToEndAsync();
                        
                        if (subject == null) subject = TryGetHeader(headerText, "Subject") ?? "(no subject)";
                        if (from == null) from = TryGetHeader(headerText, "From") ?? "";
                    }
                    catch
                    {
                        // Ignore errors, just show empty
                    }
                }

                items.Add(new EmailListItem(
                    Id: blob.Name,
                    Subject: subject ?? "(no subject)",
                    From: from ?? "",
                    Received: received,
                    RecipientUser: metaRecipient ?? "",
                    Size: size,
                    BlobName: blob.Name,
                    Container: _containerName ?? string.Empty,
                    IsRead: isRead,
                    HasAttachments: hasAttachments
                ));
            }
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
        {
            _logger.LogInformation("Blob container '{Container}' not found yet. Returning empty list.", _containerName);
        }
        catch (RequestFailedException ex)
        {
            _logger.LogWarning(ex, "Listing emails failed for container '{Container}'. Returning empty list.", _containerName);
        }
        catch (InvalidOperationException)
        {
            // Configuration missing; already logged above
        }
        return items.OrderByDescending(i => i.Received).ToList();
    }

    public async Task<EmailMessage?> GetEmailAsync(string blobName, CancellationToken ct = default)
    {
        try
        {
            var container = await GetOrCreateContainerAsync(ct);
            var blob = container.GetBlobClient(blobName);
            if (!await blob.ExistsAsync(ct))
            {
                _logger.LogWarning("Blob not found: {Blob}", blobName);
                return null;
            }

            using var ms = new MemoryStream();
            await blob.DownloadToAsync(ms, ct);
            ms.Position = 0;
            var raw = Encoding.UTF8.GetString(ms.ToArray());

            // Quick header parse for Subject/From even if the original headers were appended after custom ones
            string subject = TryGetHeader(raw, "Subject") ?? "(no subject)";
            string from = TryGetHeader(raw, "From") ?? "";
            string recipient = TryGetHeader(raw, "X-SMTP-Server-Recipient-User") ?? "";
            
            if (string.IsNullOrEmpty(recipient))
            {
                var parts = blobName.Split('/');
                if (parts.Length >= 3)
                {
                    recipient = $"{parts[1]}@{parts[0]}";
                }
                else if (parts.Length == 2)
                {
                    recipient = parts[0];
                }
            }

            string receivedAt = TryGetHeader(raw, "X-SMTP-Server-Received") ?? DateTime.UtcNow.ToString("R");
            var received = DateTimeOffset.TryParse(receivedAt, out var r) ? r : DateTimeOffset.UtcNow;

            var props = await blob.GetPropertiesAsync(cancellationToken: ct);
            var size = props.Value.ContentLength;
            var metadata = props.Value.Metadata;

            var item = new EmailListItem(
                Id: blobName,
                Subject: subject,
                From: from,
                Received: received,
                RecipientUser: recipient,
                Size: size,
                BlobName: blobName,
                Container: _containerName ?? string.Empty,
                IsRead: metadata.TryGetValue("IsRead", out var readValue)
                    && bool.TryParse(readValue, out var parsedRead) && parsedRead,
                HasAttachments: metadata.TryGetValue("HasAttachments", out var attachmentValue)
                    && bool.TryParse(attachmentValue, out var parsedAttachments) && parsedAttachments
            );

            return new EmailMessage(item, raw);
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
        {
            _logger.LogWarning("Blob container '{Container}' not found when fetching blob {Blob}.", _containerName, blobName);
            return null;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogWarning(ex, "Fetching blob '{Blob}' failed for container '{Container}'. Returning null.", blobName, _containerName);
            return null;
        }
        catch (InvalidOperationException)
        {
            // Configuration missing; already logged above
            return null;
        }
    }

    public async Task<bool> DeleteEmailAsync(string blobName, CancellationToken ct = default)
    {
        if (!IsValidEmailBlobName(blobName))
            return false;

        try
        {
            var container = await GetOrCreateContainerAsync(ct);
            var blob = container.GetBlobClient(blobName);
            var response = await blob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
            if (!response.Value)
            {
                _logger.LogInformation("Blob '{Blob}' did not exist when attempting delete.", blobName);
            }
            return true; // treat as success if it no longer exists
        }
        catch (RequestFailedException ex)
        {
            _logger.LogWarning(ex, "Deleting blob '{Blob}' failed for container '{Container}'.", blobName, _containerName);
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public async Task<BulkDeleteResult> DeleteEmailsAsync(
        IReadOnlyCollection<string> blobNames, CancellationToken ct = default)
    {
        var deleted = new List<string>();
        var failed = new List<DeleteFailure>();
        using var gate = new SemaphoreSlim(8);
        var tasks = blobNames.Distinct(StringComparer.Ordinal).Select(async blobName =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (await DeleteEmailAsync(blobName, ct))
                    lock (deleted) deleted.Add(blobName);
                else
                    lock (failed) failed.Add(new DeleteFailure(blobName, "Blob deletion failed."));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (failed) failed.Add(new DeleteFailure(blobName, ex.Message));
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
        return new BulkDeleteResult(deleted, failed);
    }

    public Task<bool> SetReadAsync(string blobName, bool isRead, CancellationToken ct = default) =>
        UpdateMetadataAsync(blobName, new Dictionary<string, string> { ["IsRead"] = isRead.ToString().ToLowerInvariant() }, ct);

    public async Task<IReadOnlyList<string>> SetReadAsync(
        IReadOnlyCollection<string> blobNames, bool isRead, CancellationToken ct = default)
    {
        var updated = new List<string>();
        foreach (var blobName in blobNames.Distinct(StringComparer.Ordinal))
        {
            if (await SetReadAsync(blobName, isRead, ct))
                updated.Add(blobName);
        }
        return updated;
    }

    public async Task<int> GetUnreadCountAsync(string? recipientFolder, CancellationToken ct = default)
    {
        var messages = await ListEmailsAsync(recipientFolder, ct);
        return messages.Count(message => !message.IsRead);
    }

    private async Task<bool> UpdateMetadataAsync(string blobName, IDictionary<string, string> updates, CancellationToken ct)
    {
        if (!IsValidEmailBlobName(blobName))
            return false;

        try
        {
            var container = await GetOrCreateContainerAsync(ct);
            var blob = container.GetBlobClient(blobName);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var properties = await blob.GetPropertiesAsync(cancellationToken: ct);
                var metadata = new Dictionary<string, string>(properties.Value.Metadata, StringComparer.OrdinalIgnoreCase);
                foreach (var update in updates)
                    metadata[update.Key] = update.Value;
                try
                {
                    await blob.SetMetadataAsync(metadata,
                        new BlobRequestConditions { IfMatch = properties.Value.ETag }, ct);
                    return true;
                }
                catch (RequestFailedException ex) when (ex.Status == 412 && attempt == 0)
                {
                }
            }
        }
        catch (RequestFailedException ex)
        {
            _logger.LogWarning(ex, "Updating metadata failed for blob {Blob}", blobName);
        }
        return false;
    }

    private static bool IsValidEmailBlobName(string blobName)
    {
        if (string.IsNullOrWhiteSpace(blobName) || blobName.Contains("..", StringComparison.Ordinal))
            return false;

        var parts = blobName.Split('/');
        return parts.Length == 3
            && parts.All(part => !string.IsNullOrWhiteSpace(part))
            && blobName.EndsWith(".eml", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetHeader(string eml, string header)
    {
        using var reader = new StringReader(eml);
        string? line;
        var headerPrefix = header + ":";
        int blankBlocksSeen = 0;
        int linesScanned = 0;
        while ((line = reader.ReadLine()) != null)
        {
            linesScanned++;
            if (linesScanned > 400) break; // safety bound near top of file

            if (line.StartsWith(headerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return line.Substring(headerPrefix.Length).Trim();
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                blankBlocksSeen++;
                if (blankBlocksSeen >= 2)
                {
                    // We scanned two header-like blocks (custom + original). Bail to avoid scanning body.
                    break;
                }
                continue;
            }
        }
        return null;
    }
}
