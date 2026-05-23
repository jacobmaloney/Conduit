using System.Text;
using Newtonsoft.Json;

namespace Conduit.Emulator.GoogleWorkspace.Infrastructure;

public sealed class PageCursor
{
    [JsonProperty("o")] public int Offset { get; set; }
    [JsonProperty("s")] public string? Sort { get; set; }
    [JsonProperty("k")] public string? LastKey { get; set; }
}

public static class PageTokenEncoder
{
    public static string Encode(PageCursor cursor)
    {
        var json = JsonConvert.SerializeObject(cursor);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static PageCursor? Decode(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var padded = token.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            return JsonConvert.DeserializeObject<PageCursor>(json);
        }
        catch
        {
            return null;
        }
    }
}
