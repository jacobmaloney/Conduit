using Conduit.Connectors.IdentityCenter;
using Conduit.Core.Models;
using Conduit.Core.SyncModels;
using Conduit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// ZERO-TOUCH-01 (UC038, Jacob 2026-11-09: "we make a key in identity center for conduit, make the connection, and
/// that's it"), the Conduit half: an IdentityCenter definition is read as IdentityCenter writes it and becomes a local
/// project bound to it (created on the first claim, rebuilt when the fingerprint changes, a hand-bound project left
/// alone); the heartbeat announces who this installation is and the connected systems it serves, never a credential;
/// objects pushed to an IdentityCenter carry the agent it enrolled this installation as.
/// </summary>
public class IcProjectMirrorTests
{
    private static readonly Guid IcProjectId = Guid.Parse("7c3d04cb-6cbb-4788-8cf0-5e04d7b2d584");
    private const string BaseUrl = "http://192.168.1.60:8080";

    private static IcProjectDefinition Definition(string fingerprint = "f1", string? connectionName = "Domain.local", string filter = "(objectClass=user)") =>
        new(IcProjectId, "Domain.local by container", "Reads the lab domain", "ObjectSync",
            connectionName is null ? null : new IcProjectConnection(Guid.NewGuid(), connectionName, "ActiveDirectory", "Conduit", Guid.NewGuid()),
            new[]
            {
                new IcProjectWorkflow("Users", null, "user", "FullSync", 1, true, new[]
                {
                    new IcProjectStep("Users", 1, "user", "Upsert", filter, null, new[] { "OU=People,DC=example,DC=com" }, new[] { "OU=Old,OU=People,DC=example,DC=com" }, "Subtree", 500, true, new[]
                    {
                        new IcProjectMapping("givenName", "FirstName", "IdentityColumn", "Direct", null, true, 1),
                        new IcProjectMapping("mail", "Email", "IdentityColumn", "Lower", null, false, 2),
                        new IcProjectMapping("dn", "ManagerDn", "IdentityColumn", "DNLookup", "lookup(manager)", false, 3),
                    }),
                    new IcProjectStep("Managers", 2, "user", "Lookup", null, null, Array.Empty<string>(), Array.Empty<string>(), "Subtree", 1000, false, Array.Empty<IcProjectMapping>()),
                }),
                new IcProjectWorkflow("Groups", "Security groups", "group", "FullSync", 2, true, Array.Empty<IcProjectStep>()),
            }, fingerprint);

    // ── The graph ───────────────────────────────────────────────────────────

    [Fact]
    public void The_definition_becomes_Conduit_workflows_steps_scopes_and_mappings_in_IdentityCenters_order()
    {
        var projectId = Guid.NewGuid();
        var rows = IcProjectGraph.Build(projectId, Definition());

        Assert.Equal(new[] { "Users", "Groups" }, rows.Select(r => r.Workflow.Name));
        Assert.Equal(new[] { 0, 1 }, rows.Select(r => r.Workflow.Ordinal));
        Assert.All(rows, r => Assert.Equal(projectId, r.Workflow.SyncProjectId));

        var users = rows[0].Steps;
        Assert.Equal(new[] { WorkflowStepTypes.Mapping, WorkflowStepTypes.Lookup }, users.Select(s => s.Step.StepType));
        Assert.Equal(new[] { true, false }, users.Select(s => s.Step.Enabled));
        var scope = users[0].Scope;
        Assert.Equal(projectId, scope.SyncProjectId);
        Assert.Equal(users[0].Step.Id, scope.WorkflowStepId);
        Assert.Equal("(objectClass=user)", scope.LdapFilter);
        Assert.Equal("OU=People,DC=example,DC=com", scope.BaseDN);
        Assert.Equal(new[] { "OU=People,DC=example,DC=com" }, scope.GetIncludedBaseList());
        Assert.Equal(new[] { "OU=Old,OU=People,DC=example,DC=com" }, scope.GetExcludedBaseList());
        Assert.Equal(500, scope.PageSize);

        var mappings = users[0].Mappings;
        Assert.Equal(new[] { ("givenName", "FirstName", (string?)null, true), ("mail", "Email", "Lower", false), ("dn", "ManagerDn", "lookup(manager)", false) },
            mappings.Select(m => (m.SourceAttribute, m.SinkAttribute, m.TransformExpr, m.IsRequired)));
        Assert.All(mappings, m => { Assert.Equal(projectId, m.SyncProjectId); Assert.Equal(users[0].Step.Id, m.WorkflowStepId); });

        Assert.Equal("user", IcProjectGraph.ProjectObjectClass(Definition()));
        Assert.Equal("Custom", IcProjectGraph.MapStepType("Custom"));
        Assert.Equal(WorkflowStepTypes.Mapping, IcProjectGraph.MapStepType(null));
    }

