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

        var settings = new ArsConnectionSettings(user!, pass!, host) { TestBindDn = testDn };
        var resolver = new StaticArsConnectionResolver(settings);
        var ct = CancellationToken.None;

        var cmd = args[0].ToLowerInvariant();
        try
        {
            switch (cmd)
            {
                case "test":
                    return await TestAsync(resolver, loggerFactory, ct);
                case "read":
                    return await ReadAsync(args, resolver, loggerFactory, ct);
                case "write":
                    return await WriteAsync(args, resolver, loggerFactory, ct);
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
        string[] args, IArsConnectionResolver resolver, ILoggerFactory lf, CancellationToken ct)
    {
        // read <objectClass> <baseDN> [count] [--va <attr>]
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: read <objectClass> <baseDN> [count] [--va <attr>]");
            return 2;
        }
        var objectClass = args[1];
        var baseDn = args[2];
        var count = 5;
        var vaAttr = "UNITE-HelpDeskAuditor";
        for (var i = 3; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--va", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                vaAttr = args[++i];
            }
            else if (int.TryParse(args[i], out var n))
            {
                count = n;
            }
        }

        var source = new ActiveRolesSource(resolver, lf.CreateLogger<ActiveRolesSource>());
        var scope = new SyncProjectScope
        {
            BaseDN = baseDn,
            MaxObjects = count,
            PageSize = Math.Max(count, 100),
            // Ask for the VA explicitly so the source resolves it per-object through
            // ARS (it is a virtual attribute the searcher projection won't return).
            RequestedAttributes = new List<string> { "sAMAccountName", vaAttr }
        };

        Console.WriteLine($"[read] class='{objectClass}' baseDN='{baseDn}' count={count} va='{vaAttr}'");
        var emitted = 0;
        await foreach (var obj in source.ReadAsync(objectClass, scope, ct))
        {
            emitted++;
            var sam = Str(obj, "sAMAccountName") ?? Str(obj, "name") ?? "<no sam>";
            var va = obj.Attributes.TryGetValue(vaAttr, out var vv) && vv is not null
                ? vv.ToString()
                : "<not present>";
            Console.WriteLine($"  [{emitted}] DN={obj.SourceId}");
            Console.WriteLine($"        sAMAccountName={sam}  {vaAttr}={va}");
            if (emitted >= count) break;
        }
        Console.WriteLine($"[read] emitted {emitted} object(s).");
        return emitted > 0 ? 0 : 1;
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
        Console.WriteLine("  arscli read <objectClass> <baseDN> [count] [--va <attr>]");
        Console.WriteLine("  arscli write <userDN> <attr> <true|false>");
        Console.WriteLine();
        Console.WriteLine("Connection: appsettings.json (Ars:ServiceHost/BindUser/BindPassword) or");
        Console.WriteLine("env ARS_HOST / ARS_USER / ARS_PASSWORD. Requires the Active Roles ADSI");
        Console.WriteLine("provider installed on this host (EDMS://).");
    }
}
