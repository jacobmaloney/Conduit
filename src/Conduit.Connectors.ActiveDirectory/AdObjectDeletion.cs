using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Conduit.Connectors.ActiveDirectory;

/// <summary>One directory object as the delete guard reads it: its GUID, DN, class chain and the three marks that protect it.</summary>
public sealed record AdDeleteNode(
    Guid ObjectGuid,
    string DistinguishedName,
    IReadOnlyList<string> ObjectClasses,
    bool IsCriticalSystemObject,
    bool AdminCount,
    int UserAccountControl)
{
    /// <summary>userAccountControl SERVER_TRUST_ACCOUNT: the computer account of a domain controller.</summary>
    public const int ServerTrustAccount = 0x2000;

    /// <summary>The most specific class (AD lists objectClass from top down).</summary>
    public string PrimaryClass => ObjectClasses.Count == 0 ? "object" : ObjectClasses[^1];

    public bool IsDomainControllerAccount => (UserAccountControl & ServerTrustAccount) != 0;

    /// <summary>How deep the DN sits: its unescaped commas. A child is always deeper than its parent.</summary>
    public int Depth => AdObjectDeletePlan.DepthOf(DistinguishedName);
}

/// <summary>
/// What the agent read before a delete (OBJECT-DELETE-01, Jacob 2026-11-15 "Yes, any object"): the target, the
/// directory's naming contexts off the RootDSE, how many objects sit directly under the target, and (only when the
/// request asked for the subtree) every object under it, listed once.
/// </summary>
public sealed record AdDeleteFacts(
    AdDeleteNode Target,
    IReadOnlyList<string> NamingContexts,
    string? ConfigurationNamingContext,
    string? SchemaNamingContext,
    int ChildCount,
    IReadOnlyList<AdDeleteNode>? Subtree);

/// <summary>What the delete will do once every guard passed: the target, and every object to delete deepest first, the target last.</summary>
public sealed record AdDeletePlan(AdDeleteNode Target, IReadOnlyList<AdDeleteNode> Order)
{
    public int DescendantCount => Order.Count - 1;
    public bool Subtree => Order.Count > 1;
}

/// <summary>What a run deleted, in order, and why it stopped early (null when it finished).</summary>
public sealed record AdDeleteRun(IReadOnlyList<AdDeleteNode> Deleted, string? StoppedReason)
{
    public bool Complete => StoppedReason == null;
}

/// <summary>The two directory calls a run makes, one object at a time; <see cref="LdapDeleteDirectory"/> on a live bind.</summary>
public interface IAdDeleteDirectory
{
    /// <summary>A fresh read of the object by GUID, or null when it is gone.</summary>
    AdDeleteNode? Read(Guid objectGuid);

    /// <summary>One DeleteRequest addressed by GUID. Never a tree delete: an object with children fails with notAllowedOnNonLeaf.</summary>
    AdDeleteOutcome Delete(Guid objectGuid);
}

/// <summary>How one DeleteRequest ended; <see cref="Message"/> carries the directory's words for a refusal.</summary>
public sealed record AdDeleteOutcome(AdDeleteOutcomeKind Kind, string? Message = null)
{
    public static readonly AdDeleteOutcome Deleted = new(AdDeleteOutcomeKind.Deleted);
    public static readonly AdDeleteOutcome NotLeaf = new(AdDeleteOutcomeKind.NotLeaf);
    public static readonly AdDeleteOutcome Gone = new(AdDeleteOutcomeKind.Gone);
}

public enum AdDeleteOutcomeKind { Deleted, NotLeaf, Gone, Refused }

/// <summary>
/// OBJECT-DELETE-01: the decision of an Active Directory delete, made from what the agent read and nothing else. Every
/// refusal is decided here, before any write, in plain words. No Tree Delete control is ever sent: a subtree is
/// listed once, every object in it is judged, and the objects are then deleted one by one, deepest first, each re-read
/// and re-judged just before its own DeleteRequest (<see cref="Run"/>). The AD Recycle Bin is the only undo.
/// </summary>
public static class AdObjectDeletePlan
{
    public const string DomainControllersRdn = "OU=Domain Controllers";

