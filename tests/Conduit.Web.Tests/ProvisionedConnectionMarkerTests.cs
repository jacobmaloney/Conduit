using System.Text.Json.Nodes;
using Conduit.Web.Services;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// Guards the pin that authorizes the startup schema build. An empty-but-configured
/// database is migrated at boot ONLY when the secrets.json marker names the exact server
/// AND database that the currently-resolved connection string names. If this ever
/// degrades into a boolean "auto-provision" flag, any connection string that happens to
/// be configured — an env var, a stale deploy value, a hand-edit — would get a schema
/// built into it unattended. These tests assert the REASON, not just the outcome.
/// </summary>
public class ProvisionedConnectionMarkerTests
{
    private const string Configured =
        "Server=localhost;Database=Conduit18;User Id=sa;Password=p@ss;Encrypt=False;TrustServerCertificate=True;";

    /// <summary>A marker as it would have been written on THIS machine.</summary>
    private static ProvisionedConnectionTarget Marker(string server, string database) =>
        new(server, database, ProvisionedConnectionMarker.CurrentMachineName);

    /// <summary>A marker carrying an explicit machine identity.</summary>
    private static ProvisionedConnectionTarget MarkerFrom(string server, string database, string machine) =>
        new(server, database, machine);

    // ── The decision the startup path actually makes ─────────────────────────

    [Fact]
    public void Marker_matching_the_configured_connection_authorizes_migration()
    {
        var decision = ProvisionedSchemaService.Decide(
            Configured, Marker("localhost", "conduit18"), out var configured);

        Assert.Equal(ProvisionDecision.Migrate, decision);
        Assert.Equal("localhost", configured.DataSource);
        Assert.Equal("conduit18", configured.InitialCatalog);
    }

    [Fact]
    public void Marker_naming_a_different_database_must_NOT_migrate()
    {
        var decision = ProvisionedSchemaService.Decide(
            Configured, Marker("localhost", "Payroll"), out _);

        Assert.Equal(ProvisionDecision.MarkerMismatch, decision);
    }

    [Fact]
    public void Marker_naming_a_different_server_must_NOT_migrate()
    {
        var decision = ProvisionedSchemaService.Decide(
            Configured, Marker("192.168.1.60", "Conduit18"), out _);

        Assert.Equal(ProvisionDecision.MarkerMismatch, decision);
    }

    [Fact]
    public void Absent_marker_must_NOT_migrate()
    {
        var decision = ProvisionedSchemaService.Decide(Configured, marker: null, out _);

        Assert.Equal(ProvisionDecision.MarkerAbsent, decision);
    }

    [Fact]
    public void No_configured_connection_must_NOT_migrate_even_with_a_marker()
    {
        var decision = ProvisionedSchemaService.Decide(
            configuredConnectionString: null, Marker("localhost", "Conduit18"), out _);

        Assert.Equal(ProvisionDecision.NoConnection, decision);
    }

    [Fact]
    public void Placeholder_connection_must_NOT_migrate()
    {
        var decision = ProvisionedSchemaService.Decide(
            "Server=YOUR_SERVER;Database=YOUR_DATABASE;", Marker("your_server", "your_database"), out _);

        Assert.Equal(ProvisionDecision.NoConnection, decision);
    }

    [Fact]
    public void Connection_string_without_a_database_name_must_NOT_migrate()
    {
        var decision = ProvisionedSchemaService.Decide(
            "Server=localhost;Trusted_Connection=True;", Marker("localhost", ""), out _);

        Assert.Equal(ProvisionDecision.NoConnection, decision);
    }

    [Fact]
    public void Empty_marker_fields_authorize_nothing()
    {
        Assert.False(ProvisionedConnectionMarker.Matches(
            Marker(string.Empty, string.Empty), Marker(string.Empty, string.Empty)));
    }

    // ── Normalization / comparison ───────────────────────────────────────────

    [Theory]
    [InlineData("Server=LOCALHOST;Database=CONDUIT18;Trusted_Connection=True;", "localhost", "conduit18")]
    [InlineData("Data Source= localhost ;Initial Catalog= Conduit18 ;Trusted_Connection=True;", "localhost", "conduit18")]
    [InlineData("Server=(localdb)\\mssqllocaldb;Database=Conduit;Trusted_Connection=True;", "(localdb)\\mssqllocaldb", "conduit")]
    [InlineData("Server=.\\SQLEXPRESS;Database=Conduit;Trusted_Connection=True;", ".\\sqlexpress", "conduit")]
    public void Connection_strings_normalize_to_a_lowercase_trimmed_server_and_database(
        string connectionString, string expectedServer, string expectedDatabase)
    {
        Assert.True(ProvisionedConnectionMarker.TryParseConnectionString(connectionString, out var target));
        Assert.Equal(expectedServer, target.DataSource);
        Assert.Equal(expectedDatabase, target.InitialCatalog);
    }

