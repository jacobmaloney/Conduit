using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Sync.Connectors;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.ActiveDirectory;

/// <summary>
/// DYNAMIC-GROUPS-01 (UC034-D12, first slice): the agent evaluates a dynamic group's rules as the Active Roles service
/// does, by LDAP search on the connection it already holds, and reports what a rebuild would write. READ-ONLY: it never
/// touches <c>member</c>. The group is addressed by objectGUID and re-read live; the definition is the group's own
/// <c>accountNameHistory</c> (<see cref="DynamicGroupRules"/>); a query rule is a subtree search under the rule's base
/// container with the rule's filter verbatim; an explicit rule is the object; a group-members rule is that group's
/// direct <c>member</c> list. The decision and the difference are <see cref="DynamicGroupRules.Decide"/> and
/// <see cref="DynamicGroupRules.Diff"/>. Cross-domain membership stays off (UC034-D05): searches do not chase referrals.
/// </summary>
public sealed class ActiveDirectoryDynamicGroupEvaluator : IConnectorDynamicGroupEvaluator
{
    /// <summary>Cap on the objects one query rule may match; above it the report is marked truncated rather than left to run.</summary>
    public const int MaxMatchesPerRule = 20_000;
    private const int PageSize = 1000;

    private readonly Func<Task<LdapConnection>> _open;
    private readonly ILogger _logger;

    /// <param name="open">A bound connection for the tenant (the sink's <c>OpenConnectionAsync</c>): one bind path for every AD reader and writer.</param>
    public ActiveDirectoryDynamicGroupEvaluator(Func<Task<LdapConnection>> open, ILogger logger)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DynamicGroupEvaluation> EvaluateAsync(string groupObjectGuid, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(groupObjectGuid, out var groupGuid) || groupGuid == Guid.Empty)
            return DynamicGroupEvaluation.Fail("The group's objectGuid is missing or not a GUID.");

        LdapConnection connection;
        try { connection = await _open().ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dynamic group evaluation: the connection could not be opened.");
            return DynamicGroupEvaluation.Fail("The directory connection could not be opened: " + ex.Message);
        }

