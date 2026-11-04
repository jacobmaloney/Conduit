# Changelog

All notable changes to Conduit. Format roughly follows [Keep a Changelog](https://keepachangelog.com).

## [Unreleased]

### Added — dynamic groups, the agent's evaluator (DYNAMIC-GROUPS-01, IdentityCenter UC034)
- `DynamicGroupRules` (Conduit.Connectors.ActiveDirectory): reads the Active Roles dynamic-group definition a group carries in `accountNameHistory` (`<DynamicGroupXML Conditions="[type;objectGUID;filter]...[DG]" .../>`), the managed-unit grammar; the seven rule types; the precedence for conflicting rules; `Decide` and `Diff`. A blob that does not parse is refused with the reason.
- `ActiveDirectoryDynamicGroupEvaluator` (`IConnectorDynamicGroupEvaluator`): re-reads the group by objectGUID, runs each rule by LDAP search (explicit object, a group's direct members, a paged subtree search with the filter verbatim), reads `member` whole by ranged retrieval, and reports current, desired, adds and removes. Read-only. Opened on the sink's new `OpenConnectionAsync` (one bind path).
- `AdAgentDynamicGroupExecutor`: the `EvaluateDynamicGroup` agent command, read-only, same gates as BrowseContainers; the poller dispatches it; the agent advertises `AdDynamicGroups`.
- Tests: `DynamicGroupRulesTests`, `AdAgentDynamicGroupExecutorTests`.

### Added — dynamic groups, the rebuild (DYNAMIC-GROUPS-03, IdentityCenter UC034)
- `ActiveDirectorySink.ModifyMember`: the one delta change of a member on a group on an open connection (Add/Delete on `member`, idempotent on "already so"); `AddGroupMemberAsync`/`RemoveGroupMemberAsync` resolve by GUID and call it.
- `ActiveDirectoryDynamicGroupEvaluator.RebuildAsync`: evaluates, then writes each add and remove through `ModifyMember` on the same connection, reading each member's objectGUID first; refuses the whole rebuild on a static group, an unevaluated rule or a capped search; reports every change's outcome.
- `AdAgentDynamicGroupExecutor` runs `RebuildDynamicGroup` too: the payload's operation must match the command; the target must be a group and never a privileged group (`AdAgentWriteExecutor.IsPrivilegedGroup`, now internal). The poller dispatches it.

### Added — dynamic groups, Convert and the rules write (DYNAMIC-GROUPS-04, IdentityCenter UC034)
- `DynamicGroupRules.Serialize` writes the definition in the service's own spelling (the lab blob round-trips byte for byte) under IdentityCenter's own service GUID; `IsWritable` admits a group's six rule types with an object and a sound filter.
- `ActiveDirectoryDynamicGroupEvaluator.SetRulesAsync`: on a conversion every member is removed first, one change each and reported; then one modify replaces `accountNameHistory` and stamps `info`. `ConvertToStaticAsync` deletes the definition and the stamped Notes; the members stay.
- `AdAgentDynamicGroupExecutor` runs `SetDynamicGroupRules` (rules capped at 64, each checked before any LDAP call) and `ConvertDynamicGroupToStatic`, one result shape for the three writes. The poller dispatches both.

### Added — dynamic groups, the clock (DYNAMIC-GROUPS-05, IdentityCenter UC034-D09)
- `DynamicGroupUpdaterJob` (a scheduled job, one tick a minute): per active Active Directory connection, a scheduled rebuild of every dynamic group this product originated (`DynamicGroups:RebuildSchedule`, a cron expression in UTC as a sync project's, default `0 3 * * *`; a connection never rebuilt waits for the next instant) and a change watch (`DynamicGroups:ChangeWatchMinutes`, default 5; the directory is asked whether anything changed since the last look, and a change runs the same rebuild). `DynamicGroups:OwnGroupsOnly` (default on) leaves the other service's groups to its own clock; `DynamicGroups:Enabled` turns the clock off. The marks live in `SystemConfiguration` (`DynamicGroups:Clock:{tenant}:LastRebuildUtc` / `LastWatchUtc`).
- A rebuild that changed something is reported to IdentityCenter as a completed `RebuildDynamicGroup` command of the agent's own (`POST /api/agent/commands/report`, the channel key; `IcAgentWriteReporter`), with `trigger` `Schedule` or `DirectoryChange`, so the catalog edges and the history rows follow through the one settler. An older server answers 404 and the catalog follows on the next sync.
- `IConnectorDynamicGroupEvaluator.ListAsync` (every group carrying a definition, with its originating service) and `AnyChangeSinceAsync` (one entry with `whenChanged` past the instant); the AD evaluator implements both on the sink's bind. `ActiveDirectorySink.ReadDefaultNamingContext` and `ActiveDirectorySource.ToGeneralizedTime(DateTime)` shared rather than copied. `SinkConnectionCredentialMapRepository.GetSourceConnectionNamesByTenantAsync` (the name a rebuild is reported under).
- `AdAgentDynamicGroupExecutor` implements `IDynamicGroupClockWork`: the clock rebuilds through the same evaluator resolution and the same write guard (resolve, a group, never privileged) as the commands.
- Tests: `DynamicGroupUpdaterJobTests` (what is due, the baseline look, the watch, the pass, own groups only, the report shape, never throws), `AdAgentDynamicGroupExecutorTests` extended.

### Changed — agent attribute allow-list (ALLOW-LIST-02, IdentityCenter UC015-D06 / UC017-F06)
- `AdAgentWriteExecutor` SetAttributes now also writes `employeeID`, `employeeType`, `employeeNumber`, `accountExpires`, `homeDirectory`, `homeDrive` and `extensionAttribute1`-`15` (22 -> 43 names). `userAccountControl` stays out: Enable/Disable are their own operations.
- New `AdAttributeValueShaper`: the one place a sent value takes the shape AD stores. `accountExpires` accepts a date (`yyyy-MM-dd` = end of that day, DC-local, as ADUC stores it), an ISO 8601 date-time, a FILETIME, `0` or `never`; anything else refuses the whole command before any LDAP call. Used by SetAttributes and by the create executor's attribute bag.
- `AdAgentCreateExecutor` no longer forbids `accountExpires` in the bag (an expiry date is not an enable/UAC shape); a contractor's account is created with its end date. The password/enable/UAC keys stay forbidden.
- Tests: `AdAttributeValueShaperTests` (shaper cases, the create object carries a FILETIME, the allow-list and the order of shaping before the first write pinned by source); the two create-executor test lists updated. IdentityCenter's `DirectoryAttributeCatalogTests` mirror the list from this source in the same change.
- REUSE-PASS-01: `AdFileTime` (Conduit.Connectors.ActiveDirectory) is the one owner of FILETIME text in both directions; `ActiveDirectorySource`'s private read converter and the shaper's write conversion both moved into it. `SourceMappingEdit.IsAdTimestamp` stays (SourceBrowsing does not reference the connector; a three-line validity check).

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