    [Fact]
    public void Case_and_whitespace_differences_still_match()
    {
        Assert.True(ProvisionedConnectionMarker.TryParseConnectionString(
            "Server=LocalHost;Database=Conduit18;Trusted_Connection=True;", out var written));
        Assert.True(ProvisionedConnectionMarker.TryParseConnectionString(
            "Data Source=localhost;Initial Catalog=conduit18;Integrated Security=True;", out var configured));

        Assert.True(ProvisionedConnectionMarker.Matches(written, configured));
    }

    [Fact]
    public void A_database_name_that_only_differs_by_suffix_does_not_match()
    {
        Assert.True(ProvisionedConnectionMarker.TryParseConnectionString(
            "Server=localhost;Database=Conduit1;Trusted_Connection=True;", out var written));
        Assert.True(ProvisionedConnectionMarker.TryParseConnectionString(
            "Server=localhost;Database=Conduit17;Trusted_Connection=True;", out var configured));

        Assert.False(ProvisionedConnectionMarker.Matches(written, configured));
    }

    // ── secrets.json round-trip ──────────────────────────────────────────────

    [Fact]
    public void Apply_and_Parse_round_trip_through_a_secrets_root()
    {
        var root = new JsonObject
        {
            ["Jwt"] = new JsonObject { ["SecretKey"] = "unrelated" }
        };

        ProvisionedConnectionMarker.Apply(root, Marker("localhost", "conduit18"));
        var parsed = ProvisionedConnectionMarker.Parse(root);

        Assert.NotNull(parsed);
        Assert.Equal("localhost", parsed!.DataSource);
        Assert.Equal("conduit18", parsed.InitialCatalog);

        // Read-merge-rewrite: unrelated sections survive.
        Assert.Equal("unrelated", root["Jwt"]!["SecretKey"]!.GetValue<string>());
    }

    [Fact]
    public void Parse_returns_null_when_the_section_is_absent_or_incomplete()
    {
        Assert.Null(ProvisionedConnectionMarker.Parse(new JsonObject()));

        Assert.Null(ProvisionedConnectionMarker.Parse(new JsonObject
        {
            [ProvisionedConnectionMarker.SectionName] = new JsonObject { ["DataSource"] = "localhost" }
        }));

        Assert.Null(ProvisionedConnectionMarker.Parse(new JsonObject
        {
            [ProvisionedConnectionMarker.SectionName] = new JsonObject
            {
                ["DataSource"] = "localhost",
                ["InitialCatalog"] = "   "
            }
        }));
    }

    // ── Placeholder classification ───────────────────────────────────────────

    [Fact]
    public void LocalDB_is_a_real_data_source_not_a_placeholder()
    {
        // Both Setup.razor and DatabaseSettings.razor GENERATE this exact shape for the
        // LocalDB option. Treating it as a placeholder classified every genuine LocalDB
        // install as NotConfigured forever — it could never reach Ready.
        Assert.False(SetupService.IsPlaceholderConnectionString(
            "Server=(localdb)\\mssqllocaldb;Database=Conduit;Trusted_Connection=True;"));
    }

    // ── Machine binding ──────────────────────────────────────────────────────

    /// <summary>
    /// The server half of a marker is routinely HOST-RELATIVE — "localhost", ".",
    /// "(local)", "(localdb)\…" all name a different physical server depending on where
    /// they are read. So a marker copied to another box, or restored from a backup, matches
    /// there on text alone and authorizes an unattended schema build against a database
    /// nobody designated. Machine identity closes that; the failure direction is the wizard,
    /// which is correct.
    /// </summary>
    [Fact]
    public void A_marker_issued_on_ANOTHER_machine_must_NOT_migrate()
    {
        var decision = ProvisionedSchemaService.Decide(
            Configured, MarkerFrom("localhost", "conduit18", "some-other-host"), out _);

        // Specifically MachineMismatch, not a generic mismatch: the server and database DO
        // match, which is exactly why this case needs its own answer.
        Assert.Equal(ProvisionDecision.MachineMismatch, decision);
    }

    [Fact]
    public void The_same_marker_on_its_OWN_machine_still_migrates()
    {
        // Paired positive — without it, the test above would pass against a Decide() that
        // refused everything.
        Assert.Equal(ProvisionDecision.Migrate, ProvisionedSchemaService.Decide(
            Configured, MarkerFrom("localhost", "conduit18", ProvisionedConnectionMarker.CurrentMachineName), out _));
    }

    [Fact]
    public void Machine_identity_is_compared_case_insensitively_and_trimmed()
    {
        // Environment.MachineName casing is not stable across the ways it can be read;
        // a case difference must not lock an operator out of their own install.
        var shouted = ProvisionedConnectionMarker.CurrentMachineName.ToUpperInvariant();

        Assert.Equal(ProvisionDecision.Migrate, ProvisionedSchemaService.Decide(
            Configured, MarkerFrom("LOCALHOST", "Conduit18", "  " + shouted + "  "), out _));
    }

