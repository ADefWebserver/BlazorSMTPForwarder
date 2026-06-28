using BlazorDX.Primitives.Grid;

namespace BlazorSMTPForwarder.Web.GridRows;

[GridRow]
public sealed class EmailRow
{
    [GridColumn("Id", Order = 0)] public string Id { get; set; } = "";
    [GridColumn("Subject", Order = 1)] public string Subject { get; set; } = "";
    [GridColumn("From", Order = 2)] public string From { get; set; } = "";
    [GridColumn("To", Order = 3)] public string RecipientUser { get; set; } = "";
    [GridColumn("Received", Order = 4)] public string Received { get; set; } = "";
    [GridColumn("Size", Order = 5)] public int SizeBytes { get; set; }
}
