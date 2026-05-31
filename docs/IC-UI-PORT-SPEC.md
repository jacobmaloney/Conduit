# Conduit Sync-UI Port Spec — Faithfully Mirror IdentityCenter

**Status:** PLAN ONLY (no code yet). Authored 2026-05-31.
**Goal:** Conduit's sync UI must faithfully mirror IdentityCenter's working sync surface — **same pages, same names, same buttons, same logic**. IC is the gold standard ("working super sweet"). Conduit's UI drifted because it was rebuilt from the SCIMServer fork's instincts instead of mirrored from IC.

**Root cause of the drift:** IC anchors its entire sync experience on ONE tabbed hub — `SystemCenter.razor` ("Synchronization Center", `/admin/system-center`, **5,549 lines**) with sections **Connections / Projects / History / Tags / Modules**. Conduit dropped this hub and shattered it into 3 separate, renamed pages. Restore the hub and the rest falls into place.

> Honesty rule (per project memory): compiles/boots ≠ works. Each item below is "done" only when the page renders AND its buttons perform their real action against the DB/engine, verified on a live run. Verify before recording done.

---

## Reference trees
- **IC (gold standard):** `C:/Users/jacob/source/repos/IdentityCenter/Software/WebPortal/`
- **Conduit (target):** `C:/Users/jacob/source/repos/Conduit/src/Conduit.Web/`
- Conduit existing sync pages: `Pages/Sync/SyncProjects.razor`, `Pages/Sync/SyncHistory.razor`, `Pages/ConnectedSystems.razor`, `Pages/QuickConnect.razor`, `Pages/Setup.razor`
- Conduit nav: `Shared/NavMenu.razor`

---

## 1. Page inventory & mapping (IC → Conduit)

| IC page | IC file | Lines | Conduit target | Current status |
|---|---|---|---|---|
| **Synchronization Center** (hub) `/admin/system-center` | `Pages/Admin/SystemCenter.razor` | 5549 | NEW `Pages/Sync/SynchronizationCenter.razor` | **MISSING** — build it |
| Sync Projects `/admin/sync-projects` | `Pages/Admin/SyncProjects.razor` | 1758 | `Pages/Sync/SyncProjects.razor` | REDESIGNED — reconcile + add missing buttons |
| Sync Run History (per project) `/admin/sync-projects/{id}/runs` | `Pages/Admin/SyncProjectRuns.razor` | 445 | `Pages/Sync/SyncHistory.razor` | RENAMED/MERGED — align |
| Sync Run Details / Live Progress `.../runs/{runId}` | `Pages/Admin/SyncProjectRunDetails.razor` | 1263 | `Pages/Sync/SyncHistory.razor` detail / NEW detail page | DOWNGRADED — port live trace viewer |
| Sync Audit Logs (step-level) `.../step/{stepRunId}/audit` | `Pages/Admin/SyncAuditLogs.razor` | 560 | NEW drill-down in SyncHistory | **MISSING** |
| **Processing Center** `/admin/processing-center` | `Pages/Admin/ProcessingCenter.razor` | 3246 | NEW `Pages/Sync/ProcessingCenter.razor` | **MISSING** — job/queue/exec-server/agent monitor (this is the "Process Center" the user meant) |
| Schedule Manager `/admin/schedule` | `Pages/Admin/ScheduleManager.razor` | 2648 | NEW `Pages/Sync/ScheduleManager.razor` | MISSING as page (only inline modal today) |
| Quick Config wizard `/admin/quick-config` | `Pages/Admin/QuickConfig.razor` | 1286 | `Pages/QuickConnect.razor` (+`Setup.razor`) | RENAMED + thinner |
| Connections (= SystemCenter §connections) | inside `SystemCenter.razor` §191 | — | `Pages/ConnectedSystems.razor` | DIVERGED — wrong model (see §3) |

**Out of sync scope (do NOT port — IC governance):** ProcessCenter.razor (workflow orchestration), BulkOperations, NetworkScanConfig. License/SignIn/Usage/AppRole sync steps remain IC-only by the open-core split.

---

## 2. Naming drift to correct (IC name wins)

| Current Conduit name | Rename to (IC vocabulary) |
|---|---|
| Connected Systems | **Connections** (or "Directory Connections") |
| Quick Connect | **Quick Config** (confirm — Quick Connect may be an intentional keep) |
| Sync History (as top nav) | keep, but surface it as a **tab inside Synchronization Center** like IC |
| *(no hub)* | add **Synchronization Center** as the sync home |

**SCIM-origin chrome to decide fate of** (IC has no parallel — likely hide/remove for the directory-sync product): Connected-System **switcher** in NavMenu (`Shared/NavMenu.razor:32,88-143`), **"Open access"** auth pill, **API Explorer** (`Pages/ApiExplorer.razor`), **Tokens** (`Pages/Tokens.razor`), **Generate Users/Groups** (`UserGeneration.razor`/`GroupGeneration.razor`). Recommend gating these behind a dev/emulator flag, not on the main sync nav.

---

## 3. Per-page button/logic gaps

### Sync Projects (`Pages/Sync/SyncProjects.razor`)
IC inline actions (`SyncProjects.razor:1699-1751`): Run Now, **Stop Sync**, **View Live Progress**, Schedule, View Run History, **Copy Template**, Manage, Delete.
- Conduit HAS: Run Now, Edit, Delete, Schedule, inline history.
- **ADD: Stop Sync, View Live Progress, Copy Template.**

