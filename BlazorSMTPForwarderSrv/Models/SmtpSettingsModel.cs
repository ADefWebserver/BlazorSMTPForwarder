using System;
using System.Collections.Generic;
using System.Text;

namespace BlazorSMTPForwarderSrv.Models
{
    public class SmtpSettingsModel
    {
        public string ServerName { get; set; }
        public int[] ServerPorts { get; set; }
        public bool EnableSpamFiltering { get; set; }
        public string? SpamhausKey { get; set; }
        public bool EnableSpfCheck { get; set; }
        public bool EnableDkimCheck { get; set; }
        public bool EnableDmarcCheck { get; set; }
        public string? SendGridHost { get; set; }
        public int SendGridPort { get; set; }
        public string? SendGridUser { get; set; }
        public string? SendGridPass { get; set; }
    }
}
