using System.Text.Json.Nodes;
using Conduit.DataAccess;
using Conduit.DataAccess.Repositories;
using Conduit.Web.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// Guards the zero-admin recovery path.
///
/// The state under test — reachable database, schema present, ZERO active portal admins —
/// is not exotic. The startup safety net MANUFACTURES it: it migrates a fresh database,
/// which by definition has no admins. Before this path existed, that state reported
/// NotConfigured and routed to the ANONYMOUS /setup wizard, so the wizard became reachable
/// on any install that had ever finished setup, and its admin step would resolve an
/// existing username with no Active filter and UPDATE it with Active = 1 — meaning an
/// anonymous visitor who typed a deactivated admin's name took that account over.
///
/// These tests exercise the real decision functions and the real token file. They assert
/// the REFUSAL REASON, not a bare false, so a guard cannot pass for the wrong cause. Every
/// negative test is paired with a positive one proving the same call CAN succeed — a
/// refusal test that would pass against a method hardcoded to "no" is worthless.
/// </summary>
public class AdminRecoveryTests : IDisposable
{
    private readonly string _tokenPath = Path.Combine(
        Path.GetTempPath(), "conduit-recovery-tests", Guid.NewGuid().ToString("N"), "recovery.token");

    public void Dispose()
    {
        try
        {
            var dir = Path.GetDirectoryName(_tokenPath);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* temp cleanup */ }
    }

    // ── The status decision: the widening this whole change closes ───────────

    [Fact]
    public void Schema_present_with_zero_active_admins_is_NOT_routed_to_the_anonymous_wizard()
    {
        var status = SetupService.ClassifyReachableDatabase(
            schemaPresent: true, portalAdminsTableExists: true, activeAdmins: 0);

        // The exact bug: this used to be NotConfigured, and NotConfigured routes to /setup.
        Assert.NotEqual(DatabaseStatus.NotConfigured, status);
        Assert.Equal(DatabaseStatus.NeedsAdminRecovery, status);
    }

    [Fact]
    public void Schema_present_with_an_active_admin_is_Ready()
    {
        Assert.Equal(DatabaseStatus.Ready, SetupService.ClassifyReachableDatabase(
            schemaPresent: true, portalAdminsTableExists: true, activeAdmins: 1));
    }

    [Fact]
    public void A_genuine_first_run_still_reaches_the_wizard()
    {
        // No schema at all — nothing to take over, nobody to lock out. The wizard is right.
        Assert.Equal(DatabaseStatus.NotConfigured, SetupService.ClassifyReachableDatabase(
            schemaPresent: false, portalAdminsTableExists: false, activeAdmins: 0));

        // Schema too old to carry PortalAdmins: also a first run.
        Assert.Equal(DatabaseStatus.NotConfigured, SetupService.ClassifyReachableDatabase(
            schemaPresent: true, portalAdminsTableExists: false, activeAdmins: 0));
    }

    // ── The first-run gate: a populated table is not a first run ─────────────

    [Fact]
    public void Setup_is_refused_when_PortalAdmins_holds_a_row_even_if_it_is_DEACTIVATED()
    {
        // One row, zero of them active — the whole point. The old gate counted only ACTIVE
        // admins, so an install whose admins had all been deactivated read as a clean first
        // run, and ApplySetupAsync rewrites the connection string, the JWT signing key and
        // the admin credentials from an anonymous page.
        Assert.Equal(SetupGateDecision.RefuseTablePopulated,
            SetupService.DecideSetupGate(portalAdminsTableExists: true, totalAdminRows: 1));
    }

    [Fact]
    public void Setup_is_refused_when_PortalAdmins_holds_many_rows()
    {
        Assert.Equal(SetupGateDecision.RefuseTablePopulated,
            SetupService.DecideSetupGate(portalAdminsTableExists: true, totalAdminRows: 7));
    }

    [Fact]
    public void Setup_is_ALLOWED_on_a_genuinely_empty_table_and_when_the_table_is_absent()
    {
        // Pairs with the refusals above: proves the gate is not simply hardcoded shut.
        Assert.Equal(SetupGateDecision.Allow,
            SetupService.DecideSetupGate(portalAdminsTableExists: true, totalAdminRows: 0));
        Assert.Equal(SetupGateDecision.Allow,
            SetupService.DecideSetupGate(portalAdminsTableExists: false, totalAdminRows: 0));
    }

