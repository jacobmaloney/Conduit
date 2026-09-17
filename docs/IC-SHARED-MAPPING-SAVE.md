# Reviewed mapping saves (CC-08)

The shared Brand source browser now lets an administrator choose supported target fields, preview values, and explicitly save/reload mappings for the selected import step. IC and Conduit use the same component; their existing application services and repositories own the mutation. Connection-only browsing remains a trial.

Conduit targets currently supported here: AD user/group connector inputs, and IdentityCenter Objects/Identities from the selected IC destination's new `/api/source-catalog/mapping-targets` metadata endpoint. Rebuild the target IC API as well. Other destinations explain why saving is unavailable. This does not query live AD schema extensions or prove write permissions.

Saves validate fields, aliases, expressions and up to 50 fresh source rows, require administrator write access, refuse stale project/step/scope/mappings and reload the persisted set. Source-scoped API tokens cannot write mappings. Secrets, binary projections and partial mapping sets cannot be saved through this browser. Password-last-set timestamp metadata remains available. No sync, sink or scripts are started and no cursor is advanced. There is no automatic retry of an uncertain save.

Native IC uses the same UI and shared checks while retaining its HR/object/group mapping rules and existing atomic mapping history. Local IC embeds the shared source and needs no running Conduit service. This slice does not add a Conduit mapping-history store or converge the complete engines.

[Canonical scope, owners and manual acceptance](../../IdentityCenter/Documentation/Quality/source-mapping-save.md). [Verification receipt](../../_review/source-mapping-save-20260915/README.md). No new migration; current source is uncommitted and not deployed. Real SQL/directory/API and browser acceptance remain separate from offline tests.
