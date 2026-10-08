using System.Collections.Concurrent;
using System.Net;
using ARSoft.Tools.Net.Spf;
using DnsClient;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Crypto;
using Zetian.Abstractions;
using Zetian.AntiSpam.Abstractions;
using Zetian.AntiSpam.Models;

namespace BlazorSMTPForwarderSrv.Services;

/// <summary>
/// SPF, DKIM and DMARC checks, replacing Zetian.AntiSpam's built-in checkers, which are unreliable:
/// its DKIM checker hashes the decoded text part instead of the raw body (so valid signatures
/// fail), its DMARC SPF-alignment compares the From domain with itself (so it always "aligns"),
/// and its SPF evaluator ignores redirect=/exists:/qualifiers (so valid records can "Fail").
///
/// Scoring is designed so authentication alone only rejects when the sender's own domain asks
/// for it (SPF -all fail, or DMARC p=reject/quarantine); a DMARC pass cancels SPF/DKIM penalties.
/// </summary>
public class EmailAuthenticationChecker : ISpamChecker
{
    private const double SpfFailScore = 50;
    private const double SpfSoftFailScore = 20;
    private const double DkimFailScore = 20;
    private const double DmarcRejectScore = 70;
    private const double DmarcQuarantineScore = 50;

    private readonly bool _checkSpf;
    private readonly bool _checkDkim;
    private readonly bool _checkDmarc;
    private readonly ILookupClient _dns = new LookupClient();
    private readonly IDkimPublicKeyLocator _dkimKeyLocator;

    public EmailAuthenticationChecker(
        bool checkSpf, bool checkDkim, bool checkDmarc, IDkimPublicKeyLocator? dkimKeyLocator = null)
    {
        _checkSpf = checkSpf;
        _checkDkim = checkDkim;
        _checkDmarc = checkDmarc;
        _dkimKeyLocator = dkimKeyLocator ?? new DnsDkimPublicKeyLocator(_dns);
    }

    public string Name => "EmailAuthentication";

    public bool IsEnabled { get; set; } = true;

    public async Task<SpamCheckResult> CheckAsync(
        ISmtpMessage message, ISmtpSession session, CancellationToken cancellationToken = default)
    {
        var clientIp = (session.RemoteEndPoint as IPEndPoint)?.Address;
        var envelopeDomain = message.From?.Host;

        MimeMessage? mime = null;
        try
        {
            using var raw = message.GetRawDataStream();
            mime = await MimeMessage.LoadAsync(raw, cancellationToken);
        }
        catch
        {
            // Unparseable message: SPF can still run, DKIM/DMARC cannot.
        }

        var headerFromDomain = mime?.From.Mailboxes.FirstOrDefault()?.Domain;

        // SPF (needed for DMARC as well, even when SPF scoring is off).
        var spf = SpfQualifier.None;
        if ((_checkSpf || _checkDmarc) && clientIp != null)
        {
            spf = await CheckSpfAsync(clientIp, envelopeDomain ?? session.ClientDomain, message.From?.Address, session.ClientDomain, cancellationToken);
        }

        // DKIM (needed for DMARC as well).
        var dkim = DkimStatus.None;
        var dkimPassDomains = new List<string>();
        if ((_checkDkim || _checkDmarc) && mime != null)
        {
            (dkim, dkimPassDomains) = await CheckDkimAsync(mime, cancellationToken);
        }

        var notes = new List<string> { $"SPF={spf} ({envelopeDomain ?? "no envelope sender"})" };
        notes.Add(dkimPassDomains.Count > 0 ? $"DKIM={dkim} (d={string.Join(",", dkimPassDomains)})" : $"DKIM={dkim}");

        // DMARC
        DmarcOutcome dmarc = DmarcOutcome.NotChecked;
        string? dmarcPolicy = null;
        if (_checkDmarc && !string.IsNullOrEmpty(headerFromDomain))
        {
            dmarcPolicy = await GetDmarcPolicyAsync(headerFromDomain, cancellationToken);
            if (dmarcPolicy == null)
            {
                dmarc = DmarcOutcome.NoRecord;
            }
            else
            {
                var spfAligned = spf == SpfQualifier.Pass && envelopeDomain != null && IsRelaxedAligned(envelopeDomain, headerFromDomain);
                var dkimAligned = dkimPassDomains.Any(d => IsRelaxedAligned(d, headerFromDomain));
                dmarc = spfAligned || dkimAligned ? DmarcOutcome.Pass : DmarcOutcome.Fail;
            }
            notes.Add(dmarcPolicy == null ? $"DMARC={dmarc} ({headerFromDomain})" : $"DMARC={dmarc} ({headerFromDomain}, p={dmarcPolicy})");
        }

        double score = 0;
        var reasons = new List<string>();

        if (dmarc == DmarcOutcome.Pass)
        {
            // The From domain is authenticated; individual SPF/DKIM failures don't matter.
        }
        else
        {
            if (dmarc == DmarcOutcome.Fail && dmarcPolicy == "reject")
            {
                score += DmarcRejectScore; reasons.Add("DMARC fail (p=reject)");
            }
            else if (dmarc == DmarcOutcome.Fail && dmarcPolicy == "quarantine")
            {
                score += DmarcQuarantineScore; reasons.Add("DMARC fail (p=quarantine)");
            }

            if (_checkSpf && spf == SpfQualifier.Fail)
            {
                score += SpfFailScore; reasons.Add("SPF fail");
            }
            else if (_checkSpf && spf == SpfQualifier.SoftFail)
            {
                score += SpfSoftFailScore; reasons.Add("SPF softfail");
            }

            if (_checkDkim && dkim == DkimStatus.Fail)
            {
                score += DkimFailScore; reasons.Add("DKIM signature invalid");
            }
        }

        var details = string.Join("; ", notes);
        return score > 0
            ? SpamCheckResult.Spam(score, string.Join(", ", reasons), details)
            : SpamCheckResult.Clean(0, details);
    }

