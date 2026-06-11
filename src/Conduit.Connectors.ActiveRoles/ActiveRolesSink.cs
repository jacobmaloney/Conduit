using System;
using System.Collections;
using System.Collections.Generic;
using System.DirectoryServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.ActiveRoles;

/// <summary>
/// Active Roles sink via the AR ADSI provider (EDMS:// + System.DirectoryServices).
///
/// Every write binds <c>EDMS://&lt;dn&gt;</c>, which routes through the Active
/// Roles Administration Service — so on CommitChanges the AR policies, workflows
/// and virtual-attribute handlers fire. A Separation-of-Duties policy that denies
/// a toxic role pairing throws a <see cref="DirectoryServicesCOMException"/> whose
/// message carries the human-readable deny reason; we translate that into a FAILED
/// <see cref="SinkWriteResult"/> rather than swallowing it. That denied-write IS
/// the connector's reason to exist.
///
/// Mechanism proven live against ARS on 2026-06-09 (PowerShell EDMS:// bind +
/// Properties[...].Value + CommitChanges); this is the C# translation. The AR ADSI
/// provider must be installed on the host that RUNS this code — see README.md.
///
/// Phase 1 scope: per-object upsert (no bulk), find-by-DN/sAMAccountName, set
/// mapped real + virtual attributes. Tombstones are a no-op (Phase 3).
/// </summary>
public sealed class ActiveRolesSink : IConnectorSink, ITombstoneEmittingSink
{
    private readonly IArsConnectionResolver _resolver;
    private readonly ILogger<ActiveRolesSink> _logger;

    public ActiveRolesSink(IArsConnectionResolver resolver, ILogger<ActiveRolesSink> logger)
    {
        _resolver = resolver;
        _logger = logger;
    }

