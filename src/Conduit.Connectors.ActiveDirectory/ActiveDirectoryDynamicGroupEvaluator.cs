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
/// DYNAMIC-GROUPS-03: <see cref="RebuildAsync"/> is the one write: after the same evaluation it applies each add and
/// remove as a delta change on 'member' through <see cref="ActiveDirectorySink.ModifyMember"/>, on the connection it
/// evaluated on, and reports every change's outcome; a member that cannot be changed fails alone.
/// DYNAMIC-GROUPS-05: <see cref="ListAsync"/> and <see cref="AnyChangeSinceAsync"/> are what the agent's clock asks.
/// </summary>
public sealed class ActiveDirectoryDynamicGroupEvaluator : IConnectorDynamicGroupEvaluator
{
    /// <summary>Cap on the objects one query rule may match; above it the report is marked truncated rather than left to run.</summary>
    public const int MaxMatchesPerRule = 20_000;
    /// <summary>How many names a rule preview carries beside its exact count.</summary>
    public const int PreviewSample = 200;
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

    /// <summary>MEMBERSHIP-RULES-01 (UC034-D13): one rule on its own, the console's Preview Rule; read-only, nothing written.</summary>
    public async Task<DynamicGroupRulePreview> PreviewRuleAsync(DynamicGroupRuleSpec rule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (!Enum.IsDefined(typeof(DynamicGroupRuleType), rule.TypeCode))
            return DynamicGroupRulePreview.Fail(string.Concat("Rule type ", rule.TypeCode, " is not one of the seven membership rule types."));
        if (!Guid.TryParse(rule.ObjectGuid, out var objectGuid) || objectGuid == Guid.Empty)
            return DynamicGroupRulePreview.Fail("The rule's objectGuid is missing or not a GUID.");
        var parsed = new DynamicGroupRule((DynamicGroupRuleType)rule.TypeCode, objectGuid, rule.Filter);
        if (!DynamicGroupRules.IsWritable(parsed, out var reason))
            return DynamicGroupRulePreview.Fail(reason!);
        LdapConnection connection;
        try { connection = await _open().ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dynamic group rule preview: the connection could not be opened.");
            return DynamicGroupRulePreview.Fail("The directory connection could not be opened: " + ex.Message);
        }
        using (connection)
        {
            return await Task.Run(() => Preview(connection, parsed, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
    }

    private DynamicGroupRulePreview Preview(LdapConnection connection, DynamicGroupRule rule, CancellationToken ct)
    {
        try
        {
            var type = DynamicGroupRules.Describe(rule.Type);
            if (rule.IsRetain)
                return new DynamicGroupRulePreview { TypeCode = (int)rule.Type, Type = type, MatchCount = 0 };   // keeps members in place; matches nothing of its own
            var target = ReadByGuid(connection, rule.ObjectGuid, new[] { "distinguishedName", "objectClass" });
            if (target is null)
                return DynamicGroupRulePreview.Fail("The rule's object was not found in the directory.");
            IReadOnlyCollection<string> matched;
            var truncated = false;
            if (rule.IsExplicit)
                matched = new[] { target.DistinguishedName };
            else if (rule.IsGroupMembers)
            {
                if (!Classes(target).Contains("group", StringComparer.OrdinalIgnoreCase))
                    return DynamicGroupRulePreview.Fail("The rule's object is not a group.");
                matched = ReadMembers(connection, target.DistinguishedName, ct);
            }
            else
            {
                var (dns, capped) = Search(connection, target.DistinguishedName, rule.Filter ?? "(objectClass=*)", ct);
                matched = dns;
                truncated = capped;
            }
            return new DynamicGroupRulePreview
            {
                TypeCode = (int)rule.Type, Type = type, ObjectDistinguishedName = target.DistinguishedName,
                MatchCount = matched.Count, Truncated = truncated, Sample = matched.Take(PreviewSample).ToList(),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dynamic group rule preview threw.");
            return DynamicGroupRulePreview.Fail(ex.Message);
        }
    }

    /// <summary>
    /// DYNAMIC-GROUPS-05: every group under the domain's naming context whose <c>accountNameHistory</c> carries a
    /// definition, paged; each definition parsed for the service it names so the clock rebuilds its own and leaves the
    /// other service's alone. Throws when the connection cannot be opened or the search fails: the clock logs and retries.
    /// </summary>
    public async Task<IReadOnlyList<DynamicGroupListing>> ListAsync(CancellationToken cancellationToken)
    {
        using var connection = await _open().ConfigureAwait(false);
        return await Task.Run(() => List(connection, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>DYNAMIC-GROUPS-05: one page of one entry under the naming context with <c>whenChanged</c> at or past the instant; any entry answers yes.</summary>
    public async Task<bool> AnyChangeSinceAsync(DateTime sinceUtc, CancellationToken cancellationToken)
    {
        using var connection = await _open().ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var baseDn = ActiveDirectorySink.ReadDefaultNamingContext(connection) ?? throw new InvalidOperationException("The directory's naming context could not be read.");
            var request = new SearchRequest(baseDn, string.Concat("(whenChanged>=", ActiveDirectorySource.ToGeneralizedTime(sinceUtc), ")"), SearchScope.Subtree, new[] { "1.1" });
            request.Controls.Add(new PageResultRequestControl(1));
            var response = (SearchResponse)connection.SendRequest(request);
            return response.Entries.Count > 0;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<DynamicGroupListing> List(LdapConnection connection, CancellationToken ct)
    {
        var baseDn = ActiveDirectorySink.ReadDefaultNamingContext(connection) ?? throw new InvalidOperationException("The directory's naming context could not be read.");
        var listings = new List<DynamicGroupListing>();
        var page = new PageResultRequestControl(PageSize);
        var filter = string.Concat("(&(objectCategory=group)(", DynamicGroupRules.AttributeName, "=<DynamicGroupXML*))");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var request = new SearchRequest(baseDn, filter, SearchScope.Subtree, new[] { "distinguishedName", "name", "objectGUID", DynamicGroupRules.AttributeName });
            request.Controls.Add(page);
            var response = (SearchResponse)connection.SendRequest(request);
            foreach (SearchResultEntry entry in response.Entries)
            {
                var raw = entry.Attributes.Contains("objectGUID") ? entry.Attributes["objectGUID"].GetValues(typeof(byte[])) : Array.Empty<object>();
                var guid = raw.Length > 0 && raw[0] is byte[] bytes && bytes.Length == 16 ? new Guid(bytes).ToString() : string.Empty;
                var name = FirstString(entry, "name") ?? entry.DistinguishedName;
                var blob = FirstString(entry, DynamicGroupRules.AttributeName);
                if (!DynamicGroupRules.LooksLikeDefinition(blob)) continue;
                listings.Add(DynamicGroupRules.TryParse(blob, out var definition, out var error)
                    ? new DynamicGroupListing(guid, entry.DistinguishedName, name, definition!.OriginatingService, null)
                    : new DynamicGroupListing(guid, entry.DistinguishedName, name, null, error));
            }
            var cookie = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie;
            if (cookie is null || cookie.Length == 0) break;
            page.Cookie = cookie;
        }
        return listings;
    }

    public async Task<DynamicGroupRebuild> RebuildAsync(string groupObjectGuid, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(groupObjectGuid, out var groupGuid) || groupGuid == Guid.Empty)
            return DynamicGroupRebuild.Fail("The group's objectGuid is missing or not a GUID.");

        LdapConnection connection;
        try { connection = await _open().ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dynamic group rebuild: the connection could not be opened.");
            return DynamicGroupRebuild.Fail("The directory connection could not be opened: " + ex.Message);
        }

        using (connection)
        {
            return await Task.Run(() => Rebuild(connection, groupGuid, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
    }

    private DynamicGroupRebuild Rebuild(LdapConnection connection, Guid groupGuid, CancellationToken ct)
    {
        var evaluation = Evaluate(connection, groupGuid, ct);
        if (evaluation.ErrorMessage is not null)
            return DynamicGroupRebuild.Fail(evaluation.ErrorMessage);
        if (!evaluation.IsDynamic)
            return DynamicGroupRebuild.Fail(string.Concat("'", evaluation.GroupName, "' carries no membership rules; a static group is not rebuilt."));
        if (evaluation.Truncated)
            return DynamicGroupRebuild.Fail(string.Concat("A rule of '", evaluation.GroupName, "' matched more objects than the evaluator reads in one pass; the rebuild was not run on a partial set."));
        if (evaluation.Rules.Any(r => r.Error is not null && r.TypeCode != (int)DynamicGroupRuleType.RetainDeprovisioned))
            return DynamicGroupRebuild.Fail(string.Concat("A rule of '", evaluation.GroupName, "' could not be evaluated; the rebuild was not run on a partial set."));

        var groupDn = evaluation.GroupDistinguishedName!;
        var changes = new List<DynamicGroupChange>();
        int adds = 0, removes = 0, skipped = 0, failed = 0;
        foreach (var (dn, add) in evaluation.Adds.Select(d => (d, true)).Concat(evaluation.Removes.Select(d => (d, false))))
        {
            ct.ThrowIfCancellationRequested();
            var guid = ReadObjectGuid(connection, dn);
            var result = ActiveDirectorySink.ModifyMember(connection, groupDn, dn, add ? DirectoryAttributeOperation.Add : DirectoryAttributeOperation.Delete);
            var outcome = result.Outcome switch
            {
                SinkWriteOutcome.Updated => "Applied",
                SinkWriteOutcome.Skipped => "Skipped",
                _ => "Failed",
            };
            switch (outcome)
            {
                case "Applied": if (add) adds++; else removes++; break;
                case "Skipped": skipped++; break;
                default: failed++; break;
            }
            changes.Add(new DynamicGroupChange
            {
                Operation = add ? "Add" : "Remove", DistinguishedName = dn, ObjectGuid = guid, Outcome = outcome,
                Error = outcome == "Failed" ? result.ErrorMessage : null,
            });
        }

        return new DynamicGroupRebuild
        {
            Evaluation = evaluation, Changes = changes, AppliedAdds = adds, AppliedRemoves = removes, Skipped = skipped, Failed = failed,
        };
    }

    public async Task<DynamicGroupRebuild> SetRulesAsync(string groupObjectGuid, IReadOnlyList<DynamicGroupRuleSpec> rules, bool clearMembers, string? notes, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(groupObjectGuid, out var groupGuid) || groupGuid == Guid.Empty)
            return DynamicGroupRebuild.Fail("The group's objectGuid is missing or not a GUID.");
        var parsed = new List<DynamicGroupRule>();
        foreach (var spec in rules ?? Array.Empty<DynamicGroupRuleSpec>())
        {
            if (!Guid.TryParse(spec.ObjectGuid, out var objectGuid))
                return DynamicGroupRebuild.Fail("A rule names no objectGUID.");
            var rule = new DynamicGroupRule((DynamicGroupRuleType)spec.TypeCode, objectGuid, string.IsNullOrWhiteSpace(spec.Filter) ? null : spec.Filter.Trim());
            if (!DynamicGroupRules.IsWritable(rule, out var reason))
                return DynamicGroupRebuild.Fail(reason!);
            parsed.Add(rule);
        }

        LdapConnection connection;
        try { connection = await _open().ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dynamic group rules write: the connection could not be opened.");
            return DynamicGroupRebuild.Fail("The directory connection could not be opened: " + ex.Message);
        }
        using (connection)
        {
            return await Task.Run(() => WriteDefinition(connection, groupGuid, parsed, clearMembers, notes, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<DynamicGroupRebuild> ConvertToStaticAsync(string groupObjectGuid, string? notesToClear, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(groupObjectGuid, out var groupGuid) || groupGuid == Guid.Empty)
            return DynamicGroupRebuild.Fail("The group's objectGuid is missing or not a GUID.");
        LdapConnection connection;
        try { connection = await _open().ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dynamic group conversion: the connection could not be opened.");
            return DynamicGroupRebuild.Fail("The directory connection could not be opened: " + ex.Message);
        }
        using (connection)
        {
            return await Task.Run(() => RemoveDefinition(connection, groupGuid, notesToClear, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The conversion and the rule change, as the console does them (UC034-D03): on a conversion every member goes first,
    /// one delta change each so a member the directory will not release fails alone; then the definition replaces the
    /// attribute and the Notes carry the sentence. A rule change on a group already dynamic touches the definition only.
    /// </summary>
    private DynamicGroupRebuild WriteDefinition(LdapConnection connection, Guid groupGuid, IReadOnlyList<DynamicGroupRule> rules, bool clearMembers, string? notes, CancellationToken ct)
    {
        try
        {
            var group = ReadByGuid(connection, groupGuid, new[] { "distinguishedName", "name", "objectClass", DynamicGroupRules.AttributeName });
            if (group is null)
                return DynamicGroupRebuild.Fail(string.Concat("objectGuid ", groupGuid, " resolved to no directory object."));
            var groupDn = group.DistinguishedName;
            var name = FirstString(group, "name") ?? groupDn;
            if (!Classes(group).Contains("group", StringComparer.OrdinalIgnoreCase))
                return DynamicGroupRebuild.Fail(string.Concat("'", name, "' is not a group."));
            var wasDynamic = DynamicGroupRules.LooksLikeDefinition(FirstString(group, DynamicGroupRules.AttributeName));

            var changes = new List<DynamicGroupChange>();
            int removes = 0, skipped = 0, failed = 0;
            if (clearMembers)
            {
                foreach (var dn in ReadMembers(connection, groupDn, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    var guid = ReadObjectGuid(connection, dn);
                    var result = ActiveDirectorySink.ModifyMember(connection, groupDn, dn, DirectoryAttributeOperation.Delete);
                    var outcome = result.Outcome switch { SinkWriteOutcome.Updated => "Applied", SinkWriteOutcome.Skipped => "Skipped", _ => "Failed" };
                    switch (outcome) { case "Applied": removes++; break; case "Skipped": skipped++; break; default: failed++; break; }
                    changes.Add(new DynamicGroupChange { Operation = "Remove", DistinguishedName = dn, ObjectGuid = guid, Outcome = outcome, Error = outcome == "Failed" ? result.ErrorMessage : null });
                }
            }

            var definition = new DirectoryAttributeModification { Name = DynamicGroupRules.AttributeName, Operation = DirectoryAttributeOperation.Replace };
            definition.Add(DynamicGroupRules.Serialize(rules, DynamicGroupRules.IdentityCenterService));
            var modifications = new List<DirectoryAttributeModification> { definition };
            if (clearMembers && !string.IsNullOrWhiteSpace(notes))
            {
                var info = new DirectoryAttributeModification { Name = DynamicGroupRules.NotesAttributeName, Operation = DirectoryAttributeOperation.Replace };
                info.Add(notes.Trim());
                modifications.Add(info);
            }
            connection.SendRequest(new ModifyRequest(groupDn, modifications.ToArray()));

            return new DynamicGroupRebuild
            {
                Evaluation = new DynamicGroupEvaluation { GroupDistinguishedName = groupDn, GroupName = name, IsDynamic = true },
                Changes = changes, AppliedRemoves = removes, Skipped = skipped, Failed = failed,
                RulesWritten = rules.Count, Converted = clearMembers || !wasDynamic,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dynamic group rules write for {GroupGuid} threw.", groupGuid);
            return DynamicGroupRebuild.Fail(ex.Message);
        }
    }

    /// <summary>Back to a static group (UC034-D06): the definition goes, the members stay as last computed, the Notes the conversion stamped are cleared when they still read that sentence.</summary>
    private DynamicGroupRebuild RemoveDefinition(LdapConnection connection, Guid groupGuid, string? notesToClear, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var group = ReadByGuid(connection, groupGuid, new[] { "distinguishedName", "name", "objectClass", DynamicGroupRules.AttributeName, DynamicGroupRules.NotesAttributeName });
            if (group is null)
                return DynamicGroupRebuild.Fail(string.Concat("objectGuid ", groupGuid, " resolved to no directory object."));
            var groupDn = group.DistinguishedName;
            var name = FirstString(group, "name") ?? groupDn;
            if (!Classes(group).Contains("group", StringComparer.OrdinalIgnoreCase))
                return DynamicGroupRebuild.Fail(string.Concat("'", name, "' is not a group."));
            if (!DynamicGroupRules.LooksLikeDefinition(FirstString(group, DynamicGroupRules.AttributeName)))
                return DynamicGroupRebuild.Fail(string.Concat("'", name, "' carries no membership rules; it is a static group already."));

            var modifications = new List<DirectoryAttributeModification>
            {
                new() { Name = DynamicGroupRules.AttributeName, Operation = DirectoryAttributeOperation.Delete },
            };
            var info = FirstString(group, DynamicGroupRules.NotesAttributeName);
            if (!string.IsNullOrWhiteSpace(notesToClear) && string.Equals(info?.Trim(), notesToClear.Trim(), StringComparison.Ordinal))
                modifications.Add(new DirectoryAttributeModification { Name = DynamicGroupRules.NotesAttributeName, Operation = DirectoryAttributeOperation.Delete });
            connection.SendRequest(new ModifyRequest(groupDn, modifications.ToArray()));

            return new DynamicGroupRebuild
            {
                Evaluation = new DynamicGroupEvaluation { GroupDistinguishedName = groupDn, GroupName = name, IsDynamic = false },
                RulesWritten = 0, Converted = true,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dynamic group conversion of {GroupGuid} threw.", groupGuid);
            return DynamicGroupRebuild.Fail(ex.Message);
        }
    }

    /// <summary>The objectGUID of one object by DN, for the catalog's settlement; null when the read fails (the change still goes by DN).</summary>
    private static string? ReadObjectGuid(LdapConnection connection, string dn)
    {
        try
        {
            var response = (SearchResponse)connection.SendRequest(new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, new[] { "objectGUID" }));
            if (response.Entries.Count == 0 || !response.Entries[0].Attributes.Contains("objectGUID")) return null;
            var raw = response.Entries[0].Attributes["objectGUID"].GetValues(typeof(byte[]));
            return raw.Length > 0 && raw[0] is byte[] bytes && bytes.Length == 16 ? new Guid(bytes).ToString() : null;
        }
        catch (DirectoryOperationException) { return null; }
    }

    private DynamicGroupEvaluation Evaluate(LdapConnection connection, Guid groupGuid, CancellationToken ct)
    {
        try
        {
            var group = ReadByGuid(connection, groupGuid, new[] { "distinguishedName", "name", "objectClass", "groupType", DynamicGroupRules.AttributeName });
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
            IReadOnlyCollection<string>? retained = null;
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
                    // MEMBERSHIP-RULES-01 (UC034-F13): the console offers this rule for a group too. A current member
                    // whose account is disabled, which is what this product's deprovisioning does to an account, stays
                    // a member whatever the other rules say. The rule names no object and matches nothing of its own.
                    retained ??= ReadDisabledMembers(connection, groupDn, ct);
                    report.MatchCount = retained.Count;
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
            // MEMBERSHIP-RULES-01 (UC034-F12): the directory refuses a universal or domain-local group as a member of a
            // global group, and a domain-local one in a universal group; the console's preview leaves them out and so
            // does this decision, rather than a rebuild failing them one by one (63 of them on the lab's dynamo_#2).
            var ineligibleCount = 0;
            var groupType = int.TryParse(FirstString(group, "groupType"), out var parsedType) ? parsedType : 0;
            if (desired.Count > 0 && DynamicGroupRules.IneligibleMembersFilter(groupType) is { } ineligibleFilter)
            {
                var (ineligible, _) = Search(connection, ActiveDirectorySink.ReadDefaultNamingContext(connection) ?? groupDn, ineligibleFilter, ct);
                if (ineligible.Count > 0)
                {
                    var refused = new HashSet<string>(ineligible, StringComparer.OrdinalIgnoreCase);
                    var kept = desired.Where(dn => !refused.Contains(dn)).ToList();
                    ineligibleCount = desired.Count - kept.Count;
                    desired = kept;
                }
            }
            var delta = DynamicGroupRules.Diff(desired, current, retained);
            return new DynamicGroupEvaluation
            {
                GroupDistinguishedName = groupDn, GroupName = name, IsDynamic = true,
                OriginatingService = definition.OriginatingService?.ToString(),
                Rules = reports, CurrentMembers = current, DesiredMembers = delta.Desired, Adds = delta.Adds, Removes = delta.Removes,
                Truncated = truncated, Ineligible = ineligibleCount,
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

    /// <summary>The group's direct members whose account is disabled: what Retain Deprovisioned keeps in place (one search under the domain, paged).</summary>
    private static IReadOnlyCollection<string> ReadDisabledMembers(LdapConnection connection, string groupDn, CancellationToken ct)
    {
        var baseDn = ActiveDirectorySink.ReadDefaultNamingContext(connection) ?? groupDn;
        var filter = string.Concat("(&(memberOf=", ActiveDirectorySink.EscapeLdapFilter(groupDn), ")(userAccountControl:1.2.840.113556.1.4.803:=2))");
        return Search(connection, baseDn, filter, ct).Dns;
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
