using System.Net;
using System.Net.Mail;

namespace Conduit.Web.Services;

/// <summary>The relay IdentityCenter sends through, as its mail claim hands it over. The password never travels.</summary>
public sealed record IcMailRelay(string Host, int Port, bool UseSsl, string FromAddress, string? FromName, string? ReplyToAddress, bool SignInRequired);

/// <summary>One queued message, already through IdentityCenter's expiry, daily cap, recipient policy and atomic claim.</summary>
public sealed record IcMailItem(Guid Id, string To, string? ToName, string Subject, string Body, bool IsHtml);

/// <summary>What IdentityCenter answered to a mail claim. <see cref="Unavailable"/> set = nothing was claimed, and why.</summary>
public sealed record IcMailBatch(IcMailRelay? Relay, IReadOnlyList<IcMailItem> Items, int Expired, int Refused, bool DailyCapReached, string? Unavailable);

public sealed record IcMailResult(Guid Id, bool Success, string? Error);

/// <summary>Sends one message through a relay. Returns null when the relay accepted it, otherwise the reason it did not.</summary>
public interface IMailRelaySender
{
    /// <summary>This server holds relay credentials of its own (IdentityCenter never sends its password).</summary>
    bool CanSignIn { get; }

    Task<string?> SendAsync(IcMailRelay relay, IcMailItem item, CancellationToken ct);
}

/// <summary>
/// JOB-PLACEMENT-03: sends from THIS server's network. Relay credentials, when the relay needs a sign-in, are this
/// installation's own (<c>IcMail:Username</c> / <c>IcMail:Password</c>, kept in the same outside-the-repo config as
/// every other Conduit secret).
/// </summary>
public sealed class SmtpMailRelaySender : IMailRelaySender
{
    private readonly string? _username;
    private readonly string? _password;

    public SmtpMailRelaySender(IConfiguration config)
    {
        _username = config["IcMail:Username"];
        _password = config["IcMail:Password"];
    }

    public bool CanSignIn => !string.IsNullOrWhiteSpace(_username) && !string.IsNullOrEmpty(_password);

    public async Task<string?> SendAsync(IcMailRelay relay, IcMailItem item, CancellationToken ct)
    {
        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(relay.FromAddress, string.IsNullOrWhiteSpace(relay.FromName) ? null : relay.FromName),
                Subject = item.Subject,
                Body = item.Body,
                IsBodyHtml = item.IsHtml
            };
            message.To.Add(new MailAddress(item.To, string.IsNullOrWhiteSpace(item.ToName) ? null : item.ToName));
            if (!string.IsNullOrWhiteSpace(relay.ReplyToAddress)) message.ReplyToList.Add(new MailAddress(relay.ReplyToAddress));

            using var client = new SmtpClient(relay.Host, relay.Port) { EnableSsl = relay.UseSsl, DeliveryMethod = SmtpDeliveryMethod.Network };
            if (relay.SignInRequired)
            {
                if (!CanSignIn) return "The relay needs a sign-in and this server has no relay credentials (IcMail:Username / IcMail:Password).";
                client.Credentials = new NetworkCredential(_username, _password);
            }
            else
            {
                client.UseDefaultCredentials = false;
            }
            await client.SendMailAsync(message, ct);
            return null;
        }
        catch (Exception ex) when (ex is SmtpException or FormatException or InvalidOperationException or IOException or ArgumentException)
        {
            return ex.InnerException is null ? ex.Message : $"{ex.Message} ({ex.InnerException.Message})";
        }
    }
}

/// <summary>What one mail job did, for the job's completion.</summary>
public sealed record IcMailRunOutcome(bool Success, int Processed, int Sent, int Failed, string Message);

