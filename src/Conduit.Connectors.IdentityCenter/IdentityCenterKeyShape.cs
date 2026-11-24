namespace Conduit.Connectors.IdentityCenter;

/// <summary>
/// What an IdentityCenter API key looks like, and plain words for a key that cannot be one.
///
/// IdentityCenter mints every key as "ic_" followed by 64 hexadecimal characters (32 random bytes),
/// 67 characters in all, and shows it once. A key that was copied in part, or that is some other
/// text that happened to be on the clipboard, is answered with a bare 401 that names no cause
/// (the API answers every unknown key the same way, on purpose). Jacob lost a morning to that on
/// 2026-11-24: the box held about 46 characters. Saying so before anything is sent costs nothing.
/// </summary>
public static class IdentityCenterKeyShape
{
    public const string Prefix = "ic_";
    public const int Length = 67;

    /// <summary>Null when the text has the shape of a key; otherwise one sentence saying what is wrong.</summary>
    public static string? Problem(string? key, string label = "API key")
    {
        if (string.IsNullOrEmpty(key))
            return $"The {label} is empty.";

        if (key.Length != key.Trim().Length)
            return $"The {label} has a space or line break at its start or end. Paste it again without the extra space.";

        if (!key.StartsWith(Prefix, StringComparison.Ordinal))
            return $"The {label} does not start with \"{Prefix}\", so it is not a key IdentityCenter issued. Copy the key again from IdentityCenter; it is shown once, so if it was not kept, create a new one.";

        if (key.Length != Length)
            return $"The {label} is {key.Length} characters long; a key IdentityCenter issues is {Length}. It was probably copied in part. Copy the whole key again; it is shown once, so if it was not kept, create a new one.";

        for (var i = Prefix.Length; i < key.Length; i++)
        {
            if (!Uri.IsHexDigit(key[i]))
                return $"The {label} holds a character that a key IdentityCenter issues never has. Copy the key again from IdentityCenter.";
        }

        return null;
    }

    /// <summary>Plain words for the answers IdentityCenter gives a key it will not serve.</summary>
    public static string FailureMessage(int statusCode, string? reasonPhrase, string baseUrl) =>
        Explain(statusCode, baseUrl) ?? $"HTTP {statusCode}: {reasonPhrase}";

    public static string? Explain(int statusCode, string baseUrl) => statusCode switch
    {
        401 => $"IdentityCenter at {baseUrl} did not recognise this API key. The key has the right shape, so check that it was created on this IdentityCenter, that it has not been revoked, and that every character was copied.",
        403 => $"IdentityCenter at {baseUrl} recognised the key but will not let it read. A sync connection needs a key with the admin scope.",
        _ => null
    };
}