    [Fact]
    public void A_legacy_marker_with_no_machine_identity_fails_CLOSED()
    {
        // Markers written before machine binding cannot prove which host issued them.
        // Parse must reject them outright rather than treat the missing field as a wildcard.
        var legacy = ProvisionedConnectionMarker.Parse(new JsonObject
        {
            [ProvisionedConnectionMarker.SectionName] = new JsonObject
            {
                ["DataSource"] = "localhost",
                ["InitialCatalog"] = "conduit18"
            }
        });

        Assert.Null(legacy);
        Assert.Equal(ProvisionDecision.MarkerAbsent,
            ProvisionedSchemaService.Decide(Configured, legacy, out _));
    }

    [Fact]
    public void An_empty_machine_field_authorizes_nothing()
    {
        Assert.False(ProvisionedConnectionMarker.Matches(
            MarkerFrom("localhost", "conduit18", string.Empty),
            MarkerFrom("localhost", "conduit18", string.Empty)));
    }

    // ── Real-file round trip through the secret store ────────────────────────

    /// <summary>
    /// Apply/Parse exercise JSON in memory; this exercises the path that actually runs —
    /// RestrictedFileWriter, read-merge-rewrite, and removal — against a real file.
    /// </summary>
    [Fact]
    public void Write_then_Read_then_Remove_round_trips_through_a_real_secrets_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "conduit-marker-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "secrets.json");
        Directory.CreateDirectory(dir);

        try
        {
            // A pre-existing unrelated section must survive the merge.
            File.WriteAllText(path, "{ \"Jwt\": { \"SecretKey\": \"unrelated\" } }");

            ProvisionedConnectionMarker.Write(Configured, path);

            var read = ProvisionedConnectionMarker.Read(path);
            Assert.NotNull(read);
            Assert.Equal("localhost", read!.DataSource);
            Assert.Equal("conduit18", read.InitialCatalog);
            Assert.Equal(ProvisionedConnectionMarker.CurrentMachineName, read.MachineName);

            // What was written authorizes the connection it was written for, and nothing else.
            Assert.Equal(ProvisionDecision.Migrate, ProvisionedSchemaService.Decide(Configured, read, out _));
            Assert.Equal(ProvisionDecision.MarkerMismatch, ProvisionedSchemaService.Decide(
                "Server=localhost;Database=Payroll;Trusted_Connection=True;", read, out _));

            Assert.Equal("unrelated",
                System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["Jwt"]!["SecretKey"]!.GetValue<string>());

            ProvisionedConnectionMarker.Remove(path);
            Assert.Null(ProvisionedConnectionMarker.Read(path));

            // Removing the marker must not take the rest of the secret store with it.
            Assert.Equal("unrelated",
                System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["Jwt"]!["SecretKey"]!.GetValue<string>());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    [Fact]
    public void Write_refuses_a_connection_string_that_names_no_database()
    {
        var dir = Path.Combine(Path.GetTempPath(), "conduit-marker-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "secrets.json");
        Directory.CreateDirectory(dir);

        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                ProvisionedConnectionMarker.Write("Server=localhost;Trusted_Connection=True;", path));

            // A refused write leaves NO marker behind — a half-written pin is worse than none.
            Assert.Null(ProvisionedConnectionMarker.Read(path));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    // ── Zero-admin lockout: the status decision itself ───────────────────────

    /// <summary>
    /// Replaces a source-scanning guard that looked for a single line containing both
    /// "return" and "_setupCompleteFile" — which the equivalent code split across two lines
    /// walked straight past, so it only ever caught the exact literal that was already
    /// fixed. The decision now lives in a pure function and is asserted directly, on all
    /// four inputs, in AdminRecoveryTests. This test pins the property that motivated the
    /// original guard: the classifier CANNOT consult setup.complete, because it is not
    /// given anything with which to find it.
    /// </summary>
    [Fact]
    public void The_status_classifier_cannot_consult_the_setup_complete_marker_file()
    {
        var parameters = typeof(SetupService)
            .GetMethod(nameof(SetupService.ClassifyReachableDatabase))!
            .GetParameters();

        Assert.All(parameters, p => Assert.True(
            p.ParameterType == typeof(bool) || p.ParameterType == typeof(int),
            $"ClassifyReachableDatabase takes '{p.Name}' of type {p.ParameterType.Name}. It must take only the " +
            "database facts (bool/int) — anything that could reach the filesystem, configuration, or the " +
            "content root reopens the path where setup.complete rescued a zero-admin install into Ready."));

        // And the decision it makes for that exact state, stated once more here so a reader
        // of this file sees the invariant without chasing it.
        Assert.Equal(DatabaseStatus.NeedsAdminRecovery, SetupService.ClassifyReachableDatabase(
            schemaPresent: true, portalAdminsTableExists: true, activeAdmins: 0));
    }

    // ── Placeholder classification (continued) ───────────────────────────────

    [Theory]
    [InlineData("Server=YOUR_SERVER;Database=Conduit;")]
    [InlineData("Server=localhost;Database=YOUR_DATABASE;")]
    [InlineData("Server=localhost;Database=Conduit;User Id=YOUR_USER;Password=YOUR_PASSWORD;")]
    [InlineData("Server=localhost;Database=Conduit;Password=****;")]
    public void Template_text_is_still_a_placeholder(string connectionString)
    {
        Assert.True(SetupService.IsPlaceholderConnectionString(connectionString));
    }
}
