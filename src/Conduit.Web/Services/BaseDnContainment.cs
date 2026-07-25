using System.Text;

namespace Conduit.Web.Services;

/// <summary>
/// The AGENT-SIDE creation containment control (design §8). Given a target OU and the customer's
/// configured set of PERMITTED creation base DNs, decides whether the target is contained within one
/// of them — using COMPONENT-WISE RDN comparison, never string EndsWith.
///
/// Both DNs are parsed into normalized RDN sequences (split on UNESCAPED commas, honouring the
/// <c>\,</c> <c>\+</c> <c>\\</c> escapes; each RDN's attribute-value assertions have their type
/// lowercased, their value unescaped and lowercased, and multi-valued AVAs sorted). The target is
/// contained iff its RDN sequence ENDS WITH a permitted base's RDN sequence.
///
/// DENY-ALL by default and FAIL-CLOSED: a null/blank/malformed target, a null/empty permitted set,
/// and every malformed permitted base all yield <c>false</c> (create nothing). A parse error is never
/// treated as "no restriction".
/// </summary>
public static class BaseDnContainment
{
    /// <summary>
    /// True when <paramref name="dn"/> parses as a well-formed DN (component-wise, honouring escapes).
    /// Used by the settings UI to reject a malformed base DN at save time, reusing the exact same parser
    /// the containment check uses so the two can never diverge.
    /// </summary>
    public static bool IsWellFormedDn(string? dn) => ParseRdns(dn) is { Count: > 0 };

    public static bool IsContained(string? targetOu, IEnumerable<string>? permittedBaseDns)
    {
        var target = ParseRdns(targetOu);
        if (target is null || target.Count == 0)
            return false; // malformed / empty target -> deny
        if (permittedBaseDns is null)
            return false; // no allow-list -> deny-all

        foreach (var baseDn in permittedBaseDns)
        {
            var permitted = ParseRdns(baseDn);
            if (permitted is null || permitted.Count == 0)
                continue; // a malformed permitted base grants nothing (fail-closed)
            if (IsRdnSuffix(target, permitted))
                return true;
        }
        return false; // deny-all default
    }

    // target's RDN list must END WITH baseDn's RDN list (component-wise, both already normalized).
    private static bool IsRdnSuffix(List<string> target, List<string> baseDn)
    {
        if (baseDn.Count > target.Count)
            return false;
        var offset = target.Count - baseDn.Count;
        for (var i = 0; i < baseDn.Count; i++)
        {
            if (!string.Equals(target[offset + i], baseDn[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    // Parse a DN into a list of normalized RDN strings. Returns null on any malformed component.
    private static List<string>? ParseRdns(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn))
            return null;

        var rdnStrings = SplitUnescaped(dn, ',');
        if (rdnStrings is null)
            return null;

        var result = new List<string>(rdnStrings.Count);
        foreach (var rdn in rdnStrings)
        {
            var avaStrings = SplitUnescaped(rdn, '+');
            if (avaStrings is null || avaStrings.Count == 0)
                return null;

            var avas = new List<string>(avaStrings.Count);
            foreach (var ava in avaStrings)
            {
                var norm = NormalizeAva(ava);
                if (norm is null)
                    return null;
                avas.Add(norm);
            }
            avas.Sort(StringComparer.Ordinal); // multi-valued RDN order is not significant
            result.Add(string.Join("+", avas));
        }
        return result;
    }

    // "type=value" -> "lowertype=unescaped-lower-value". Null on malformed.
    private static string? NormalizeAva(string ava)
    {
        var eq = -1;
        for (var i = 0; i < ava.Length; i++)
        {
            if (ava[i] == '=' && !IsEscaped(ava, i)) { eq = i; break; }
        }
        if (eq <= 0)
            return null;

        var type = ava[..eq].Trim();
        var rawValue = ava[(eq + 1)..].Trim();
        if (type.Length == 0 || rawValue.Length == 0)
            return null;

        return type.ToLowerInvariant() + "=" + Unescape(rawValue).ToLowerInvariant();
    }

    // Backslash + one char -> that char literally (covers \, \+ \\ \< etc.).
    private static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[i + 1]); i++; }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    // Split on an unescaped separator; null if any empty component (a malformed DN like "a,,b").
    private static List<string>? SplitUnescaped(string s, char sep)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == sep && !IsEscaped(s, i))
            {
                parts.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(s[i]);
            }
        }
        parts.Add(sb.ToString());

        return parts.Any(p => p.Trim().Length == 0) ? null : parts;
    }

    private static bool IsEscaped(string s, int index)
    {
        var backslashes = 0;
        for (var i = index - 1; i >= 0 && s[i] == '\\'; i--)
            backslashes++;
        return (backslashes % 2) == 1;
    }
}