    // ── The mirror's decisions ──────────────────────────────────────────────

    private sealed class FakeStore : IIcProjectMirrorStore
    {
        public Tenant? Source { get; set; } = new() { Id = Guid.NewGuid(), Name = "Domain.local", SystemType = "ActiveDirectory", IsActive = true };
        public Tenant? Sink { get; set; } = new() { Id = Guid.NewGuid(), Name = "Identity Center", SystemType = "IdentityCenter", IsActive = true };
        public bool BindingHeld { get; set; }
        public string? AskedFor { get; private set; }
        public string? AskedBaseUrl { get; private set; }
        public SyncProject? Created { get; private set; }
        public SyncProject? Updated { get; private set; }
        public (Guid ProjectId, Guid IcProjectId, string Fingerprint)? Mirror { get; private set; }
        public IReadOnlyList<IcProjectGraph.WorkflowRows>? Workflows { get; private set; }
        public int ReplaceCalls { get; private set; }

        public Task<Tenant?> FindConnectedSystemAsync(string connectionName) { AskedFor = connectionName; return Task.FromResult(Source); }
        public Task<Tenant?> FindIdentityCenterSystemAsync(string baseUrl) { AskedBaseUrl = baseUrl; return Task.FromResult(Sink); }
        public Task<SyncProject> CreateProjectAsync(SyncProject project) { Created = project; return Task.FromResult(project); }
        public Task<bool> UpdateProjectAsync(SyncProject project) { Updated = project; return Task.FromResult(true); }
        public Task<bool> SetMirrorAsync(Guid projectId, Guid identityCenterProjectId, string fingerprint)
        {
            if (BindingHeld) return Task.FromResult(false);
            Mirror = (projectId, identityCenterProjectId, fingerprint);
            return Task.FromResult(true);
        }
        public Task ReplaceWorkflowsAsync(Guid projectId, IReadOnlyList<IcProjectGraph.WorkflowRows> workflows) { ReplaceCalls++; Workflows = workflows; return Task.CompletedTask; }
        public List<SyncProject> Others { get; } = new();
        public Task<SyncProject?> FindOverlappingProjectAsync(Guid sourceTenantId, Guid sinkTenantId, string? objectClass, Guid? exceptProjectId) =>
            Task.FromResult(Others.FirstOrDefault(p => IcProjectMirror.Overlaps(p, sourceTenantId, sinkTenantId, objectClass, exceptProjectId)));
    }

    private static IcProjectMirror Mirror(FakeStore store) => new(store, NullLogger<IcProjectMirror>.Instance);

    [Fact]
    public async Task The_first_claim_creates_a_bound_project_from_the_connected_system_the_connection_is_named_after()
    {
        var store = new FakeStore();
        var outcome = await Mirror(store).EnsureAsync(BaseUrl, Definition(), existing: null, CancellationToken.None);

        Assert.Equal(IcProjectMirrorOutcome.Created, outcome.Action);
        Assert.Null(outcome.Refusal);
        var project = outcome.Project!;
        Assert.Same(store.Created, project);
        Assert.Equal("Domain.local", store.AskedFor);
        Assert.Equal(BaseUrl, store.AskedBaseUrl);
        Assert.Equal(store.Source!.Id, project.SourceTenantId);
        Assert.Equal(store.Sink!.Id, project.SinkTenantId);
        Assert.Equal("Domain.local by container", project.Name);
        Assert.StartsWith("Defined in IdentityCenter", project.Description);
        Assert.Equal("user", project.ObjectClass);
        Assert.True(project.IsEnabled);
        Assert.Null(project.CronSchedule);                 // IdentityCenter schedules it
        Assert.Equal((project.Id, IcProjectId, "f1"), store.Mirror);
        Assert.Equal(IcProjectId, project.IdentityCenterProjectId);
        Assert.Equal("f1", project.IdentityCenterDefinitionFingerprint);
        Assert.Equal(2, store.Workflows!.Count);
        Assert.Equal(1, store.ReplaceCalls);
    }

