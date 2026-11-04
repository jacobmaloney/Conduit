using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Conduit.Connectors.ActiveDirectory;

/// <summary>
/// The seven rule types of a dynamic group or a managed unit, numbered as Active Roles numbers them
/// (the SDK's membership-condition enum; the blob spells the number in hex: <c>[0x3;guid;]</c>).
/// </summary>
public enum DynamicGroupRuleType
{
    IncludeByQuery = 1,
    ExcludeByQuery = 2,
    IncludeExplicitly = 3,
    ExcludeExplicitly = 4,
    IncludeGroupMembers = 5,
    ExcludeGroupMembers = 6,
    /// <summary>
    /// Offered for a managed unit and for a group alike (the console's rule type list, UC034-F13): a member whose account
    /// has been deprovisioned is not removed by a rebuild, whatever the other rules say. Names no object, matches nothing
    /// of its own; <see cref="DynamicGroupRules.Diff(IReadOnlyList{string}, IReadOnlyCollection{string}, IReadOnlyCollection{string}?)"/> applies it.
    /// </summary>
    RetainDeprovisioned = 7,
}

/// <summary>One rule: its type, the object it names (a container for a query, the object itself, or a group) and, for a query, the LDAP filter.</summary>
public sealed record DynamicGroupRule(DynamicGroupRuleType Type, Guid ObjectGuid, string? Filter)
{
    public bool IsInclude => Type is DynamicGroupRuleType.IncludeByQuery or DynamicGroupRuleType.IncludeExplicitly or DynamicGroupRuleType.IncludeGroupMembers;
    public bool IsQuery => Type is DynamicGroupRuleType.IncludeByQuery or DynamicGroupRuleType.ExcludeByQuery;
    public bool IsExplicit => Type is DynamicGroupRuleType.IncludeExplicitly or DynamicGroupRuleType.ExcludeExplicitly;
    public bool IsGroupMembers => Type is DynamicGroupRuleType.IncludeGroupMembers or DynamicGroupRuleType.ExcludeGroupMembers;
    public bool IsRetain => Type is DynamicGroupRuleType.RetainDeprovisioned;
}

/// <summary>A group's rule list as the directory holds it, plus the Administration Service that owned it.</summary>
public sealed record DynamicGroupDefinition(IReadOnlyList<DynamicGroupRule> Rules, Guid? OriginatingService);

/// <summary>The difference between what the rules want and what the group holds.</summary>
public sealed record DynamicGroupDelta(IReadOnlyList<string> Desired, IReadOnlyList<string> Adds, IReadOnlyList<string> Removes);

/// <summary>
/// DYNAMIC-GROUPS-01 (UC034-D01, D02, D12): the Active Roles dynamic-group definition as the group carries it in
/// Active Directory, read and decided without a directory. The group's otherwise unused <c>accountNameHistory</c>
/// holds <c>&lt;DynamicGroupXML Conditions="[type;objectGUID;filter]...[DG]" OriginatingService="guid" /&gt;</c>; the
/// Conditions grammar is the managed unit's (the configuration database's <c>edsaMUConditionsList</c> is the same text).
/// <see cref="Decide"/> applies the precedence the help states for conflicting rules: Exclude Explicitly, then Include
/// Explicitly, Exclude by Query, Exclude Group Members, Include by Query, Include Group Members. The evaluator runs the
/// searches; this class owns the text and the decision so both are testable on their own.
/// </summary>
public static class DynamicGroupRules
{
    public const string AttributeName = "accountNameHistory";
    public const string Terminator = "DG";
    /// <summary>The group's Notes attribute, stamped on conversion so an operator in ADUC learns why a hand change vanishes (UC034-D03).</summary>
    public const string NotesAttributeName = "info";
    /// <summary>The service GUID a definition written by IdentityCenter names, so an Active Roles service does not take it for its own on its daily rebuild.</summary>
    public static readonly Guid IdentityCenterService = Guid.Parse("4ec0d7a1-1d3c-4c3e-9b2a-6f0c0a51c0de");