/// <summary>
/// JOB-PLACEMENT-03 (Jacob 2026-11-06: "Conduit has or should have the ability to send emails"). When IdentityCenter's
/// mail drain job is placed on this server, the job is claimed here and the queue is drained in batches:
/// claim (IdentityCenter applies every rule and claims atomically) → send each from this network → settle each.
/// A message is never reported sent unless the relay accepted it; a batch that cannot be settled stays in
/// IdentityCenter's Sending state for its own stale-claim recovery rather than being reported as anything.
/// </summary>
public sealed class IcMailJobRunner
{
    /// <summary>Messages per claim. Small enough that a batch of HTML bodies stays well under the channel's 1 MB response cap.</summary>
    public const int BatchSize = 25;

    /// <summary>Batches per job: one job never holds the queue unbounded; the next schedule tick takes the rest.</summary>
    public const int MaxBatchesPerJob = 40;

    private readonly IMailRelaySender _sender;
    private readonly ILogger<IcMailJobRunner> _logger;

    public IcMailJobRunner(IMailRelaySender sender, ILogger<IcMailJobRunner> logger)
    {
        _sender = sender;
        _logger = logger;
    }

    public async Task<IcMailRunOutcome> RunAsync(IcSyncJobChannel channel, string baseUrl, Guid agentId, Guid jobId, CancellationToken ct)
    {
        int sent = 0, failed = 0, expired = 0, refused = 0;
        var capReached = false;

        for (var round = 0; round < MaxBatchesPerJob && !ct.IsCancellationRequested; round++)
        {
            var batch = await channel.ClaimMailAsync(baseUrl, jobId, BatchSize, _sender.CanSignIn, ct);
            if (batch is null)
                return new IcMailRunOutcome(false, sent + failed, sent, failed,
                    Summary(sent, failed, expired, refused, capReached) + " IdentityCenter's mail claim could not be read; the rest waits for the next run.");
            if (!string.IsNullOrWhiteSpace(batch.Unavailable))
                return new IcMailRunOutcome(false, sent + failed, sent, failed,
                    $"{Summary(sent, failed, expired, refused, capReached)} {IcSyncJobPump.Clean(batch.Unavailable, 300)}");

            expired += batch.Expired;
            refused += batch.Refused;
            capReached |= batch.DailyCapReached;
            if (batch.Items.Count == 0 || batch.Relay is null) break;

            var results = new List<IcMailResult>(batch.Items.Count);
            foreach (var item in batch.Items)
            {
                // CancellationToken.None: a claimed message is sent and reported whole, or not at all.
                var error = await _sender.SendAsync(batch.Relay, item, CancellationToken.None);
                results.Add(new IcMailResult(item.Id, error is null, error is null ? null : IcSyncJobPump.Clean(error, 500)));
                if (error is null) sent++; else failed++;
            }

            if (!await channel.SettleMailAsync(baseUrl, jobId, results, CancellationToken.None))
            {
                _logger.LogError("IdentityCenter did not accept the outcome of {Count} messages for mail job {JobId}; they stay claimed there until its stale-claim recovery.", results.Count, jobId);
                return new IcMailRunOutcome(false, sent + failed, sent, failed,
                    Summary(sent, failed, expired, refused, capReached) + " The last batch's outcome was NOT accepted by IdentityCenter.");
            }

            await channel.ReportProgressAsync(baseUrl, jobId, agentId, 0, Summary(sent, failed, expired, refused, capReached),
                sent + failed, sent, failed, ct);
            if (batch.DailyCapReached || batch.Items.Count < BatchSize) break;
        }

        return new IcMailRunOutcome(true, sent + failed, sent, failed, Summary(sent, failed, expired, refused, capReached));
    }

    private static string Summary(int sent, int failed, int expired, int refused, bool capReached)
    {
        var text = $"Sent {sent}, failed {failed}";
        if (expired > 0) text += $", expired {expired}";
        if (refused > 0) text += $", refused by the recipient policy {refused}";
        if (capReached) text += "; the daily sending cap was reached";
        return text + ".";
    }
}
