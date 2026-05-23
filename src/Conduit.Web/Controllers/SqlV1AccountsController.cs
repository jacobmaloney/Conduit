using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;
using Conduit.Core.Services;
using Conduit.DataAccess;
using Conduit.DataAccess.Repositories;
using Conduit.Web.Services;

namespace Conduit.Web.Controllers
{
    /// <summary>
    /// REST wrapper over SQL Server logins on a target instance. The target is
    /// configured via SystemConfiguration key <c>SqlEmulator.ConnectionString</c>.
    /// If the key is missing the endpoint returns HTTP 503 rather than throwing —
    /// the rest of the server stays usable when SQL provisioning isn't set up.
    /// </summary>
    [ApiController]
    [Route("sql/v1/accounts")]
    [Authorize]
    [EnableRateLimiting("scim")]
    public class SqlV1AccountsController : ControllerBase
    {
        private const string ConfigKey = "SqlEmulator.ConnectionString";

        private readonly DatabaseConfig _db;
        private readonly SystemConfigurationService _config;
        private readonly ITenantContext _tenant;

        public SqlV1AccountsController(DatabaseConfig db, SystemConfigurationService config, ITenantContext tenant)
        {
            _db = db;
            _config = config;
            _tenant = tenant;
        }

        public class SqlAccount
        {
            public Guid Id { get; set; }
            public Guid TenantId { get; set; }
            public string Username { get; set; } = string.Empty;
            public bool Disabled { get; set; }
            public DateTime Created { get; set; }
        }

        public class CreateRequest
        {
            public string Username { get; set; } = string.Empty;
            public string? Password { get; set; }
            public bool Disabled { get; set; }
        }

        public class PatchRequest
        {
            public bool? Disabled { get; set; }
        }

        private Guid InsertTenantId =>
            _tenant.TenantId ?? TenantRepository.DefaultTenantId;

        private string ScopeWhere(string alias) =>
            (!_tenant.IsAdmin && _tenant.TenantId.HasValue)
                ? $" AND {alias}.TenantId = @_TenantId "
                : string.Empty;

        private DynamicParameters BuildParams()
        {
            var p = new DynamicParameters();
            if (!_tenant.IsAdmin && _tenant.TenantId.HasValue) p.Add("_TenantId", _tenant.TenantId.Value);
            return p;
        }

        [HttpGet]
        public async Task<IActionResult> List()
        {
            using var conn = new SqlConnection(_db.ConnectionString);
            var sql = $"SELECT * FROM SqlAccounts a WHERE 1=1 {ScopeWhere("a")} ORDER BY Username";
            var rows = await conn.QueryAsync<SqlAccount>(sql, BuildParams());
            return Ok(rows.ToList());
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> Get(Guid id)
        {
            using var conn = new SqlConnection(_db.ConnectionString);
            var p = BuildParams();
            p.Add("Id", id);
            var row = await conn.QuerySingleOrDefaultAsync<SqlAccount>(
                $"SELECT * FROM SqlAccounts a WHERE a.Id = @Id {ScopeWhere("a")}", p);
            if (row == null) return NotFound(new { error = "SQL account not found" });
            return Ok(row);
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateRequest body)
        {
            if (string.IsNullOrWhiteSpace(body.Username))
            {
                return BadRequest(new { error = "username is required" });
            }

            var target = await _config.GetAsync(ConfigKey);
            if (string.IsNullOrWhiteSpace(target))
            {
                return StatusCode(503, new { error = "SQL emulator not configured" });
            }

            if (!IsSafeIdentifier(body.Username))
            {
                return BadRequest(new { error = "username must be a valid SQL identifier (letters, digits, underscore)" });
            }

            // Create the login on the target instance.
            try
            {
                using var sqlConn = new SqlConnection(target);
                await sqlConn.OpenAsync();
                var pwd = string.IsNullOrEmpty(body.Password) ? GenerateTransientPassword() : body.Password;
                // T-SQL doesn't accept parameterized identifiers; we validate above.
                var ddl = $"CREATE LOGIN [{body.Username}] WITH PASSWORD = N'{pwd.Replace("'", "''")}'";
                await sqlConn.ExecuteAsync(ddl);
                if (body.Disabled)
                {
                    await sqlConn.ExecuteAsync($"ALTER LOGIN [{body.Username}] DISABLE");
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = $"SQL login creation failed: {ex.Message}" });
            }

            var record = new SqlAccount
            {
                Id = Guid.NewGuid(),
                TenantId = InsertTenantId,
                Username = body.Username,
                Disabled = body.Disabled,
                Created = DateTime.UtcNow
            };
            using (var conn = new SqlConnection(_db.ConnectionString))
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO SqlAccounts (Id, TenantId, Username, Disabled, Created)
                    VALUES (@Id, @TenantId, @Username, @Disabled, @Created)", record);
            }
            return StatusCode(201, record);
        }