### Sync Run Details (`Pages/Sync/SyncHistory.razor` detail)
IC (`SyncProjectRunDetails.razor:561,754-803`): live **trace logs** with Enable/Disable Trace, live indicator, **per-step error modals**.
- Conduit HAS: live badge, Cancel/Retry on async jobs (`SyncHistory.razor:282-293`).
- **ADD: trace-log viewer (enable/disable + live tail), step-level audit drill-down (port `SyncAuditLogs.razor`).**

### Connections (`Pages/ConnectedSystems.razor`) — biggest model drift
Conduit currently exposes SCIM/REST endpoint URLs, token copy, "Quick Sync into browser tenant", Legal Hold, Clear Data (`ConnectedSystems.razor:145-183,418-472`) — **wrong model**.
IC connections = AD/Entra **directory-bind** connections with **Test Connection (LDAP bind)** + schema discovery.
- **RECONCILE: present IC's directory-connection model.** Conduit already added a Test-connection probe in `951eced` — reuse it. Keep the encrypted connector-shaped credential capture from `6ab380e`. Strip/hide the SCIM-server endpoint/token chrome from the primary connections view.

---

## 4. Processing Center (the "Process Center" the user named)

There are TWO IC pages — the sync-relevant one is **ProcessingCenter**, not ProcessCenter:
- `ProcessingCenter.razor` (`/admin/processing-center`, **3,246 ln**) — job-execution monitoring, queue management, execution-server status strip, remote-agent ops (`:12-20,67-80`). Injects `IJobHistoryService`, `IExecutionServerRegistry`, `IDistributedJobQueue`, `IScheduleService`, `ISyncRepository`. **PORT THIS.**
- `ProcessCenter.razor` (`/admin/process-center`, 1093 ln) — workflow/approval orchestration. Governance. **Do NOT port.**

Conduit has neither (zero matches for processing-center / ExecutionServer / JobQueue / JobHistory in `Conduit.Web`). It only folded async-job Cancel/Retry/Live into SyncHistory.

**DECISION (2026-05-31): Conduit Processing Center is SINGLE-NODE / LOCAL-JOBS ONLY for now.** It monitors only the jobs Conduit itself is running locally — job history, the in-process queue, run/cancel/retry. **No remote agents, no execution-server fleet** in this pass.
- **DO NOT port** `IExecutionServerRegistry` / distributed multi-node queue / remote-agent ops from IC. Drop the "Execution Servers" status strip and remote-agent UI from the IC page when mirroring.
- **DO build** a thin local equivalent: a `IJobHistory` (run records: type, project, start/end, status, counts, error) + an in-process `IJobQueue` (enqueue/running/queued/cancel) backed by the existing async-job infrastructure (`AsyncJobPollerService`).
- **Future-proof the seam (don't paint into a corner):** the user intends to LATER connect external workers to Conduit to perform certain jobs. So model the job source behind an interface (e.g. a `JobOrigin`/node-id field on job records, default `"local"`) and keep the queue abstraction node-agnostic — so a remote-worker registry can be added later WITHOUT reshaping the schema or the page. Build single-node, but leave the door open.

---

## 5. Prioritized port punch list (pages first, dependency order)

1. **(L) Synchronization Center hub** → `Pages/Sync/SynchronizationCenter.razor`. Mirror `SystemCenter.razor` tab structure (Connections / Projects / History). Make it Conduit's sync home in NavMenu. Reconcile the 3 existing pages as the tab bodies (don't leave them orphaned). **Everything hangs off this — do it first.**
2. **(L) Connections (directory-bind model)** → reconcile `ConnectedSystems.razor`. Rename to Connections, add Test Connection (LDAP bind) + schema discovery, strip SCIM-server chrome from the primary view. Biggest naming + logic drift.
3. **(M) Sync Projects parity** → add Stop Sync, View Live Progress, Copy Template to `Pages/Sync/SyncProjects.razor`.
4. **(M) Run Details + live trace logs** → port trace-log viewer + step-level Sync Audit Logs drill-down into the run detail view.
5. **(M) Processing Center** → `Pages/Sync/ProcessingCenter.razor` + net-new but THIN backend: local job history + in-process queue only (run/cancel/retry/status). **Single-node, local jobs only** (decided 2026-05-31) — no exec-server registry, no remote agents this pass. Keep the job-origin/queue abstraction node-agnostic so remote workers can attach later. Sized M (not L) because the fleet/remote half is cut.
6. **(S) Naming pass** → NavMenu + page titles to IC vocabulary; decide fate of SCIM-origin chrome (Tokens/API Explorer/Open-access pill/switcher).
7. **(M) Schedule Manager** → full page (Conduit has inline modal only). Port if cron/calendar management matters for demo.

**Suggested execution order:** 1 → 6 (naming, cheap, pairs with the hub) → 2 → 3 → 4 → 7 → 5 (Processing Center last; it carries net-new backend).

---

## Design-system note
Conduit folded IC's design library into `Conduit.Web` (commit `b22488c`, self-contained). When mirroring IC pages, reuse IC's shared components where Conduit has equivalents (Template1 header, FilterTabs, ListPanel, PersonRow, IcSearchBox, StatRow). Match IC's `<Template1>` header rule and `container-fluid identities-container` page-wrapper pattern. Check `Shared/Design/` and `Shared/PageHeader.razor` for the Conduit-side equivalents before rebuilding.

## Verification gate (before any item is "done")
- Page renders under the hub with IC name.
- Each button performs its real action (Run Now fires a run, Stop cancels, Test Connection binds, View Live Progress streams) against the live engine/DB.
- Confirmed on a live run against lab SQL 192.168.1.30 — not just compile/boot.
