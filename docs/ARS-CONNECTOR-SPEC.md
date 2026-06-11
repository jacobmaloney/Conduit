# Active Roles (ARS) Connector for Conduit — design spec

**Status:** **Phase 1 SHIPPED** (commit `d4b23fe`, branch `feature/ic-sync-ui-port`) — proven live against ARS on .56
**Author:** UNITE 2026 work
**Audience:** whoever builds the connector

> **GATE RESULT (2026-06-11):** Write-mechanism **#3 (S.DS.Protocols → AR LDAP port) was tested
> live and is DEAD** — ARS does not speak LDAP on its service port 15172 (bind fails "LDAP server
> unavailable"); 389/636 are the raw AD DC and return NO virtual attributes (bypass ARS). The chosen
> + proven mechanism is **#1, the AR ADSI provider** (`System.DirectoryServices` `EDMS://` DirectoryEntry
> + `CommitChanges`): reads resolve virtual attributes through ARS, and a toxic write is **denied by the
> SoD policy** with the message surfaced via `DirectoryServicesCOMException`. **Deployment constraint:**
> the connector RUNS only on a host with the **Active Roles ADSI provider / Management Tools** installed
> (compiles anywhere; publish self-contained since the AR server may lack the .NET runtime). Phase 1 =
> `src/Conduit.Connectors.ActiveRoles/` (Adapter/Source/Sink/credential) + standalone CLI harness
> `src/Conduit.Connectors.ActiveRoles.Cli/` + one DI line in Program.cs.

Add a connector that lets Conduit read from and write to **One Identity Active
Roles (ARS)** — usable as a sync **source** or **target**. The point is *not* "another
AD connector": it's that writes go **through the Active Roles Administration Service**,
so on every write the ARS **policies, workflows, and virtual attributes fire**. Sync a
user in, set a role, and a Separation-of-Duties policy can **deny the toxic combination
mid-sync**. This is the open-core story in one pipe — Conduit (free sync) feeding
Active Roles (governance) — and it pairs directly with the RBAC + SoD components in
`UNITE-2026/RBAC-SOD/`.

---

## 1. Goals / non-goals

**Goals**
- A `SystemType = "ActiveRoles"` connector, both `SupportsSource` and `SupportsSink`.
- **Target (sink):** write users / groups / group-membership / role flags **through
  ARS** so all policy/workflow/VA logic applies. A policy-rejected write surfaces as a
  failed object in the Conduit run log with the ARS message (e.g. the SoD deny text).
- **Source:** read ARS-managed objects **including virtual attributes**, fast.
- Honor Conduit's existing plumbing: incremental cursor, tombstones (opt-in), skip-
  unchanged hashing, per-credential context, the auto-mapping catalog.