        using (connection)
        {
            // The LDAP API is synchronous; keep the caller's thread free as the browser does.
            return await Task.Run(() => Evaluate(connection, groupGuid, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
    }

    private DynamicGroupEvaluation Evaluate(LdapConnection connection, Guid groupGuid, CancellationToken ct)
    {
        try
        {
            var group = ReadByGuid(connection, groupGuid, new[] { "distinguishedName", "name", "objectClass", DynamicGroupRules.AttributeName });
            if (group is null)
                return DynamicGroupEvaluation.Fail(string.Concat("objectGuid ", groupGuid, " resolved to no directory object."));
            var groupDn = group.DistinguishedName;
            var name = FirstString(group, "name") ?? groupDn;
            if (!Classes(group).Contains("group", StringComparer.OrdinalIgnoreCase))
                return DynamicGroupEvaluation.Fail(string.Concat("'", name, "' is not a group."));

            var blob = FirstString(group, DynamicGroupRules.AttributeName);
            var current = ReadMembers(connection, groupDn, ct);
            if (!DynamicGroupRules.LooksLikeDefinition(blob))
                return new DynamicGroupEvaluation
                {
                    GroupDistinguishedName = groupDn, GroupName = name, IsDynamic = false,
                    CurrentMembers = current, DesiredMembers = current, Adds = Array.Empty<string>(), Removes = Array.Empty<string>(),
                };

            if (!DynamicGroupRules.TryParse(blob, out var definition, out var parseError))
                return new DynamicGroupEvaluation
                {
                    GroupDistinguishedName = groupDn, GroupName = name, IsDynamic = true, CurrentMembers = current,
                    ErrorMessage = "The group's membership rules do not parse: " + parseError,
                };

            var reports = new List<DynamicGroupRuleReport>();
            var evaluations = new List<(DynamicGroupRule Rule, IReadOnlyCollection<string> Matched)>();
            var truncated = false;
            foreach (var rule in definition!.Rules)
            {
                ct.ThrowIfCancellationRequested();
                var report = new DynamicGroupRuleReport
                {
                    TypeCode = (int)rule.Type, Type = DynamicGroupRules.Describe(rule.Type), ObjectGuid = rule.ObjectGuid.ToString(), Filter = rule.Filter,
                };
                reports.Add(report);
                if (rule.Type == DynamicGroupRuleType.RetainDeprovisioned)
                {
                    report.Error = "Retain Deprovisioned applies to a managed unit, not a group; ignored.";
                    continue;
                }

                var target = ReadByGuid(connection, rule.ObjectGuid, new[] { "distinguishedName", "objectClass" });
                if (target is null)
                {
                    report.Error = "The rule's object was not found in the directory.";
                    continue;
                }
                report.ObjectDistinguishedName = target.DistinguishedName;

                IReadOnlyCollection<string> matched;
                try
                {
                    if (rule.IsExplicit)
                        matched = new[] { target.DistinguishedName };
                    else if (rule.IsGroupMembers)
                        matched = Classes(target).Contains("group", StringComparer.OrdinalIgnoreCase)
                            ? ReadMembers(connection, target.DistinguishedName, ct)
                            : throw new InvalidOperationException("The rule's object is not a group.");
                    else
                    {
                        var (dns, capped) = Search(connection, target.DistinguishedName, rule.Filter ?? "(objectClass=*)", ct);
                        matched = dns;
                        if (capped) { truncated = true; report.Truncated = true; }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    report.Error = ex.Message;
                    continue;
                }
                report.MatchCount = matched.Count;
                evaluations.Add((rule, matched));
            }

            var desired = DynamicGroupRules.Decide(evaluations, groupDn);
            var delta = DynamicGroupRules.Diff(desired, current);
            return new DynamicGroupEvaluation
            {
                GroupDistinguishedName = groupDn, GroupName = name, IsDynamic = true,
                OriginatingService = definition.OriginatingService?.ToString(),
                Rules = reports, CurrentMembers = current, DesiredMembers = delta.Desired, Adds = delta.Adds, Removes = delta.Removes,
                Truncated = truncated,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dynamic group evaluation of {GroupGuid} threw.", groupGuid);
            return DynamicGroupEvaluation.Fail(ex.Message);
        }
    }

    /// <summary>One object by GUID, through AD's GUID-DN with Base scope (the sink's own shape: no client text in a filter).</summary>
    private static SearchResultEntry? ReadByGuid(LdapConnection connection, Guid guid, string[] attributes)
    {
        var request = new SearchRequest(string.Concat("<GUID=", guid, ">"), "(objectClass=*)", SearchScope.Base, attributes);
        SearchResponse response;
        try { response = (SearchResponse)connection.SendRequest(request); }
        catch (DirectoryOperationException) { return null; }
        return response.Entries.Count == 0 ? null : response.Entries[0];
    }

    /// <summary>A query rule: every object under the base, the rule's filter verbatim, paged, capped at <see cref="MaxMatchesPerRule"/>.</summary>
    private static (IReadOnlyCollection<string> Dns, bool Truncated) Search(LdapConnection connection, string baseDn, string filter, CancellationToken ct)
    {
        var dns = new List<string>();
        var page = new PageResultRequestControl(PageSize);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var request = new SearchRequest(baseDn, filter, SearchScope.Subtree, new[] { "distinguishedName" });
            request.Controls.Add(page);
            var response = (SearchResponse)connection.SendRequest(request);
            foreach (SearchResultEntry entry in response.Entries)
            {
                if (!string.IsNullOrEmpty(entry.DistinguishedName)) dns.Add(entry.DistinguishedName);
                if (dns.Count >= MaxMatchesPerRule) return (dns, true);
            }
            var cookie = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie;
            if (cookie is null || cookie.Length == 0) break;
            page.Cookie = cookie;
        }
        return (dns, false);
    }

    /// <summary>The group's direct members, through ranged retrieval so a group past the server's 1500-value page reads whole.</summary>
    public static IReadOnlyList<string> ReadMembers(LdapConnection connection, string groupDn, CancellationToken ct)
    {
        var members = new List<string>();
        var start = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var request = new SearchRequest(groupDn, "(objectClass=*)", SearchScope.Base, new[] { string.Concat("member;range=", start, "-*") });
            var response = (SearchResponse)connection.SendRequest(request);
            if (response.Entries.Count == 0) break;
            var entry = response.Entries[0];
            string? rangedName = null;
            foreach (string attributeName in entry.Attributes.AttributeNames)
                if (attributeName.StartsWith("member", StringComparison.OrdinalIgnoreCase)) { rangedName = attributeName; break; }
            if (rangedName is null) break;   // no members at all

            foreach (var value in entry.Attributes[rangedName].GetValues(typeof(string)))
                if (value is string dn && dn.Length > 0) members.Add(dn);

            // "member;range=0-1499" continues at 1500; "member;range=0-*" (or a plain "member") is the end.
            var range = rangedName.IndexOf("range=", StringComparison.OrdinalIgnoreCase);
            if (range < 0) break;
            var span = rangedName[(range + 6)..];
            var dash = span.IndexOf('-');
            if (dash < 0 || span.EndsWith("*", StringComparison.Ordinal)) break;
            if (!int.TryParse(span[(dash + 1)..], out var end)) break;
            start = end + 1;
        }
        return members;
    }

    private static string? FirstString(SearchResultEntry entry, string attribute) =>
        entry.Attributes.Contains(attribute) && entry.Attributes[attribute].Count > 0 ? entry.Attributes[attribute][0]?.ToString() : null;

    private static List<string> Classes(SearchResultEntry entry)
    {
        var classes = new List<string>();
        if (entry.Attributes.Contains("objectClass"))
            foreach (var value in entry.Attributes["objectClass"].GetValues(typeof(string)))
                if (value is string s) classes.Add(s);
        return classes;
    }
}
