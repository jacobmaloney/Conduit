# Conduit data-access policy

## Rules

1. **All runtime DB calls use Dapper.** This includes every SELECT, INSERT,
   UPDATE, DELETE, MERGE, and stored-procedure call issued during normal
   request/job processing. Use `IDbConnection` + `Dapper.QueryAsync<T>` /
   `ExecuteAsync` / `QueryFirstOrDefaultAsync` etc.

2. **EF Core is permitted only for schema management** — i.e. design-time
   migration generation and the migration runner. EF Core `DbContext` /
   `DbSet<T>` / LINQ-to-Entities / `.Include()` / `IQueryable<T>` are
   not permitted in runtime code paths.

3. **ASP.NET Identity is an acceptable exception** if Conduit ever adopts
   the standard Identity stack — `UserManager<ApplicationUser>` requires
   EF and is allowed for the portal-user table only. As of Phase 0,
   Conduit does not use ASP.NET Identity; if it is added later, document
   the exception here.

## Enforcement

- All new repositories live under `Conduit.DataAccess\Repositories\` and
  derive from the existing Dapper repository base.
- PRs that add `using Microsoft.EntityFrameworkCore` to any file outside
  the migration project are rejected.
- The audit in `Conduit.DataAccess\Repositories\` should show zero
  `DbContext` references outside the migration runner.

## Migration system

Phase 0 preserves the custom `DatabaseMigrator.cs` runner inherited from
SCIMServer. EF migrations are not introduced in Phase 0.

Rule #2 above ("EF Core permitted for schema management") is forward-
looking and reserves the right to migrate to EF migrations later. It
does not require it now. Until that decision is made, treat the custom
runner as the canonical schema tool.

## Phase 0 audit result (2026-05-22)

EF Core usage scan across `src\**\*.cs` for `Microsoft.EntityFrameworkCore | DbContext | DbSet< | IQueryable | .Include( | .AsQueryable( | EF.Functions`:

- Runtime hits: **0**
- Migration-runner hits (permitted): **0** (custom `DatabaseMigrator` is raw-SQL, no EF dependency)
- Deferred conversions: **none**

The only mention of EF Core in the tree is a logging-category filter in
`docs/DEVELOPMENT_GUIDE.md`. No source files reference EF Core types.
