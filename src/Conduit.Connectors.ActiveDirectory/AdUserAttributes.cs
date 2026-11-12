using System;
using System.Collections.Generic;
using System.Linq;

namespace Conduit.Connectors.ActiveDirectory;

/// <summary>
/// CREATE-ATTRS-01 (IdentityCenter UC035-S04, UC007): THE list of user attributes this connector writes to a
/// directory on behalf of a console — on a create (the sink's add request), on an update (the sink's modify, which
/// the sync upsert also uses) and through the agent's attribute write (whose allow-list reads it). Before this, the
/// create and update projection was nine attribute families while the write allow-list was forty names, so a
/// create carrying employeeType, an office, a phone or an address wrote the account and silently dropped the rest
/// (the lab's Nora Vance, 2026-11-11: the request carried employeeType=Contractor; the directory got none).
///
/// Canonical AD spelling, user class only. Structural and credential names (sAMAccountName, userPrincipalName, cn,
/// the OU, userAccountControl, unicodePwd, manager) are NOT here: each is its own controlled field or operation.
/// ALLOW-LIST-02 notes still hold: accountExpires takes the shape the value shaper gives it; employeeID/employeeType/
/// employeeNumber are the HR identifiers the Mover keeps current; extensionAttribute1-15 are the customer's own
/// fields; msExchHideFromAddressLists is the one Exchange attribute (a domain without the Exchange schema refuses it,
/// and the results card shows why).
/// </summary>
public static class AdUserAttributes
{
    public static readonly IReadOnlyList<string> Writable = new[]
    {
        "displayName", "givenName", "sn", "mail", "title", "department", "company", "description",
        "physicalDeliveryOfficeName", "telephoneNumber", "mobile", "homePhone", "facsimileTelephoneNumber", "wWWHomePage",
        "streetAddress", "l", "st", "postalCode", "co",
        "employeeID", "employeeType", "employeeNumber",
        "accountExpires", "homeDirectory", "homeDrive",
        "msExchHideFromAddressLists",
        "extensionAttribute1", "extensionAttribute2", "extensionAttribute3", "extensionAttribute4", "extensionAttribute5",
        "extensionAttribute6", "extensionAttribute7", "extensionAttribute8", "extensionAttribute9", "extensionAttribute10",
        "extensionAttribute11", "extensionAttribute12", "extensionAttribute13", "extensionAttribute14", "extensionAttribute15",
    };

    /// <summary>
    /// The sync's historic ConnectorObject spellings for a few names (a mapping or a template may still carry them);
    /// the sink reads the alias when the canonical name is absent and writes the canonical attribute.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["sn"] = new[] { "familyName" },
        ["mail"] = new[] { "email" },
        ["telephoneNumber"] = new[] { "phoneNumber" },
    };

    /// <summary>Each writable attribute as the sink projects it: the canonical name first, then its aliases.</summary>
    public static readonly IReadOnlyList<string[]> WriteFamilies =
        Writable.Select(name => Aliases.TryGetValue(name, out var aliases) ? new[] { name }.Concat(aliases).ToArray() : new[] { name }).ToList();

    private static readonly Dictionary<string, string> Canonical = Writable.ToDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase);

    /// <summary>True with the canonical spelling when <paramref name="name"/> is a writable user attribute (case-insensitive).</summary>
    public static bool TryCanonical(string? name, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(name) || !Canonical.TryGetValue(name.Trim(), out var found)) return false;
        canonical = found;
        return true;
    }
}