        [HttpPatch("{id}")]
        public async Task<IActionResult> Patch(Guid id, [FromBody] PatchRequest body)
        {
            using var conn = new SqlConnection(_db.ConnectionString);
            var p = BuildParams();
            p.Add("Id", id);
            var row = await conn.QuerySingleOrDefaultAsync<SqlAccount>(
                $"SELECT * FROM SqlAccounts a WHERE a.Id = @Id {ScopeWhere("a")}", p);
            if (row == null) return NotFound(new { error = "SQL account not found" });

            if (body.Disabled.HasValue && body.Disabled.Value != row.Disabled)
            {
                var target = await _config.GetAsync(ConfigKey);
                if (string.IsNullOrWhiteSpace(target))
                {
                    return StatusCode(503, new { error = "SQL emulator not configured" });
                }
                if (!IsSafeIdentifier(row.Username))
                {
                    return StatusCode(500, new { error = "Stored username is not a safe identifier; refusing to ALTER LOGIN." });
                }
                try
                {
                    using var sqlConn = new SqlConnection(target);
                    await sqlConn.OpenAsync();
                    var verb = body.Disabled.Value ? "DISABLE" : "ENABLE";
                    await sqlConn.ExecuteAsync($"ALTER LOGIN [{row.Username}] {verb}");
                }
                catch (Exception ex)
                {
                    return StatusCode(500, new { error = $"ALTER LOGIN failed: {ex.Message}" });
                }
                row.Disabled = body.Disabled.Value;
                await conn.ExecuteAsync("UPDATE SqlAccounts SET Disabled = @Disabled WHERE Id = @Id",
                    new { row.Disabled, Id = id });
            }
            return Ok(row);
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(Guid id)
        {
            using var conn = new SqlConnection(_db.ConnectionString);
            var p = BuildParams();
            p.Add("Id", id);
            var row = await conn.QuerySingleOrDefaultAsync<SqlAccount>(
                $"SELECT * FROM SqlAccounts a WHERE a.Id = @Id {ScopeWhere("a")}", p);
            if (row == null) return NotFound(new { error = "SQL account not found" });

            var target = await _config.GetAsync(ConfigKey);
            if (string.IsNullOrWhiteSpace(target))
            {
                return StatusCode(503, new { error = "SQL emulator not configured" });
            }
            if (!IsSafeIdentifier(row.Username))
            {
                return StatusCode(500, new { error = "Stored username is not a safe identifier; refusing to DROP LOGIN." });
            }

            try
            {
                using var sqlConn = new SqlConnection(target);
                await sqlConn.OpenAsync();
                await sqlConn.ExecuteAsync($"DROP LOGIN [{row.Username}]");
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = $"DROP LOGIN failed: {ex.Message}" });
            }

            await conn.ExecuteAsync("DELETE FROM SqlAccounts WHERE Id = @Id", new { Id = id });
            return NoContent();
        }

        private static bool IsSafeIdentifier(string s) =>
            !string.IsNullOrWhiteSpace(s) && s.All(c => char.IsLetterOrDigit(c) || c == '_');

        private static string GenerateTransientPassword()
        {
            var bytes = new byte[20];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            return "Tmp!" + Convert.ToBase64String(bytes).Replace("/", "x").Replace("+", "y").Replace("=", "z");
        }
    }
}
