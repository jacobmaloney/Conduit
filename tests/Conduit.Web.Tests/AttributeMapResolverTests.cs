using System;
using System.Collections.Concurrent;
using System.Linq;
using Conduit.Sync.Templates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Conduit.Web.Tests;

/// <summary>
/// Pins the canonical INNER JOIN in <see cref="AttributeMapResolver"/>. The join
/// silently discarded any source attribute the sink template lacked, which cost
/// every AD -> IdentityCenter User project its account-state trio
/// (userAccountControl / pwdLastSet / lastLogonTimestamp) — dropped before the
/// LDAP read, so IC saw every user as active with no password age and no logon
/// data. Computers were unaffected only because IC has no Computer sink template
/// and therefore took the passthrough branch.
/// </summary>
public class AttributeMapResolverTests
{
    private const string Ad = AttributeTemplateCatalog.Systems.ActiveDirectory;
    private const string Ic = AttributeTemplateCatalog.Systems.IdentityCenter;

    [Fact]
    public void AdUserAndIcUserTemplates_HaveTheExpectedSize()
    {
        Assert.Equal(25, AttributeTemplateCatalog.Get(Ad, "User")!.Count);
        Assert.Equal(27, AttributeTemplateCatalog.Get(Ic, "User")!.Count);
    }

    [Fact]
    public void AdUserToIc_ResolvesEverySourceAttribute_AndDropsNothing()
    {
        var mappings = AttributeMapResolver.Resolve(Ad, Ic, "User", out var dropped);

        Assert.Empty(dropped);
        Assert.Equal(25, mappings.Count);
    }

    [Theory]
    [InlineData("userAccountControl", "UserAccountControl")]
    [InlineData("pwdLastSet", "PasswordLastSet")]
    [InlineData("lastLogonTimestamp", "LastLogonTimestamp")]
    [InlineData("lastLogon", "LastLogon")]
    public void AccountStateAttributes_SurviveTheJoin(string sourceAttribute, string sinkAttribute)
    {
        var mappings = AttributeMapResolver.Resolve(Ad, Ic, "User");

        var m = Assert.Single(mappings, x => x.SourceAttribute == sourceAttribute);
        Assert.Equal(sinkAttribute, m.SinkAttribute);
    }

    [Fact]
    public void AdManager_LandsOnIcAsManagerSourceId_NotAsManager()
    {
        var mappings = AttributeMapResolver.Resolve(Ad, Ic, "User");

        var m = Assert.Single(mappings, x => x.SourceAttribute == "manager");
        Assert.Equal("ManagerSourceId", m.SinkAttribute);
    }

    /// <summary>
    /// The sink-name override must not leak into the other direction: IC SOURCES
    /// the manager reference as "manager" (that is the key IdentityCenterSource
    /// emits from IC's ObjectAttributes bag), so an IC-sourced project must still
    /// READ "manager".
    /// </summary>
    [Fact]
    public void IcAsSource_StillReadsManagerUnderItsNativeName()
    {
        var mappings = AttributeMapResolver.Resolve(Ic, Ad, "User");

        var m = Assert.Single(mappings, x => x.SinkAttribute == "manager");
        Assert.Equal("manager", m.SourceAttribute);
    }

    /// <summary>
    /// SECURITY REGRESSION GUARD. The account-state trio is declared on the IC User
    /// template so IC can RECEIVE it. IC must never hand it back out: IC's
    /// ObjectAttributes is app-writable, and the AD/ARS templates declare the same
    /// canonicals, so an IC-sourced project would otherwise resolve a mapping that
    /// writes an app-settable integer into AD's userAccountControl bitmask — a
    /// re-enable-a-terminated-account / grant-delegation primitive. Both sinks below
    /// write whatever key they are handed.
    /// </summary>
    [Theory]
    [InlineData(AttributeTemplateCatalog.Systems.ActiveDirectory)]
    [InlineData(AttributeTemplateCatalog.Systems.ActiveRoles)]
    [InlineData(AttributeTemplateCatalog.Systems.GenericLdap)]
    public void IcAsSource_NeverEmitsAccountStateAttributes(string sinkSystem)
    {
        var mappings = AttributeMapResolver.Resolve(Ic, sinkSystem, "User");

        Assert.DoesNotContain(mappings, m => m.SourceAttribute == "UserAccountControl");
        Assert.DoesNotContain(mappings, m => m.SourceAttribute == "PasswordLastSet");
        Assert.DoesNotContain(mappings, m => m.SinkAttribute == "userAccountControl");
        Assert.DoesNotContain(mappings, m => m.SinkAttribute == "pwdLastSet");
    }