    private static readonly Regex ConditionsAttribute = new("Conditions\\s*=\\s*\"(?<v>[^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex OriginatingServiceAttribute = new("OriginatingService\\s*=\\s*\"(?<v>[^\"]*)\"", RegexOptions.Compiled);

    /// <summary>The precedence of a rule when rules conflict on one object; the higher wins.</summary>
    public static int Precedence(DynamicGroupRuleType type) => type switch
    {
        DynamicGroupRuleType.ExcludeExplicitly => 6,
        DynamicGroupRuleType.IncludeExplicitly => 5,
        DynamicGroupRuleType.ExcludeByQuery => 4,
        DynamicGroupRuleType.ExcludeGroupMembers => 3,
        DynamicGroupRuleType.IncludeByQuery => 2,
        DynamicGroupRuleType.IncludeGroupMembers => 1,
        _ => 0,
    };

    /// <summary>
    /// MEMBERSHIP-RULES-01 (UC034-F12): the group objects the directory refuses as members of a group of this
    /// <paramref name="groupType"/>, as an LDAP filter: a global group holds no universal and no domain-local group, a
    /// universal group no domain-local group; a domain-local group holds any. Null when nothing is refused by scope.
    /// The console's preview leaves these out; so does the decision, rather than failing them one by one on a rebuild.
    /// </summary>
    public static string? IneligibleMembersFilter(int groupType)
    {
        const int global = 2, domainLocal = 4, universal = 8;
        if ((groupType & global) != 0)
            return "(&(objectClass=group)(|(groupType:1.2.840.113556.1.4.803:=4)(groupType:1.2.840.113556.1.4.803:=8)))";
        if ((groupType & universal) != 0)
            return "(&(objectClass=group)(groupType:1.2.840.113556.1.4.803:=4))";
        _ = domainLocal;
        return null;
    }

    /// <summary>The wizard's name for a type ("Include by Query"), for a report.</summary>
    public static string Describe(DynamicGroupRuleType type) => type switch
    {
        DynamicGroupRuleType.IncludeByQuery => "Include by Query",
        DynamicGroupRuleType.ExcludeByQuery => "Exclude by Query",
        DynamicGroupRuleType.IncludeExplicitly => "Include Explicitly",
        DynamicGroupRuleType.ExcludeExplicitly => "Exclude Explicitly",
        DynamicGroupRuleType.IncludeGroupMembers => "Include Group Members",
        DynamicGroupRuleType.ExcludeGroupMembers => "Exclude Group Members",
        DynamicGroupRuleType.RetainDeprovisioned => "Retain Deprovisioned",
        _ => type.ToString(),
    };

    /// <summary>
    /// The definition as the directory stores it, in the service's own spelling (DYNAMIC-GROUPS-04): <c>[0x&lt;type&gt;;guid;filter]</c>
    /// per rule, <c>[DG]</c> last, the filter verbatim with its '&amp;' raw as the service writes it, the service GUID. What
    /// <see cref="TryParse"/> reads back is the same list.
    /// </summary>
    public static string Serialize(IReadOnlyList<DynamicGroupRule> rules, Guid? originatingService = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var conditions = new System.Text.StringBuilder();
        foreach (var rule in rules)
        {
            conditions.Append('[').Append("0x").Append(((int)rule.Type).ToString("x")).Append(';')
                .Append(rule.ObjectGuid.ToString("D")).Append(';')
                .Append(rule.IsQuery ? (rule.Filter ?? string.Empty).Trim() : string.Empty)
                .Append(']');
        }
        conditions.Append('[').Append(Terminator).Append(']');
        return string.Concat("<DynamicGroupXML Conditions=\"", conditions.ToString(), "\" OriginatingService=\"",
            (originatingService ?? IdentityCenterService).ToString("D"), "\" />");
    }

