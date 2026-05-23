# Google Workspace Emulator (Admin SDK Directory API v1)

Local emulator that looks and behaves like Google Workspace's Admin SDK Directory API. Long-lived development infrastructure — point real Google API client libraries at it and they should just work.

## What's emulated (P1)

- **OAuth 2.0** token endpoint (`/oauth2/v4/token`) with realistic JWT-bearer assertion validation
- **Directory API v1**: Users, Groups, Members, Customers, Domains
- **Seeded tenant**: `acme.example.com`, customer `C00acme01`, ~350 users across 7 org units, 22 groups, 2 service accounts with downloadable key files
- **Google-fidelity**: error envelope shape, `kind` literals, ETag + `If-Match`, opaque paging tokens, soft-delete/undelete, `userKey` resolves by id|primaryEmail|alias

Deferred to P2: OrgUnits CRUD, Aliases endpoints, Roles/RoleAssignments, custom Schemas, Blazor admin UI, `users/watch` push channels, batch endpoint.

## Running

```powershell
# From the Conduit root:
dotnet run --project src/Conduit.Emulator.GoogleWorkspace
```

The emulator binds:
- `http://localhost:7443`
- `https://localhost:7444`

On first run it applies `Database/GoogleWorkspaceSchema.sql` against the shared Conduit DB and seeds the tenant (idempotent — skipped if `gw_customers` already has rows).

Connection string is read from `src/Conduit.Emulator.GoogleWorkspace/appsettings.json`. Default points at `(localdb)\mssqllocaldb` / `Conduit` — update it to match your environment.

## Pointing Google client libraries at it

Google's client libraries resolve `admin.googleapis.com` and `oauth2.googleapis.com` via DNS. To redirect them to the emulator, add to your hosts file (Administrator on Windows):

```
127.0.0.1  admin.googleapis.com
127.0.0.1  oauth2.googleapis.com
```

Then run the emulator on port 443 (via reverse proxy or elevated launch) **or** set the client's `BaseUri` / service endpoint to `https://localhost:7444`. The latter is usually easier for dev:

```csharp
var service = new DirectoryService(new BaseClientService.Initializer
{
    HttpClientInitializer = credential,
    ApplicationName = "IgaConnector",
    BaseUri = "https://localhost:7444"
});
```

### TLS trust

The emulator uses the ASP.NET dev cert on `localhost`. Run `dotnet dev-certs https --trust` once. For `admin.googleapis.com` hostname validation you'll need a cert whose SAN includes that host — see P2 for automated issuance. For P1, point `BaseUri` at `https://localhost:7444` and the dev cert is fine.

## Service-account credentials

Two service accounts are seeded on first run:

- `iga-connector@acme-emulator.iam.gserviceaccount.com` — full read/write scopes
- `readonly@acme-emulator.iam.gserviceaccount.com` — read-only scopes

Download a Google-shaped credentials JSON (same shape as the real thing):

```
GET http://localhost:7443/emulator/service-accounts
GET http://localhost:7443/emulator/service-accounts/iga-connector@acme-emulator.iam.gserviceaccount.com.json
```

Feed the file straight to `GoogleCredential.FromFile(path)` in client code.

## Quick sanity check (curl)

```bash
# 1. List service accounts
curl http://localhost:7443/emulator/service-accounts

# 2. Download a key file for the full-scope account
curl -o iga.json http://localhost:7443/emulator/service-accounts/iga-connector@acme-emulator.iam.gserviceaccount.com.json

# 3. Build a JWT assertion (use the Google client library, or jwt.io with RS256 and the private_key from the file)
#    Required claims: iss=client_email, aud=https://oauth2.googleapis.com/token, scope, iat, exp (iat+3600 max)

# 4. Exchange for an access token
curl -X POST http://localhost:7443/oauth2/v4/token \
  -d grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer \
  -d "assertion=<SIGNED_JWT>"

# 5. Call the Directory API
curl -H "Authorization: Bearer <ACCESS_TOKEN>" \
  "http://localhost:7443/admin/directory/v1/users?customer=my_customer&maxResults=5"
```

## Endpoints implemented

| Method | Path | Notes |
|---|---|---|
| POST | `/oauth2/v4/token` | JWT-bearer grant only; full iss/aud/exp/scope/signature validation |
| GET | `/admin/directory/v1/users` | `customer`, `domain`, `query`, `orgUnitPath`, `orderBy`, `sortOrder`, `maxResults` (≤500), `pageToken`, `showDeleted`, `projection` |
| GET | `/admin/directory/v1/users/{userKey}` | userKey = id \| primaryEmail \| alias |
| POST | `/admin/directory/v1/users` | Insert |
| PUT/PATCH | `/admin/directory/v1/users/{userKey}` | `If-Match` ETag enforced when present |
| DELETE | `/admin/directory/v1/users/{userKey}` | Soft-delete |
| POST | `/admin/directory/v1/users/{userKey}/undelete` | |
| POST | `/admin/directory/v1/users/{userKey}/makeAdmin` | Body: `{ "status": true }` |
| GET/POST | `/admin/directory/v1/groups` | `customer`, `domain`, `userKey`, `query`, `maxResults` (≤200) |
| GET/PUT/PATCH/DELETE | `/admin/directory/v1/groups/{groupKey}` | |
| GET/POST | `/admin/directory/v1/groups/{groupKey}/members` | `roles`, `maxResults`, `pageToken`, `includeDerivedMembership` |
| GET | `/admin/directory/v1/groups/{groupKey}/members/{memberKey}/hasMember` | |
| GET/PUT/PATCH/DELETE | `/admin/directory/v1/groups/{groupKey}/members/{memberKey}` | |
| GET | `/admin/directory/v1/customers/{customerKey}` | `customerKey` = id or `my_customer` |
| GET | `/admin/directory/v1/customer/{customer}/domains` | |
| GET | `/admin/directory/v1/customer/{customer}/domains/{domainName}` | |

Error envelope matches Google's exactly (`error.code/message/errors[].domain/reason/message` for Directory API; `error/error_description` for OAuth2).

## Tables (shared with SCIM DB)

`gw_customers`, `gw_domains`, `gw_orgunits`, `gw_users`, `gw_groups`, `gw_members`, `gw_aliases`, `gw_roles`, `gw_role_assignments`, `gw_schemas`, `gw_service_accounts`, `gw_access_tokens`.

Full DDL: `Database/GoogleWorkspaceSchema.sql`. Apply manually with `sqlcmd -i`, or just start the emulator — it runs the script on boot.

## Using this for IdentityCenter's Google Workspace connector

When the IdentityCenter connector is built, it can:

1. Download `iga-connector@acme-emulator.iam.gserviceaccount.com.json` from `/emulator/service-accounts/`
2. Use `GoogleCredential.FromFile(...)` exactly as with a real tenant
3. Point `BaseUri` at `https://localhost:7444` for dev, or host-map `admin.googleapis.com` for SDK-level transparency
4. Exercise full sync against a stable, offline tenant with realistic scale (~350 users, 22 groups)

Same key file, same auth flow, same API surface. Years of dev without touching a real Google tenant.
