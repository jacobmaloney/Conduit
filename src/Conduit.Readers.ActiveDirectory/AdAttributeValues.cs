using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Linq;
using System.Security.Principal;
using System.Text;

namespace Conduit.Readers.ActiveDirectory;

/// <summary>
/// THE conversion of Active Directory attribute values, on the way out of the protocol and on the
/// way into text.
///
/// Until 2026-10-16 a multi-valued attribute left <c>LdapSession</c> as the raw wire values
/// (<c>byte[]</c> each), because <c>DirectoryAttribute</c> stores every received value as bytes and
/// only its indexer decodes them; enumerating the collection does not. A one-member group therefore
/// carried a DN string and a two-member group carried byte arrays, which every downstream
/// <c>ToString()</c> turned into the literal text "System.Byte[]": 36,021 member references and
/// 19,746 objectGUID copies landed in IdentityCenter as that string, and no group had members.
///
/// Rules: an attribute whose name says it is binary (objectGUID, objectSid, ntSecurityDescriptor,
/// *GUID, *Sid, photos, certificates) is read as bytes, never decoded by chance; everything else is
/// read as UTF-8 text. Text for a binary value is the shape operators and IdentityCenter expect: a
/// GUID string for a 16-byte *GUID, an S-1-5 string for a SID, Base64 for the rest.
/// </summary>
public static class AdAttributeValues
{
    private static readonly string[] BinaryExact =
    {
        "objectGUID", "objectSid", "sIDHistory", "ntSecurityDescriptor", "thumbnailPhoto", "jpegPhoto",
        "userCertificate", "userSMIMECertificate", "msDS-AllowedToActOnBehalfOfOtherIdentity",
        "logonHours", "mS-DS-CreatorSID", "securityIdentifier", "tokenGroups", "tokenGroupsGlobalAndUniversal",
    };

    /// <summary>True when the attribute's values are bytes by schema, not text.</summary>
    public static bool IsBinaryAttribute(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (BinaryExact.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        return name.EndsWith("GUID", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Sid", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One attribute's value(s) as the reader exposes them: null for none, one value, or an array of
    /// values; text attributes as <see cref="string"/>, binary attributes as <c>byte[]</c>.
    /// </summary>
    public static object? FromDirectoryAttribute(DirectoryAttribute attribute)
    {
        if (attribute is null || attribute.Count == 0) return null;
        var values = IsBinaryAttribute(attribute.Name)
            ? attribute.GetValues(typeof(byte[])).Cast<object?>().ToArray()
            : attribute.GetValues(typeof(string)).Cast<object?>().ToArray();
        return values.Length == 1 ? values[0] : values;
    }

    /// <summary>
    /// A value as text: strings as they are, bytes as GUID / SID / Base64 by attribute name, a
    /// collection as each value's text (the caller joins). Null for null.
    /// </summary>
    public static string? ToText(string? attributeName, object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return s;
            case byte[] bytes:
                return BytesToText(attributeName, bytes);
            case System.Collections.IEnumerable e:
                return string.Join(";", ToTextList(attributeName, e));
            default:
                return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Every value of a collection as text, nulls and blanks dropped.</summary>
    public static List<string> ToTextList(string? attributeName, System.Collections.IEnumerable values)
    {
        var list = new List<string>();
        foreach (var item in values)
        {
            if (item is System.Collections.IEnumerable nested && item is not string && item is not byte[])
            {
                list.AddRange(ToTextList(attributeName, nested));
                continue;
            }
            var text = ToText(attributeName, item);
            if (!string.IsNullOrWhiteSpace(text)) list.Add(text!);
        }
        return list;
    }

    /// <summary>
    /// The same value shaped for a connector object: a string, or a list of strings for a
    /// multi-valued attribute, never bytes. Keeps null.
    /// </summary>
    public static object? ToTextValue(string? attributeName, object? value) => value switch
    {
        null => null,
        string s => s,
        byte[] bytes => BytesToText(attributeName, bytes),
        System.Collections.IEnumerable e => ToTextList(attributeName, e),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string BytesToText(string? attributeName, byte[] bytes)
    {
        var name = attributeName ?? string.Empty;
        if (bytes.Length == 16 && name.EndsWith("GUID", StringComparison.OrdinalIgnoreCase))
            return new Guid(bytes).ToString();
        if (name.EndsWith("Sid", StringComparison.OrdinalIgnoreCase) || name.Equals("securityIdentifier", StringComparison.OrdinalIgnoreCase))
        {
            try { return new SecurityIdentifier(bytes, 0).Value; }
            catch (ArgumentException) { /* not a SID after all; fall through to Base64 */ }
        }
        if (!IsBinaryAttribute(name))
        {
            // A text attribute that still arrived as bytes (a caller bypassed the reader): decode strictly.
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { }
        }
        return Convert.ToBase64String(bytes);
    }
}
