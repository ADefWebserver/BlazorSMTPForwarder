using BlazorSMTPForwarder.ServiceDefaults.Models;

namespace BlazorSMTPForwarder.Web.GridRows;

internal static class GridProjections
{
    public static EmailRow ToRow(this EmailListItem m) => new()
    {
        Id = m.Id,
        Subject = string.IsNullOrWhiteSpace(m.Subject) ? "(no subject)" : m.Subject,
        From = m.From,
        RecipientUser = m.RecipientUser,
        Received = m.Received.ToLocalTime().ToString("g"),
        SizeBytes = (int)Math.Min(m.Size, int.MaxValue)
    };

    public static ServerLogRow ToRow(this ServerLog l) => new()
    {
        Time = l.Timestamp?.ToLocalTime().ToString("g") ?? "",
        Level = l.Level ?? "",
        Source = l.Source ?? "",
        Message = l.Message ?? "",
        Exception = l.Exception ?? ""
    };

    public static SpamLogRow ToRow(this SpamLog s) => new()
    {
        Time = s.Timestamp?.ToLocalTime().ToString("g") ?? "",
        From = s.From ?? "",
        To = s.To ?? "",
        Subject = s.Subject ?? "",
        IP = s.IP ?? "",
        DetectionReason = s.DetectionReason ?? ""
    };
}