    // ── Recovery admission: token first, insert-only ────────────────────────

    [Fact]
    public void Recovery_requires_a_valid_token()
    {
        Assert.Equal(RecoveryRefusal.InvalidToken, SetupService.DecideRecovery(
            tokenValid: false, DatabaseStatus.NeedsAdminRecovery, "newadmin", "correct horse", userNameExists: false));
    }

    [Fact]
    public void An_invalid_token_is_refused_BEFORE_anything_else_is_considered()
    {
        // Everything else about this request is perfect. The refusal must still be the
        // token, and specifically not a downstream reason — otherwise a caller without host
        // access learns whether a username is taken, or what state the install is in.
        var refusal = SetupService.DecideRecovery(
            tokenValid: false, DatabaseStatus.Ready, userName: "", password: "x", userNameExists: true);

        Assert.Equal(RecoveryRefusal.InvalidToken, refusal);
    }

    [Fact]
    public void A_valid_token_on_a_healthy_install_grants_nothing()
    {
        // The token is not a master key. It authorizes recovery only while the install
        // actually needs recovering.
        Assert.Equal(RecoveryRefusal.NotInRecoveryState, SetupService.DecideRecovery(
            tokenValid: true, DatabaseStatus.Ready, "newadmin", "correct horse", userNameExists: false));
    }

    [Fact]
    public void Recovery_NEVER_reactivates_an_existing_account_even_with_a_valid_token()
    {
        // userNameExists is deliberately "any row, active or not". This is the takeover
        // that used to succeed: name the deactivated admin, get their identity and a
        // password of your choosing. It must be a hard refusal, never an update.
        var refusal = SetupService.DecideRecovery(
            tokenValid: true, DatabaseStatus.NeedsAdminRecovery,
            "deactivated.admin", "correct horse", userNameExists: true);

        Assert.Equal(RecoveryRefusal.UserNameTaken, refusal);
    }

