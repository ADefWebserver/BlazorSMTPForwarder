using BlazorDX.Primitives.Grid;

namespace BlazorSMTPForwarder.Web.GridRows;

[GridRow]
public sealed class ServerLogRow
{
    [GridColumn("Time", Order = 0)] public string Time { get; set; } = "";
    [GridColumn("Level", Order = 1)] public string Level { get; set; } = "";
    [GridColumn("Source", Order = 2)] public string Source { get; set; } = "";
    [GridColumn("Message", Order = 3)] public string Message { get; set; } = "";
    [GridColumn("Exception", Order = 4)] public string Exception { get; set; } = "";
}
