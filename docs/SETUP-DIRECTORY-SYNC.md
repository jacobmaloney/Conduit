# Setting up directory sync to IdentityCenter

This guide walks a customer through installing **Conduit** and syncing an on-premises
or cloud directory (**Active Directory** or **Microsoft Entra ID**) into an
**IdentityCenter** tenant. When you're done, users and groups from your directory
flow into IdentityCenter automatically on a schedule.

Conduit is the free, self-hosted **sync engine**. It runs inside your network (so your
directory credentials never leave your environment), reads your directory, and pushes
the objects up to your IdentityCenter tenant over HTTPS.

> **Status:** Conduit is demo/pilot-grade today and is meant to run in a lab or a
> controlled pilot, not yet as unattended production infrastructure. See
> [Known limitations](#known-limitations) before a production rollout.

---

## At a glance

```
  Your directory                Conduit (runs in YOUR network)          IdentityCenter (SaaS)
 ┌──────────────┐   read over    ┌───────────────────────────┐   HTTPS   ┌──────────────────┐
 │ Active Dir.  │ ───LDAP──────► │  pump: read → map → push  │ ────────► │  your tenant      │
 │  or Entra ID │ ───Graph─────► │  (credentials stay local) │  (443)    │  Users / Groups   │
 └──────────────┘                └───────────────────────────┘           └──────────────────┘
```

You'll do five things:

1. **[Prepare](#1-prerequisites)** the host, network, and directory credentials.
2. **[Install](#2-install-conduit)** Conduit and complete first-run setup.
3. **[Enroll](#3-enroll-against-your-identitycenter-tenant)** Conduit against your IdentityCenter tenant (one code, one time).
4. **[Connect](#4-connect-your-source-directory)** your source directory.
5. **[Create, run, and schedule](#5-create-run-and-schedule-the-sync)** the sync — then verify.

Budget about **30–45 minutes** for a first run.

---

## 1. Prerequisites

### Host

| Requirement | Notes |
|---|---|
| Windows Server or Windows 10/11 | Recommended — required if you want to run Conduit as a service and to reach Active Directory. Linux works for Entra-only syncs. |
| .NET 8 Runtime | Or use the self-contained build, which bundles the runtime. |
| SQL Server | LocalDB, Express, or full. Conduit keeps its own small database here (config, credentials, sync history). This is **not** your directory. |

### Network

| From Conduit to… | Port | Why |
|---|---|---|
| Your IdentityCenter API (`https://…`) | 443 | Enrollment + pushing synced objects |
| A domain controller (AD sync) | 389 / 636 | LDAP / LDAPS read |
| Microsoft Graph (Entra sync) | 443 | `graph.microsoft.com` |

### Directory credentials (pick the one you're syncing)

**Active Directory** — a **read-only service account**:
- The DC hostname or IP, and the LDAP port (636/LDAPS recommended).
- The account's bind username and password.
- The base DN(s) you want to sync, e.g. `OU=People,DC=corp,DC=example,DC=com`.
- No special privileges beyond directory read.

**Microsoft Entra ID** — an **app registration** in your tenant with:
- **Tenant ID**, **Client ID**, and a **Client Secret**.
- Application (not delegated) Microsoft Graph permissions, admin-consented:
  `User.Read.All`, `Group.Read.All`, `GroupMember.Read.All`
  (or `Directory.Read.All`, which covers all three).

### From IdentityCenter

- An **enrollment code** for your tenant. Generate it in the IdentityCenter tenant
  portal (Agents / Connectors area). Codes are **single-use** and expire — generate a
  fresh one right before you enroll.

---

## 2. Install Conduit

**Option A — run from source (quickest for a pilot):**

```bash
git clone https://github.com/jacobmaloney/Conduit.git
cd Conduit
dotnet run --project src/Conduit.Web
```

**Option B — run the published executable:** unzip the release and run `Conduit.Web.exe`.

On first launch Conduit has no database configured and sends you to **`/setup`**:

1. Paste a SQL Server connection string (LocalDB or a real instance).
2. Choose a database name — the wizard offers to create it if it doesn't exist.
3. Create a **portal admin** username and password. (These live in a separate
   `PortalAdmins` table, so directory data operations can never lock you out.)

When setup finishes you'll land in the portal. The database schema migrates itself on
every start — there's nothing to run by hand.

---

## 3. Enroll against your IdentityCenter tenant

Enrollment is a one-shot, single-use-code handshake that wires this Conduit instance to
your IdentityCenter tenant. It creates the IdentityCenter connection (the sync
destination) and starts the background agent, which begins its heartbeat and picks up
sync work on its own.

Run Conduit with the enrollment arguments (use the URL and code from your tenant portal):

```
Conduit.Web.exe --enroll-url=https://api.YOUR-TENANT.certification-center.com --enroll-code=XXXX-XXXX-XXXX-XXXX
```

Equivalents:
- **appsettings:** set `Enroll:Url` and `Enroll:Code` (command line wins if both are set).
- **Already running?** Use the **Configuration → IC Agent** panel in the portal.

What a successful enrollment does: persists an `IdentityCenter` connection plus the
encrypted credentials it received (base URL, shared key, and a per-agent key), then the
agent poller claims the connection on its next tick — heartbeat and sync start with no
further configuration.

**Notes**
- Enrollment is **idempotent**: on later restarts the consumed code is not re-sent.
- A **403** means the code is invalid, expired, or already used — generate a new one.
- Enrollment failures never block startup; the result shows read-only on the
  **Configuration → IC Agent** panel.
- After a successful enroll, **remove the code** from the command line / appsettings —
  it's consumed, and leaving it there is needless residue.

---

## 4. Connect your source directory

In the portal, open **Connected Systems** and add your directory as a new connection,
choosing the connector type:

- **Active Directory** — enter the DC host, port, base DN(s), and the read service
  account's bind username + password.
- **Entra ID** — enter the Tenant ID, Client ID, and Client Secret from your app
  registration.

Conduit stores these credentials **encrypted in its own database** (a per-instance
credential keyring); they are never written to disk in plain text and never sent to
IdentityCenter — only the synced objects are.

**Verify the connection before moving on:** for AD, Conduit can browse your directory
tree so you can confirm the base DNs resolve; for Entra, a successful test means the app
registration and Graph permissions are correct. If a browse or test fails, it's almost
always (a) a wrong base DN, (b) missing admin consent on the Graph permissions, or
(c) a network/port block — fix that here rather than later.

---

## 5. Create, run, and schedule the sync

A **sync project** ties a **source** (the directory you just added) to the **sink**
(your enrolled IdentityCenter connection).

1. **Create a sync project.** Choose your directory as the source and IdentityCenter as
   the target.
2. **Pick what to sync.** Select the object classes (typically `user` and `group`) and
   set the **scope** — the base DN subtrees to include or exclude (AD), and an optional
   LDAP filter to narrow the set.
3. **Map attributes** — accept the generated defaults to start (e.g. `displayName`,
   `mail`, `userPrincipalName`). You can refine mappings later.
4. **Run it once, on demand.** Watch the run in the sync logs.
5. **Verify.** A healthy run reports objects **read** and **created/updated** with zero
   errors. Then open your **IdentityCenter tenant** and confirm the users and groups
   appear. (A run that reads **0 objects** is reported as **failed** on purpose — that
   almost always means the scope/filter matched nothing; widen the base DN.)
6. **Schedule it.** Set a cron schedule on the project (e.g. hourly or nightly). After
   the first full run, subsequent runs are **incremental** — only changes are carried,
   so they're fast.

---

## Run Conduit as a Windows service

For a pilot that should survive reboots, install Conduit as a service. Include the
enrollment arguments on the service binary path the first time:

```
sc.exe create IdentityCenterConduit binPath= "\"C:\path\to\Conduit.Web.exe\" --enroll-url=https://api.YOUR-TENANT.certification-center.com --enroll-code=XXXX-XXXX-XXXX-XXXX" start= auto
sc.exe start IdentityCenterConduit
```

After the first successful start, edit the `binPath` to **remove the enroll code** (it's
already consumed):

```
sc.exe config IdentityCenterConduit binPath= "\"C:\path\to\Conduit.Web.exe\""
```

Run the service under an account that can reach your directory (for AD, a domain account
with directory-read rights is simplest).

---

## Verification checklist

- [ ] Portal reachable and you can log in as the portal admin.
- [ ] **Configuration → IC Agent** shows the tenant connected and a recent heartbeat.
- [ ] Source connection tests/browses successfully.
- [ ] A manual sync run reports objects read + created/updated, **0 errors**.
- [ ] Users and groups are visible in your IdentityCenter tenant.
- [ ] A schedule is set and a scheduled run has completed at least once.

---

## Troubleshooting

| Symptom | Likely cause & fix |
|---|---|
| Enrollment returns **403** | Code invalid/expired/already used — generate a fresh one in the tenant portal. |
| IC Agent panel shows "not connected" | Enrollment didn't complete, or no network to the IdentityCenter API — check outbound 443 and re-enroll from **Configuration → IC Agent**. |
| Entra connection test fails | App registration missing **admin-consented** application Graph permissions (`User.Read.All`, `Group.Read.All`, `GroupMember.Read.All`), or a wrong Tenant/Client ID/secret. |
| AD browse/test fails | Wrong base DN, bad bind credentials, or LDAP port blocked (try 636/LDAPS). |
| Sync run reports **0 objects / failed** | The scope/filter matched nothing. Widen the base DN or relax the LDAP filter. This is a deliberate "don't report false success" guard, not a crash. |
| Objects sync but don't appear in IdentityCenter | Confirm the sink is the correct IC connection and the agent heartbeat is recent; check the sync run log for sink write errors. |
| Nothing runs on schedule | Confirm the project is **enabled** and has a cron schedule, and that Conduit (or its service) is running continuously. |

---

## Known limitations

Be honest with pilot customers about the current state:

- **Demo/pilot-grade.** Suitable for a lab or a supervised pilot, not yet unattended
  production infrastructure.
- **Installer is not externally code-signed yet** — expect an OS "unknown publisher"
  prompt when launching the executable.
- **Secrets:** directory credentials are encrypted in Conduit's database keyring, but
  the app's own signing key is read from local config (no external secret manager yet).
- **Bulk SCIM, a full audit-log UI, and the ARS write-back handoff** are on the roadmap,
  not shipped.

For the full engineering detail, see the top-level [`README.md`](../README.md) and
[`docs/ISSUES_AND_IMPROVEMENTS.md`](ISSUES_AND_IMPROVEMENTS.md).