    [Fact]
    public async Task An_unchanged_fingerprint_touches_nothing_and_a_changed_one_rebuilds_the_tree_and_the_header()
    {
        var store = new FakeStore();
        var existing = new SyncProject { Id = Guid.NewGuid(), Name = "Old name", IdentityCenterProjectId = IcProjectId, IdentityCenterDefinitionFingerprint = "f1", ObjectClass = "group" };

        var same = await Mirror(store).EnsureAsync(BaseUrl, Definition("f1"), existing, CancellationToken.None);
        Assert.Equal(IcProjectMirrorOutcome.Unchanged, same.Action);
        Assert.Same(existing, same.Project);
        Assert.Equal(0, store.ReplaceCalls);
        Assert.Null(store.Updated);

        var changed = await Mirror(store).EnsureAsync(BaseUrl, Definition("f2", filter: "(department=Plant)"), existing, CancellationToken.None);
        Assert.Equal(IcProjectMirrorOutcome.Refreshed, changed.Action);
        Assert.Same(existing, changed.Project);
        Assert.Equal("Domain.local by container", existing.Name);
        Assert.Equal("user", existing.ObjectClass);
        Assert.Equal("f2", existing.IdentityCenterDefinitionFingerprint);
        Assert.Same(existing, store.Updated);
        Assert.Equal(1, store.ReplaceCalls);
        Assert.Equal("(department=Plant)", store.Workflows![0].Steps[0].Scope.LdapFilter);
        Assert.Equal((existing.Id, IcProjectId, "f2"), store.Mirror);
        Assert.Null(store.Created);                        // never a second project
        Assert.Null(store.AskedFor);                       // the source is the project's own, not looked up again
    }

    [Fact]
    public async Task A_project_bound_by_hand_runs_as_it_is_and_a_running_mirror_is_not_rebuilt_under_its_run()
    {
        var store = new FakeStore();
        var handBound = new SyncProject { Id = Guid.NewGuid(), Name = "Domain.local to Identity Center (Core)", IdentityCenterProjectId = IcProjectId, IdentityCenterDefinitionFingerprint = null };
        var outcome = await Mirror(store).EnsureAsync(BaseUrl, Definition("f9"), handBound, CancellationToken.None);
        Assert.Equal(IcProjectMirrorOutcome.HandBound, outcome.Action);
        Assert.Same(handBound, outcome.Project);
        Assert.Equal(0, store.ReplaceCalls);
        Assert.Null(store.Mirror);

        var running = new SyncProject { Id = Guid.NewGuid(), Name = "Mirror", IdentityCenterProjectId = IcProjectId, IdentityCenterDefinitionFingerprint = "f1", IsRunning = true };
        var busy = await Mirror(store).EnsureAsync(BaseUrl, Definition("f2"), running, CancellationToken.None);
        Assert.Equal(IcProjectMirrorOutcome.Unchanged, busy.Action);
        Assert.Equal("f1", running.IdentityCenterDefinitionFingerprint);
        Assert.Equal(0, store.ReplaceCalls);
    }

    // ── MIRROR-OVERLAP-01 (UC038-F07): never beside an enabled project writing the same objects ─────────────────

    [Fact]
    public async Task An_enabled_project_over_the_same_source_sink_and_class_refuses_the_mirror_by_name_and_creates_nothing()
    {
        var store = new FakeStore();
        var core = new SyncProject { Id = Guid.NewGuid(), Name = "Domain.local to Identity Center (Core)", SourceTenantId = store.Source!.Id, SinkTenantId = store.Sink!.Id, ObjectClass = "user", IsEnabled = true, CronSchedule = "0 * * * *" };
        store.Others.Add(core);

        var outcome = await Mirror(store).EnsureAsync(BaseUrl, Definition(), existing: null, CancellationToken.None);

        Assert.Equal("Refused", outcome.Action);
        Assert.Null(outcome.Project);
        Assert.Contains("'Domain.local to Identity Center (Core)'", outcome.Refusal);
        Assert.Contains(core.Id.ToString(), outcome.Refusal);
        Assert.Contains("Disable it in Schedules", outcome.Refusal);
        Assert.Null(store.Created);
        Assert.Equal(0, store.ReplaceCalls);
        Assert.Null(store.Mirror);
    }