**Non-goals (initially)**
- Replacing the raw-AD connector (it stays; it's the "bypass ARS" path).
- ARS configuration management (creating policies/VAs/workflows) — that's MMC /
  the `ars-expert` tooling, not a sync connector.
- Approval-workflow interaction beyond "the write succeeded or was rejected."

---

## 2. Why not just use the existing AD connector

Conduit already has `Conduit.Connectors.ActiveDirectory` (source + sink, raw LDAP to a
DC). That connector **bypasses Active Roles** — it talks to AD directly, so no ARS
policy runs and virtual attributes are invisible. The ARS connector is the deliberate
opposite:

| | AD connector (existing) | **ARS connector (this spec)** |
|---|---|---|
| Write target | the DC, raw LDAP | the **AR Administration Service** |
| Policy / workflow on write | none | **SoD, provisioning, group rules all fire** |
| Virtual attributes | invisible | first-class (read + write) |
| Use it when | ARS isn't in the path | ARS owns governance and you want it enforced on sync |

---

## 3. The access-method model (the speed crux)

ARS reads and writes want **different paths**. This asymmetry is the heart of the
design (and the subject of AJ Lindner's UNITE speed material — fold his numbers in
here when available).

| Operation | Path | Latency | Notes |
|---|---|---|---|
| **Bulk read (source)** | Direct **AD LDAP** (S.DS.Protocols, paged) **+ ARS SQL `CVSAValues`** join for virtual attributes | **ms/object** | Bypasses the AR service — no per-object policy eval. Fast enough for full enumeration of large directories. |
| Read, policy-applied | ARS ADSI / the AR service's LDAP endpoint / AR REST | **slow** | Every object round-trips the AR service applying read policy + resolving VAs natively. Only use when you specifically need the policy-applied view. |
| **Write (sink)** | **Through the AR service** (one of §5 options) | slower per write, **required** | This is where SoD / workflows / VA logic fire. Non-negotiable — it's the entire value of the connector. |

**Design rule:** the **source defaults to the fast direct path** (LDAP + `CVSAValues`),
with an optional "policy-applied" toggle. The **sink always goes through the AR
service**. Write volume is small relative to read volume, so the write latency is
acceptable; read volume is where you'd die going through the service, so you don't.

---

## 4. How it plugs into Conduit

The connector model is clean; a new connector is **3 classes + one DI line**. Nothing
else needs wiring (registry, UI dropdown, credential store, cursor/tombstone/skip-
unchanged are all generic).

### 4.1 Classes (new project `src/Conduit.Connectors.ActiveRoles/`)

```
ActiveRolesAdapter.cs    : IConnectorAdapter        (~100 LOC)
ActiveRolesSource.cs     : IConnectorSource         (~600-1000 LOC; ~80% forkable from ActiveDirectorySource)
ActiveRolesSink.cs       : IConnectorSink (+ ITombstoneEmittingSink)  (~600-1000 LOC)
ActiveRolesCredential.cs : credential DTO (host/port/bind/password + direct-read AD/SQL fields)
```

### 4.2 The adapter (declares identity + capabilities)

```csharp
public sealed class ActiveRolesAdapter : IConnectorAdapter
{
    public string SystemType  => "ActiveRoles";
    public string DisplayName => "Active Roles";
    public bool SupportsSource => true;
    public bool SupportsSink   => true;

    public ConnectorCapabilities Capabilities { get; } = new()
    {
        SupportsBulk        = false,   // ARS writes are per-object (the service serializes policy)
        MaxBatchSize        = 1,
        SupportsIncremental = true,    // whenChanged / USN cursor on the read side
        SupportsCreate      = true,    // provisioning through ARS (Phase 3)
        SupportsMove        = true,
        SupportsResetPassword = true,
        SupportsAssignManager = true,
    };

    public IReadOnlyList<CredentialTypeInfo> CredentialTypes => /* see 4.4 */;
    public IReadOnlyList<TenantFieldRequirement> TenantFieldRequirements => /* Domain */;

    public IConnectorSource? CreateSource(Guid tenantId) => new ActiveRolesSource(tenantId, ...);
    public IConnectorSink?   CreateSink(Guid tenantId)   => new ActiveRolesSink(tenantId, ...);
}
```

### 4.3 The contracts (from `Conduit.Sync/Connectors/IConnectorAdapter.cs`)

**Source** — `ReadAsync(objectClass, scope, ct)` streams `ConnectorObject`s;
`EnumerateAsync(objectClass, scope, cursor, ct)` is the incremental path returning a
`SyncEnumerationResult { Objects, ResolveNewCursor, IsIncremental, WasCompleteRead }`;
`TestConnectionAsync`. **`WasCompleteRead()` must only be true on a proven full,
untruncated read — it gates tombstone safety.**

**Sink** — `UpsertAsync(obj, ct)` create-or-update by `SourceId`; `DeleteAsync` (opt);
`TestConnectionAsync`; plus optional `ITombstoneEmittingSink.EmitTombstonesAsync` for
soft-deletes, and the Phase-3 provisioning methods (`CreateAsync`/`MoveAsync`/
`ResetPasswordAsync`/`AssignManagerAsync`).

**Object in transit** — `ConnectorObject { string SourceId; string ObjectClass;
Dictionary<string,object?> Attributes; }`. Tombstone marker = `_deleted=true` in
`Attributes`. The orchestrator remaps source-named attrs → sink-named attrs via the
`AttributeMappings` table between read and write.

### 4.4 Credentials (generic store; just declare the fields)

Stored per-tenant, AES-GCM, via `CredentialProtector` + `ConnectionCredentialRepository`
(no schema change). The adapter declares a `CredentialTypeInfo` named e.g. `"ars"` with
`CredentialFieldSpec`s. Because reads and writes use different endpoints, the credential
blob carries **both**:

```
arsServiceHost      (req)         the AR Administration Service host
arsServicePort      (req, dflt)   the AR service's directory/LDAP port (e.g. 15172)
bindUser            (req)         the AR-side service account (DOMAIN\user or UPN)
bindPassword        (req, secret)
--- fast direct-read path (optional; enables the ms-latency source) ---
adHost              (opt)         a DC to read raw LDAP from (defaults to the domain)
arsSqlConnString    (opt, secret) read-only conn to the ARS config DB for CVSAValues (virtual attrs)
readMode            (enum)        "fast" (direct LDAP+SQL) | "policy" (through the AR service)
```

### 4.5 Wire-in points (the whole list)

1. **`src/Conduit.Web/Program.cs`** (~line 414): one line —
   `builder.Services.AddScoped<IConnectorAdapter, ActiveRolesAdapter>();`
2. **`AttributeTemplateCatalog.cs`** (optional): add `("ActiveRoles","User"/"Group")`
   attribute→canonical maps so the UI auto-suggests mappings (incl. the VA columns).
3. **`Conduit.sln` / project ref** from `Conduit.Web`.
4. Everything else — registry, the Connected Systems dropdown, credential UI from the
   field specs, cursor/tombstone/skip-unchanged — is **automatic**.

---

## 5. The write-path decision (ARS-native — owner's call)

How `ActiveRolesSink` actually talks to ARS. All four apply policy; they differ in host
dependencies and modernity. **This is the one real decision — the ARS architect owns
it.** AJ's speed comparison likely ranks these.

| # | Mechanism | Pros | Cons |
|---|---|---|---|
| 1 | **AR ADSI provider** (Aelita COM, `LDAP://<ars>/<dn>` via the AR provider, `System.DirectoryServices`) | the classic, complete policy path | COM; the AR ADSI provider must be installed on the Conduit host; legacy |
| 2 | **AR Management Shell runspace** (`Set-QADObject -Proxy`) | exactly what the lab uses; well-supported | hosts PowerShell; the shell must be installed; heavier per call |
| 3 | **S.DS.Protocols bound to the AR service's LDAP port** (`:15172`-style) | **same code shape as the AD connector** — point it at the service instead of the DC; writes through it fire policy; no COM/shell | reads through it are the "slow" path (use direct for bulk); confirm the service's LDAP endpoint accepts the writes/VAs you need |
| 4 | **AR REST API (8.x)** or **ARSGateway** fronting ARS | clean HTTP, no COM/shell on Conduit, productizable; dovetails with the ARSGateway project | another service in the path; HTTP per object |

**Recommendation:** **#3 for the prototype** (maximal reuse of the AD connector's
S.DS.P code, real policy enforcement, fewest host dependencies), **#4 / ARSGateway as
the productized path**. Pick before Phase 1 — it sets the sink's bind/connection code.

