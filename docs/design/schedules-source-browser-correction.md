# Schedules and source browser correction — 2026-09-15

Request: readable Schedules in dark mode, honest Browse availability, and an easier AD browse screen where users/groups are selectable instead of a premature read-only scope summary.

Canonical presentation: IdentityCenter.Brand `BrandListTable`, `BrandBadge`, `BrandSourceBrowser`, and shared table/primitives CSS. Conduit retains all scheduling, authorization and source-reading services. Legacy Conduit CSS copies are not edited.

Intended files: Conduit `Pages/Sync/ScheduleManager.razor`, `Pages/Sync/SyncProjects.razor`, `Pages/ConnectedSystems.razor`, `Services/SourceBrowseService.cs`, `Services/SourceCatalogService.cs`, `Shared/Design/SourceDataBrowser.razor`; Brand `Components/Modals/BrandSourceBrowser.razor`, `Models/BrandSourceBrowserModels.cs`, `wwwroot/css/components/primitives.css`, generic `ObjectsDemo.razor` and `TableDemo.razor` Showcase samples. Focused source-browser rendering/service checks cover the changed interaction; the canonical consolidation ledger records verification.

Acceptance:
- Schedule names, descriptions, statuses and actions use the shared theme-aware table; existing run/edit/enable behavior is preserved.
- Unsupported/inactive sources explain why Browse is unavailable. A permanent capability restriction does not offer an ineffective Retry.
- Saved project import steps are visibly selectable before showing scope or mapping controls. No sample is silently read and no project defaults substitute for a chosen step.
- Selecting a step shows its saved scope and enables Load sample; source results lead the page, with optional mapping tools clearly secondary.
- Both products receive the same source-browser presentation through Brand. No database/connector writes or automatic syncs are introduced.

Verification permitted: isolated consumer/Showcase compilation, focused offline rendering and browse service tests, shared asset/architecture checks, scoped whitespace review. No app/server/browser startup, real source access, commit, push or deployment. Live appearance and AD reads remain for Jacob to verify.

Status: implemented; isolated verification passed. Browser/human acceptance pending.

Implemented: Schedules now composes BrandListTable and BrandBadge, and Conduit's layout delivers badge.css. The shared source browser presents clickable saved import steps (Conduit labels include object class), hides scope/sample controls until a step is selected, uses compact collapsible filter summaries, and places results before an optional mapping editor. A visible capability reason replaces a misleading enabled Browse button for unsupported/inactive sources; permanent host refusals do not offer Retry. Existing scheduling, scope authorization and mapping persistence behavior remain owned by the product services.

Additional exact verification files: Conduit `tests/Conduit.Web.Tests/SourceBrowserPresentationTests.cs`, the existing `SourceBrowseTests.cs`, and Brand `scripts/Verify-TableIntegration.ps1`. The table check now enforces Conduit's shared table, actions, badges and asset links; a missing-badge fixture fails for that exact reason.

Evidence: 95 focused Conduit tests plus five existing shared-browser mapping/cancellation/save regressions passed, zero failures/skips. Conduit.Web and IC WebPortal compile; IC build reports 169 warnings, zero errors. Brand Showcase builds with zero warnings/errors. Brand architecture/41 assets/25 components in seven families and both product adapter checks pass. Both apps and Showcase consume identical Brand DLLs. The 77 combined dirty source files match isolated overlays (these include prior work); unchanged table/badge/browse CSS differs only in checkout line endings. Receipt: ../../../_review/conduit-browser-schedules-20260915/README.md.

Manual acceptance after rebuild/restart:
1. Open Schedules in dark/light modes; confirm names, descriptions, badges and row controls are readable. Scheduling behavior was not changed or exercised against live projects.
2. Open an AD project's Browse source. Click its User or Group import-step option, then Load sample. The selected step's saved class/filters must match its records.
3. Change selection; the previous sample must not appear under the new step. Use Keep current selection to return without changing the draft.
4. Expand View source filters, open record fields, then open Field mappings only when needed. Check narrow-screen scrolling and keyboard selection.
5. Unsupported connectors show a named limitation before browsing; AD and CSV are still the supported readers. This correction does not add Graph/M365 readers.

No database migration, live sync/source operation, app/browser startup, commit, push or deployment was performed. Next: Jacob's theme/AD browser acceptance, then extend source browsing to a named additional connector as a separate capability slice.
