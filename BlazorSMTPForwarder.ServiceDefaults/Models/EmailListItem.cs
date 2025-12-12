using System;

namespace BlazorSMTPForwarder.ServiceDefaults.Models;

public record EmailListItem(
    string Id,
    string Subject,
    string From,
    DateTimeOffset Received,
    string RecipientUser,
    long Size,
    string BlobName,
    string Container
);
