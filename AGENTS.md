# Conduit shared UI contract

Before UI/component work, read ../IdentityCenter.Brand/AGENTS.md and ../IdentityCenter.Brand/docs/shared-components-contract.md. Resolve the actual IdentityCenter.Brand checkout when working outside the usual sibling layout (normally C:/Users/jacob/source/repos/IdentityCenter.Brand). The former `brand` repository is retired.
Conduit and IdentityCenter consume one executable presentation implementation in IdentityCenter.Brand and its shared CSS/tokens/classes. Conduit's product identity is data, not separate markup or CSS. Preserve Conduit's application/authentication services through thin adapters.
For login work, resume ../IdentityCenter.Brand/docs/login-consistency-plan.md. Follow its correction log and verification states; update shared rules once rather than add local overrides.
Respect current session scope and concurrent edits. These instructions do not authorize starting servers, tests, credential changes, commits, deployments or changes to security protections.

For navigation work, resume ../IdentityCenter.Brand/docs/navigation-consistency-plan.md. MainLayout/NavMenu are product adapters; shared presentation is in IdentityCenter.Brand/Components/Navigation.
