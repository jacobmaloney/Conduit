# Conduit Phase 0 — Handoff

**Status as of 2026-05-22:** Phase 0 done except one environmental verification step. Pick this up in a new session.

## What exists now

- `C:\Users\jacob\source\repos\Conduit\` — new standalone solution, forked from SCIMServer.
- Initial commit `1ef26ef` on a fresh git repo. Commit message references SCIMServer source SHA `a8cd81348ee9b107c92b010cb86abdd3482c0d7b`.
- 7 projects, all building clean (0 errors, 0 warnings):
  - `Conduit.Core`
  - `Conduit.DataAccess`
  - `Conduit.Web`
  - `Conduit.Emulator.GoogleWorkspace`
  - `Conduit.Installer`
  - `Conduit.Sync` (Phase 0 placeholder only — `PlaceholderMarker.cs`)
  - `Conduit.Scheduling` (lightweight `BackgroundService`, single-process, non-persistent, `ConnectorHealthCheckJob` stub)
- `src/Conduit.DataAccess/DATA-ACCESS-POLICY.md` — Dapper-runtime / EF-schema-only policy + Phase 0 EF audit (0 hits).
- All ports/GUIDs distinct from SCIMServer (so both can run on the same machine).

## What's still open (the one blocker)

**SCIMServer-side `dotnet build` verification.** Step 10 of the Phase 0 prompt requires both solutions to build clean. SCIMServer's build failed with 8× `MSB3027` / `MSB3021` errors — **all file-lock errors only**, no code errors. VS 2022 (pid 12244 at the time) had the SCIMServer solution open and `SCIMServer.Web` (pid 22384) was running, holding `SCIMServer.Core.dll` and `SCIMServer.DataAccess.dll` (+ matching `.pdb`s).

**SCIMServer source is hash-verified byte-identical to its pre-Phase-0 state** — three SHA-256 hashes captured before Phase 0 still match after Phase 0:

| File | SHA-256 |
|---|---|
| `ars-integration/examples/Deploy-UNITEBonusDayWorkflow.ps1` | `DEF53C7206D4479191479B63E03EF7BC025CEE869E59C8F70E851B08F615EA40` |
| `ars-integration/examples/SETUP.md` | `075BF1BAA0177CBCBFC557AA282ADB3CD67F7077378986F23D8D6F88AC9EF077` |
| `ars-integration/examples/UNITE-BonusDay.ps1` | `13AA4F84AB5E2A84F2760A2312573DBA6730C6CADD0F327B2F22A9F40D1F2650` |

`git -C C:/Users/jacob/source/repos/SCIMServer status --porcelain` shows **only** these 3 files modified — the same 3 that were dirty before Phase 0 started. No new modifications introduced.

## To finish Step 10 in the next session

1. Close `SCIMServer.sln` in VS 2022.
2. Stop the running `SCIMServer.Web` process (check Task Manager for `dotnet.exe` / `SCIMServer.Web`).
3. Run:
   ```powershell
   dotnet build C:\Users\jacob\source\repos\SCIMServer\SCIMServer.sln --configuration Debug
   ```
   Expected: exit 0.
4. Confirm SCIMServer git status still shows only those 3 pre-existing modifications.
5. Mark Phase 0 fully done.

## What ports each side uses (kept distinct on purpose)

|  | Conduit | SCIMServer |
|---|---|---|
| Web HTTPS/HTTP (Kestrel dev profile) | 7292 / 5252 | 7192 / 5152 |
| IIS Express HTTP/SSL | 13899 / 44474 | 13799 / 44374 |
| Kestrel default `appsettings` URL | 5100 | 5000 |
| Google Workspace Emulator | 7543 / 7544 | 7443 / 7444 |
| Docker host: web | 5100 / 5101 | 5000 / 5001 |
| Docker host: SQL Server | 1533 | 1433 |
| Docker host: Adminer | 8180 | 8080 |

## Phase 1 prerequisites (don't start Phase 1 until these are answered)

From `C:\Users\jacob\source\repos\IdentityCenter\PLANNING-FOR-COWORK.md` §9.3 — six open decisions:

1. Wire protocol — SCIM 2.0 alone, or also REST + custom?
2. Data ownership — does Conduit own its own DB schema or share IdentityCenter's?
3. License — Apache 2.0? BSL? AGPL?
4. ARSGateway disposition — fold into Conduit, keep separate, or kill?
5. First connector after Google Workspace — Entra ID, Okta, AD?
6. Governance product name (replacing "IdentityCenter") — Tessera was rejected, Census + Vouch were pending. Pick one.

UNITE June 18 launch question is **also** unresolved and time-sensitive (~3 weeks out).

## Where to start in the new session

Open this file. Read MEMORY.md to refresh state. Then either:
- **Path A (recommended):** finish Step 10 above (close VS, build SCIMServer, confirm). 5 minutes of work to fully close Phase 0.
- **Path B:** start answering §9.3 questions so Phase 1 can be scoped.

## Key files

- Phase 0 prompt (already executed, kept for archaeology): `C:\Users\jacob\source\repos\IdentityCenter\CONDUIT-PHASE-0-PROMPT.md`
- Strategy doc: `C:\Users\jacob\source\repos\IdentityCenter\PLANNING-FOR-COWORK.md` (§9 onwards)
- This handoff: `C:\Users\jacob\source\repos\Conduit\PHASE-0-HANDOFF.md`
- Conduit data-access policy: `C:\Users\jacob\source\repos\Conduit\src\Conduit.DataAccess\DATA-ACCESS-POLICY.md`
- Memory entry: `C:\Users\jacob\.claude\projects\C--Users-jacob-source-repos\memory\project_conduit_phase0_executed_2026_05_22.md`
