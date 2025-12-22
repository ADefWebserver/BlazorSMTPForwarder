using System;
using System.Collections.Generic;
using System.Text;

namespace BlazorSMTPForwarderSrv.Models
{
    public class SmtpSettingsModel
    {
        public string? ServerName { get; set; }
        public string? ServerDomains { get; set; }
        public bool EnableSpamFiltering { get; set; }
        public string? SpamhausKey { get; set; }
        public bool EnableSpfCheck { get; set; }
        public bool EnableDkimCheck { get; set; }
        public bool EnableDmarcCheck { get; set; }
        public string? SendGridApiKey { get; set; }
        public string? SendGridFromEmail { get; set; }
        public string? DomainsJson { get; set; }
        public bool DoNotSaveMessages { get; set; }
    }
}