---

## 6. The read path

- **Fast (default):** reuse `ActiveDirectorySource`'s paged S.DS.P enumeration against a
  DC for real AD attributes, then **left-join virtual-attribute values from the ARS
  config DB `CVSAValues`** (keyed by object GUID) into the `ConnectorObject.Attributes`.
  This yields the full ARS-managed view (real + virtual) at AD-LDAP speed.
- **Policy-applied (toggle):** enumerate through the AR service endpoint (§5 #3/#4)
  instead. Slower; use only when read-side policy/scoping must be honored.
- **Incremental cursor:** `whenChanged >= <generalizedTime>` (or `highestCommittedUSN`)
  — same approach as the AD source. The cursor is an opaque string in the workflow step;
  `ResolveNewCursor()` returns the new watermark after a complete read.
- **Object classes:** `user`, `group` (+ `computer` if wanted). Group membership read as
  `member` / resolved per the existing AD source's membership handling.

---

## 7. Incremental + tombstones (safety)

- Tombstones are **opt-in** (`ActiveRolesSink : ITombstoneEmittingSink`) and only fire
  when `WasCompleteRead() == true` on the source pass — never on a partial/errored read.
  Honor Conduit's per-class `SinkRecordHashes` scoping (the V26 fix) so a multi-class
  ARS project never tombstones across classes.
- On the read side, AD recycle-bin tombstones (`isDeleted`) should be emitted as
  `_deleted` ConnectorObjects only on incremental passes, mirroring the AD source — and
  the sink must route `_deleted` to the tombstone path, never upsert them.

---

## 8. Phasing

**Phase 1 — ARS as TARGET (the headline, ~1–2 days).** Sink only. Pick the §5 write
mechanism, implement `UpsertAsync` (create/update user + set role VAs + group
membership through ARS) + `TestConnectionAsync`. Map a Conduit sync **AD → Conduit →
ARS**. *Demo:* a user receives two conflicting roles and the **SoD policy denies it
during the sync** — the failed object shows the SoD message in the Conduit run log.

**Phase 2 — ARS as SOURCE (~1–2 days).** Fast read (direct LDAP + `CVSAValues`) so
virtual attributes flow *out* of ARS into Conduit (and onward to IdentityCenter, the
emulator, etc.). Add the incremental cursor.

**Phase 3 — polish.** Tombstones, provisioning steps (`CreateAsync`/`MoveAsync`/
`ResetPasswordAsync`), the policy-applied read toggle, the attribute-template catalog
entries, and a `TestConnectionAsync` that proves both the read and write endpoints.

---

## 9. The demo payoff

This closes the loop on the whole UNITE story. Conduit — the **free** sync engine —
pushes a user into Active Roles, and the **RBAC + SoD spine** (the published
`RBAC-SOD/` components) enforces governance on the synced data **in real time**: a
toxic role pairing is rejected mid-sync with the human-readable SoD reason. Free sync,
paid governance, demonstrated in a single run.

---

## 10. Open questions (for the ARS architect / AJ)

1. **Write mechanism (§5):** #3 (AR service LDAP port) vs #4 (REST/ARSGateway) for the
   prototype? This gates the sink's connection code.
2. **Does the AR service's LDAP endpoint accept the writes we need** (VA sets, group
   membership) with full policy, or are some operations ADSI/REST-only?