    [Fact]
    public async Task An_existing_mirror_is_refused_too_and_not_rebuilt_while_the_rival_is_enabled()
    {
        var store = new FakeStore();
        var mirror = new SyncProject { Id = Guid.NewGuid(), Name = "Domain.local by container", SourceTenantId = store.Source!.Id, SinkTenantId = store.Sink!.Id, ObjectClass = "user",
            IsEnabled = true, IdentityCenterProjectId = IcProjectId, IdentityCenterDefinitionFingerprint = "f1" };
        store.Others.Add(mirror);   // the mirror itself never counts against itself
        store.Others.Add(new SyncProject { Id = Guid.NewGuid(), Name = "Core", SourceTenantId = store.Source.Id, SinkTenantId = store.Sink.Id, ObjectClass = null, IsEnabled = true });

        var outcome = await Mirror(store).EnsureAsync(BaseUrl, Definition("f2"), mirror, CancellationToken.None);

        Assert.Equal("Refused", outcome.Action);
        Assert.Contains("'Core'", outcome.Refusal);
        Assert.Equal("f1", mirror.IdentityCenterDefinitionFingerprint);
        Assert.Equal(0, store.ReplaceCalls);
    }

    [Fact]
    public async Task A_paused_project_another_source_another_class_or_another_mirror_lets_the_mirror_run()
    {
        var store = new FakeStore();
        var src = store.Source!.Id;
        var sink = store.Sink!.Id;
        store.Others.Add(new SyncProject { Id = Guid.NewGuid(), Name = "Paused core", SourceTenantId = src, SinkTenantId = sink, ObjectClass = "user", IsEnabled = false, CronSchedule = "0 * * * *" });
        store.Others.Add(new SyncProject { Id = Guid.NewGuid(), Name = "Granite HR", SourceTenantId = Guid.NewGuid(), SinkTenantId = sink, ObjectClass = "user", IsEnabled = true });
        store.Others.Add(new SyncProject { Id = Guid.NewGuid(), Name = "Groups", SourceTenantId = src, SinkTenantId = sink, ObjectClass = "group", IsEnabled = true });
        store.Others.Add(new SyncProject { Id = Guid.NewGuid(), Name = "Another mirror", SourceTenantId = src, SinkTenantId = sink, ObjectClass = "user", IsEnabled = true, IdentityCenterDefinitionFingerprint = "zz" });

        var outcome = await Mirror(store).EnsureAsync(BaseUrl, Definition(), existing: null, CancellationToken.None);

        Assert.Equal(IcProjectMirrorOutcome.Created, outcome.Action);
        Assert.Null(outcome.Refusal);
    }

    [Fact]
    public async Task A_project_bound_by_hand_is_the_operators_own_and_is_not_held_to_the_overlap_rule()
    {
        var store = new FakeStore();
        var handBound = new SyncProject { Id = Guid.NewGuid(), Name = "Hand", SourceTenantId = store.Source!.Id, SinkTenantId = store.Sink!.Id, ObjectClass = "user", IsEnabled = true, IdentityCenterProjectId = IcProjectId };
        store.Others.Add(new SyncProject { Id = Guid.NewGuid(), Name = "Other", SourceTenantId = store.Source.Id, SinkTenantId = store.Sink.Id, ObjectClass = "user", IsEnabled = true });

        var outcome = await Mirror(store).EnsureAsync(BaseUrl, Definition(), handBound, CancellationToken.None);

        Assert.Equal(IcProjectMirrorOutcome.HandBound, outcome.Action);
    }

    [Fact]
    public async Task Each_refusal_names_what_is_missing_and_creates_nothing()
    {
        var noConnection = await Mirror(new FakeStore()).EnsureAsync(BaseUrl, Definition(connectionName: null), null, CancellationToken.None);
        Assert.Equal(IcProjectMirror.NoSourceConnectionReason(Definition(connectionName: null)), noConnection.Refusal);

        var noSource = new FakeStore { Source = null };
        var missingSystem = await Mirror(noSource).EnsureAsync(BaseUrl, Definition(connectionName: "Plant AD"), null, CancellationToken.None);
        Assert.Equal(IcProjectMirror.NoConnectedSystemReason(Definition(connectionName: "Plant AD"), "Plant AD"), missingSystem.Refusal);
        Assert.Contains("no active connected system of that name", missingSystem.Refusal);
        Assert.Null(noSource.Created);

        var noSink = new FakeStore { Sink = null };
        var missingSink = await Mirror(noSink).EnsureAsync(BaseUrl, Definition(), null, CancellationToken.None);
        Assert.Equal(IcProjectMirror.NoIdentityCenterSystemReason(BaseUrl), missingSink.Refusal);
        Assert.Null(noSink.Created);

        var held = new FakeStore { BindingHeld = true };
        var taken = await Mirror(held).EnsureAsync(BaseUrl, Definition(), null, CancellationToken.None);
        Assert.Equal(IcProjectMirror.BindingHeldReason(Definition()), taken.Refusal);
        Assert.Null(taken.Project);
    }

