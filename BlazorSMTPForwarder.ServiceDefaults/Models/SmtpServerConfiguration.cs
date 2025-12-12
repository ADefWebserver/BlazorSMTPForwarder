namespace BlazorSMTPForwarder.ServiceDefaults.Models;

public class SmtpServerConfiguration
{
    public const string SectionName = "SmtpServer";

    public string? ServerName { get; set; }
    public string? Domain { get; set; }
    public int[]? Ports { get; set; }
}
