using BlazorDX.Primitives.Grid;

namespace BlazorSMTPForwarder.Web.GridRows;

[GridRow]
public sealed class SpamLogRow
{
    [GridColumn("Time", Order = 0)] public string Time { get; set; } = "";
    [GridColumn("From", Order = 1)] public string From { get; set; } = "";
    [GridColumn("To", Order = 2)] public string To { get; set; } = "";
    [GridColumn("Subject", Order = 3)] public string Subject { get; set; } = "";
    [GridColumn("IP", Order = 4)] public string IP { get; set; } = "";
    [GridColumn("Reason", Order = 5)] public string DetectionReason { get; set; } = "";
}