3. **`CVSAValues` read:** confirm the table/columns for virtual-attribute values on the
   current 8.3.0.548 build and the GUID join key (the schema-map table names varied
   after the reinstall — verify against the live DB).
4. **AJ's speed numbers:** drop the actual ADSI-vs-LDAP-vs-REST latency figures into §3.
5. **Role model:** are roles the boolean VAs (like `RBAC-SOD/`) or AD group membership,
   or both? Determines what `UpsertAsync` writes to trigger entitlements.

---

## Appendix — Conduit reference (templates & seams)

| Purpose | Path |
|---|---|
| Connector interfaces | `src/Conduit.Sync/Connectors/IConnectorAdapter.cs` |
| Object DTO | `src/Conduit.Sync/Connectors/ConnectorObject.cs` |
| Registry (auto-collects by SystemType) | `src/Conduit.Sync/Connectors/ConnectorRegistry.cs` |
| Credential encrypt/store | `src/Conduit.Sync/Security/CredentialProtector.cs`, `src/Conduit.DataAccess/Repositories/ConnectionCredentialRepository.cs` |
| Per-credential context | `src/Conduit.Sync/Security/CredentialNameContext.cs` |
| DI registration site | `src/Conduit.Web/Program.cs` (~line 414) |
| Auto-map catalog | `src/Conduit.Sync/Templates/AttributeTemplateCatalog.cs` |
| **Source template** | `src/Conduit.Connectors.ActiveDirectory/ActiveDirectorySource.cs` (~1100 LOC, paged S.DS.P + cursor + tombstone) |
| **Sink template** | `src/Conduit.Connectors.ActiveDirectory/ActiveDirectorySink.cs` (~900 LOC, LDAP modify) |
| Adapter template (both source+sink) | `src/Conduit.Connectors.ActiveDirectory/ActiveDirectoryAdapter.cs` (~117 LOC) |
| Simplest sink ref | `src/Conduit.Connectors.Emulator/EmulatorSink.cs` (255 LOC) |
| Orchestrator (pump) | `src/Conduit.Sync/Orchestration/SyncProjectOrchestrator.cs` |
| Per-class hash scoping | `src/Conduit.DataAccess/Repositories/SinkRecordHashRepository.cs` |

**Related:** `UNITE-2026/RBAC-SOD/` (the policy/VA/WI this connector would feed),
`UNITE-2026/active-roles-expert/` (the ARS access/deploy knowledge), and the ARSGateway
master plan (the fast-read + ARS-write precedent this design follows).
