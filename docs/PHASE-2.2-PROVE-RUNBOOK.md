# Phase 2.2 Prove — AD → Conduit → IdentityCenter dual-run + tombstone

**Runbook for Jacob. Setup is already wired (config written to the lab DBs). You run the test.**

Prepared by Picard's setup pass. Phase 2.2 code is committed/pushed: **IC `f1650dd6`**, **Conduit `2d90d12`**.
Everything below is repeatable; the SQL config rows are idempotent.

---

## 0. TL;DR — what's already done for you

| Thing | State | Where |
|---|---|---|
| IC Admin API key (raw captured) | **Minted** in `IdentityCenter15.ApiKeys` | see §2 for the raw key |
| Conduit IC **sink** Connected System | **Created** (`SystemType=IdentityCenter`) | Conduit `Tenants`, Id `A1C0DE00-…-022` |
| Conduit IC sink **credential** | **Stored**, AES-GCM with Conduit's pinned key | `ConnectionCredentials`, name `identitycenter` |
| Conduit **sync project** AD → IC | **Created, enabled, NOT run** | `SyncProjects`, Id `B2C0DE00-…-022` |
| Project scope + 9 attribute mappings | **Created** | `SyncProjectScopes` / `AttributeMappings` |
| Conduit Admin **scim_ token** (raw captured) | **Minted** in `Conduit.ApiTokens` | see §2 for the raw token |
| IC `Objects.CreatedAt/ModifiedAt` columns | **Repaired** (were missing on .56 — see §7) | additive, non-destructive |

**Source = "Lab AD"** (`11111111-1111-1111-1111-111111111111`, the proven-good AD source on .56 — it
read 32 users successfully today). **Sink = IdentityCenter (Phase 2.2)**. Distinct source/sink → satisfies
the new same-source/sink guard. ObjectClass = `User`.

**IC Objects BEFORE-count for the Conduit connection = `0`** (no `SourceType='Conduit'` rows exist yet).
Total live Objects before the run = **3303** (3010 AD users-as-objects + 293 groups). IC's own internal AD
sync is **untouched** — this is purely additive (a new `Conduit` DirectoryConnection gets auto-seeded on
first push), so the dual-run is safe.

---

## 1. Start the three apps

> All three read the lab at **192.168.1.56** (SQL + AD). Connection strings for IC live in **dotnet
> user-secrets**, not appsettings. Make sure **.56 is up** first (it flaps — see §7 troubleshooting).

### IC.API (the sink target — must be reachable by Conduit)
```powershell
cd C:\Users\jacob\source\repos\IdentityCenter\Software\IdentityCenter.API
dotnet run -c Debug --no-launch-profile --urls "http://localhost:5062"
```
- Dev URL: **http://localhost:5062** (HTTP only on purpose — see note).
- **Why http-only:** the Conduit sink credential is configured with `BaseUrl=http://localhost:5062`.
  IC.API has `UseHttpsRedirection()`; if you let it bind an HTTPS port it will 307-redirect Conduit's
  POSTs. Binding HTTP-only makes the redirect a harmless no-op (you'll see one
  `Failed to determine the https port for redirect` warning — ignore it).
- Swagger (optional sanity): http://localhost:5062/swagger

### IC WebPortal (to eyeball results in the UI — optional but recommended)
```powershell
cd C:\Users\jacob\source\repos\IdentityCenter\Software\WebPortal
dotnet run
```
- Open the launched URL, go to **Directory Objects** (the Objects page). You'll filter for the new
  `Conduit` source after the run.
- NOTE: WebPortal boot also runs IC migrations against .56. That's fine and expected.

### Conduit (the pump + trigger API)
```powershell
cd C:\Users\jacob\source\repos\Conduit\src\Conduit.Web
dotnet run
```
- Dev URL: **http://localhost:5500** (sync UI). Admin login: **admin / <conduit-admin-password — redacted>**.
- Conduit DB = `Conduit` on .56.

---

## 2. Credentials (already configured vs. what you paste)