    /// <summary>
    /// A rule a group may carry, before it is written: one of the seven rule types, an object (Retain Deprovisioned
    /// names the group itself), and for a query type a filter that is at least a parenthesised, balanced LDAP filter.
    /// False with the reason otherwise.
    /// </summary>
    public static bool IsWritable(DynamicGroupRule rule, out string? reason)
    {
        reason = null;
        if (rule.Type is < DynamicGroupRuleType.IncludeByQuery or > DynamicGroupRuleType.RetainDeprovisioned)
        {
            reason = string.Concat("Rule type ", (int)rule.Type, " is not one of the seven membership rule types.");
            return false;
        }
        if (rule.ObjectGuid == Guid.Empty)
        {
            reason = "A rule names no object.";
            return false;
        }
        if (rule.IsQuery)
        {
            var filter = (rule.Filter ?? string.Empty).Trim();
            if (filter.Length < 3 || filter[0] != '(' || filter[^1] != ')' || !Balanced(filter) || filter.Contains('[') || filter.Contains(']'))
            {
                reason = "A query rule needs an LDAP filter in parentheses, balanced, without square brackets.";
                return false;
            }
        }
        return true;
    }

    private static bool Balanced(string filter)
    {
        var depth = 0;
        for (var i = 0; i < filter.Length; i++)
        {
            if (filter[i] == '\\') { i++; continue; }
            if (filter[i] == '(') depth++;
            else if (filter[i] == ')' && --depth < 0) return false;
        }
        return depth == 0;
    }

    /// <summary>True when the attribute value is a dynamic-group definition at all (the element name), whatever its rules.</summary>
    public static bool LooksLikeDefinition(string? accountNameHistory) =>
        !string.IsNullOrWhiteSpace(accountNameHistory) && accountNameHistory.Contains("<DynamicGroupXML", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the definition out of the attribute's text. False with the reason when the text is not a definition or a
    /// rule does not parse: a group whose blob does not parse is listed, not guessed (UC034-D10).
    /// </summary>
    public static bool TryParse(string? accountNameHistory, out DynamicGroupDefinition? definition, out string? error)
    {
        definition = null;
        error = null;
        if (!LooksLikeDefinition(accountNameHistory))
        {
            error = "The attribute holds no DynamicGroupXML element.";
            return false;
        }

        string? conditions = null;
        string? originating = null;
        try
        {
            var element = XElement.Parse(accountNameHistory!.Trim());
            conditions = element.Attribute("Conditions")?.Value;
            originating = element.Attribute("OriginatingService")?.Value;
        }
        catch (System.Xml.XmlException)
        {
            // An LDAP filter holds '&' and the service does not always escape it; read the two attributes by shape.
            var c = ConditionsAttribute.Match(accountNameHistory!);
            if (c.Success) conditions = Unescape(c.Groups["v"].Value);
            var o = OriginatingServiceAttribute.Match(accountNameHistory!);
            if (o.Success) originating = o.Groups["v"].Value;
        }

        if (conditions is null)
        {
            error = "The DynamicGroupXML element has no Conditions attribute.";
            return false;
        }

        if (!TryParseConditions(conditions, out var rules, out error)) return false;
        Guid? service = Guid.TryParse(originating, out var g) ? g : null;
        definition = new DynamicGroupDefinition(rules!, service);
        return true;
    }

    /// <summary>The Conditions grammar alone: <c>[type;guid;filter]</c> tokens closed by <c>[DG]</c>. A managed unit's list is the same text without the terminator.</summary>
    public static bool TryParseConditions(string conditions, out IReadOnlyList<DynamicGroupRule>? rules, out string? error)
    {
        rules = null;
        error = null;
        var list = new List<DynamicGroupRule>();
        var text = conditions.Trim();
        var i = 0;
        var terminated = false;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (text[i] != '[')
            {
                error = string.Concat("Unexpected text at position ", i, ": expected '['.");
                return false;
            }
            var close = text.IndexOf(']', i + 1);
            if (close < 0)
            {
                error = string.Concat("Unclosed rule at position ", i, ".");
                return false;
            }
            var token = text.Substring(i + 1, close - i - 1);
            i = close + 1;

            if (string.Equals(token.Trim(), Terminator, StringComparison.OrdinalIgnoreCase))
            {
                terminated = true;
                continue;
            }

            // type;guid;filter: the filter may itself hold ';', so split on the first two only.
            var first = token.IndexOf(';');
            var second = first < 0 ? -1 : token.IndexOf(';', first + 1);
            if (first < 0 || second < 0)
            {
                error = string.Concat("Rule '", token, "' is not type;objectGUID;filter.");
                return false;
            }
            var typeText = token[..first].Trim();
            var guidText = token[(first + 1)..second].Trim();
            var filter = token[(second + 1)..].Trim();

            if (!TryParseType(typeText, out var type))
            {
                error = string.Concat("Rule type '", typeText, "' is not one of the seven membership rule types.");
                return false;
            }
            if (!Guid.TryParse(guidText, out var guid))
            {
                error = string.Concat("Rule '", token, "' names no objectGUID.");
                return false;
            }
            list.Add(new DynamicGroupRule(type, guid, filter.Length == 0 ? null : filter));
        }

        if (!terminated && list.Count == 0)
        {
            error = "The Conditions list is empty.";
            return false;
        }
        rules = list;
        return true;
    }

