using System.Security.Cryptography;
using System.Text;

namespace Conduit.Emulator.GoogleWorkspace.Infrastructure;

public static class EtagGenerator
{
    public static string New() => "\"" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)).TrimEnd('=') + "\"";

    public static string ForContent(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return "\"" + Convert.ToBase64String(hash, 0, 16).TrimEnd('=') + "\"";
    }
}