### IC Admin API key — RAW (already stored in the Conduit sink; you do NOT need to paste it anywhere)
```
<IC-ADMIN-API-KEY — redacted; stored as a SHA-256 hash in IdentityCenter15.ApiKeys and already encrypted into Conduit's `identitycenter` credential. Keep the raw value in a local secure note only.>
```
- Stored in `IdentityCenter15.ApiKeys` as SHA-256 hash (KeyType=Admin, Scopes=`admin`, `ic_` prefix).
- Already encrypted into Conduit's `identitycenter` credential (BaseUrl `http://localhost:5062` + this key).
- IC's API expects header **`X-API-Key: <raw>`**, scheme `ApiKey`, policy `AdminPolicy` (requires the
  `admin` scope — this key has it).
- Keep it only if you want to curl IC directly; the sink already has it.

### Conduit Admin scim_ token — RAW (paste this into the trigger curl in §3, Option B)
```
<CONDUIT-ADMIN-BEARER-TOKEN — redacted; stored as a hash in Conduit.ApiTokens. Keep the raw scim_ token in a local secure note only.>
```
- Stored in `Conduit.ApiTokens` as SHA-256/base64 hash, **Scope=Admin, TenantId=NULL** → authorizes any
  sync project's run endpoint.

---

## 3. Run the AD → IdentityCenter project

Project Id: **`B2C0DE00-0000-4000-8000-000000000022`** — name **"AD -> IdentityCenter (Phase 2.2 prove)"**.

### Option A — UI (recommended)
1. Conduit at **http://localhost:5500**, log in (admin / <conduit-admin-password — redacted>).
2. Go to **Sync Projects** (Synchronization).
3. Find **"AD -> IdentityCenter (Phase 2.2 prove)"**.
4. Click **Run** (Run-Now). Watch live progress / run history; wait for **Succeeded**.

### Option B — Trigger API (curl)
```bash
curl -i -X POST http://localhost:5500/api/v1/sync-projects/B2C0DE00-0000-4000-8000-000000000022/runs \
  -H "Authorization: Bearer <CONDUIT-ADMIN-BEARER-TOKEN — redacted; stored as a hash in Conduit.ApiTokens. Keep the raw scim_ token in a local secure note only.>" \
  -H "Content-Type: application/json" \
  -d "{\"reason\":\"phase22-prove\"}"
```
Expect **202 Accepted** `{"projectId":"..."}`. The run is fire-and-forget; poll run status:
```bash
curl -s http://localhost:5500/api/v1/sync-projects/B2C0DE00-0000-4000-8000-000000000022/runs?limit=1 \
  -H "Authorization: Bearer <CONDUIT-ADMIN-BEARER-TOKEN — redacted; stored as a hash in Conduit.ApiTokens. Keep the raw scim_ token in a local secure note only.>"
```
Look for `"status":"Succeeded"` with `recordsCreated`/`recordsUpdated` > 0.

> The project scope caps at **MaxObjects=100**, LdapFilter `(&(objectClass=user)(objectCategory=person))`,
> BaseDN `DC=Domain,DC=Local`. Small, bounded, fast.

---

## 4. Verify success

### 4a. IC Objects count rose for the Conduit connection (SQL)
Before = **0**. After a successful run it should equal the number of AD users Conduit pushed (≤100).
```sql
-- run against IdentityCenter15 on 192.168.1.56
-- Conduit-projected rows are tagged SourceType='Conduit' and carry OriginalSource='ActiveDirectory'
SELECT COUNT(*) AS conduit_live
FROM Objects
WHERE SourceType = 'Conduit' AND DeletedAt IS NULL;

-- breakdown + provenance
SELECT TOP 20 Username, DisplayName, Email, Department, OriginalSource,
       ManagerSourceId, ManagerObjectId, CreatedAt
FROM Objects
WHERE SourceType = 'Conduit' AND DeletedAt IS NULL
ORDER BY CreatedAt DESC;

-- the auto-seeded connection (created by IC's bulk endpoint on first push)
SELECT Id, Name, ConnectionType, IsActive
FROM DirectoryConnections
WHERE Name = 'Conduit';
```

### 4b. See them in the IC WebPortal
- **Directory Objects** page → filter / search. The Conduit-pushed users appear as `user` objects.
  Their typed columns (DisplayName, Email, Department, JobTitle, Username) are populated because the
  project maps AD → IC PascalCase columns (see §6). `OriginalSource` shows `ActiveDirectory`.

