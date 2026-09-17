# CC-03 - Shared field selection and defaults

Implemented from the latest local source on 2026-09-15. `Conduit.Mapping` now owns
source selection, default conditions/order, transformation invocation and target
presence for one field. `SourceAttributes.Read` preserves a separate Found flag;
`FieldProjection.Project` returns value, target, ShouldWrite, SourceFound and
UsedDefault. Omitted fields and explicit nulls are not interchangeable.

`ConnectorMappingPlan` translates saved Conduit mappings into these rules once per
run. SyncProjectOrchestrator uses the plan; its old private mapping implementation
is removed. The plan snapshots mapping definitions, preserves their supplied
order, carries structural username/DN fields, and leaves source objects unchanged.
An empty mapping set retains the previous passthrough behavior. No sink, database
or network call occurs inside the plan or the shared library.

IC AttributeMappingService uses the same field operation in the direct object
import/preview path and delegates its other source lookups to SourceAttributes.
It retains enabled/order rules, array/string conversion, automatic directory fields
and target coercion. A related fix accepts canonical `ObjectColumn` alongside
legacy `IdentityColumn`/`CoreProperty`; valid ObjectColumn definitions were
previously ignored by this mapper.

## Explicit compatibility policies

| Behavior | Conduit | IC directory mapper |
|---|---|---|
| Source lookup | Source dictionary's comparer | Exact, lowercase, then first ordinal-insensitive match |
| Default | At its position in the expression chain; null/empty string | Raw null only; default is not transformed |
| No transform | Present source, including null, is emitted | Field value is handed to IC's target writer |
| Transform produces null | Omitted unless required or expression can generate a missing-source value | Target writer receives null |
| Required field missing | Explicit null emission; no new schema validation | Existing target-writing behavior retained |
| Multiple values | Direct preserves the object; text expressions coerce first list element | Directory conversion joins values; objectClass uses last entry |
| Duplicate target | Supplied order; last emitted field wins | Enabled ExecutionOrder, existing target rules |

Conduit's `default:` stage also uses the shared AttributeDefaults condition owner.
IC HR/group projection and default rules have not been migrated; HR text operations
still share the CC-02 library. Unknown expressions and invalid-replace errors keep
their existing contracts. The projection library propagates errors; it does not
invent an empty-success result or return partial output from a failed plan.

## Verification and delivery

Final verification passed 191 focused offline tests (93 Conduit, 98 IC), zero
failures/skips. All 108 combined changed source hashes match tested overlays;
Conduit.Sync, IC API and WebPortal outputs contain the identical shared DLL.
Referenced projects compiled; warnings remain. Evidence is recorded in IC's
`Documentation/Quality/conduit-field-projection.md` and the sibling receipt
`../_review/conduit-field-projection-20260915/README.md` (relative to this repository
root). The shared-mapping CI filter now includes SharedFieldProjectionTests and
ConnectorMappingPlanTests. Hosted CI and live effects were not exercised.

Normal local builds consume the sibling Conduit source. Hosted IC builds need the
published Conduit revision containing CC-03 (an older CC-02-only library lacks the
field API). Publish source first when authorized, update SHARED_CONDUIT_REVISION,
and verify coordinated builds. No new migration, service startup, commit or
deployment is part of this change.

Next: extract one read-only connector path behind host-neutral contracts and use it
from IC local mode and Conduit. Host adapters must retain credential resolution,
tenant/authorization boundaries, scope, pagination, cancellation and errors. Full
orchestrator hosting, schema validation and target coercion convergence, shared
HR/group projection and remote preview remain separate work.
