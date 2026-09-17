using System.Text.Json;

namespace Conduit.SourceBrowsing;

public static class SourceBrowseJson
{
    public const int MaximumResponseBytes = 24 * 1024 * 1024;
    /// <summary>Bound both chunked and length-declared payloads before deserializing. No unbounded ReadFromJsonAsync.</summary>
    public static async Task<T> ReadAsync<T>(Stream source, CancellationToken ct, int maximumBytes = MaximumResponseBytes)
    {
        using var buffer = new MemoryStream();
        var block = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(block.AsMemory(0, Math.Min(block.Length, maximumBytes + 1 - (int)buffer.Length)), ct)) > 0)
        {
            if (buffer.Length + count > maximumBytes)
                throw new SourceBrowseException("ResponseTooLarge", "The remote response exceeds the browse limit. Narrow the scope and retry.");
            buffer.Write(block, 0, count);
        }
        ct.ThrowIfCancellationRequested();
        buffer.Position = 0;
        return await JsonSerializer.DeserializeAsync<T>(buffer, new JsonSerializerOptions(JsonSerializerDefaults.Web), ct)
            ?? throw new SourceBrowseException("InvalidResponse", "The remote source returned an empty response.");
    }
}