### 4c. Confirm post-processing fired (person-link + manager graph)
IC's bulk endpoint auto-enqueues post-processing (person-match + manager resolution) per touched
connection. After the run, give it a few seconds, then:
```sql
-- ManagerObjectId populated => manager-DN (ManagerSourceId) was resolved to an IC Objects row
SELECT COUNT(*) AS managers_resolved
FROM Objects
WHERE SourceType = 'Conduit' AND DeletedAt IS NULL AND ManagerObjectId IS NOT NULL;

-- person/identity links (person-match step)
SELECT COUNT(*) AS identity_linked
FROM Objects
WHERE SourceType = 'Conduit' AND DeletedAt IS NULL AND IdentityId IS NOT NULL;

-- audit trail of the ingest (Conduit-tagged)
SELECT TOP 20 Timestamp, OperationType, EntityType, Source, Success
FROM ChangeAuditLogs
WHERE Source IN ('Conduit-Bulk-API','Conduit-Tombstone')
ORDER BY Timestamp DESC;
```
> Manager resolution only links a manager that is **also in the same Conduit batch/connection**. With a
> 100-user cap some managers may sit outside the slice — partial `managers_resolved` is expected, not a bug.

---

## 5. TOMBSTONE test (OPTIONAL — this is the destructive half; AD mutations are yours to make)

This proves the Phase 2.2 reversible soft-delete. **Picard did NOT do any AD create/delete** — these are
the only steps that mutate AD. Do them on a throwaway account.

### 5a. Create a throwaway AD test user
PowerShell on a box with the AD module / RSAT, or run remotely against .56:
```powershell
# domain\administrator / <AD-ADMIN-PASSWORD — redacted>  — base DC=Domain,DC=Local
New-ADUser -Name "p22 tombstone" -SamAccountName "p22tomb" `
  -UserPrincipalName "p22tomb@domain.local" `
  -Path "CN=Users,DC=Domain,DC=Local" `
  -DisplayName "P22 Tombstone" -GivenName "P22" -Surname "Tombstone" `
  -AccountPassword (ConvertTo-SecureString "<AD-ADMIN-PASSWORD — redacted>" -AsPlainText -Force) `
  -Enabled $true -Server 192.168.1.56
```
ADUC alternative: Users container → New User → sam `p22tomb`, UPN `p22tomb@domain.local`, enabled.

### 5b. Run the project, confirm the test user landed in IC
- Run the project (§3). Then:
```sql
SELECT Username, DisplayName, SourceUniqueId, DeletedAt
FROM Objects
WHERE SourceType='Conduit' AND Username='p22tomb';   -- DeletedAt should be NULL (live)
```

### 5c. Delete the AD user, re-run, confirm IC soft-deletes it
```powershell
Remove-ADUser -Identity "p22tomb" -Server 192.168.1.56 -Confirm:$false
```
- Run the project again (§3). Conduit detects `p22tomb` disappeared from a **complete** source read and
  POSTs a tombstone to IC `/api/objects/tombstones` (Override=false).
```sql
-- DeletedAt should now be SET (soft-deleted), IsActive=0. Row is NOT hard-deleted (reversible).
SELECT Username, DisplayName, DeletedAt, IsActive
FROM Objects
WHERE SourceType='Conduit' AND Username='p22tomb';