    private static bool TryParseType(string text, out DynamicGroupRuleType type)
    {
        type = default;
        int code;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out code)) return false;
        }
        else if (!int.TryParse(text, out code)) return false;
        if (code is < 1 or > 7) return false;
        type = (DynamicGroupRuleType)code;
        return true;
    }

    private static string Unescape(string value) =>
        value.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&");

    /// <summary>
    /// The member set the rules want, from what each rule matched. An object enters through an include rule; among the
    /// rules that matched it, the one with the highest precedence decides. Distinguished names compare case-insensitively
    /// and keep their first spelling. <paramref name="self"/>, the group's own DN, is never a member of itself.
    /// </summary>
    public static IReadOnlyList<string> Decide(IReadOnlyList<(DynamicGroupRule Rule, IReadOnlyCollection<string> Matched)> evaluations, string? self = null)
    {
        var best = new Dictionary<string, (int Precedence, bool Include)>(StringComparer.OrdinalIgnoreCase);
        var spelling = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rule, matched) in evaluations)
        {
            var precedence = Precedence(rule.Type);
            if (precedence == 0) continue;   // Retain Deprovisioned decides nothing of its own; Diff applies it to the current members
            foreach (var dn in matched)
            {
                if (string.IsNullOrWhiteSpace(dn)) continue;
                if (self is not null && string.Equals(dn, self, StringComparison.OrdinalIgnoreCase)) continue;
                spelling.TryAdd(dn, dn);
                if (!best.TryGetValue(dn, out var current) || precedence > current.Precedence)
                    best[dn] = (precedence, rule.IsInclude);
            }
        }
        return best.Where(kv => kv.Value.Include).Select(kv => spelling[kv.Key]).OrderBy(dn => dn, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>What a rebuild would write: the desired set, the names to add and the names to remove.</summary>
    public static DynamicGroupDelta Diff(IReadOnlyList<string> desired, IReadOnlyCollection<string> current) => Diff(desired, current, null);

    /// <summary>
    /// The same difference with Retain Deprovisioned applied (MEMBERSHIP-RULES-01, UC034-F13): every name in
    /// <paramref name="retained"/> that is a current member stays one, so it is never in the removes and it is in the
    /// desired set. Null means the definition carries no Retain rule.
    /// </summary>
    public static DynamicGroupDelta Diff(IReadOnlyList<string> desired, IReadOnlyCollection<string> current, IReadOnlyCollection<string>? retained)
    {
        var have = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
        var keep = retained is null ? null : new HashSet<string>(retained.Where(have.Contains), StringComparer.OrdinalIgnoreCase);
        var wanted = keep is { Count: > 0 }
            ? desired.Concat(current.Where(keep.Contains)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(dn => dn, StringComparer.OrdinalIgnoreCase).ToList()
            : desired;
        var want = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
        var adds = wanted.Where(dn => !have.Contains(dn)).ToList();
        var removes = current.Where(dn => !want.Contains(dn)).OrderBy(dn => dn, StringComparer.OrdinalIgnoreCase).ToList();
        return new DynamicGroupDelta(wanted, adds, removes);
    }
}
