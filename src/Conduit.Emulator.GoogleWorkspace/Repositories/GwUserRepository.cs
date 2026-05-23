using Dapper;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using Conduit.DataAccess;
using Conduit.Emulator.GoogleWorkspace.Infrastructure;
using Conduit.Emulator.GoogleWorkspace.Models;

namespace Conduit.Emulator.GoogleWorkspace.Repositories;

public sealed class GwUserRepository
{
    private readonly DatabaseConfig _config;

    public GwUserRepository(DatabaseConfig config) => _config = config;

    private SqlConnection Open()
    {
        var c = new SqlConnection(_config.ConnectionString);
        c.Open();
        return c;
    }

    public async Task<GwUser?> ResolveAsync(string userKey, bool includeDeleted = false)
    {
        // userKey may be id | primaryEmail | alias
        using var c = Open();
        const string sql = @"
SELECT TOP 1 u.*
FROM gw_users u
WHERE (@Key = u.Id OR u.PrimaryEmail = @Key OR EXISTS(
       SELECT 1 FROM gw_aliases a WHERE a.Alias = @Key AND a.TargetKind = 'user' AND a.TargetId = u.Id))
  AND (@IncludeDeleted = 1 OR u.DeletionTime IS NULL);";
        var row = await c.QuerySingleOrDefaultAsync<GwUserRow>(sql, new { Key = userKey, IncludeDeleted = includeDeleted ? 1 : 0 });
        return row is null ? null : Hydrate(row, await LoadAliasesAsync(c, row.Id));
    }