    [Fact]
    public void Recovery_is_ADMITTED_for_a_fresh_username_with_a_valid_token()
    {
        // The paired positive. Without it, every refusal test above would still pass
        // against a DecideRecovery that returned a refusal unconditionally.
        Assert.Equal(RecoveryRefusal.None, SetupService.DecideRecovery(
            tokenValid: true, DatabaseStatus.NeedsAdminRecovery,
            "newadmin", "correct horse", userNameExists: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Recovery_requires_a_username(string? userName)
    {
        Assert.Equal(RecoveryRefusal.UserNameRequired, SetupService.DecideRecovery(
            tokenValid: true, DatabaseStatus.NeedsAdminRecovery, userName, "correct horse", userNameExists: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("1234567")]
    public void Recovery_requires_a_password_of_at_least_eight_characters(string password)
    {
        Assert.Equal(RecoveryRefusal.PasswordTooShort, SetupService.DecideRecovery(
            tokenValid: true, DatabaseStatus.NeedsAdminRecovery, "newadmin", password, userNameExists: false));
    }

    // ── The token file itself ───────────────────────────────────────────────

    [Fact]
    public void An_absent_token_file_validates_nothing()
    {
        Assert.False(AdminRecoveryToken.IsOutstanding(_tokenPath));

        // Including the empty string and a plausible-looking guess.
        Assert.False(AdminRecoveryToken.Validate("", _tokenPath));
        Assert.False(AdminRecoveryToken.Validate("any-token-at-all", _tokenPath));
    }

    [Fact]
    public void An_issued_token_validates_only_its_own_exact_value()
    {
        AdminRecoveryToken.EnsureIssued(_tokenPath);
        var issued = ReadIssuedToken();

        Assert.True(AdminRecoveryToken.Validate(issued, _tokenPath));

        // Paired negatives against the SAME outstanding token, so this cannot pass by the
        // file being absent.
        Assert.False(AdminRecoveryToken.Validate(issued + "x", _tokenPath));
        Assert.False(AdminRecoveryToken.Validate(issued[..^1], _tokenPath));
        Assert.False(AdminRecoveryToken.Validate(issued.ToUpperInvariant() + "!", _tokenPath));
        Assert.False(AdminRecoveryToken.Validate("", _tokenPath));
        Assert.False(AdminRecoveryToken.Validate(null, _tokenPath));
    }

    [Fact]
    public void Issuing_is_idempotent_so_a_probe_loop_cannot_rotate_the_token_under_the_operator()
    {
        // The detection point is the status probe, which runs on a 5s cache. Re-issuing on
        // every probe would invalidate the token between the operator opening the file and
        // pasting the value.
        AdminRecoveryToken.EnsureIssued(_tokenPath);
        var first = ReadIssuedToken();

        AdminRecoveryToken.EnsureIssued(_tokenPath);
        AdminRecoveryToken.EnsureIssued(_tokenPath);

        Assert.Equal(first, ReadIssuedToken());
        Assert.True(AdminRecoveryToken.Validate(first, _tokenPath));
    }

    [Fact]
    public void A_consumed_token_stops_validating_and_a_reissue_is_a_different_token()
    {
        AdminRecoveryToken.EnsureIssued(_tokenPath);
        var first = ReadIssuedToken();
        Assert.True(AdminRecoveryToken.Validate(first, _tokenPath));

        // The return value is what tells the Ready probe whether it actually retired
        // anything, and therefore whether its log line is true.
        Assert.True(AdminRecoveryToken.Consume(_tokenPath));
        Assert.False(AdminRecoveryToken.Consume(_tokenPath));

        Assert.False(AdminRecoveryToken.IsOutstanding(_tokenPath));
        Assert.False(AdminRecoveryToken.Validate(first, _tokenPath));

        // One token authorizes exactly one recovery.
        AdminRecoveryToken.EnsureIssued(_tokenPath);
        Assert.NotEqual(first, ReadIssuedToken());
        Assert.False(AdminRecoveryToken.Validate(first, _tokenPath));
    }

    [Fact]
    public void An_issued_token_carries_enough_entropy_to_be_unguessable()
    {
        // 32 random bytes, base64url. Guarding the length guards the property the design
        // rests on for BRUTE FORCE specifically — and only that. It says nothing about the
        // cost of an unmetered attempt, which is bounded separately by the per-circuit
        // budget below.
        AdminRecoveryToken.EnsureIssued(_tokenPath);
        var issued = ReadIssuedToken();

        Assert.True(issued.Length >= 43, $"Recovery token is only {issued.Length} chars — expected >= 43 (32 bytes, base64url).");
    }

    [Fact]
    public void A_malformed_token_file_validates_nothing_rather_than_throwing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_tokenPath)!);
        File.WriteAllText(_tokenPath, "this is not json");

        Assert.False(AdminRecoveryToken.Validate("anything", _tokenPath));
    }

    // ── Token expiry ────────────────────────────────────────────────────────

    [Fact]
    public void The_TTL_boundary_is_the_lifetime_exactly()
    {
        var issued = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.False(AdminRecoveryToken.IsExpired(issued, issued));
        Assert.False(AdminRecoveryToken.IsExpired(issued, issued + AdminRecoveryToken.Lifetime - TimeSpan.FromSeconds(1)));
        Assert.True(AdminRecoveryToken.IsExpired(issued, issued + AdminRecoveryToken.Lifetime));
        Assert.True(AdminRecoveryToken.IsExpired(issued, issued + TimeSpan.FromDays(365)));
    }

    [Fact]
    public void An_EXPIRED_token_authorizes_nothing()
    {
        // The gap consumption alone did not close: an operator who recovered some OTHER way
        // — a restore, a fresh database, a repoint — never consumes the token, so it sat on
        // disk silently authorizing the NEXT zero-admin event, and rode along in every backup
        // of the data directory taken since.
        AdminRecoveryToken.EnsureIssued(_tokenPath);
        var original = ReadIssuedToken();
        Assert.True(AdminRecoveryToken.Validate(original, _tokenPath)); // paired positive

        BackdateIssuedAt(AdminRecoveryToken.Lifetime + TimeSpan.FromMinutes(1));

        Assert.False(AdminRecoveryToken.Validate(original, _tokenPath));
        Assert.False(AdminRecoveryToken.IsOutstanding(_tokenPath));
    }

    [Fact]
    public void Expiry_does_not_brick_recovery_the_next_detection_issues_a_live_token()
    {
        // A TTL that could permanently lock an operator out of their own install would be a
        // worse bug than the one it fixes. The status probe re-issues while the zero-admin
        // state persists, which requires EnsureIssued to REPLACE a dead file rather than
        // treat its mere existence as "one is outstanding."
        AdminRecoveryToken.EnsureIssued(_tokenPath);
        var original = ReadIssuedToken();
        BackdateIssuedAt(AdminRecoveryToken.Lifetime + TimeSpan.FromMinutes(1));

        AdminRecoveryToken.EnsureIssued(_tokenPath);

        var replacement = ReadIssuedToken();
        Assert.NotEqual(original, replacement);
        Assert.True(AdminRecoveryToken.Validate(replacement, _tokenPath));
        Assert.False(AdminRecoveryToken.Validate(original, _tokenPath));
    }

    [Fact]
    public void A_token_file_with_no_usable_issue_time_cannot_be_aged_and_authorizes_nothing()
    {
        // Fail closed. An undated or unparseable token is one the TTL cannot be applied to,
        // which is exactly the shape a hand-edited file would take to sidestep expiry.
        Directory.CreateDirectory(Path.GetDirectoryName(_tokenPath)!);
        File.WriteAllText(_tokenPath, new JsonObject { ["Token"] = "undated-token-value" }.ToJsonString());

        Assert.False(AdminRecoveryToken.Validate("undated-token-value", _tokenPath));
        Assert.False(AdminRecoveryToken.IsOutstanding(_tokenPath));
    }

    // ── The first-run gate's I/O precondition ───────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Server=YOUR_SERVER;Database=YOUR_DATABASE;User Id=YOUR_USER;Password=YOUR_PASSWORD")]
    public async Task A_fresh_install_reaches_the_ALLOW_decision_without_opening_a_connection(string? connectionString)
    {
        // THE REGRESSION THIS EXISTS FOR. appsettings.json ships DefaultConnection as "", and
        // this gate is the first thing on the setup path that touches SQL — IsSetupRequiredAsync
        // short-circuits on the empty string and never opens a connection. Querying here means
        // new SqlConnection("").Open(), which throws InvalidOperationException, and it threw
        // outside ApplySetupAsync's try block: it killed the wizard's Blazor circuit for every
        // legitimate first-run operator, and it aborted startup on an installer-provisioned
        // host. Asserting the DECISION is not enough — the reader must not run at all.
        var readerRan = false;
        Func<Task<(bool TableExists, int TotalAdminRows)>> reader = () =>
        {
            readerRan = true;
            throw new InvalidOperationException("The ConnectionString property has not been initialized.");
        };

        var decision = await SetupService.EvaluateSetupGateAsync(connectionString, reader);

        Assert.Equal(SetupGateDecision.Allow, decision);
        Assert.False(readerRan, "The gate queried the database on an install that has no database configured.");
    }

    [Fact]
    public async Task A_CONFIGURED_install_still_consults_the_database_and_refuses_a_populated_table()
    {
        // The paired positive. Without it the test above would pass against a gate that
        // never queried at all, which would hand the anonymous wizard back to every install.
        var readerRan = false;
        var decision = await SetupService.EvaluateSetupGateAsync(
            "Server=sql01;Database=Conduit;Integrated Security=True",
            () => { readerRan = true; return Task.FromResult((true, 1)); });

        Assert.True(readerRan, "The gate skipped the database on an install that HAS one configured.");
        Assert.Equal(SetupGateDecision.RefuseTablePopulated, decision);
    }

    [Fact]
    public async Task A_configured_install_with_a_genuinely_empty_table_is_allowed()
    {
        var decision = await SetupService.EvaluateSetupGateAsync(
            "Server=sql01;Database=Conduit;Integrated Security=True",
            () => Task.FromResult((true, 0)));

        Assert.Equal(SetupGateDecision.Allow, decision);
    }

    [Fact]
    public async Task An_UNREACHABLE_configured_database_is_NEVER_treated_as_a_fresh_install()
    {
        // The distinction the whole fix rests on: "no database was ever configured" allows,
        // "the configured database did not answer" does not. Widening this to
        // "unreachable ⇒ allow" would let anyone who can knock the SQL host offline re-open
        // the anonymous wizard against a live install — the exact property GetDatabaseStatusAsync
        // protects by refusing Unreachable. The failure must propagate, not become a pass.
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await SetupService.EvaluateSetupGateAsync(
                "Server=sql01;Database=Conduit;Integrated Security=True",
                () => throw new TimeoutException("Connection Timeout Expired.")));
    }

    [Fact]
    public void Only_an_unnamed_database_counts_as_having_no_history()
    {
        Assert.False(SetupService.CouldHoldAdminHistory(null));
        Assert.False(SetupService.CouldHoldAdminHistory(""));
        Assert.False(SetupService.CouldHoldAdminHistory("   "));
        Assert.False(SetupService.CouldHoldAdminHistory("Server=YOUR_SERVER;Database=Conduit"));
        Assert.False(SetupService.CouldHoldAdminHistory("Server=sql01;Database=YOUR_DATABASE"));

        // A real string — including LocalDB, which the wizard generates for its default
        // option and which must never be mistaken for a placeholder.
        Assert.True(SetupService.CouldHoldAdminHistory("Server=sql01;Database=Conduit;Integrated Security=True"));
        Assert.True(SetupService.CouldHoldAdminHistory(@"Server=(localdb)\mssqllocaldb;Database=Conduit;Trusted_Connection=True"));

        // No database named: the connection lands on the login's DEFAULT database, which can
        // absolutely hold an admin history. Absent is not the same as template.
        Assert.True(SetupService.CouldHoldAdminHistory("Server=sql01;Integrated Security=True"));
    }

    [Theory]
    [InlineData("Server=sql01;Database=Conduit;User Id=conduit;Password=Xy**7q")]
    [InlineData("Server=sql01;Database=Conduit;User Id=conduit;Password=a**b**c")]
    public void A_PASSWORD_containing_asterisks_does_not_make_a_live_install_look_unconfigured(string connectionString)
    {
        // THE TAKEOVER THIS PREVENTS. IsPlaceholderConnectionString is a substring match over
        // the WHOLE RAW STRING and treats "**" as template text — correct for the display and
        // routing decisions it was written for, catastrophic as an authorization input. Using
        // it here meant a real, working, POPULATED install whose SQL password happened to
        // contain two adjacent asterisks classified as "no database configured", the setup
        // gate skipped its query, and the anonymous wizard could rewrite that install's
        // connection string, JWT signing key and admin credentials.
        //
        // A password cannot appear in DataSource or InitialCatalog, so the decision is made
        // on those parsed fields only.
        Assert.True(SetupService.CouldHoldAdminHistory(connectionString));

        // And the gate that depends on it must therefore still consult the database.
        Assert.True(SetupService.IsPlaceholderConnectionString(connectionString),
            "Precondition: the raw-string heuristic really does misclassify this — that is the trap being avoided.");
    }

    [Fact]
    public async Task The_setup_gate_still_refuses_a_populated_install_whose_password_contains_asterisks()
    {
        // End to end through the gate, not just the predicate: this is the exact input that
        // would otherwise have reached Allow without a query on a live install.
        var readerRan = false;
        var decision = await SetupService.EvaluateSetupGateAsync(
            "Server=sql01;Database=Conduit;User Id=conduit;Password=Xy**7q",
            () => { readerRan = true; return Task.FromResult((true, 3)); });

        Assert.True(readerRan, "The gate skipped the query on a live install because of its PASSWORD.");
        Assert.Equal(SetupGateDecision.RefuseTablePopulated, decision);
    }

    [Fact]
    public void An_UNPARSEABLE_connection_string_fails_closed()
    {
        // Broken configuration, not a first run. It must claim it could hold a history so the
        // gate queries, rather than becoming a free pass.
        const string malformed = "Server=sql01;Not A Real Keyword=x";

        // Precondition, asserted rather than assumed: this really does fail to parse, so the
        // check below is exercising the catch branch and not the happy path. Without this the
        // test would pass identically against a string that parsed fine.
        Assert.ThrowsAny<Exception>(() => new SqlConnectionStringBuilder(malformed));

        Assert.True(SetupService.CouldHoldAdminHistory(malformed));
    }

    // ── The per-circuit attempt budget ──────────────────────────────────────

    [Fact]
    public async Task A_circuit_gets_a_bounded_number_of_recovery_attempts()
    {
        // RecoverAdminAsync is invoked over the Blazor SignalR hub, and hub invocations are
        // not HTTP requests, so the rate limiters in Program.cs — which partition HttpContext
        // — never see one. Unbounded, each invocation is a blocking token-file read plus a
        // log line: a disk-fill and a log-flood that buries the line naming the token file.
        var service = NewService();

        // Inside the budget, every attempt is judged on its merits.
        for (var i = 0; i < SetupService.MaxRecoveryAttemptsPerCircuit; i++)
        {
            var (ok, message) = await service.RecoverAdminAsync("not-the-token", "newadmin", "correct horse");
            Assert.False(ok);
            Assert.Contains("recovery token is not valid", message);
        }

        // Past it, refused before the token file is read at all — a different reason, not a
        // louder version of the same one.
        var (stillOk, capped) = await service.RecoverAdminAsync("not-the-token", "newadmin", "correct horse");
        Assert.False(stillOk);
        Assert.Contains("Too many recovery attempts", capped);
    }

    [Fact]
    public async Task The_budget_is_PER_CIRCUIT_and_does_not_latch_the_install_shut()
    {
        // A cap that outlived the circuit would be a denial of service against the operator:
        // one hostile visitor could spend the budget and nobody could ever recover the
        // install. A fresh circuit gets a fresh budget.
        var spent = NewService();
        for (var i = 0; i <= SetupService.MaxRecoveryAttemptsPerCircuit; i++)
        {
            await spent.RecoverAdminAsync("not-the-token", "newadmin", "correct horse");
        }
        Assert.Contains("Too many recovery attempts",
            (await spent.RecoverAdminAsync("not-the-token", "newadmin", "correct horse")).Message);

        var fresh = NewService();
        Assert.Contains("recovery token is not valid",
            (await fresh.RecoverAdminAsync("not-the-token", "newadmin", "correct horse")).Message);
    }

    [Fact]
    public void The_budget_decision_admits_up_to_the_cap_and_refuses_beyond_it()
    {
        Assert.True(SetupService.IsRecoveryAttemptAllowed(0));
        Assert.True(SetupService.IsRecoveryAttemptAllowed(SetupService.MaxRecoveryAttemptsPerCircuit - 1));
        Assert.False(SetupService.IsRecoveryAttemptAllowed(SetupService.MaxRecoveryAttemptsPerCircuit));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A real SetupService with no database behind it. Safe because an invalid token is
    /// refused before RecoverAdminAsync consults the database at all — which is itself part
    /// of the contract (a caller without host access learns nothing about the install).
    /// </summary>
    private SetupService NewService() => new(
        new ConfigurationBuilder().Build(),
        NullLogger<SetupService>.Instance,
        new DatabaseConfig(),
        new SetupRepository(new DatabaseConfig()),
        new FakeHostEnvironment())
    {
        RecoveryTokenPathOverride = _tokenPath
    };

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Conduit.Web.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private void BackdateIssuedAt(TimeSpan age)
    {
        var root = JsonNode.Parse(File.ReadAllText(_tokenPath))!.AsObject();
        root["IssuedAtUtc"] = DateTime.UtcNow.Subtract(age).ToString("O");
        File.WriteAllText(_tokenPath, root.ToJsonString());
    }

    private string ReadIssuedToken() =>
        JsonNode.Parse(File.ReadAllText(_tokenPath))!["Token"]!.GetValue<string>();
}
