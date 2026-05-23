using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Conduit.Core.Models;
using Conduit.DataAccess.Repositories;

namespace Conduit.Web.Services
{
    /// <summary>
    /// Idempotently provisions the conference demo: three Connected Systems
    /// (IT Helpdesk Portal, Finance Suite, HR Connect) for the ARS attribute-
    /// based provisioning demo, with seed users + groups in IT Helpdesk Portal
    /// including the SOD trap pair, and five fixed-value API tokens (one per
    /// system + admin + ARS proxy) with known raw values for engineer
    /// convenience. Safe to re-run.
    /// </summary>
    public class DemoSeedService
    {
        public record SeedResult(
            int ConnectedSystemsCreated,
            int UsersCreated,
            int GroupsCreated,
            int MembershipsCreated,
            int TokensCreated,
            List<string> Notes);

        private readonly TenantRepository _tenants;
        private readonly UserRepository _users;
        private readonly GroupRepository _groups;
        private readonly ApiTokenService _tokens;

        public DemoSeedService(TenantRepository tenants, UserRepository users, GroupRepository groups, ApiTokenService tokens)
        {
            _tenants = tenants;
            _users = users;
            _groups = groups;
            _tokens = tokens;
        }

        private sealed class Counter { public int Value; }

        public async Task<SeedResult> SeedAsync()
        {
            var notes = new List<string>();
            var sysCreated = new Counter();
            var userCreated = new Counter();
            var groupCreated = new Counter();
            var memberCreated = 0;
            var tokenCreated = 0;

            // ─── Connected Systems ────────────────────────────────────────────
            // Three department-based apps for the Unite 2026 ARS attribute-based
            // provisioning demo. IT Helpdesk Portal carries the SOD trap pair +
            // representative users / groups; Finance Suite and HR Connect are
            // empty targets that ARS workflows provision into during the demo.
            var itHelpdesk = await EnsureTenant("IT Helpdesk Portal", "it-helpdesk",
                "Conference demo system — SOD conflict users pre-loaded",
                "Emulator", "it.demo.local", notes, sysCreated);

            var financeSuite = await EnsureTenant("Finance Suite", "finance-suite",
                "Demo target for finance app provisioning",
                "Emulator", "finance.demo.local", notes, sysCreated);

            var hrConnect = await EnsureTenant("HR Connect", "hr-connect",
                "Demo target for HR app provisioning",
                "Emulator", "hr.demo.local", notes, sysCreated);

            // ─── Users in IT Helpdesk Portal ──────────────────────────────────
            var itUsers = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

            // Finance — 5. ap.user1 and ar.user1 are the SOD pair; sod.conflict is in both groups.
            foreach (var u in new[]
            {
                new SeedUser("ap.user1",      "AP",  "User1",   "ap.user1@it.demo.local",      "Finance"),
                new SeedUser("ar.user1",      "AR",  "User1",   "ar.user1@it.demo.local",      "Finance"),
                new SeedUser("sod.conflict",  "SOD", "Conflict","sod.conflict@it.demo.local",  "Finance"),
                new SeedUser("fin.user2",     "Pat", "Morgan",  "fin.user2@it.demo.local",     "Finance"),
                new SeedUser("fin.user3",     "Sam", "Lee",     "fin.user3@it.demo.local",     "Finance"),
            })
            {
                itUsers[u.UserName] = await EnsureUser(itHelpdesk.Id, u, notes, userCreated);
            }

            // Engineering — 8
            for (int i = 1; i <= 8; i++)
            {
                var u = new SeedUser($"eng.user{i}", $"Eng{i}", "Doe", $"eng.user{i}@it.demo.local", "Engineering");
                itUsers[u.UserName] = await EnsureUser(itHelpdesk.Id, u, notes, userCreated);
            }

            // HR — 4
            for (int i = 1; i <= 4; i++)
            {
                var u = new SeedUser($"hr.user{i}", $"HR{i}", "Smith", $"hr.user{i}@it.demo.local", "HR");
                itUsers[u.UserName] = await EnsureUser(itHelpdesk.Id, u, notes, userCreated);
            }

            // IT — 5
            for (int i = 1; i <= 5; i++)
            {
                var u = new SeedUser($"it.user{i}", $"IT{i}", "Brown", $"it.user{i}@it.demo.local", "IT");
                itUsers[u.UserName] = await EnsureUser(itHelpdesk.Id, u, notes, userCreated);
            }

            // Operations — 3
            for (int i = 1; i <= 3; i++)
            {
                var u = new SeedUser($"ops.user{i}", $"Ops{i}", "Jones", $"ops.user{i}@it.demo.local", "Operations");
                itUsers[u.UserName] = await EnsureUser(itHelpdesk.Id, u, notes, userCreated);
            }

            // ─── Groups in IT Helpdesk Portal ─────────────────────────────────
            var financeAp = await EnsureGroup(itHelpdesk.Id, "Finance-AP", "Accounts Payable", notes, groupCreated);
            var financeAr = await EnsureGroup(itHelpdesk.Id, "Finance-AR", "Accounts Receivable", notes, groupCreated);
            var engDev    = await EnsureGroup(itHelpdesk.Id, "Engineering-Dev", "Engineering — development team", notes, groupCreated);
            var itAdmins  = await EnsureGroup(itHelpdesk.Id, "IT-Admins", "IT administrators", notes, groupCreated);
            var hrTeam    = await EnsureGroup(itHelpdesk.Id, "HR-Team", "HR — full team", notes, groupCreated);

            // SOD setup
            memberCreated += await EnsureMembership(financeAp, itUsers["ap.user1"]);
            memberCreated += await EnsureMembership(financeAr, itUsers["ar.user1"]);
            memberCreated += await EnsureMembership(financeAp, itUsers["sod.conflict"]);
            memberCreated += await EnsureMembership(financeAr, itUsers["sod.conflict"]);
            // Sensible defaults for the rest so the demo doesn't look empty
            for (int i = 1; i <= 8; i++) memberCreated += await EnsureMembership(engDev, itUsers[$"eng.user{i}"]);
            for (int i = 1; i <= 5; i++) memberCreated += await EnsureMembership(itAdmins, itUsers[$"it.user{i}"]);
            for (int i = 1; i <= 4; i++) memberCreated += await EnsureMembership(hrTeam, itUsers[$"hr.user{i}"]);

            // ─── API tokens ────────────────────────────────────────────────────
            // One token per Connected System (Tenant scope, slug-locked) + admin + ARS proxy.
            tokenCreated += await EnsureToken("it-helpdesk-token", "demo-it-2024", itHelpdesk.Id, "Tenant",
                "ARS PowerShell workflows targeting IT Helpdesk Portal", notes);
            tokenCreated += await EnsureToken("finance-suite-token", "demo-finance-2024", financeSuite.Id, "Tenant",
                "ARS PowerShell workflows targeting Finance Suite", notes);
            tokenCreated += await EnsureToken("hr-connect-token", "demo-hr-2024", hrConnect.Id, "Tenant",
                "ARS PowerShell workflows targeting HR Connect", notes);
            tokenCreated += await EnsureToken("admin-token", "admin-conduit-2024", tenantId: null, "Admin",
                "Admin token — full access across all connected systems", notes);
            tokenCreated += await EnsureToken("ars-proxy-token", "ars-proxy-2024", tenantId: null, "ArsProxy",
                "Token for POST /ars/v1/ inbound demo", notes);

            return new SeedResult(sysCreated.Value, userCreated.Value, groupCreated.Value, memberCreated, tokenCreated, notes);
        }

