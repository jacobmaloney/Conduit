using System.Text.Json;
using Conduit.Sync.Connectors;
using Conduit.Web.Connectors;
using Microsoft.Extensions.DependencyInjection;

// Capability export utility.
//
// Emits the connector capability catalog to a STATIC JSON file for the marketing site.
// It runs the SAME ConnectorCapabilityDescriptor.From mapper over the SAME real
// ConnectorRegistry that the /api/connectors/capabilities endpoint serves — populated
// through the shared AddConduitConnectors registration — so the shipped file and the
// live endpoint can never diverge. NOTHING here is a hand-built list.
//
// It touches no database and starts no server: AddConnectorMetadataDependencies wires
// only inert leaf dependencies so the 18 adapters can be constructed for metadata reads.
//
// USAGE (from the repo root):
//   dotnet run --project tools/Conduit.CapabilityExport -c Release
// Optional explicit output path:
//   dotnet run --project tools/Conduit.CapabilityExport -c Release -- <output.json>

var services = new ServiceCollection()
    .AddConnectorMetadataDependencies()
    .AddConduitConnectors();

using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var registry = scope.ServiceProvider.GetRequiredService<ConnectorRegistry>();

var descriptors = registry.All
    .Select(ConnectorCapabilityDescriptor.From)
    .OrderBy(d => d.SystemType, StringComparer.OrdinalIgnoreCase)
    .ToList();

var options = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
};
var json = JsonSerializer.Serialize(descriptors, options);

var outputPath = args.Length > 0 ? args[0] : DefaultOutputPath();
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
File.WriteAllText(outputPath, json);

Console.WriteLine($"Wrote {descriptors.Count} connector descriptors to {outputPath}");
return 0;

// Resolve src/Conduit.Web/wwwroot/connector-capabilities.json by walking up from the
// build output until the directory holding Conduit.sln (the repo root) is found.
static string DefaultOutputPath()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conduit.sln")))
        dir = dir.Parent;

    if (dir is null)
        throw new InvalidOperationException(
            "Could not locate the repo root (Conduit.sln). Pass an explicit output path as the first argument.");

    return Path.Combine(dir.FullName, "src", "Conduit.Web", "wwwroot", "connector-capabilities.json");
}
