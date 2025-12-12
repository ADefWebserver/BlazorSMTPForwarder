using System.Collections.Generic;

namespace BlazorSMTPForwarder.ServiceDefaults.Models;

public class DomainConfiguration
{
    public string DomainName { get; set; } = string.Empty;
    public List<EmailForwardingRule> ForwardingRules { get; set; } = new();
    public CatchAllAction CatchAll { get; set; } = new();
}

public class EmailForwardingRule
{
    public string IncomingEmail { get; set; } = string.Empty;
    public string DestinationEmail { get; set; } = string.Empty;
}

public class CatchAllAction
{
    public CatchAllType Type { get; set; } = CatchAllType.Reject;
    public string? ForwardToEmail { get; set; }
}

public enum CatchAllType
{
    Reject,
    Delete,
    Forward
}
