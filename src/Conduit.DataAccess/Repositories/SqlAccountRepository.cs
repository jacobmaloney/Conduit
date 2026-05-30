using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace Conduit.DataAccess.Repositories;

/// <summary>
/// Row shape for the SqlAccounts table (Conduit's own record of provisioned
/// SQL logins). The actual CREATE/ALTER/DROP LOGIN DDL targets the external
/// SQL instance and stays in SqlV1AccountsController — this repository owns
/// only the bookkeeping table.
/// </summary>
public class SqlAccountRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Username { get; set; } = string.Empty;
    public bool Disabled { get; set; }
    public DateTime Created { get; set; }
}

/// <summary>
/// Dapper data access for the SqlAccounts table. Tenant scoping is passed in by
/// the controller (the controller still owns the TenantContext); when
/// <paramref name="tenantScope"/> is non-null the query is filtered to it.
/// </summary>
public class SqlAccountRepository : BaseRepository
{
    public SqlAccountRepository(DatabaseConfig config) : base(config) { }

    private static (string where, DynamicParameters p) Scope(Guid? tenantScope, string alias)
    {
        var p = new DynamicParameters();
        if (tenantScope.HasValue)
        {
            p.Add("_TenantId", tenantScope.Value);
            return ($" AND {alias}.TenantId = @_TenantId ", p);
        }
        return (string.Empty, p);
    }

    public async Task<List<SqlAccountRecord>> ListAsync(Guid? tenantScope)
    {
        var (where, p) = Scope(tenantScope, "a");
        var rows = await QueryAsync<SqlAccountRecord>(
            $"SELECT * FROM SqlAccounts a WHERE 1=1 {where} ORDER BY Username", p);
        return rows.ToList();
    }

    public Task<SqlAccountRecord?> GetAsync(Guid id, Guid? tenantScope)
    {
        var (where, p) = Scope(tenantScope, "a");
        p.Add("Id", id);
        return QuerySingleOrDefaultAsync<SqlAccountRecord>(
            $"SELECT * FROM SqlAccounts a WHERE a.Id = @Id {where}", p);
    }

    public Task InsertAsync(SqlAccountRecord record) =>
        ExecuteAsync(@"
            INSERT INTO SqlAccounts (Id, TenantId, Username, Disabled, Created)
            VALUES (@Id, @TenantId, @Username, @Disabled, @Created)", record);

    public Task SetDisabledAsync(Guid id, bool disabled) =>
        ExecuteAsync("UPDATE SqlAccounts SET Disabled = @Disabled WHERE Id = @Id",
            new { Disabled = disabled, Id = id });

    public Task DeleteAsync(Guid id) =>
        ExecuteAsync("DELETE FROM SqlAccounts WHERE Id = @Id", new { Id = id });
}