    /// <summary>The most objects one delete takes (and the result reports, under IdentityCenter's 64 KB result cap).</summary>
    public const int MaxObjectsPerRun = 150;

    /// <summary>The largest result a delete may plan to report: 48 KB, under IdentityCenter's 64 KB result cap with headroom.</summary>
    public const int MaxResultBytes = 48 * 1024;

    /// <param name="confirmedCount">The number of objects under the target the person saw in the confirm; a subtree larger than it is refused.</param>
    /// <param name="isPermitted">True when a DN lies strictly below a permitted base DN of the connection.</param>
    public static (AdDeletePlan? Plan, string? Refusal) Decide(AdDeleteFacts facts, bool includeSubtree, int? confirmedCount, Func<string, bool> isPermitted)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(isPermitted);
        var target = facts.Target;
        var dn = target.DistinguishedName.Trim();

        if (facts.NamingContexts.Any(nc => SameDn(nc, dn)))
            return (null, "it is the head of a naming context (the domain root or a directory partition); deleting it would remove the whole partition.");
        if (IsWithin(dn, facts.SchemaNamingContext))
            return (null, "it is in the schema partition, which defines every class and attribute in the forest.");
        if (IsWithin(dn, facts.ConfigurationNamingContext))
            return (null, "it is in the configuration partition, which holds the forest's own settings.");
        if (!isPermitted(dn))
            return (null, "it is not below a permitted base DN of this connection (a permitted base itself is never deleted).");
        if (Protection(target, facts.NamingContexts) is { } why)
            return (null, string.Concat("it ", why, "."));

        if (facts.ChildCount == 0)
            return (new AdDeletePlan(target, new[] { target }), null);
        if (!includeSubtree)
            return (null, string.Concat("it has ", facts.ChildCount.ToString(), facts.ChildCount == 1 ? " child" : " children",
                "; delete it with everything under it, or empty it first."));

        if (facts.Subtree is null)
            return (null, "the objects under it were not listed.");
        if (confirmedCount is null or < 0)
            return (null, "the confirm did not say how many objects are under it.");
        var listed = facts.Subtree.Count;
        if (listed > MaxObjectsPerRun)
            return (null, string.Concat("more than ", MaxObjectsPerRun.ToString(), " objects are under it; delete the containers inside it first."));
        if (listed > confirmedCount.Value)
            return (null, string.Concat("the directory holds ", listed.ToString(), listed == 1 ? " object" : " objects", " under it, more than the ",
                confirmedCount.Value.ToString(), " the confirm showed; nothing was deleted."));
        foreach (var node in facts.Subtree)
        {
            if (Protection(node, facts.NamingContexts) is { } offence)
                return (null, string.Concat("the objects under it include ", node.DistinguishedName, ", which ", offence, "; nothing was deleted."));
            if (!isPermitted(node.DistinguishedName) || !IsWithin(node.DistinguishedName, dn))
                return (null, string.Concat("the objects under it include ", node.DistinguishedName, ", which is not below a permitted base DN of this connection; nothing was deleted."));
        }

