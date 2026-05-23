# Changelog

All notable changes to Conduit. Format roughly follows [Keep a Changelog](https://keepachangelog.com).

## [Unreleased]

### Changed — demo seed
- Replaced demo Connected Systems (Kodak — Entra ID Staging, Internal App Demo) with three department-based apps for the Unite 2026 ARS attribute-based provisioning demo: **IT Helpdesk Portal** (`it-helpdesk`), **Finance Suite** (`finance-suite`), **HR Connect** (`hr-connect`).
- Seed users + groups + SOD trap pair now land in IT Helpdesk Portal; Finance Suite and HR Connect are empty targets the ARS workflow provisions into.
- Fixed-value tokens: `it-helpdesk-token` (`demo-it-2024`), `finance-suite-token` (`demo-finance-2024`), `hr-connect-token` (`demo-hr-2024`) replace the old Kodak / Internal App tokens. `admin-token` and `ars-proxy-token` unchanged.
- All references to `kodak-entraid` / `demo-kodak-2024` / `internal-app` / `demo-internal-2024` removed from README, ConnectedSystems modal, placeholder text, and code comments.

### Added — persistent brute-force throttle + CI
- Migration **v11**: `LoginAttempts` + `LoginLockouts` tables. `LoginThrottle` rewritten as a SQL-backed Scoped service — failure counters survive restarts. Fails open on DB outage so a SQL blip doesn't lock everyone out.
- `LoginThrottlePruner` background service trims old rows every 30 min.
- GitHub Actions: `build.yml` (Release with `/warnaserror`) and `security.yml` (weekly + PR + push vulnerability scan via `dotnet list package --vulnerable` + `dependency-review-action`).
- Cleared the last CS1998 warnings in `Conduit.Installer` so `/warnaserror` is clean across the whole solution.

### Added — security hardening pass
- Rate limiting with four named policies (`auth`, `scim`, `anon`, plus a per-IP global fallback). `Retry-After` + JSON envelope on 429.
- `LoginThrottle` — per-(user, IP) sliding-window soft-delay + hard lockout. Login page returns 429.
- `SecurityHeadersMiddleware` — HSTS (HTTPS only), CSP varies by route, X-Frame-Options DENY, X-Content-Type-Options nosniff, Referrer-Policy, Permissions-Policy, Cross-Origin-Opener/Resource-Policy. Strips X-Powered-By / X-AspNet-Version. Kestrel server header off.
- Kestrel limits — MaxConcurrentConnections, MaxRequestBodySize 256KB, header caps, KeepAlive/RequestHeaders timeouts, Min*DataRate against slow-loris.
- PBKDF2 iterations bumped 100k → 600k (OWASP 2023); `Verify` accepts both costs for backward compatibility.
- Default token expiration of 90 days when caller doesn't specify.
- Two-track production exception handler: HTML routes → `/Error`, API routes → JSON envelope with correlation id, no stack-trace leak.
- Audit log entries for login success / failure / lockout (`Login.Succeeded`, `.Failed`, `.Lockout` with IP + user).

### Added — portal/data separation
- Migration **v10**: new `PortalAdmins` table for portal/web-UI admin accounts; existing `IsAdmin=1` users copied over and removed from `Users` (with FK cleanup) so SCIM data operations cannot invalidate the portal login.
- `PortalAdminRepository` (CRUD + `MarkLoggedInAsync`); `LoginService` rewritten against it.
- `PortalAdminRepository.DeleteAsync` refuses to delete the last active admin.
- `SetupService.CreateAdminUserAsync` writes to `PortalAdmins`.

### Added — token-create UX
- Modal shows the full `Bearer scim_xxx` header (raw value as secondary field).
- "Test this token now" button hits `/scim/v2/ServiceProviderConfig` and reports status before close.
- "I've copied the token" checkbox gates the Done button — dialog cannot close without acknowledgment.

### Added — URL-slug routing
- All SCIM controllers accept both `/scim/v2/...` (token-scoped) and `/scim/v2/t/{slug}/...` (slug-scoped) forms.
- `ApiTokenAuthMiddleware` rewritten to enforce URL-vs-token consistency. Mismatched scope returns **403 insufficient_scope**.
- Cookie scheme returns clean `401 + WWW-Authenticate: Bearer` for `/scim/`, `/api/v1/`, `/sql/v1/`, `/ars/v1/` instead of HTML login redirect.

### Added — connection-aware UI
- `<PageHeader>` with always-visible "Connected to …" chip + Quick Connect.
- Sidebar shows live Users / Groups counts for the active Connected System; `DataChangeNotifier` event bus refreshes on every mutation.
- Data Cleanup modal got a scope radio: "This Connected System only" vs "Entire system" — was silently sweeping all tenants.
- `ConnectedSystems` Endpoints modal has copy buttons on URLs, bearer headers, and code blocks.

### Fixed
- `GroupRepository.MapToScimGroup` cast `record.Version` (int column) to string — Groups page no longer 500s.
- `UserRepository.DeleteAsync` transactionally clears FK references (`GroupMembers`, `Groups.OwnerId`, `Users.ManagerId`) so bulk delete doesn't trip FK_Groups_Owner.
- Manager column on Users actually populates: `ResolveManagerDisplayNamesAsync` batch-loads display names; mapper now includes `EnterpriseExtension` whenever any enterprise field (incl. `ManagerId`) is set.
- `Users.razor` Edit modal got a searchable Manager picker.
- User generator deduplicates usernames + emails per run AND across runs (pre-loaded from tenant).
- `GenerationService` persists the generator's manager hierarchy in a second pass.
- `Tokens.razor`: build warning fixes; cleared modal state on open/close.
- `Logs.razor`: async-without-await warning.
- `BaseRepository.ExecuteScalarAsync<T>` return type now `T?`.
- VS BrowserLink crash: launch profile disables hot-reload by default.

## [0.1.0] — initial public-shape

Multi-tenant SCIM 2.0 server with portal UI, demo seed, and built-in user generator.
