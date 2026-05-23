using System.Security.Cryptography;

namespace Conduit.Emulator.GoogleWorkspace.Infrastructure;

// Google user IDs are 21-character numeric strings. Group/member/role IDs use
// shorter numeric or alpha-numeric forms. This generator produces values that
// "look Google" so connector-side ID parsers don't have surprises.
public static class IdGenerator
{
    public static string NewUserId() => New21DigitNumeric();
    public static string NewGroupId() => "0" + New19DigitNumeric();                // 20 chars
    public static string NewCustomerId() => "C" + NewRandomLower(8);               // e.g. C03xyz12
    public static string NewOrgUnitId() => "id:" + NewRandomLower(16);
    public static string NewSchemaId() => NewRandomAlphaNum(16);
    public static string NewAccessToken() => "ya29." + NewRandomBase64Url(80);     // resembles real
    public static long NewRoleId() => (long)(Random.Shared.NextInt64(1_000_000_000L, 9_999_999_999L));
    public static long NewRoleAssignmentId() => (long)(Random.Shared.NextInt64(1_000_000_000L, 9_999_999_999L));
    public static string NewPrivateKeyId() => NewRandomLower(40);
    public static string NewServiceAccountClientId() => New21DigitNumeric();

    private static string New21DigitNumeric()
    {
        Span<byte> buf = stackalloc byte[16];
        RandomNumberGenerator.Fill(buf);
        var chars = new char[21];
        // Leading digit 1 (Google IDs don't start with 0 in user IDs)
        chars[0] = '1';
        for (int i = 1; i < 21; i++) chars[i] = (char)('0' + (buf[i % buf.Length] % 10));
        return new string(chars);
    }

    private static string New19DigitNumeric()
    {
        Span<byte> buf = stackalloc byte[16];
        RandomNumberGenerator.Fill(buf);
        var chars = new char[19];
        for (int i = 0; i < 19; i++) chars[i] = (char)('0' + (buf[i % buf.Length] % 10));
        return new string(chars);
    }

    private static string NewRandomLower(int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        Span<byte> buf = stackalloc byte[length];
        RandomNumberGenerator.Fill(buf);
        var chars = new char[length];
        for (int i = 0; i < length; i++) chars[i] = alphabet[buf[i] % alphabet.Length];
        return new string(chars);
    }

    private static string NewRandomAlphaNum(int length)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        Span<byte> buf = stackalloc byte[length];
        RandomNumberGenerator.Fill(buf);
        var chars = new char[length];
        for (int i = 0; i < length; i++) chars[i] = alphabet[buf[i] % alphabet.Length];
        return new string(chars);
    }

    private static string NewRandomBase64Url(int byteLength)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteLength);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