        var order = facts.Subtree.OrderByDescending(n => n.Depth).ThenBy(n => n.DistinguishedName, StringComparer.OrdinalIgnoreCase).ToList();
        order.Add(target);
        // The result must reach IdentityCenter (it refuses one over 64 KB) after the deletes happened, so its size is
        // measured now, with the serializer the result uses (it escapes non-ASCII, six bytes a character) and a stop
        // reason carrying the longest DN, against a budget that leaves headroom.
        var longest = order.OrderByDescending(n => n.DistinguishedName.Length).First().DistinguishedName;
        var worst = ResultJson(target, new AdDeleteRun(order, string.Concat(new string('x', 200), longest)));
        if (System.Text.Encoding.UTF8.GetByteCount(worst) > MaxResultBytes)
            return (null, "too many objects with long names to report in one delete; delete part of the subtree first.");
        return (new AdDeletePlan(target, order), null);
    }

    /// <summary>
    /// Deletes the plan one object at a time, deepest first. Each object is read again right before its delete; a run
    /// stops (and reports what it deleted) when an object moved out of the permitted base or out from under the
    /// target, turned protected, or gained objects under it (the directory refuses a non-leaf delete). An object
    /// already gone is passed over.
    /// </summary>
    public static AdDeleteRun Run(IAdDeleteDirectory directory, AdDeletePlan plan, IReadOnlyList<string> namingContexts,
        Func<string, bool> isPermitted, CancellationToken ct = default)
    {
        var deleted = new List<AdDeleteNode>();
        var targetDn = plan.Target.DistinguishedName.Trim();
        foreach (var listed in plan.Order)
        {
            if (ct.IsCancellationRequested) return new AdDeleteRun(deleted, "the run was cancelled.");
            try
            {
                if (Step(directory, plan, listed, namingContexts, isPermitted, targetDn, deleted) is { } stop)
                    return new AdDeleteRun(deleted, stop);
            }
            catch (Exception ex)
            {
                // A dropped connection (LdapException) or anything else: stop, and still report what was deleted, so the
                // catalog follows the objects that are already gone.
                return new AdDeleteRun(deleted, string.Concat("the directory could not be reached while deleting ", listed.DistinguishedName, ": ", ex.Message));
            }
        }
        return new AdDeleteRun(deleted, null);
    }

    /// <summary>One object of a run: null to go on, else why the run stops.</summary>
    private static string? Step(IAdDeleteDirectory directory, AdDeletePlan plan, AdDeleteNode listed, IReadOnlyList<string> namingContexts,
        Func<string, bool> isPermitted, string targetDn, List<AdDeleteNode> deleted)
    {
        {
            var isTarget = listed.ObjectGuid == plan.Target.ObjectGuid;
            var fresh = directory.Read(listed.ObjectGuid);
            if (fresh is null)
                return isTarget ? string.Concat(listed.DistinguishedName, " is no longer in the directory.") : null;
            if (!isPermitted(fresh.DistinguishedName) || (!isTarget && !IsWithin(fresh.DistinguishedName, targetDn)))
                return string.Concat(fresh.DistinguishedName, " moved since it was listed; the run stopped there.");
            if (Protection(fresh, namingContexts) is { } why)
                return string.Concat(fresh.DistinguishedName, " ", why, " now; the run stopped there.");
            var outcome = directory.Delete(fresh.ObjectGuid);
            switch (outcome.Kind)
            {
                case AdDeleteOutcomeKind.NotLeaf:
                    return string.Concat(fresh.DistinguishedName, " has objects under it that were not listed; the run stopped there.");
                case AdDeleteOutcomeKind.Gone:
                    return null;
                case AdDeleteOutcomeKind.Refused:
                    return string.Concat("the directory refused deleting ", fresh.DistinguishedName, ": ", outcome.Message);
            }
            deleted.Add(fresh);
            return null;
        }
    }

    /// <summary>Why the directory must keep this object, or null: critical system object, AdminSDHolder, a domain controller.</summary>
    public static string? Protection(AdDeleteNode node, IReadOnlyList<string> namingContexts)
    {
        if (node.IsCriticalSystemObject)
            return "is a critical system object (isCriticalSystemObject is TRUE)";
        if (node.AdminCount)
            return "is protected by AdminSDHolder (adminCount is 1)";
        if (node.IsDomainControllerAccount)
            return "is a domain controller's computer account";
        if (namingContexts.Any(nc => IsWithinOrSame(node.DistinguishedName, string.Concat(DomainControllersRdn, ",", nc))))
            return "is in the Domain Controllers OU";
        return null;
    }

    /// <summary>The result the agent returns; IdentityCenter settles the catalog from the deleted list, by objectGUID.</summary>
    public static string ResultJson(AdDeleteNode target, AdDeleteRun run) => JsonSerializer.Serialize(new
    {
        objectGuid = target.ObjectGuid.ToString(),
        distinguishedName = target.DistinguishedName,
        objectClass = target.PrimaryClass,
        complete = run.Complete,
        stoppedReason = run.StoppedReason,
        deletedCount = run.Deleted.Count,
        deleted = run.Deleted.Select(d => new { objectGuid = d.ObjectGuid.ToString(), distinguishedName = d.DistinguishedName, objectClass = d.PrimaryClass })
    });

    /// <summary>The number of unescaped commas in a DN.</summary>
    public static int DepthOf(string dn)
    {
        var depth = 0;
        for (var i = 0; i < dn.Length; i++)
        {
            if (dn[i] == '\\') { i++; continue; }
            if (dn[i] == ',') depth++;
        }
        return depth;
    }

    private static bool SameDn(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="dn"/> ends with "," + <paramref name="container"/> and that comma is not escaped.</summary>
    internal static bool IsWithin(string dn, string? container)
    {
        if (string.IsNullOrWhiteSpace(container)) return false;
        var suffix = string.Concat(",", container.Trim());
        dn = dn.Trim();
        if (dn.Length <= suffix.Length || !dn.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        var comma = dn.Length - suffix.Length;
        var slashes = 0;
        for (var i = comma - 1; i >= 0 && dn[i] == '\\'; i--) slashes++;
        return slashes % 2 == 0;
    }

    private static bool IsWithinOrSame(string dn, string container) => SameDn(dn, container) || IsWithin(dn, container);
}

/// <summary>
/// OBJECT-DELETE-01: the LDAP half of the delete, on a connection the sink opened (<see cref="ActiveDirectorySink.OpenConnectionAsync"/>).
/// <see cref="ReadFacts"/> reads only; writes go through <see cref="LdapDeleteDirectory"/>, one object per DeleteRequest.
/// </summary>
public static class AdObjectDeletion
{
    private const int PageSize = 500;
    internal static readonly string[] NodeAttributes = { "objectGUID", "distinguishedName", "objectClass", "isCriticalSystemObject", "adminCount", "userAccountControl" };

    /// <summary>Null when the GUID resolves to nothing. Throws when the directory cannot be read, so the caller refuses.</summary>
    public static AdDeleteFacts? ReadFacts(LdapConnection conn, Guid objectGuid, bool includeSubtree, CancellationToken ct = default)
    {
        var target = ReadNode(conn, objectGuid);
        if (target is null) return null;

        var root = (SearchResponse)conn.SendRequest(new SearchRequest(string.Empty, "(objectClass=*)", SearchScope.Base,
            "namingContexts", "defaultNamingContext", "configurationNamingContext", "schemaNamingContext", "rootDomainNamingContext"));
        var contexts = new List<string>();
        string? configNc = null, schemaNc = null;
        if (root.Entries.Count > 0)
        {
            var attrs = root.Entries[0].Attributes;
            contexts.AddRange(Strings(attrs, "namingContexts"));
            configNc = Strings(attrs, "configurationNamingContext").FirstOrDefault();
            schemaNc = Strings(attrs, "schemaNamingContext").FirstOrDefault();
            foreach (var nc in new[] { Strings(attrs, "defaultNamingContext").FirstOrDefault(), configNc, schemaNc, Strings(attrs, "rootDomainNamingContext").FirstOrDefault() })
                if (!string.IsNullOrWhiteSpace(nc) && !contexts.Contains(nc, StringComparer.OrdinalIgnoreCase)) contexts.Add(nc);
        }

        var children = Search(conn, target.DistinguishedName, SearchScope.OneLevel, new[] { "1.1" }, 1, ct).Count;
        IReadOnlyList<AdDeleteNode>? subtree = null;
        if (includeSubtree && children > 0)
        {
            // Listed once, capped one past the run limit so the plan can refuse an oversized subtree without reading it all.
            subtree = Search(conn, target.DistinguishedName, SearchScope.Subtree, NodeAttributes, AdObjectDeletePlan.MaxObjectsPerRun + 2, ct)
                .Where(e => !string.Equals(e.DistinguishedName, target.DistinguishedName, StringComparison.OrdinalIgnoreCase))
                .Select(ToNode).ToList();
        }
        return new AdDeleteFacts(target, contexts, configNc, schemaNc, children, subtree);
    }

    internal static AdDeleteNode? ReadNode(LdapConnection conn, Guid objectGuid)
    {
        SearchResponse resp;
        try { resp = (SearchResponse)conn.SendRequest(new SearchRequest($"<GUID={objectGuid}>", "(objectClass=*)", SearchScope.Base, NodeAttributes)); }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject) { return null; }
        return resp.Entries.Count == 0 ? null : ToNode(resp.Entries[0]);
    }

    /// <summary>The request one delete sends: by GUID, never carrying the Tree Delete control.</summary>
    public static DeleteRequest BuildRequest(Guid objectGuid) => new($"<GUID={objectGuid}>");

    private static List<SearchResultEntry> Search(LdapConnection conn, string baseDn, SearchScope scope, string[] attributes, int limit, CancellationToken ct)
    {
        var found = new List<SearchResultEntry>();
        var page = new PageResultRequestControl(PageSize);
        while (found.Count < limit)
        {
            ct.ThrowIfCancellationRequested();
            var request = new SearchRequest(baseDn, "(objectClass=*)", scope, attributes);
            request.Controls.Add(page);
            var response = (SearchResponse)conn.SendRequest(request);
            foreach (SearchResultEntry entry in response.Entries) found.Add(entry);
            var cookie = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie;
            if (cookie is null || cookie.Length == 0) break;
            page.Cookie = cookie;
        }
        return found;
    }

    private static AdDeleteNode ToNode(SearchResultEntry e)
    {
        var raw = e.Attributes.Contains("objectGUID") ? e.Attributes["objectGUID"].GetValues(typeof(byte[])) : Array.Empty<object>();
        var guid = raw.Length > 0 && raw[0] is byte[] bytes && bytes.Length == 16 ? new Guid(bytes) : Guid.Empty;
        var classes = Strings(e.Attributes, "objectClass").ToList();
        var critical = string.Equals(Strings(e.Attributes, "isCriticalSystemObject").FirstOrDefault()?.Trim(), "TRUE", StringComparison.OrdinalIgnoreCase);
        var adminCount = string.Equals(Strings(e.Attributes, "adminCount").FirstOrDefault()?.Trim(), "1", StringComparison.Ordinal);
        _ = int.TryParse(Strings(e.Attributes, "userAccountControl").FirstOrDefault(), out var uac);
        return new AdDeleteNode(guid, e.DistinguishedName, classes, critical, adminCount, uac);
    }

    private static IEnumerable<string> Strings(SearchResultAttributeCollection attrs, string name)
    {
        if (!attrs.Contains(name)) yield break;
        foreach (var v in attrs[name].GetValues(typeof(string)))
            if (v is string s && !string.IsNullOrWhiteSpace(s)) yield return s;
    }
}

/// <summary>The live directory behind <see cref="AdObjectDeletePlan.Run"/>: a base read and one DeleteRequest, both by GUID.</summary>
public sealed class LdapDeleteDirectory : IAdDeleteDirectory
{
    private readonly LdapConnection _conn;

    public LdapDeleteDirectory(LdapConnection conn) => _conn = conn ?? throw new ArgumentNullException(nameof(conn));

    public AdDeleteNode? Read(Guid objectGuid) => AdObjectDeletion.ReadNode(_conn, objectGuid);

    public AdDeleteOutcome Delete(Guid objectGuid)
    {
        try
        {
            _conn.SendRequest(AdObjectDeletion.BuildRequest(objectGuid));
            return AdDeleteOutcome.Deleted;
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NotAllowedOnNonLeaf) { return AdDeleteOutcome.NotLeaf; }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject) { return AdDeleteOutcome.Gone; }
        catch (DirectoryOperationException ex) { return new AdDeleteOutcome(AdDeleteOutcomeKind.Refused, ex.Message); }
    }
}
