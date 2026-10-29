using Conduit.Connectors.ActiveDirectory;

namespace Conduit.Web.Services;

/// <summary>
/// ALLOW-LIST-02 (IdentityCenter UC015-D06 / UC017-F03, F06). The agent writes attribute values as the
/// strings it is sent; most AD attributes take them as-is. <c>accountExpires</c> does not: it is a
/// FILETIME and the sender holds a date. This is the ONE place an attribute NAME selects a shape, used
/// by both the update executor (SetAttributes) and the create executor (the attribute bag), so the two
/// cannot disagree. The FILETIME conversion itself is <see cref="AdFileTime"/>, the connector's single
/// owner of that text in both directions (REUSE-PASS-01).
/// </summary>
public static class AdAttributeValueShaper
{
    public const string AccountExpires = "accountExpires";

    public static bool TryShape(string canonicalName, string? value, out string? shaped, out string? error)
    {
        error = null;
        if (!string.Equals(canonicalName, AccountExpires, StringComparison.OrdinalIgnoreCase))
        {
            shaped = value;
            return true;
        }
        return AdFileTime.TryToFileTimeString(value, out shaped, out error);
    }

    /// <summary>Kept as the executors' and tests' name for the accountExpires shape; the work is <see cref="AdFileTime.TryToFileTimeString"/>.</summary>
    public static bool TryShapeAccountExpires(string? value, out string? shaped, out string? error) =>
        AdFileTime.TryToFileTimeString(value, out shaped, out error);
}