    // ── The definition as IdentityCenter writes it ──────────────────────────

    [Fact]
    public void The_definition_IdentityCenter_writes_is_read_back_and_anything_else_is_not_one()
    {
        const string json = """
        {"projectId":"7c3d04cb-6cbb-4788-8cf0-5e04d7b2d584","name":"Domain.local by container","description":null,"projectType":"ObjectSync",
         "sourceConnection":{"id":"1ee0c1a0-8ecb-4b7c-b4e5-3a3b76430453","name":"Domain.local","providerType":"ActiveDirectory","transport":"Conduit","agentId":"917d8262-fe30-4fbf-9045-b183b46e6d6d"},
         "workflows":[{"name":"Users","description":null,"objectClass":"user","workflowType":"FullSync","executionOrder":1,"isEnabled":true,
           "steps":[{"name":"Users","executionOrder":1,"objectClass":"user","stepType":"Upsert","ldapFilter":"(objectClass=user)","searchBase":null,
                     "searchBases":[" OU=People,DC=example,DC=com "],"excludedSearchBases":[],"searchScope":"Subtree","ldapPageSize":500,"isEnabled":true,
                     "mappings":[{"sourceAttribute":"mail","targetAttribute":"Email","targetType":"IdentityColumn","transformationType":"Direct","transformationExpression":null,"isRequired":false,"executionOrder":1}]}]}],
         "fingerprint":"abc123"}
        """;
        var definition = IcProjectDefinitionReader.Parse(json)!;
        Assert.Equal(IcProjectId, definition.ProjectId);
        Assert.Equal("abc123", definition.Fingerprint);
        Assert.Equal("Domain.local", definition.SourceConnection!.Name);
        Assert.Equal(Guid.Parse("917d8262-fe30-4fbf-9045-b183b46e6d6d"), definition.SourceConnection.AgentId);
        var step = Assert.Single(Assert.Single(definition.Workflows).Steps);
        Assert.Equal(new[] { "OU=People,DC=example,DC=com" }, step.SearchBases);
        Assert.Equal(500, step.LdapPageSize);
        Assert.Equal("Email", Assert.Single(step.Mappings).TargetAttribute);

        Assert.Null(IcProjectDefinitionReader.Parse("""{"projectId":"7c3d04cb-6cbb-4788-8cf0-5e04d7b2d584","name":"x"}"""));   // no fingerprint
        Assert.Null(IcProjectDefinitionReader.Parse("""{"fingerprint":"abc","name":"x"}"""));                                      // no project id
        Assert.Null(IcProjectDefinitionReader.Parse("[]"));
        Assert.Null(IcProjectDefinitionReader.Parse("not json"));
        Assert.Null(IcProjectDefinitionReader.Parse(null));

        Assert.Equal("HTTP 403: Job x is not held by server y.", IcHeartbeatBody.DescribeRefusal(403, """{"error":"Job x is not held by server y."}"""));
        Assert.Equal("HTTP 503", IcHeartbeatBody.DescribeRefusal(503, "<html>gateway</html>"));
        Assert.Equal("HTTP 404", IcHeartbeatBody.DescribeRefusal(404, null));
    }

    // ── The heartbeat ───────────────────────────────────────────────────────

