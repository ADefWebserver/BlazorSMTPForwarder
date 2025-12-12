namespace BlazorSMTPForwarder.ServiceDefaults.Models;

public record EmailMessage(
    EmailListItem Metadata,
    string RawEml
);