        // ────────────────────────────────────────────────────────────────────
        private record SeedUser(string UserName, string GivenName, string FamilyName, string Email, string Department);

        private async Task<Tenant> EnsureTenant(string name, string slug, string desc, string type, string? domain, List<string> notes, Counter created)
        {
            var existing = await _tenants.GetBySlugAsync(slug);
            if (existing != null)
            {
                notes.Add($"Connected System '{slug}' already exists — skipped.");
                return existing;
            }
            var t = new Tenant
            {
                Name = name, Slug = slug, Description = desc, SystemType = type, Domain = domain, IsActive = true
            };
            t = await _tenants.CreateAsync(t);
            created.Value++;
            notes.Add($"Created Connected System '{slug}' ({t.Id}).");
            return t;
        }

        private async Task<Guid> EnsureUser(Guid tenantId, SeedUser u, List<string> notes, Counter created)
        {
            // The repo's GetByUserNameAsync filters by current tenant context. We're running
            // in an admin/dev context, but to be safe we still scope the lookup manually by
            // pulling all users in the target tenant and matching there. For demo scale it's fine.
            // Simpler: directly create with tenantOverride; duplicates throw on UQ_Users_UserName.
            // We use the explicit tenantOverride form so this works under any caller context.
            try
            {
                var scim = new ScimUser
                {
                    UserName = u.UserName,
                    Active = true,
                    Name = new ScimName { GivenName = u.GivenName, FamilyName = u.FamilyName },
                    DisplayName = $"{u.GivenName} {u.FamilyName}".Trim(),
                    Emails = new List<ScimEmail>
                    {
                        new() { Value = u.Email, Type = "work", Primary = true }
                    },
                    EnterpriseExtension = new ScimEnterpriseUser
                    {
                        Department = u.Department
                    }
                };
                var createdUser = await _users.CreateAsync(scim, tenantOverride: tenantId);
                created.Value++;
                return Guid.Parse(createdUser.Id);
            }
            catch (Exception ex) when (ex.Message.Contains("UQ_Users_UserName") || ex.Message.Contains("duplicate key"))
            {
                // Fetch the existing one. UserName is globally unique today; that's a known
                // pre-multi-tenant constraint. Future: relax to (TenantId, UserName).
                var existing = await _users.GetByUserNameAsync(u.UserName);
                notes.Add($"User '{u.UserName}' already exists — skipped.");
                return existing != null ? Guid.Parse(existing.Id) : Guid.Empty;
            }
        }