-- the tombstone audit row
SELECT TOP 5 Timestamp, OperationType, Source, NewValue
FROM ChangeAuditLogs
WHERE Source='Conduit-Tombstone' ORDER BY Timestamp DESC;
```
- **Reversibility bonus:** recreate `p22tomb` in AD and re-run → IC clears `DeletedAt` (revive), audited
  as "Revived".

---

## 6. What the project maps (for reference)
Project-level pass-through with explicit AD→IC typed-column mappings (no Phase-7 workflow; orchestrator
runs the legacy single-pass):

| AD source attr | IC sink column |
|---|---|
| sAMAccountName | Username (required) |
| userPrincipalName | UserPrincipalName |
| displayName | DisplayName |
| mail | Email |
| givenName | FirstName |
| sn | LastName |
| department | Department |
| title | JobTitle |
| manager | ManagerSourceId (→ resolved to ManagerObjectId by IC post-processing) |

`_source` is auto-stamped `ActiveDirectory` by the orchestrator → lands in IC `OriginalSource`.
IC tags everything it ingests with `SourceType='Conduit'`.

---

## 7. Troubleshooting

- **Key auth 401 from IC.API.** Two causes: (1) `X-API-Key` header missing/typo'd, or (2) **.56 SQL is
  unreachable** — IC validates the key via a DB query, and if SQL is down the validation throws and the
  request 401s. Check .56 is up (`Test-NetConnection 192.168.1.56 -Port 1433`) before blaming the key.
  The key itself is verified-good (see §8).
- **Sink unreachable / Conduit "Test Connection" fails.** Conduit's sink Test calls IC
  `GET /api/objects/query`. Make sure IC.API is running on **http://localhost:5062** and `.56` is up.
  If you changed the IC port, update the sink credential (the stored `BaseUrl`).
- **Partial source read → no tombstones (by design).** Conduit only emits tombstones after a **complete**
  source read. If the AD read was truncated/incremental, delete-detection is skipped and nothing is
  tombstoned — that's the safety contract, not a failure.
- **The 50% cap.** IC aborts a tombstone batch (returns `Aborted=true`, deletes nothing) if it would
  soft-delete >50% of the connection's live objects. Conduit never sets Override. For the single-user
  tombstone test this never trips. If you ever see `Aborted`, it means the delta looked too big — re-check
  the source read was complete and correct.
- **`Invalid column name 'ModifiedAt' / 'CreatedAt'` 500s.** This was the fresh-deploy schema gap on .56
  (V003 self-skipped because Objects didn't exist yet when V003 "ran", then V004 created Objects without
  those columns; the migrator recorded V003 as applied so it never re-runs). **Already repaired** by this
  setup pass (additive `ALTER TABLE Objects ADD CreatedAt/ModifiedAt` + backfill from FirstSyncedAt /
  LastSyncedAt). If you ever rebuild IdentityCenter15 from scratch, this gap returns — re-apply
  `DataAccessLibrary/Migrations/Scripts/V003__AddObjectCreatedAtModifiedAt.sql` by hand, or fix the
  migrator's V003/V004 ordering. (Do not git-commit a code change without review.)
- **.56 flaps.** The lab box drops SQL/LDAP intermittently. If a run fails mid-flight with connection
  errors, just wait for .56 and re-run — all setup config is persisted in the DBs.

---

## 8. Light-verify results from the setup pass (what Picard already confirmed)
- IC API key **authenticates**: `GET /api/objects/query` with **no** key → **401**; **with** the minted
  key → IC log shows `API key authenticated: Conduit Phase 2.2 Prove (Admin)`. Auth path PROVEN.
- After the §7 schema repair, the query endpoint stops 500-ing (re-confirm yourself once .56 is back if
  the box was down at handoff).
- Conduit IC sink credential **round-trips** (AES-GCM encrypt+decrypt verified against the pinned key in
  `%PROGRAMDATA%\Conduit\credential.key`) — the running Conduit process will decrypt it cleanly.
- Conduit setup committed: IC sink tenant (SystemType=IdentityCenter), credential `identitycenter`
  (106-byte ciphertext), project (Lab AD → IC, enabled), 9 mappings, Admin scim token.
- **NOT yet proven (left for you):** the actual AD→IC write run and the tombstone round-trip. No full
  sync was run by the setup pass.

---

## 9. IDs cheat-sheet
| Name | Id |
|---|---|
| Conduit source tenant "Lab AD" | `11111111-1111-1111-1111-111111111111` |
| Conduit sink tenant "IdentityCenter (Phase 2.2)" | `A1C0DE00-0000-4000-8000-000000000022` |
| Conduit sync project "AD -> IdentityCenter (Phase 2.2 prove)" | `B2C0DE00-0000-4000-8000-000000000022` |
| IC AD DirectoryConnection "Domain.local" | `BA58B468-E9DB-4F79-8CBA-36366097F848` |
