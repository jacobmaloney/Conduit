using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Connectors.ActiveRoles;
using Conduit.Core.SyncModels;
using Conduit.Sync.Connectors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.ActiveRoles.Cli;

/// <summary>
/// Standalone proof harness for the Active Roles connector. References only the
/// connector (+ Conduit.Sync transitively) — NOT Conduit.Web and NO database.
/// Connection settings come from a local, UNCOMMITTED appsettings.json (see
/// appsettings.sample.json) or environment variables (ARS_HOST / ARS_USER /
/// ARS_PASSWORD).
///
/// Commands:
///   test
///   read &lt;objectClass&gt; &lt;baseDN&gt; [count] [--va &lt;attr&gt;]
///   write &lt;userDN&gt; &lt;attr&gt; &lt;true|false&gt;
///
/// MUST run on a host with the Active Roles ADSI provider installed (the AR
/// server or an admin workstation with AR Management Tools). EDMS:// will fail to
/// bind anywhere else.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var host = config["Ars:ServiceHost"] ?? Environment.GetEnvironmentVariable("ARS_HOST");
        var user = config["Ars:BindUser"] ?? Environment.GetEnvironmentVariable("ARS_USER");
        var pass = config["Ars:BindPassword"] ?? Environment.GetEnvironmentVariable("ARS_PASSWORD");
        // Optional raw-AD DC for objectGUID → DN resolution (write-guid) AND the
        // Phase 2 fast read's direct LDAP. Falls back to the AR service host inside
        // the connector when left unset.
        var adHost = config["Ars:AdHost"] ?? Environment.GetEnvironmentVariable("ARS_ADHOST");
        // Phase 2 fast read: read-only connection to the ARS config DB (CVSAValues /
        // VirtualSchema). NEVER committed — supplied via uncommitted appsettings/env.
        var sqlConn = config["Ars:ArsSqlConnString"] ?? Environment.GetEnvironmentVariable("ARS_SQLCONN");

        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
        {
            Console.Error.WriteLine(
                "ERROR: missing bind credentials. Provide Ars:BindUser / Ars:BindPassword in appsettings.json " +
                "or ARS_USER / ARS_PASSWORD env vars. (Ars:ServiceHost is optional.)");
            return 2;
        }

        using var loggerFactory = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
            .SetMinimumLevel(LogLevel.Information));

        // Optional anchor DN for the `test` probe (AR ADSI has no bindable bare
        // root). From config Ars:TestBindDn / env ARS_TESTDN, else the `test`
        // command's positional arg.
        var testDn = config["Ars:TestBindDn"] ?? Environment.GetEnvironmentVariable("ARS_TESTDN");
        if (string.Equals(args[0], "test", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
            testDn = args[1];

        // Settings factory so the read/bench commands can vary readMode per call.
        ArsConnectionSettings MakeSettings(string? readMode) =>
            new ArsConnectionSettings(user!, pass!, host)
            {
                TestBindDn = testDn,
                AdHost = adHost,
                ArsSqlConnString = sqlConn,
                ReadMode = readMode
            };

        var ct = CancellationToken.None;
        var cmd = args[0].ToLowerInvariant();
        try
        {
            switch (cmd)
            {
                case "test":
                    return await TestAsync(new StaticArsConnectionResolver(MakeSettings(null)), loggerFactory, ct);
                case "read":
                    return await ReadAsync(args, MakeSettings, loggerFactory, ct);
                case "bench":
                    return await BenchAsync(args, MakeSettings, loggerFactory, ct);
                case "write":
                    return await WriteAsync(args, new StaticArsConnectionResolver(MakeSettings(null)), loggerFactory, ct);
                case "write-guid":
                    return await WriteGuidAsync(args, new StaticArsConnectionResolver(MakeSettings(null)), loggerFactory, ct);
                case "write-guid-twice":
                    return await WriteGuidTwiceAsync(args, new StaticArsConnectionResolver(MakeSettings(null)), loggerFactory, ct);
                default:
                    Console.Error.WriteLine($"Unknown command '{cmd}'.");
                    PrintUsage();
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FATAL: " + ex);
            return 1;
        }
    }

    private static async Task<int> TestAsync(
        IArsConnectionResolver resolver, ILoggerFactory lf, CancellationToken ct)
    {
        var sink = new ActiveRolesSink(resolver, lf.CreateLogger<ActiveRolesSink>());
        var result = await sink.TestConnectionAsync(ct);
        Console.WriteLine($"[test] IsSuccessful={result.IsSuccessful}");
        Console.WriteLine($"[test] Message={result.Message}");
        return result.IsSuccessful ? 0 : 1;
    }

    private static async Task<int> ReadAsync(
        string[] args, Func<string?, ArsConnectionSettings> makeSettings, ILoggerFactory lf, CancellationToken ct)
    {
        // read <objectClass> <baseDN> [count] [--va <attr>] [--mode fast|policy]
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: read <objectClass> <baseDN> [count] [--va <attr> ...] [--mode fast|policy]");
            return 2;
        }
        var objectClass = args[1];
        var baseDn = args[2];
        var count = 5;
        var vaAttrs = new List<string>();
        string? mode = null;
        for (var i = 3; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--va", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                vaAttrs.Add(args[++i]);
            else if (string.Equals(args[i], "--mode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                mode = args[++i];
            else if (int.TryParse(args[i], out var n))
                count = n;
        }
        if (vaAttrs.Count == 0) vaAttrs.Add("UNITE-HelpDeskAuditor");

        var resolver = new StaticArsConnectionResolver(makeSettings(mode));
        var source = new ActiveRolesSource(resolver, lf.CreateLogger<ActiveRolesSource>());
        var requested = new List<string> { "sAMAccountName" };
        requested.AddRange(vaAttrs);
        var scope = new SyncProjectScope
        {
            BaseDN = baseDn,
            MaxObjects = count,
            PageSize = Math.Max(count, 100),
            RequestedAttributes = requested
        };

        Console.WriteLine($"[read] class='{objectClass}' baseDN='{baseDn}' count={count} mode='{mode ?? "fast(default)"}' va=[{string.Join(",", vaAttrs)}]");
        var emitted = 0;
        await foreach (var obj in source.ReadAsync(objectClass, scope, ct))
        {
            emitted++;
            var sam = Str(obj, "sAMAccountName") ?? Str(obj, "name") ?? "<no sam>";
            Console.WriteLine($"  [{emitted}] DN={obj.SourceId}");
            Console.Write($"        sAMAccountName={sam}");
            foreach (var va in vaAttrs)
            {
                var v = obj.Attributes.TryGetValue(va, out var vv) && vv is not null ? vv.ToString() : "<not present>";
                Console.Write($"  {va}={v}");
            }
            Console.WriteLine();
            if (emitted >= count) break;
        }
        Console.WriteLine($"[read] emitted {emitted} object(s).");
        return emitted > 0 ? 0 : 1;
    }

    private static async Task<int> BenchAsync(
        string[] args, Func<string?, ArsConnectionSettings> makeSettings, ILoggerFactory lf, CancellationToken ct)
    {
        // bench <objectClass> <baseDN> [count] [--va <attr> ...]
        // Runs the SAME scope through fast then policy, prints VA values from BOTH
        // (to prove correctness) and the wall-clock time of each (to show the speedup).
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: bench <objectClass> <baseDN> [count] [--va <attr> ...]");
            return 2;
        }
        var objectClass = args[1];
        var baseDn = args[2];
        var count = int.MaxValue;
        var vaAttrs = new List<string>();
        for (var i = 3; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--va", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                vaAttrs.Add(args[++i]);
            else if (int.TryParse(args[i], out var n))
                count = n;
        }
        if (vaAttrs.Count == 0) { vaAttrs.Add("UNITE-VPNAdmin"); vaAttrs.Add("UNITE-HelpDeskAuditor"); }

        var requested = new List<string> { "sAMAccountName" };
        requested.AddRange(vaAttrs);
        SyncProjectScope MakeScope() => new SyncProjectScope
        {
            BaseDN = baseDn,
            MaxObjects = count == int.MaxValue ? null : count,
            PageSize = 1000,
            RequestedAttributes = new List<string>(requested)
        };

        async Task<(long ms, int n, Dictionary<string, Dictionary<string, string>> byUser)> RunAsync(string runMode)
        {
            var resolver = new StaticArsConnectionResolver(makeSettings(runMode));
            var source = new ActiveRolesSource(resolver, lf.CreateLogger<ActiveRolesSource>());
            var byUser = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var n = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await foreach (var obj in source.ReadAsync(objectClass, MakeScope(), ct))
            {
                n++;
                var sam = Str(obj, "sAMAccountName") ?? obj.SourceId;
                var vals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var va in vaAttrs)
                    vals[va] = obj.Attributes.TryGetValue(va, out var vv) && vv is not null ? vv.ToString()! : "<none>";
                byUser[sam] = vals;
            }
            sw.Stop();
            return (sw.ElapsedMilliseconds, n, byUser);
        }

        Console.WriteLine($"[bench] class='{objectClass}' baseDN='{baseDn}' va=[{string.Join(",", vaAttrs)}]");
        Console.WriteLine("[bench] === FAST (raw AD LDAP + CVSAValues SQL join) ===");
        var fast = await RunAsync("fast");
        Console.WriteLine($"[bench] fast:   {fast.n} objects in {fast.ms} ms");
        Console.WriteLine("[bench] === POLICY (EDMS:// per-object through the AR service) ===");
        var policy = await RunAsync("policy");
        Console.WriteLine($"[bench] policy: {policy.n} objects in {policy.ms} ms");

        // Correctness: compare VA values for users present in BOTH runs.
        var mismatches = 0; var compared = 0;
        foreach (var kvp in fast.byUser)
        {
            if (!policy.byUser.TryGetValue(kvp.Key, out var pvals)) continue;
            foreach (var va in vaAttrs)
            {
                compared++;
                var f = kvp.Value.TryGetValue(va, out var fv) ? fv : "<none>";
                var p = pvals.TryGetValue(va, out var pv) ? pv : "<none>";
                if (!string.Equals(f, p, StringComparison.OrdinalIgnoreCase))
                {
                    mismatches++;
                    Console.WriteLine($"[bench] MISMATCH {kvp.Key}.{va}: fast='{f}' policy='{p}'");
                }
            }
        }
        Console.WriteLine($"[bench] compared {compared} VA value(s) across {fast.byUser.Count(u => policy.byUser.ContainsKey(u.Key))} shared user(s); {mismatches} mismatch(es).");
        if (policy.ms > 0 && fast.ms >= 0)
            Console.WriteLine($"[bench] speedup: policy/fast = {(fast.ms == 0 ? double.PositiveInfinity : (double)policy.ms / fast.ms):F1}x (fast {fast.ms} ms vs policy {policy.ms} ms)");
        return mismatches == 0 ? 0 : 1;
    }

    private static async Task<int> WriteAsync(
        string[] args, IArsConnectionResolver resolver, ILoggerFactory lf, CancellationToken ct)
    {
        // write <userDN> <attr> <true|false|value>
        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: write <userDN> <attr> <true|false>");
            return 2;
        }
        var dn = args[1];
        var attr = args[2];
        var rawVal = args[3];
        object val = bool.TryParse(rawVal, out var b) ? b : rawVal;

        var sink = new ActiveRolesSink(resolver, lf.CreateLogger<ActiveRolesSink>());
        var obj = new ConnectorObject
        {
            SourceId = dn,
            ObjectClass = "user",
            Attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [attr] = val
            }
        };

        Console.WriteLine($"[write] DN={dn}  {attr}={val}");
        var result = await sink.UpsertAsync(obj, ct);
        Console.WriteLine($"[write] Outcome={result.Outcome}");
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            Console.WriteLine($"[write] ErrorMessage={result.ErrorMessage}");
        return result.Outcome == SinkWriteOutcome.Failed ? 1 : 0;
    }

    private static async Task<int> WriteGuidAsync(
        string[] args, IArsConnectionResolver resolver, ILoggerFactory lf, CancellationToken ct)
    {
        // write-guid <objectGUID> <attr> <true|false|value>
        // Proves the AD->ARS path: SourceId is an objectGUID exactly as the Conduit
        // AD source emits it; the sink must resolve GUID -> DN -> EDMS:// write.
        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: write-guid <objectGUID> <attr> <true|false>");
            return 2;
        }
        var guid = args[1];
        var attr = args[2];
        var rawVal = args[3];
        object val = bool.TryParse(rawVal, out var b) ? b : rawVal;

        var sink = new ActiveRolesSink(resolver, lf.CreateLogger<ActiveRolesSink>());
        var obj = new ConnectorObject
        {
            SourceId = guid,
            ObjectClass = "user",
            Attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [attr] = val
            }
        };

        Console.WriteLine($"[write-guid] objectGUID={guid}  {attr}={val}");
        var result = await sink.UpsertAsync(obj, ct);
        Console.WriteLine($"[write-guid] Outcome={result.Outcome}");
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            Console.WriteLine($"[write-guid] ErrorMessage={result.ErrorMessage}");
        return result.Outcome == SinkWriteOutcome.Failed ? 1 : 0;
    }

    private static async Task<int> WriteGuidTwiceAsync(
        string[] args, IArsConnectionResolver resolver, ILoggerFactory lf, CancellationToken ct)
    {
        // write-guid-twice <objectGUID> <attr> <true|false|value>
        // Proves the Fix-1 per-run GUID→DN cache: two upserts of the SAME objectGUID
        // on ONE sink instance must resolve GUID→DN via the DC only ONCE — the second
        // upsert hits the cache (watch for "GUID→DN cache hit" at Debug, and the
        // absence of a second raw-AD LDAP GUID bind).
        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: write-guid-twice <objectGUID> <attr> <true|false>");
            return 2;
        }
        var guid = args[1];
        var attr = args[2];
        var rawVal = args[3];
        object val = bool.TryParse(rawVal, out var b) ? b : rawVal;

        // Debug level so the cache-hit log line is visible.
        using var dbgFactory = LoggerFactory.Create(bld => bld
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
            .SetMinimumLevel(LogLevel.Debug));

        var sink = new ActiveRolesSink(resolver, dbgFactory.CreateLogger<ActiveRolesSink>());
        ConnectorObject MakeObj() => new ConnectorObject
        {
            SourceId = guid,
            ObjectClass = "user",
            Attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { [attr] = val }
        };

        Console.WriteLine($"[write-guid-twice] objectGUID={guid}  {attr}={val}  (same sink, two upserts)");
        Console.WriteLine("[write-guid-twice] --- upsert #1 (expect a raw-AD LDAP GUID bind) ---");
        var r1 = await sink.UpsertAsync(MakeObj(), ct);
        Console.WriteLine($"[write-guid-twice] #1 Outcome={r1.Outcome}{(string.IsNullOrWhiteSpace(r1.ErrorMessage) ? "" : " Error=" + r1.ErrorMessage)}");
        Console.WriteLine("[write-guid-twice] --- upsert #2 (expect 'GUID→DN cache hit', NO second bind) ---");
        var r2 = await sink.UpsertAsync(MakeObj(), ct);
        Console.WriteLine($"[write-guid-twice] #2 Outcome={r2.Outcome}{(string.IsNullOrWhiteSpace(r2.ErrorMessage) ? "" : " Error=" + r2.ErrorMessage)}");
        return (r1.Outcome == SinkWriteOutcome.Failed || r2.Outcome == SinkWriteOutcome.Failed) ? 1 : 0;
    }

    private static string? Str(ConnectorObject obj, string key)
    {
        if (!obj.Attributes.TryGetValue(key, out var v) || v is null) return null;
        if (v is string s) return s;
        if (v is System.Collections.IList list && list.Count > 0) return list[0]?.ToString();
        return v.ToString();
    }

    private static void PrintUsage()
    {
        Console.WriteLine("arscli — Active Roles connector proof harness");
        Console.WriteLine();
        Console.WriteLine("  arscli test");
        Console.WriteLine("  arscli read <objectClass> <baseDN> [count] [--va <attr> ...] [--mode fast|policy]");
        Console.WriteLine("  arscli bench <objectClass> <baseDN> [count] [--va <attr> ...]");
        Console.WriteLine("  arscli write <userDN> <attr> <true|false>");
        Console.WriteLine("  arscli write-guid <objectGUID> <attr> <true|false>");
        Console.WriteLine("  arscli write-guid-twice <objectGUID> <attr> <true|false>");
        Console.WriteLine();
        Console.WriteLine("Connection: appsettings.json (Ars:ServiceHost/BindUser/BindPassword,");
        Console.WriteLine("Ars:AdHost = a DC for the fast read, Ars:ArsSqlConnString = read-only conn");
        Console.WriteLine("to the ARS config DB for CVSAValues) or env ARS_HOST / ARS_USER /");
        Console.WriteLine("ARS_PASSWORD / ARS_ADHOST / ARS_SQLCONN.");
        Console.WriteLine();
        Console.WriteLine("read/bench default to FAST mode (raw AD LDAP + CVSAValues SQL join, no AR");
        Console.WriteLine("service). --mode policy (and write/write-guid) use EDMS:// and REQUIRE the");
        Console.WriteLine("Active Roles ADSI provider installed on this host.");
    }
}