        private async Task<Guid> EnsureGroup(Guid tenantId, string name, string description, List<string> notes, Counter created)
        {
            try
            {
                var g = new ScimGroup
                {
                    DisplayName = name,
                    Description = description,
                    Members = new List<ScimGroupMember>()
                };
                var createdGroup = await _groups.CreateAsync(g, tenantOverride: tenantId);
                created.Value++;
                return Guid.Parse(createdGroup.Id);
            }
            catch (Exception ex) when (ex.Message.Contains("UQ_Groups_DisplayName") || ex.Message.Contains("duplicate key"))
            {
                notes.Add($"Group '{name}' already exists — skipped.");
                // We don't have GetByDisplayNameAsync; emit a placeholder and rely on caller to skip memberships.
                return Guid.Empty;
            }
        }

        private async Task<int> EnsureMembership(Guid groupId, Guid userId)
        {
            if (groupId == Guid.Empty || userId == Guid.Empty) return 0;
            var group = await _groups.GetByIdAsync(groupId);
            if (group == null) return 0;
            group.Members ??= new List<ScimGroupMember>();
            if (group.Members.Any(m => string.Equals(m.Value, userId.ToString(), StringComparison.OrdinalIgnoreCase)))
            {
                return 0;
            }
            group.Members.Add(new ScimGroupMember { Value = userId.ToString(), Type = "User" });
            await _groups.UpdateAsync(groupId, group);
            return 1;
        }

        private async Task<int> EnsureToken(string name, string rawValue, Guid? tenantId, string scope, string description, List<string> notes)
        {
            var before = await _tokens.GetAllTokensAsync();
            // EnsureFixedTokenAsync is idempotent — checks hash internally.
            await _tokens.EnsureFixedTokenAsync(name, rawValue, tenantId, scope, description);
            var after = await _tokens.GetAllTokensAsync();
            if (after.Count > before.Count)
            {
                notes.Add($"Created token '{name}' (raw value: scim_{rawValue}).");
                return 1;
            }
            notes.Add($"Token '{name}' already exists — skipped.");
            return 0;
        }
    }
}