    /// <summary>
    /// Sink-only must not leak into the passthrough branch either — an unknown sink
    /// maps every source entry to its canonical, which would reintroduce the same
    /// IC-as-authority exposure against any connector without a template.
    /// </summary>
    [Fact]
    public void IcAsSource_ToUnknownSink_AlsoWithholdsAccountState()
    {
        var mappings = AttributeMapResolver.Resolve(Ic, "NoSuchSystem", "User");

        Assert.NotEmpty(mappings);
        Assert.DoesNotContain(mappings, m => m.SinkAttribute == "UserAccountControl");
        Assert.DoesNotContain(mappings, m => m.SinkAttribute == "PasswordLastSet");
    }

    /// <summary>
    /// The three AD Group attributes IC actually consumes must now survive the join.
    /// "member" is the one that mattered most: it is not a structural attribute, so
    /// without a mapping the LDAP read never requests it and the orchestrator's
    /// group-membership second pass finds an empty bag — a project that syncs groups
    /// and silently no group members.
    /// </summary>
    [Theory]
    [InlineData("member", "member")]
    [InlineData("groupType", "groupType")]
    [InlineData("managedBy", "managedBy")]
    public void AdGroupToIc_KeepsTheAttributesIcConsumes(string sourceAttribute, string sinkAttribute)
    {
        var mappings = AttributeMapResolver.Resolve(Ad, Ic, "Group");

        var m = Assert.Single(mappings, x => x.SourceAttribute == sourceAttribute);
        Assert.Equal(sinkAttribute, m.SinkAttribute);
    }

    /// <summary>
    /// Writing a member list into a directory group is a REPLACE, not an append, and
    /// IC never authors group scope/type. Both are declared sink-only so an IC-sourced
    /// project cannot resolve a mapping that hands them to a directory sink.
    /// </summary>
    [Theory]
    [InlineData(AttributeTemplateCatalog.Systems.ActiveDirectory)]
    [InlineData(AttributeTemplateCatalog.Systems.ActiveRoles)]
    public void IcAsSource_NeverEmitsGroupMembersOrGroupType(string sinkSystem)
    {
        var mappings = AttributeMapResolver.Resolve(Ic, sinkSystem, "Group");

        Assert.DoesNotContain(mappings, m => m.SinkAttribute == "member");
        Assert.DoesNotContain(mappings, m => m.SinkAttribute == "groupType");
    }

    /// <summary>
    /// The drop set is the whole point of the change — a gap must be reportable,
    /// not swallowed. adminCount and isCriticalSystemObject stay deliberately absent
    /// from the IC Group template (IC has no reader for either), so they remain the
    /// standing example of a reportable drop.
    /// </summary>
    [Fact]
    public void AdGroupToIc_ReportsItsDrops()
    {
        AttributeMapResolver.Resolve(Ad, Ic, "Group", out var dropped);

        Assert.NotEmpty(dropped);
        Assert.Contains(dropped, d => d.Canonical == "AdminCount");
        Assert.All(dropped, d => Assert.False(string.IsNullOrWhiteSpace(d.SourceAttribute)));
    }

    /// <summary>
    /// An unknown sink takes the passthrough branch — every source attribute
    /// survives, keyed by its canonical name. This is why AD Computers kept all
    /// 18 attributes while Users lost three.
    /// </summary>
    [Fact]
    public void UnknownSink_PassesEverythingThrough_AndDropsNothing()
    {
        var mappings = AttributeMapResolver.Resolve(Ad, "NoSuchSystem", "Computer", out var dropped);

        Assert.Empty(dropped);
        Assert.Equal(AttributeTemplateCatalog.Get(Ad, "Computer")!.Count, mappings.Count);
        Assert.Contains(mappings, m => m.SinkAttribute == "UserAccountControl");
    }

    /// <summary>
    /// The drop set is only useful if the warning actually reaches a log. Resolve
    /// the service the way Program.cs does — through DI — and assert a real
    /// Warning is emitted naming the object class and the dropped canonical key.
    /// A warning that silently never fires is the same defect class as the silent
    /// drop it is meant to expose.
    /// </summary>
    [Fact]
    public void BuildMappings_EmitsAWarningNamingTheDroppedCanonical()
    {
        var sink = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(sink));
        services.AddSingleton<IAttributeMapService, AttributeMapService>();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IAttributeMapService>().BuildMappings(Ad, Ic, "Group");

        var warning = Assert.Single(sink.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("Group", warning.Message);
        Assert.Contains("AdminCount", warning.Message);
        Assert.Contains(Ic, warning.Message);
    }

    [Fact]
    public void BuildMappings_StaysSilentWhenNothingIsDropped()
    {
        var sink = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(sink));
        services.AddSingleton<IAttributeMapService, AttributeMapService>();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IAttributeMapService>().BuildMappings(Ad, Ic, "User");

        Assert.DoesNotContain(sink.Entries, e => e.Level == LogLevel.Warning);
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capturing(this);
        public void Dispose() { }

        private sealed class Capturing : ILogger
        {
            private readonly CapturingLoggerProvider _owner;
            public Capturing(CapturingLoggerProvider owner) => _owner = owner;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => _owner.Entries.Enqueue(new LogEntry(logLevel, formatter(state, exception)));
        }
    }
}
