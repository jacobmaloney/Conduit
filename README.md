# Conduit

A small, opinionated, security-hardened SCIM 2.0 server you can run on a laptop. Built with ASP.NET Core 8 + Blazor Server + Dapper + SQL Server. Multi-tenant from the ground up — every workload is a **Connected System**, every API call carries a slug or a tenant-scoped bearer token, and the same backend serves SCIM 2.0, a flat REST surface, and a SQL-account emulator.

The intent is to stand up a believable provisioning target in five minutes — for demoing Entra ID, Okta, ARS, or any other identity source — without having to learn anyone's SaaS console.

[![build](https://github.com/jacobmaloney/Conduit/actions/workflows/build.yml/badge.svg)](https://github.com/jacobmaloney/Conduit/actions/workflows/build.yml) [![security](https://github.com/jacobmaloney/Conduit/actions/workflows/security.yml/badge.svg)](https://github.com/jacobmaloney/Conduit/actions/workflows/security.yml) ![SCIM 2.0](https://img.shields.io/badge/SCIM-2.0-10b981) ![.NET 8](https://img.shields.io/badge/.NET-8-512bd4) ![Blazor Server](https://img.shields.io/badge/Blazor-Server-512bd4) ![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)

---

## What's in here

- **SCIM 2.0 surface** — `Users`, `Groups`, `Schemas`, `ResourceTypes`, `ServiceProviderConfig`. Filter parser, PATCH operations, location round-trip, enterprise extension. Listens at `/scim/v2/...` (token-scoped) and `/scim/v2/t/{slug}/...` (slug-scoped, recommended).
- **Flat REST emulator** — `/api/v1/users` + `/api/v1/groups` for callers that don't speak SCIM.
- **SQL account emulator** — `/sql/v1/accounts` for database-account provisioning demos.
- **ARS inbound proxy** — `/ars/v1/users` + `/ars/v1/groups/{id}/members` for ARS PowerShell workflows that want to write back into AD via the Administration Service.
- **Multi-tenant Connected Systems** — each "workload" you provision into is its own tenant: a name, a URL slug, demo or real, isolated user/group data, its own tokens.
- **Portal UI** — Users / Groups / Tokens / Connected Systems / Quick Connect / Setup wizard / Generator / Logs. Active connection is always visible at the top of the page; the sidebar drives scope for every list.
- **Demo seed** — one click and you get 2 Connected Systems with 25 users + 5 groups each, plus 4 fixed-value tokens you can copy out of the Endpoints modal.
- **Built-in test user generator** — generate up to 10,000 realistic users (org chart, departments, locations) into any Connected System; dedupes against the live tenant so re-runs don't collide.

## Security posture

This server is intended to pass routine pen-test sweeps out of the box. Hardening that ships in this repo:

| Concern | Defence |
|---|---|
| Brute-force on `/login` | Per-(user, IP) sliding-window throttle backed by SQL — failure counters survive process restarts. Soft delay after 5 failures, 15-min hard lockout after 10. Returns `429 + Retry-After`. Events go to the audit log. Background pruner trims old rows on a timer. |
| Brute-force on API tokens | Per-IP `auth` rate-limit policy (10/min) on the login + token-mint routes; per-token bucket on the API surface (200 token bucket, refills 100/10s). |
| DDoS / slow-loris | Kestrel `MaxConcurrentConnections`, `MaxRequestBodySize 256KB`, `KeepAliveTimeout 60s`, `RequestHeadersTimeout 15s`, `Min*DataRate 100 B/s` with grace. |
| Clickjacking / MIME sniff / framing | `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy`, `Permissions-Policy`, `Cross-Origin-Opener/Resource-Policy: same-origin`. |
| Cross-site script injection | Strict CSP. JSON API routes get `default-src 'none'`; Blazor portal gets the framework-required CSP. |
| Token / password storage | PBKDF2-SHA256 @ **600,000 iterations** (OWASP 2023). API tokens are stored as SHA-256 of the raw value; raw values are shown once at mint. |
| Token sprawl | Tokens default to a **90-day expiry** when no explicit value is set. |
| Server fingerprinting | `X-Powered-By` / `X-AspNet-Version` stripped; Kestrel server header off. |
| HTTPS | HSTS issued only over HTTPS; `UseHttpsRedirection` wires only when HTTPS is actually configured. |
| Auth bypass via URL form | A bearer token whose `TenantId` doesn't match the URL slug is rejected with **403 insufficient_scope** — a leaked token can't be aimed at a different connection just by changing the URL. |
| Cookie auth + API mixing | `/scim/`, `/api/v1/`, `/sql/v1/`, `/ars/v1/` return clean `401 + WWW-Authenticate: Bearer` instead of an HTML login redirect — SCIM clients don't get surprise HTML. |
| Stack-trace leak | Production exception handler returns a generic error + correlation id; details land in logs only. JSON responses for API routes, HTML for portal routes. |
| Audit | Login successes / failures / lockouts, token mints, admin CRUD, and mass-cleanup operations write to the AuditLogs table with IP, user-agent, and correlation id. |
| Portal lockout | Portal admin accounts live in a **separate `PortalAdmins` table** from SCIM `Users`. No data operation against `Users` can invalidate the portal login. |

Documented exclusions (don't run this in production without addressing these):
- No external secret management — `Jwt:SecretKey` is read from local config.
- No client-cert pinning for outbound HTTP. The SQL emulator and ARS proxy targets connect with the credentials in config.
- CI vulnerability scan ships (`.github/workflows/security.yml` — weekly + per-PR `dotnet list package --vulnerable` + GitHub `dependency-review-action`); no SBOM artifact yet.

See [`docs/ISSUES_AND_IMPROVEMENTS.md`](docs/ISSUES_AND_IMPROVEMENTS.md) for the full list.

---

## Quick start (laptop)

```bash
git clone https://github.com/jacobmaloney/Conduit.git
cd Conduit
dotnet run --project src/Conduit.Web
```

The first time, the app sees no configured database and routes you to **`/setup`**:

1. Paste a SQL Server connection string (LocalDB or a real instance — anything Dapper can open).
2. Pick a database name. The wizard offers to create it if it doesn't exist.
3. Pick a portal admin username + password. These live in the dedicated **`PortalAdmins`** table, kept separate from the SCIM `Users` table so a "Delete All Users" can't lock you out of your own server.

After setup, navigate to **`/connected-systems`** and click **Seed Demo**. You'll get:

- **Default** (slug `default`) — empty, your sandbox
- **IT Helpdesk Portal** (slug `it-helpdesk`, domain `it.demo.local`) — 25 users, 5 groups, includes the SOD trap pair
- **Finance Suite** (slug `finance-suite`, domain `finance.demo.local`) — empty target for finance app provisioning
- **HR Connect** (slug `hr-connect`, domain `hr.demo.local`) — empty target for HR app provisioning
- Five fixed-value tokens (raw values are documented in the Endpoints modal):
  - `admin-token` → Admin scope, can reach any slug
  - `it-helpdesk-token` → scoped to IT Helpdesk Portal
  - `finance-suite-token` → scoped to Finance Suite
  - `hr-connect-token` → scoped to HR Connect
  - `ars-proxy-token` → scoped to the `/ars/v1` surface

That's enough to do every SCIM call against a real server.

---

## API at a glance

There are **two URL forms** for the SCIM surface and they're functionally identical:

| Form | Example | Tenant determined by | When to use |
|---|---|---|---|
| **Slug** (recommended) | `/scim/v2/t/it-helpdesk/Users` | The slug in the URL | Real apps. Mistakes are visible at the URL — a leaked token can't be aimed at a different system. |
| **Token-only** (legacy) | `/scim/v2/Users` | The bearer token's `TenantId` | Demos. Useful when one client speaks to one connection only. |

When both are present, the URL wins — and a token whose `TenantId` doesn't match the slug returns **403 insufficient_scope** instead of silently re-scoping.

### Authentication

Every API call wants `Authorization: Bearer scim_<value>`. The raw value is shown **once** at creation time on `/tokens` — there's a "Test this token now" button right in the create dialog that hits `/scim/v2/ServiceProviderConfig` so you can verify before walking away.

### Worked examples

**List users in a Connected System** (cURL):
```bash
curl -H "Authorization: Bearer scim_demo-it-2024" \
     -H "Accept: application/scim+json" \
     "http://localhost:5000/scim/v2/t/it-helpdesk/Users?count=10"
```

**Create a user** (PowerShell):
```powershell
$headers = @{
    "Authorization" = "Bearer scim_demo-it-2024"
    "Content-Type"  = "application/scim+json"
}
$body = @{
    schemas  = @("urn:ietf:params:scim:schemas:core:2.0:User")
    userName = "jsmith"
    name     = @{ givenName = "John"; familyName = "Smith" }
    emails   = @(@{ value = "jsmith@example.com"; primary = $true })
    active   = $true
} | ConvertTo-Json -Depth 5

Invoke-RestMethod -Method Post `
    -Uri "http://localhost:5000/scim/v2/t/it-helpdesk/Users" `
    -Headers $headers -Body $body
```

**Disable a user** (SCIM PATCH):
```powershell
$body = @{
    schemas    = @("urn:ietf:params:scim:api:messages:2.0:PatchOp")
    Operations = @(@{ op = "replace"; path = "active"; value = $false })
} | ConvertTo-Json -Depth 5

Invoke-RestMethod -Method Patch `
    -Uri "http://localhost:5000/scim/v2/t/it-helpdesk/Users/<user-id>" `
    -Headers $headers -Body $body
```

**Filter by userName**:
```
GET /scim/v2/t/it-helpdesk/Users?filter=userName%20eq%20%22jsmith%22
```

Open any Connected System in the UI → **Endpoints** to get a copy-pasteable cURL + PowerShell example pre-filled with the active slug and token for that system.

---

## Solution layout

```
Conduit/
├── src/
│   ├── Conduit.Core/                       — Domain models, SCIM filter parser, generation
│   ├── Conduit.DataAccess/                 — Dapper repositories + DatabaseMigrator
│   ├── Conduit.Web/                        — Blazor portal + REST controllers + middleware
│   ├── Conduit.Installer/                  — Optional install wizard
│   └── Conduit.Emulator.GoogleWorkspace/   — Separate process: Google Workspace
│                                                Admin SDK emulator (standalone; not started by default)
├── Database/                                  — CreateDatabase.sql + emulator schema
├── docs/                                      — API reference, dev guide, issues log
└── docker-compose.yml                         — Optional containerized run
```

### Tech stack
- **Framework:** ASP.NET Core 8.0
- **UI:** Blazor Server + custom design system overlay on Bootstrap
- **ORM:** Dapper (no EF Core)
- **Database:** SQL Server (LocalDB / Express / full)
- **Auth:** Cookie session for the portal, `scim_*` API tokens for the SCIM/REST surfaces, with a `SmartAuth` policy scheme that picks per-request based on headers

### Auth model in one paragraph

The `ApiTokenAuthMiddleware` runs after `UseRouting` and sees `Authorization: Bearer scim_*`. Validation hashes the raw value, looks it up in `ApiTokens`, and writes `TenantId` + `Scope` into `HttpContext.Items`. If a URL slug is present, it's authoritative — a token scoped to a different tenant gets 403. For browser sessions there's a Cookie scheme that's wired so any request to `/scim/`, `/api/v1/`, `/sql/v1/`, or `/ars/v1/` without a Bearer header gets a clean `401 + WWW-Authenticate: Bearer` instead of an HTML login redirect.

### Pipeline order

```
Kestrel (limits)
  → UseExceptionHandler (prod-only JSON/HTML split)
  → UseHsts                       (HTTPS only)
  → UseHttpsRedirection           (only when HTTPS configured)
  → SecurityHeadersMiddleware
  → UseStaticFiles
  → UseRouting
  → UseSetupCheck
  → UseCors
  → UseRateLimiter                (auth / scim / anon / global)
  → ApiTokenAuthMiddleware        (Bearer scim_*)
  → UseAuthentication             (SmartAuth → JWT or Cookie)
  → UseAuthorization
  → ScimConnectionLoggingMiddleware
  → MapBlazorHub + MapControllers + MapRazorPages
```

---

## Configuration

The Setup wizard writes everything — you don't normally edit JSON. The keys it touches:

```jsonc
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=.;Database=Conduit;Trusted_Connection=True;TrustServerCertificate=True;"
  },
  "JwtConfig": {
    "SecretKey": "<auto-generated; do not check into git>",
    "Issuer": "Conduit",
    "Audience": "ConduitAPI",
    "ExpirationMinutes": 60
  },
  "Cors": {
    "AllowedOrigins": [],
    "AllowedMethods": [ "GET", "POST", "PUT", "PATCH", "DELETE" ],
    "AllowedHeaders": [ "Authorization", "Content-Type" ]
  }
}
```

If `Cors:AllowedOrigins` is empty (the default) nothing cross-origin is allowed — the right default for an identity service. Add origins to permit specific SCIM clients.

---

## Database migrations

The schema is managed by `Conduit.DataAccess.DatabaseMigrator` — no EF Core. It runs on every app start and applies anything newer than the last `SchemaVersion` row. As of today:

- **v1** — initial schema (CreateDatabase.sql)
- **v2–v3** — schema repairs for GroupMembers and Groups.OwnerId
- **v4–v5** — `ApiTokens` shape + `IsAdmin` on Users (now superseded)
- **v6 (v8)** — multi-tenant Tenants table + per-row `TenantId`
- **v7 (v9)** — `SqlAccounts` table for the `/sql/v1/` emulator
- **v10** — **PortalAdmins** table separation: portal/web-UI admin accounts move out of `Users` so SCIM data ops can never lock the operator out
- **v11** — `LoginAttempts` + `LoginLockouts` tables for the persistent brute-force throttle

Migrations are idempotent; running them against an already-current database is a no-op.

---

## Demoing this thing

A useful 5-minute path through the UI:

1. `/setup` → create the DB
2. `/connected-systems` → click **Seed Demo**
3. Pick a system in the upper-left switcher → `/users` and `/groups` now scope to it
4. `/quick-connect` (or any system's **Endpoints** modal) → copy a cURL example
5. Make a SCIM POST → see the new user in `/users` immediately
6. PATCH it `active=false` → status pill turns red in the UI

The page title shows **the active connection** on every page, the sidebar shows live **user + group counts** for the active system, and every code block in the Endpoints modal has a one-click copy button.

---

## What's not done yet

Honest list. See [`docs/ISSUES_AND_IMPROVEMENTS.md`](docs/ISSUES_AND_IMPROVEMENTS.md) for detail.

- **Bulk operations** — `/scim/v2/Bulk` endpoint is on the roadmap, not shipped.
- **Audit log surface** — events are written to the DB but the UI viewer is minimal.
- **Google Workspace emulator** — separate ASP.NET project (`Conduit.Emulator.GoogleWorkspace`), not started by the main process. Has its own Admin SDK-style endpoints + OAuth2 service-account token flow.
- **ARS proxy execution** — `/ars/v1/...` accepts and logs requests; PowerShell handoff to the live Administration Service is the next iteration.
- **External secret management** — `Jwt:SecretKey` in local config; no Key Vault integration yet.

---

## Docker

```bash
# dev
docker-compose -f docker-compose.dev.yml up

# prod-shape
docker-compose up
```

Or build directly:
```bash
docker build -t conduit .
docker run -p 5000:5000 conduit
```

---

## License

MIT — see [`LICENSE`](LICENSE).

## Acknowledgments

- SCIM 2.0 (RFC 7642 / 7643 / 7644) — IETF
- ASP.NET Core team
- Dapper contributors
- The Google Workspace emulator companion project was contributed by [Claude Cowork](https://claude.com/coworking)

---

**Status:** active development, demo-grade today. Suitable for non-production identity demos, lab environments, and SCIM client testing. Production-grade hardening — persistent rate counters, external secret management, full audit UI — is on the roadmap; see the issues log for the gap.
