using System.Security.Cryptography;

namespace Conduit.Web.Services;

/// <summary>
/// The ONE generator for credential material an agent command lands in a directory: the password
/// resets in <see cref="AdAgentWriteExecutor"/> and the enable-on-create initial password in
/// <see cref="AdAgentCreateExecutor"/> (UC008-D07). CSPRNG; guaranteed lower/upper/digit/symbol;
/// no ambiguous glyphs (0/O, 1/l/I); Fisher-Yates so the guaranteed classes are not positionally
/// predictable. Callers drop the reference after use and never log, return or message the value.
/// </summary>
internal static class AgentPasswordGenerator
{
    /// <summary>First attempt. A fine-grained policy that refuses it gets ONE retry at <see cref="RetryLength"/>.</summary>
    public const int DefaultLength = 24;
    public const int RetryLength = 32;

    public static string Generate(int length)
    {
        if (length < 4)
            throw new ArgumentOutOfRangeException(nameof(length), "A generated password needs at least four characters (one per guaranteed class).");

        const string lower = "abcdefghjkmnpqrstuvwxyz";
        const string upper = "ABCDEFGHJKMNPQRSTUVWXYZ";
        const string digit = "23456789";
        const string symbol = "!@#$%^*-_=+?";
        const string all = lower + upper + digit + symbol;

        var chars = new char[length];
        chars[0] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        chars[1] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        chars[2] = digit[RandomNumberGenerator.GetInt32(digit.Length)];
        chars[3] = symbol[RandomNumberGenerator.GetInt32(symbol.Length)];
        for (var i = 4; i < length; i++)
            chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        // Fisher-Yates so the guaranteed classes are not positionally predictable.
        for (var i = length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
    }
}