    [Fact]
    public void The_heartbeat_announces_the_installation_and_its_connected_systems_but_never_an_IdentityCenter_sink_or_a_credential()
    {
        var tenants = new[]
        {
            new Tenant { Name = "Identity Center", SystemType = "IdentityCenter", IsActive = true },
            new Tenant { Name = "Domain.local", SystemType = "ActiveDirectory", Domain = "192.168.1.60:636", IsActive = true },
            new Tenant { Name = "Granite HR", SystemType = "CSV", IsActive = true },
            new Tenant { Name = "Old plant", SystemType = "ActiveDirectory", IsActive = false },
            new Tenant { Name = "Default", SystemType = "Emulator", IsActive = true },
        };
        var systems = IcHeartbeatBody.Announceable(tenants);
        Assert.Equal(new[] { "Default", "Domain.local", "Granite HR" }, systems.Select(s => s.Name));

        var body = System.Text.Json.JsonDocument.Parse(IcHeartbeatBody.Build("1.0.0.0", new[] { "AdIdentitySync" }, "Conduit 1 lab agent", "WIN-V4C0OU9VE03", systems)).RootElement;
        Assert.Equal("1.0.0.0", body.GetProperty("version").GetString());
        Assert.Equal("Conduit 1 lab agent", body.GetProperty("name").GetString());
        Assert.Equal("WIN-V4C0OU9VE03", body.GetProperty("machineName").GetString());
        var announced = body.GetProperty("connectedSystems").EnumerateArray().ToList();
        Assert.Equal(3, announced.Count);
        Assert.Equal("ActiveDirectory", announced[1].GetProperty("systemType").GetString());
        Assert.Equal("192.168.1.60:636", announced[1].GetProperty("domain").GetString());
        // Only name, kind and domain hint travel: nothing else is a property of an announced system.
        Assert.Equal(new[] { "name", "systemType", "domain" }, announced[1].EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void Objects_pushed_to_an_IdentityCenter_carry_the_agent_it_enrolled_this_installation_as()
    {
        ConduitInstanceIdentity.ForgetEnrolledAgents();
        try
        {
            var enrolled = Guid.Parse("917d8262-fe30-4fbf-9045-b183b46e6d6d");
            Assert.Null(ConduitInstanceIdentity.EnrolledAgentFor("http://192.168.1.60:8080"));
            ConduitInstanceIdentity.RecordEnrolledAgent("http://192.168.1.60:8080/", enrolled);
            // The sink's credential spells the url its own way; the origin is what matters.
            Assert.Equal(enrolled, ConduitInstanceIdentity.JobServerIdFor("HTTP://192.168.1.60:8080/api/"));
            Assert.Equal(enrolled, ConduitInstanceIdentity.EnrolledAgentFor("http://192.168.1.60:8080"));
            // Another IdentityCenter, or no url at all, falls back to this installation's own id.
            Assert.Null(ConduitInstanceIdentity.EnrolledAgentFor("https://other.example.com"));
            Assert.Null(ConduitInstanceIdentity.EnrolledAgentFor("not a url"));
            Assert.Null(ConduitInstanceIdentity.OriginKey("file:///c:/x"));
            ConduitInstanceIdentity.RecordEnrolledAgent("http://192.168.1.60:8080", Guid.Empty);   // an empty id is not an identity
            Assert.Equal(enrolled, ConduitInstanceIdentity.EnrolledAgentFor("http://192.168.1.60:8080"));
        }
        finally
        {
            ConduitInstanceIdentity.ForgetEnrolledAgents();
        }
    }

    [Fact]
    public void The_sink_stamps_the_enrolled_agent_and_the_list_shows_a_mirror_without_a_Manage_button()
    {
        var sink = File.ReadAllText(Repo("src/Conduit.Connectors.IdentityCenter/IdentityCenterSink.cs"));
        Assert.DoesNotContain("SourceJobServerId = ConduitInstanceIdentity.InstanceId", sink);
        Assert.Equal(7, sink.Split("SourceJobServerId = ConduitInstanceIdentity.JobServerIdFor(creds.BaseUrl)").Length - 1);
        var poller = File.ReadAllText(Repo("src/Conduit.Web/Services/IcAgentCommandPollerService.cs"));
        Assert.Contains("ConduitInstanceIdentity.RecordEnrolledAgent(baseUrl, agentId.Value)", poller);
        Assert.Contains("IcHeartbeatBody.Build(version, capabilities, ConduitInstanceIdentity.Name, Environment.MachineName, await AnnounceableSystemsAsync())", poller);
        var page = File.ReadAllText(Repo("src/Conduit.Web/Pages/Sync/SyncProjects.razor"));
        Assert.Contains("private static bool IsMirror(SyncProject p) => p.IdentityCenterProjectId is not null && !string.IsNullOrEmpty(p.IdentityCenterDefinitionFingerprint);", page);
        Assert.Equal(2, page.Split("@if (!IsMirror(project))").Length - 1);   // both Manage buttons
        Assert.Contains("Text=\"@IcProjectMirror.DefinedInIdentityCenter\"", page);
        var migrator = File.ReadAllText(Repo("src/Conduit.DataAccess/DatabaseMigrator.cs"));
        Assert.Contains("Version = 37,", migrator);
        Assert.Contains("ADD [IdentityCenterDefinitionFingerprint] NVARCHAR(64) NULL", migrator);
    }

    private static string Repo(string relative, [System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relative.Replace('/', Path.DirectorySeparatorChar)));
}