    public async Task<(IReadOnlyList<GwUser> Users, int Total)> ListAsync(
        string customerId,
        string? domain,
        string? query,
        string? orgUnitPath,
        bool? isAdmin,
        bool? isSuspended,
        bool showDeleted,
        string orderBy,
        string sortOrder,
        int offset,
        int limit)
    {
        using var c = Open();
        var p = new DynamicParameters();
        p.Add("CustomerId", customerId);
        p.Add("Offset", offset);
        p.Add("Limit", limit);

        var filters = new List<string> { "u.CustomerId = @CustomerId" };
        if (!showDeleted) filters.Add("u.DeletionTime IS NULL");
        if (showDeleted) filters.Add("u.DeletionTime IS NOT NULL");

        if (!string.IsNullOrWhiteSpace(domain))
        {
            filters.Add("u.PrimaryEmail LIKE @Domain");
            p.Add("Domain", "%@" + domain);
        }

        if (!string.IsNullOrWhiteSpace(orgUnitPath))
        {
            filters.Add("(u.OrgUnitPath = @OrgUnitPath OR u.OrgUnitPath LIKE @OrgUnitPathPrefix)");
            p.Add("OrgUnitPath", orgUnitPath);
            p.Add("OrgUnitPathPrefix", orgUnitPath.TrimEnd('/') + "/%");
        }

        if (isAdmin.HasValue)
        {
            filters.Add("u.IsAdmin = @IsAdmin");
            p.Add("IsAdmin", isAdmin.Value ? 1 : 0);
        }
        if (isSuspended.HasValue)
        {
            filters.Add("u.Suspended = @Suspended");
            p.Add("Suspended", isSuspended.Value ? 1 : 0);
        }

        // ?query= is Google's free-text search — we support: email:, name:, givenName:, familyName:
        if (!string.IsNullOrWhiteSpace(query))
        {
            ApplyQueryString(query!, filters, p);
        }

        var order = orderBy switch
        {
            "email" => "u.PrimaryEmail",
            "givenName" => "u.GivenName",
            "familyName" => "u.FamilyName",
            _ => "u.PrimaryEmail"
        };
        var dir = string.Equals(sortOrder, "DESCENDING", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";

        var where = string.Join(" AND ", filters);
        var total = await c.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM gw_users u WHERE {where}", p);
        var sql = $@"
SELECT u.*
FROM gw_users u
WHERE {where}
ORDER BY {order} {dir}, u.Id
OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY;";
        var rows = (await c.QueryAsync<GwUserRow>(sql, p)).ToList();
        if (rows.Count == 0) return (Array.Empty<GwUser>(), total);

        var ids = rows.Select(r => r.Id).ToArray();
        var aliasLookup = await LoadAliasesBulkAsync(c, ids);
        var users = rows.Select(r => Hydrate(r, aliasLookup.TryGetValue(r.Id, out var a) ? a : new List<string>())).ToList();
        return (users, total);
    }

    private static void ApplyQueryString(string query, List<string> filters, DynamicParameters p)
    {
        // Google syntax is rich; we cover the common cases without pretending to be complete.
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int n = 0;
        foreach (var raw in tokens)
        {
            var token = raw.Trim();
            var colon = token.IndexOf(':');
            var eq = token.IndexOf('=');
            if (colon > 0)
            {
                var key = token[..colon].ToLowerInvariant();
                var val = token[(colon + 1)..].Trim('*');
                var paramName = $"q{n++}";
                p.Add(paramName, "%" + val + "%");
                switch (key)
                {
                    case "email": filters.Add($"u.PrimaryEmail LIKE @{paramName}"); break;
                    case "name": filters.Add($"(u.FullName LIKE @{paramName} OR u.PrimaryEmail LIKE @{paramName})"); break;
                    case "givenname": filters.Add($"u.GivenName LIKE @{paramName}"); break;
                    case "familyname": filters.Add($"u.FamilyName LIKE @{paramName}"); break;
                    case "orgunitpath": filters.Add($"u.OrgUnitPath LIKE @{paramName}"); break;
                }
            }
            else if (eq > 0)
            {
                var key = token[..eq].ToLowerInvariant();
                var val = token[(eq + 1)..];
                var paramName = $"q{n++}";
                if (key == "isadmin") { p.Add(paramName, val == "true" ? 1 : 0); filters.Add($"u.IsAdmin = @{paramName}"); }
                else if (key == "issuspended") { p.Add(paramName, val == "true" ? 1 : 0); filters.Add($"u.Suspended = @{paramName}"); }
                else if (key == "orgunitpath") { p.Add(paramName, val); filters.Add($"u.OrgUnitPath = @{paramName}"); }
            }
            else
            {
                var paramName = $"q{n++}";
                p.Add(paramName, "%" + token + "%");
                filters.Add($"(u.PrimaryEmail LIKE @{paramName} OR u.FullName LIKE @{paramName})");
            }
        }
    }

    public async Task<GwUser> InsertAsync(GwUser user, IDbConnectionOrNull? conn = null)
    {
        using var c = Open();
        var row = ToRow(user);
        row.Etag = EtagGenerator.New();
        row.CreationTime = DateTime.UtcNow;
        const string sql = @"
INSERT INTO gw_users
(Id, CustomerId, PrimaryEmail, GivenName, FamilyName, FullName, OrgUnitPath, Suspended, SuspensionReason, Archived,
 IsAdmin, IsDelegatedAdmin, AgreedToTerms, ChangePasswordAtNextLogin, IpWhitelisted, IsMailboxSetup, IncludeInGlobalAddressList,
 HashedPassword, RecoveryEmail, RecoveryPhone, ThumbnailPhotoUrl, CreationTime, LastLoginTime, DeletionTime, Etag,
 Emails_JSON, Phones_JSON, Addresses_JSON, Organizations_JSON, Relations_JSON, Websites_JSON, Languages_JSON, CustomSchemas_JSON)
VALUES
(@Id, @CustomerId, @PrimaryEmail, @GivenName, @FamilyName, @FullName, @OrgUnitPath, @Suspended, @SuspensionReason, @Archived,
 @IsAdmin, @IsDelegatedAdmin, @AgreedToTerms, @ChangePasswordAtNextLogin, @IpWhitelisted, @IsMailboxSetup, @IncludeInGlobalAddressList,
 @HashedPassword, @RecoveryEmail, @RecoveryPhone, @ThumbnailPhotoUrl, @CreationTime, @LastLoginTime, @DeletionTime, @Etag,
 @Emails_JSON, @Phones_JSON, @Addresses_JSON, @Organizations_JSON, @Relations_JSON, @Websites_JSON, @Languages_JSON, @CustomSchemas_JSON);";
        await c.ExecuteAsync(sql, row);
        return Hydrate(row, new List<string>());
    }

    public async Task<GwUser?> UpdateAsync(string id, GwUser user, string? ifMatch)
    {
        using var c = Open();
        var existing = await c.QuerySingleOrDefaultAsync<GwUserRow>(
            "SELECT * FROM gw_users WHERE Id = @Id", new { Id = id });
        if (existing is null) return null;
        if (!string.IsNullOrEmpty(ifMatch) && !string.Equals(ifMatch, existing.Etag, StringComparison.Ordinal))
            throw new EtagMismatchException();

        var row = ToRow(user);
        row.Id = existing.Id;
        row.CustomerId = existing.CustomerId;
        row.CreationTime = existing.CreationTime;
        row.Etag = EtagGenerator.New();

        const string sql = @"
UPDATE gw_users SET
 PrimaryEmail = @PrimaryEmail, GivenName = @GivenName, FamilyName = @FamilyName, FullName = @FullName,
 OrgUnitPath = @OrgUnitPath, Suspended = @Suspended, SuspensionReason = @SuspensionReason, Archived = @Archived,
 IsAdmin = @IsAdmin, IsDelegatedAdmin = @IsDelegatedAdmin, AgreedToTerms = @AgreedToTerms,
 ChangePasswordAtNextLogin = @ChangePasswordAtNextLogin, IpWhitelisted = @IpWhitelisted,
 IsMailboxSetup = @IsMailboxSetup, IncludeInGlobalAddressList = @IncludeInGlobalAddressList,
 HashedPassword = @HashedPassword, RecoveryEmail = @RecoveryEmail, RecoveryPhone = @RecoveryPhone,
 ThumbnailPhotoUrl = @ThumbnailPhotoUrl, LastLoginTime = @LastLoginTime,
 Etag = @Etag,
 Emails_JSON = @Emails_JSON, Phones_JSON = @Phones_JSON, Addresses_JSON = @Addresses_JSON,
 Organizations_JSON = @Organizations_JSON, Relations_JSON = @Relations_JSON,
 Websites_JSON = @Websites_JSON, Languages_JSON = @Languages_JSON, CustomSchemas_JSON = @CustomSchemas_JSON
WHERE Id = @Id;";
        await c.ExecuteAsync(sql, row);
        return Hydrate(row, await LoadAliasesAsync(c, row.Id));
    }

    public async Task<bool> SoftDeleteAsync(string id)
    {
        using var c = Open();
        var rows = await c.ExecuteAsync(
            "UPDATE gw_users SET DeletionTime = SYSUTCDATETIME(), Etag = @Etag WHERE Id = @Id AND DeletionTime IS NULL",
            new { Id = id, Etag = EtagGenerator.New() });
        return rows > 0;
    }

    public async Task<bool> UndeleteAsync(string id)
    {
        using var c = Open();
        var rows = await c.ExecuteAsync(
            "UPDATE gw_users SET DeletionTime = NULL, Etag = @Etag WHERE Id = @Id AND DeletionTime IS NOT NULL",
            new { Id = id, Etag = EtagGenerator.New() });
        return rows > 0;
    }

    public async Task<bool> MakeAdminAsync(string id, bool isAdmin)
    {
        using var c = Open();
        var rows = await c.ExecuteAsync(
            "UPDATE gw_users SET IsAdmin = @IsAdmin, Etag = @Etag WHERE Id = @Id AND DeletionTime IS NULL",
            new { Id = id, IsAdmin = isAdmin ? 1 : 0, Etag = EtagGenerator.New() });
        return rows > 0;
    }

    private static async Task<List<string>> LoadAliasesAsync(SqlConnection c, string userId)
    {
        var rows = await c.QueryAsync<string>(
            "SELECT Alias FROM gw_aliases WHERE TargetKind = 'user' AND TargetId = @Id", new { Id = userId });
        return rows.ToList();
    }

    private static async Task<Dictionary<string, List<string>>> LoadAliasesBulkAsync(SqlConnection c, string[] userIds)
    {
        if (userIds.Length == 0) return new();
        var rows = await c.QueryAsync<(string TargetId, string Alias)>(
            "SELECT TargetId, Alias FROM gw_aliases WHERE TargetKind = 'user' AND TargetId IN @Ids",
            new { Ids = userIds });
        return rows.GroupBy(r => r.TargetId).ToDictionary(g => g.Key, g => g.Select(x => x.Alias).ToList());
    }

    private static GwUser Hydrate(GwUserRow r, List<string> aliases)
        => new()
        {
            Id = r.Id,
            Etag = r.Etag,
            PrimaryEmail = r.PrimaryEmail,
            Name = new GwName
            {
                GivenName = r.GivenName,
                FamilyName = r.FamilyName,
                FullName = r.FullName
            },
            IsAdmin = r.IsAdmin,
            IsDelegatedAdmin = r.IsDelegatedAdmin,
            LastLoginTime = r.LastLoginTime,
            CreationTime = r.CreationTime,
            DeletionTime = r.DeletionTime,
            AgreedToTerms = r.AgreedToTerms,
            Suspended = r.Suspended,
            SuspensionReason = r.SuspensionReason,
            Archived = r.Archived,
            ChangePasswordAtNextLogin = r.ChangePasswordAtNextLogin,
            IpWhitelisted = r.IpWhitelisted,
            CustomerId = r.CustomerId,
            OrgUnitPath = r.OrgUnitPath,
            IsMailboxSetup = r.IsMailboxSetup,
            IncludeInGlobalAddressList = r.IncludeInGlobalAddressList,
            ThumbnailPhotoUrl = r.ThumbnailPhotoUrl,
            RecoveryEmail = r.RecoveryEmail,
            RecoveryPhone = r.RecoveryPhone,
            Emails = Deserialize<List<GwEmail>>(r.Emails_JSON),
            Phones = Deserialize<List<GwPhone>>(r.Phones_JSON),
            Addresses = Deserialize<List<GwAddress>>(r.Addresses_JSON),
            Organizations = Deserialize<List<GwOrganization>>(r.Organizations_JSON),
            Relations = Deserialize<List<GwRelation>>(r.Relations_JSON),
            Websites = Deserialize<List<GwWebsite>>(r.Websites_JSON),
            Languages = Deserialize<List<GwLanguage>>(r.Languages_JSON),
            CustomSchemas = Deserialize<Dictionary<string, Dictionary<string, object>>>(r.CustomSchemas_JSON),
            Aliases = aliases.Count == 0 ? null : aliases
        };

    private static GwUserRow ToRow(GwUser u) => new()
    {
        Id = u.Id,
        CustomerId = u.CustomerId,
        PrimaryEmail = u.PrimaryEmail,
        GivenName = u.Name.GivenName,
        FamilyName = u.Name.FamilyName,
        FullName = string.IsNullOrEmpty(u.Name.FullName) ? $"{u.Name.GivenName} {u.Name.FamilyName}".Trim() : u.Name.FullName,
        OrgUnitPath = u.OrgUnitPath,
        Suspended = u.Suspended,
        SuspensionReason = u.SuspensionReason,
        Archived = u.Archived,
        IsAdmin = u.IsAdmin,
        IsDelegatedAdmin = u.IsDelegatedAdmin,
        AgreedToTerms = u.AgreedToTerms,
        ChangePasswordAtNextLogin = u.ChangePasswordAtNextLogin,
        IpWhitelisted = u.IpWhitelisted,
        IsMailboxSetup = u.IsMailboxSetup,
        IncludeInGlobalAddressList = u.IncludeInGlobalAddressList,
        HashedPassword = u.Password,                 // accept plaintext in emulator, never echoed back
        RecoveryEmail = u.RecoveryEmail,
        RecoveryPhone = u.RecoveryPhone,
        ThumbnailPhotoUrl = u.ThumbnailPhotoUrl,
        LastLoginTime = u.LastLoginTime,
        Emails_JSON = Serialize(u.Emails),
        Phones_JSON = Serialize(u.Phones),
        Addresses_JSON = Serialize(u.Addresses),
        Organizations_JSON = Serialize(u.Organizations),
        Relations_JSON = Serialize(u.Relations),
        Websites_JSON = Serialize(u.Websites),
        Languages_JSON = Serialize(u.Languages),
        CustomSchemas_JSON = Serialize(u.CustomSchemas)
    };

    private static string? Serialize(object? obj) => obj is null ? null : JsonConvert.SerializeObject(obj);
    private static T? Deserialize<T>(string? s) => string.IsNullOrEmpty(s) ? default : JsonConvert.DeserializeObject<T>(s);
}

// Marker — some inserts accept an external connection later; keep the option.
public interface IDbConnectionOrNull { }

public sealed class EtagMismatchException : Exception { public EtagMismatchException() : base("ETag mismatch.") { } }
