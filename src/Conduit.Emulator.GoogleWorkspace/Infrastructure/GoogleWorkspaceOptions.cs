namespace Conduit.Emulator.GoogleWorkspace.Infrastructure;

public sealed class GoogleWorkspaceOptions
{
    public string CustomerId { get; set; } = "C00acme01";
    public string PrimaryDomain { get; set; } = "acme.example.com";
    public List<string> SecondaryDomains { get; set; } = new();
    public bool SeedOnStartup { get; set; } = true;
    public int SeedUserCount { get; set; } = 350;
    public int SeedGroupCount { get; set; } = 22;
    public double SuspendedRatio { get; set; } = 0.04;
    public int AdminCount { get; set; } = 3;
    public int AccessTokenTtlSeconds { get; set; } = 3600;
    public int MaxAssertionClockSkewSeconds { get; set; } = 300;
    public List<string> AllowedScopes { get; set; } = new();
}
