using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Sync.Connectors;
using Conduit.Sync.Security;
using Microsoft.Extensions.Logging;

namespace Conduit.Connectors.GenericLdap;

/// <summary>
/// Generic LDAP sink. Lookup by DN first (SourceId is treated as a DN); if not
/// found, optionally search by uid/cn. Add via AddRequest, modify via
/// ModifyRequest, delete via DeleteRequest. NO AD-specific tricks — no
/// unicodePwd write, no UAC bit-twiddling.
///
/// Target DN: if the inbound object lacks a "distinguishedName" attribute, we
/// build cn=&lt;cn|displayName&gt;,&lt;baseDN-from-scope-or-default&gt;. Default
/// container comes from `defaultContainer` attribute on the inbound object (set
/// by the orchestrator via scope.BaseDN stamping, mirroring AD sink).
/// </summary>
public sealed class GenericLdapSink : IConnectorSink
{
    private readonly Guid _tenantId;
    private readonly CredentialProtector _protector;
    private readonly ILogger<GenericLdapSink> _logger;

    public GenericLdapSink(Guid tenantId, CredentialProtector protector, ILogger<GenericLdapSink> logger)
    {
        _tenantId = tenantId;
        _protector = protector;
        _logger = logger;
    }

    public async Task<SinkWriteResult> UpsertAsync(ConnectorObject obj, CancellationToken cancellationToken)
    {
        try
        {
            var creds = await GenericLdapCredentialReader.ReadAsync(_protector, _tenantId)
                ?? throw new InvalidOperationException($"No 'ldap' credential for tenant {_tenantId}.");
            var (host, port, secure) = GenericLdapCredentialReader.ParseServerUrl(creds.ServerUrl);
            using var conn = new LdapConnection(new LdapDirectoryIdentifier(host, port)) { AuthType = AuthType.Basic };
            conn.SessionOptions.ProtocolVersion = 3;
            GenericLdapCredentialReader.ApplyTls(_logger, _tenantId, conn, host, secure, creds.UseTls,
                creds.AllowUntrustedCertificate, creds.ExpectedServerCertificateThumbprint);
            conn.Credential = new NetworkCredential(creds.BindDN, creds.BindPassword);
            conn.Bind();

            var dn = ResolveTargetDN(obj);
            if (string.IsNullOrWhiteSpace(dn))
                return SinkWriteResult.Fail("Generic LDAP sink could not resolve a target DN (need distinguishedName attr or cn/displayName + targetOU).");

            var exists = ExistsAsync(conn, dn);
            if (!exists)
            {
                var add = BuildAddRequest(dn, obj);
                conn.SendRequest(add);
                return SinkWriteResult.Ok(SinkWriteOutcome.Created);
            }
            var mod = BuildModifyRequest(dn, obj);
            if (mod.Modifications.Count == 0)
                return SinkWriteResult.Ok(SinkWriteOutcome.Skipped);
            conn.SendRequest(mod);
            return SinkWriteResult.Ok(SinkWriteOutcome.Updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Generic LDAP sink upsert failed for {SourceId}", obj.SourceId);
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    public async Task<SinkWriteResult> DeleteAsync(string sourceId, CancellationToken cancellationToken)
    {
        try
        {
            var creds = await GenericLdapCredentialReader.ReadAsync(_protector, _tenantId)
                ?? throw new InvalidOperationException($"No 'ldap' credential for tenant {_tenantId}.");
            var (host, port, secure) = GenericLdapCredentialReader.ParseServerUrl(creds.ServerUrl);
            using var conn = new LdapConnection(new LdapDirectoryIdentifier(host, port)) { AuthType = AuthType.Basic };
            conn.SessionOptions.ProtocolVersion = 3;
            GenericLdapCredentialReader.ApplyTls(_logger, _tenantId, conn, host, secure, creds.UseTls,
                creds.AllowUntrustedCertificate, creds.ExpectedServerCertificateThumbprint);
            conn.Credential = new NetworkCredential(creds.BindDN, creds.BindPassword);
            conn.Bind();
            try { conn.SendRequest(new DeleteRequest(sourceId)); return SinkWriteResult.Ok(SinkWriteOutcome.Updated); }
            catch (DirectoryOperationException) { return SinkWriteResult.Ok(SinkWriteOutcome.Skipped); }
        }
        catch (Exception ex)
        {
            return SinkWriteResult.Fail(ex.Message);
        }
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var src = new GenericLdapSource(_tenantId, _protector,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GenericLdapSource>.Instance);
        return await src.TestConnectionAsync(cancellationToken);
    }

    private static bool ExistsAsync(LdapConnection conn, string dn)
    {
        try
        {
            var req = new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, new[] { "objectClass" });
            var resp = (SearchResponse)conn.SendRequest(req);
            return resp.Entries.Count > 0;
        }
        catch (DirectoryOperationException) { return false; }
    }

    private static string? ResolveTargetDN(ConnectorObject obj)
    {
        if (obj.Attributes.TryGetValue("distinguishedName", out var dn) && dn is string dns && !string.IsNullOrWhiteSpace(dns))
            return dns;
        if (obj.Attributes.TryGetValue("dn", out var dn2) && dn2 is string dns2 && !string.IsNullOrWhiteSpace(dns2))
            return dns2;
        // Build from cn/displayName + targetOU.
        var cn = Get(obj, "cn") ?? Get(obj, "displayName") ?? Get(obj, "userName");
        var baseDn = Get(obj, "targetOU") ?? Get(obj, "baseDN");
        if (string.IsNullOrWhiteSpace(cn) || string.IsNullOrWhiteSpace(baseDn)) return null;
        return $"cn={cn},{baseDn}";
    }

    private static AddRequest BuildAddRequest(string dn, ConnectorObject obj)
    {
        var oc = string.Equals(obj.ObjectClass, "Group", StringComparison.OrdinalIgnoreCase)
            ? new[] { "top", "groupOfNames" }
            : new[] { "top", "person", "organizationalPerson", "inetOrgPerson" };
        var req = new AddRequest(dn);
        req.Attributes.Add(new DirectoryAttribute("objectClass", oc));
        foreach (var (k, v) in obj.Attributes)
        {
            if (IsReservedKey(k) || v is null) continue;
            var s = v as string;
            if (string.IsNullOrEmpty(s)) continue;
            req.Attributes.Add(new DirectoryAttribute(k, s));
        }
        return req;
    }

    private static ModifyRequest BuildModifyRequest(string dn, ConnectorObject obj)
    {
        var req = new ModifyRequest(dn);
        foreach (var (k, v) in obj.Attributes)
        {
            if (IsReservedKey(k) || v is null) continue;
            var s = v as string;
            if (string.IsNullOrEmpty(s)) continue;
            req.Modifications.Add(new DirectoryAttributeModification
            {
                Name = k,
                Operation = DirectoryAttributeOperation.Replace
            }.AddValue(s));
        }
        return req;
    }

    private static bool IsReservedKey(string k) =>
        k.Equals("distinguishedName", StringComparison.OrdinalIgnoreCase)
        || k.Equals("dn", StringComparison.OrdinalIgnoreCase)
        || k.Equals("objectClass", StringComparison.OrdinalIgnoreCase)
        || k.Equals("targetOU", StringComparison.OrdinalIgnoreCase)
        || k.Equals("baseDN", StringComparison.OrdinalIgnoreCase)
        || k.Equals("members", StringComparison.OrdinalIgnoreCase)   // handled by AD-style group flow elsewhere
        || k.Equals("userPassword", StringComparison.OrdinalIgnoreCase); // password write is out-of-scope phase 1.5

    private static string? Get(ConnectorObject obj, string k) =>
        obj.Attributes.TryGetValue(k, out var v) && v is string s && !string.IsNullOrEmpty(s) ? s : null;
}

internal static class DirectoryAttributeModificationExtensions
{
    public static DirectoryAttributeModification AddValue(this DirectoryAttributeModification mod, string value)
    {
        mod.Add(value);
        return mod;
    }
}