    private static async Task<SpfQualifier> CheckSpfAsync(
        IPAddress ip, string? domain, string? sender, string? heloDomain, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return SpfQualifier.None;
        }

        try
        {
            var validator = new SpfValidator();
            if (!string.IsNullOrWhiteSpace(heloDomain))
            {
                try { validator.HeloDomain = ARSoft.Tools.Net.DomainName.Parse(heloDomain); } catch { }
            }

            var result = await validator.CheckHostAsync(ip, domain, sender ?? $"postmaster@{domain}", token: cancellationToken);
            return result.Result;
        }
        catch
        {
            return SpfQualifier.TempError;
        }
    }

    private async Task<(DkimStatus Status, List<string> PassDomains)> CheckDkimAsync(
        MimeMessage mime, CancellationToken cancellationToken)
    {
        var signatures = mime.Headers.Where(h => h.Id == HeaderId.DkimSignature).ToList();
        var passDomains = new List<string>();
        if (signatures.Count == 0)
        {
            return (DkimStatus.None, passDomains);
        }

        var verifier = new DkimVerifier(_dkimKeyLocator);
        var anyTempError = false;
        foreach (var signature in signatures)
        {
            try
            {
                if (await verifier.VerifyAsync(mime, signature, cancellationToken))
                {
                    var domain = GetTag(signature.Value, "d");
                    if (!string.IsNullOrEmpty(domain))
                    {
                        passDomains.Add(domain.ToLowerInvariant());
                    }
                }
            }
            catch (DnsLookupException)
            {
                anyTempError = true;
            }
            catch
            {
                // Malformed signature or key: counts as an invalid signature.
            }
        }

        if (passDomains.Count > 0) return (DkimStatus.Pass, passDomains);
        return (anyTempError ? DkimStatus.TempError : DkimStatus.Fail, passDomains);
    }

    private async Task<string?> GetDmarcPolicyAsync(string fromDomain, CancellationToken cancellationToken)
    {
        // Look up the exact domain, then the organizational domain (RFC 7489 section 6.6.3).
        foreach (var domain in new[] { fromDomain, GetOrganizationalDomain(fromDomain) }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var response = await _dns.QueryAsync($"_dmarc.{domain}", QueryType.TXT, cancellationToken: cancellationToken);
                var record = response.Answers.TxtRecords()
                    .Select(r => string.Concat(r.Text))
                    .FirstOrDefault(t => t.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase));
                if (record == null) continue;

                // For a subdomain, sp= overrides p= when the record came from the organizational domain.
                var isOrgRecord = !domain.Equals(fromDomain, StringComparison.OrdinalIgnoreCase);
                var policy = (isOrgRecord ? GetTag(record, "sp") : null) ?? GetTag(record, "p");
                return policy?.ToLowerInvariant() switch
                {
                    "reject" => "reject",
                    "quarantine" => "quarantine",
                    _ => "none"
                };
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    private static bool IsRelaxedAligned(string a, string b) =>
        GetOrganizationalDomain(a).Equals(GetOrganizationalDomain(b), StringComparison.OrdinalIgnoreCase);

    // Approximation of the organizational domain without a full public suffix list:
    // keeps three labels for common second-level registries like example.co.uk.
    private static string GetOrganizationalDomain(string domain)
    {
        var labels = domain.Trim('.').ToLowerInvariant().Split('.');
        if (labels.Length <= 2) return string.Join('.', labels);
        string[] secondLevel = ["co", "com", "net", "org", "gov", "edu", "ac"];
        var keep = labels[^1].Length == 2 && secondLevel.Contains(labels[^2]) ? 3 : 2;
        return string.Join('.', labels.TakeLast(keep));
    }

    private static string? GetTag(string value, string tag)
    {
        foreach (var part in value.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals(tag, StringComparison.OrdinalIgnoreCase))
            {
                return new string(kv[1].Where(c => !char.IsWhiteSpace(c)).ToArray());
            }
        }
        return null;
    }

    private enum DkimStatus { None, Pass, Fail, TempError }

    private enum DmarcOutcome { NotChecked, NoRecord, Pass, Fail }

    private class DnsLookupException(string message) : Exception(message);

    private class DnsDkimPublicKeyLocator(ILookupClient dns) : DkimPublicKeyLocatorBase
    {
        private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);
        private static readonly ConcurrentDictionary<string, (AsymmetricKeyParameter Key, DateTime Expires)> Cache = new();

        public override AsymmetricKeyParameter LocatePublicKey(
            string methods, string domain, string selector, CancellationToken cancellationToken = default) =>
            LocatePublicKeyAsync(methods, domain, selector, cancellationToken).GetAwaiter().GetResult();

        public override async Task<AsymmetricKeyParameter> LocatePublicKeyAsync(
            string methods, string domain, string selector, CancellationToken cancellationToken = default)
        {
            var name = $"{selector}._domainkey.{domain}";
            if (Cache.TryGetValue(name, out var cached) && cached.Expires > DateTime.UtcNow) return cached.Key;

            IDnsQueryResponse response;
            try
            {
                response = await dns.QueryAsync(name, QueryType.TXT, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                throw new DnsLookupException($"DKIM key lookup failed for {name}: {ex.Message}");
            }

            var txt = response.Answers.TxtRecords().Select(r => string.Concat(r.Text)).FirstOrDefault()
                      ?? throw new InvalidOperationException($"No DKIM key found at {name}");
            var key = GetPublicKey(txt);
            Cache[name] = (key, DateTime.UtcNow.Add(CacheDuration));
            return key;
        }
    }
}