    public async Task<SinkWriteResult> UpsertAsync(ConnectorObject obj, CancellationToken cancellationToken)
    {
        // Tombstone marker: never upsert a delete. Phase 1 does not soft-delete
        // through ARS — route to the tombstone path (which is a no-op here).
        if (obj.Attributes.TryGetValue("_deleted", out var dv) && dv is bool db && db)
        {
            _logger.LogInformation(
                "ARS sink saw _deleted marker for {SourceId} — not deleting (Phase 1 no-op).", obj.SourceId);
            return SinkWriteResult.Ok(SinkWriteOutcome.Skipped);
        }

        var settings = await _resolver.ResolveAsync(CredentialSide.Sink, cancellationToken);
        if (settings is null)
            return SinkWriteResult.Fail(
                "No 'ars' credential resolved. Save Active Roles bind credentials before running.");

        try
        {
            // Resolve the target DN. Phase 1 supports an explicit DN SourceId or a
            // search by sAMAccountName under a supplied base.
            var dn = ResolveTargetDn(settings, obj, out var found);
            if (dn is null)
                return SinkWriteResult.Fail(
                    $"Could not resolve a target Active Roles object for SourceId='{obj.SourceId}'. " +
                    "Phase 1 requires either a DN as the SourceId, or a 'sAMAccountName' attribute plus a " +
                    "'baseDN' attribute to search under.");

            using var entry = ArsBind.Bind(settings, dn);
            // RefreshCache makes the provider resolve the object (and virtual
            // attributes) through the AR service before we mutate it.
            entry.RefreshCache();

            var wrote = ApplyAttributes(entry, obj);
            if (!wrote)
            {
                _logger.LogInformation("ARS sink: no mapped attributes to write for {Dn}; skipping.", dn);
                return SinkWriteResult.Ok(SinkWriteOutcome.Skipped);
            }

            // The policy/workflow/VA logic fires HERE. A SoD deny throws.
            entry.CommitChanges();

            return SinkWriteResult.Ok(found ? SinkWriteOutcome.Updated : SinkWriteOutcome.Created);
        }
        catch (DirectoryServicesCOMException ex)
        {
            var msg = PolicyMessage(ex);
            _logger.LogWarning("ARS sink write denied/failed for SourceId={SourceId}: {Message}", obj.SourceId, msg);
            return SinkWriteResult.Fail(msg);
        }
        catch (COMException ex)
        {
            var msg = string.IsNullOrWhiteSpace(ex.Message) ? ex.ToString() : ex.Message;
            _logger.LogWarning("ARS sink write failed (COM) for SourceId={SourceId}: {Message}", obj.SourceId, msg);
            return SinkWriteResult.Fail(msg);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ARS sink upsert failed for SourceId={SourceId}", obj.SourceId);
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var settings = await _resolver.ResolveAsync(CredentialSide.Sink, cancellationToken);
        if (settings is null)
            return new ConnectorTestResult { IsSuccessful = false, Message = "No 'ars' credential resolved." };

        return ArsProbe.Test(settings, _logger);
    }

    /// <summary>Phase 3: ARS soft-delete is not wired yet. Inert by construction.</summary>
    public Task<TombstoneEmitResult> EmitTombstonesAsync(
        string source,
        IReadOnlyList<string> sourceUniqueIds,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "ARS sink EmitTombstones called for {Count} id(s) — Phase 1 no-op (soft-delete deferred to Phase 3).",
            sourceUniqueIds.Count);
        return Task.FromResult(TombstoneEmitResult.Nothing());
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Apply the object's mapped attributes onto the bound entry. Real attributes
    /// and Active Roles virtual attributes (e.g. UNITE-HelpDeskAuditor) are set the
    /// same way — through <c>Properties[name].Value</c>; the AR provider resolves
    /// whether each is a stored or virtual attribute. Reserved/structural keys are
    /// skipped. Returns true if anything was staged for commit.
    /// </summary>
    private static bool ApplyAttributes(DirectoryEntry entry, ConnectorObject obj)
    {
        var wrote = false;
        foreach (var kvp in obj.Attributes)
        {
            if (IsReservedKey(kvp.Key)) continue;
            if (kvp.Value is null) continue;

            entry.Properties[kvp.Key].Value = CoerceValue(kvp.Value);
            wrote = true;
        }
        return wrote;
    }

    /// <summary>
    /// Keys that address the object or carry routing hints rather than directory
    /// attributes — never written onto the AR entry.
    /// </summary>
    private static bool IsReservedKey(string key) => key switch
    {
        "_deleted" => true,
        "dn" => true,
        "distinguishedName" => true,
        "baseDN" => true,
        "targetOU" => true,
        "objectClass" => true,
        _ => false
    };

    /// <summary>
    /// Coerce a ConnectorObject value into something the ADSI provider accepts.
    /// Booleans (the SoD role VAs are boolean), strings and numbers pass straight
    /// through; a single-element list collapses to its element; everything else
    /// falls back to ToString().
    /// </summary>
    private static object? CoerceValue(object value)
    {
        switch (value)
        {
            case bool b: return b;
            case string s: return s;
            case int or long or double or decimal: return value;
        }

        // Normalize common boolean string forms so a mapping that delivered "true"
        // still sets a boolean virtual attribute correctly.
        if (value is string strv && bool.TryParse(strv, out var parsed)) return parsed;

        if (value is IEnumerable en and not string)
        {
            object? only = null;
            var count = 0;
            foreach (var item in en) { only = item; if (++count > 1) break; }
            if (count == 1) return only;
            // Multi-valued: hand the provider the array as-is.
            return value;
        }

        return value;
    }

    /// <summary>
    /// Phase 1 DN resolution. An explicit DN SourceId binds directly. Otherwise we
    /// search by sAMAccountName under a supplied baseDN (through EDMS:// so the
    /// search itself is policy/VA-aware). Returns null when neither path resolves.
    /// </summary>
    private string? ResolveTargetDn(ArsConnectionSettings settings, ConnectorObject obj, out bool found)
    {
        found = false;

        var dn = LooksLikeDn(obj.SourceId) ? obj.SourceId
               : GetStr(obj, "distinguishedName") ?? GetStr(obj, "dn");
        if (LooksLikeDn(dn))
        {
            found = true;
            return dn;
        }

        var sam = GetStr(obj, "sAMAccountName") ?? GetStr(obj, "userName") ?? GetStr(obj, "UserName");
        var baseDn = GetStr(obj, "baseDN") ?? GetStr(obj, "targetOU");
        if (string.IsNullOrWhiteSpace(sam) || string.IsNullOrWhiteSpace(baseDn))
            return null;

        try
        {
            using var root = ArsBind.Bind(settings, baseDn!);
            using var searcher = new DirectorySearcher(root)
            {
                Filter = $"(sAMAccountName={EscapeFilter(sam!)})",
                SearchScope = SearchScope.Subtree,
                PageSize = 1
            };
            searcher.PropertiesToLoad.Add("distinguishedName");
            var result = searcher.FindOne();
            if (result is null) return null;
            found = true;
            // distinguishedName from the AR provider includes the value to rebind on.
            if (result.Properties.Contains("distinguishedName") &&
                result.Properties["distinguishedName"].Count > 0)
            {
                return result.Properties["distinguishedName"][0]?.ToString();
            }
            // Fall back to the result path's DN component (strip the EDMS:// moniker).
            var path = result.Path;
            var idx = path.IndexOf("//", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var rest = path.Substring(idx + 2);
                var slash = rest.IndexOf('/');
                return slash >= 0 ? rest.Substring(slash + 1) : rest;
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ARS sink DN search failed for sAMAccountName under {BaseDn}", baseDn);
            return null;
        }
    }

    /// <summary>
    /// Pull the AR policy text out of a COM exception. The deny reason can land in
    /// <see cref="DirectoryServicesCOMException.ExtendedErrorMessage"/> and/or the
    /// plain <see cref="Exception.Message"/>; return whichever is non-empty (both,
    /// joined, when both carry text) so the SoD reason is never lost.
    /// </summary>
    internal static string PolicyMessage(DirectoryServicesCOMException ex)
    {
        var ext = ex.ExtendedErrorMessage;
        var msg = ex.Message;
        var hasExt = !string.IsNullOrWhiteSpace(ext);
        var hasMsg = !string.IsNullOrWhiteSpace(msg);

        if (hasExt && hasMsg)
        {
            // Avoid duplicating when one already contains the other.
            if (msg!.Contains(ext!, StringComparison.OrdinalIgnoreCase)) return msg;
            if (ext!.Contains(msg, StringComparison.OrdinalIgnoreCase)) return ext;
            return $"{msg} | {ext}";
        }
        if (hasExt) return ext!;
        if (hasMsg) return msg!;
        return ex.ToString();
    }

    private static string? GetStr(ConnectorObject obj, string key)
    {
        if (!obj.Attributes.TryGetValue(key, out var v) || v is null) return null;
        if (v is string s) return s;
        if (v is IList list && list.Count > 0) return list[0]?.ToString();
        return v.ToString();
    }

    private static bool LooksLikeDn(string? s) =>
        !string.IsNullOrEmpty(s) && s!.Contains('=', StringComparison.Ordinal) && s.Contains(',', StringComparison.Ordinal);

    private static string EscapeFilter(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\5c"); break;
                case '*': sb.Append(@"\2a"); break;
                case '(': sb.Append(@"\28"); break;
                case ')': sb.Append(@"\29"); break;
                case '\0': sb.Append(@"\00"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
