# Changing Conduit's database connection

How to point a running Conduit at a different SQL Server or a brand-new database,
and where that connection is actually stored.

Read the [Where the connection string lives](#where-the-connection-string-lives)
section before editing any config file by hand. The precedence rules there are the
single most common source of lost time on this — editing `appsettings.json` does
**not** work, and fails silently.

---

## At a glance

| You want to… | Use | Requires |
|---|---|---|
| Point at a different server or a **new** database, while Conduit is running | **`/database-settings`** | Portal login |
| Recover when Conduit **can't reach the database at all** | **`/setup`** | Nothing |
| Recover when the database is fine but **no admin can sign in** | **`/admin-recovery`** | A token file read on the Conduit host |

The first two write the connection string and require a **restart** to take effect.
`/admin-recovery` writes only a `PortalAdmins` row and takes effect immediately.

On `/database-settings` this is now a **single action** — "Create and prepare
database" — which creates the database, builds the schema, sets up the sign-in, and
saves the connection, in that order, and saves nothing at all if any step fails.

---

## Path A — `/database-settings` (normal path)

Use this whenever Conduit is up and you can log in. It is non-destructive: nothing
is deleted, and the old database is left untouched.

Reach it from the left nav under **Configure**, from the Configuration page, or
directly at `/database-settings`.

1. **Change the Database field** to the name you want — e.g. `Conduit18`.
   To create a *new* database, type a name that does not exist yet.
   Leave Server and credentials alone if you're only changing the database.
2. **Choose who signs in after restart.** The default — *"Me — `<you>`, same
   password"* — copies your own portal login into the new database so nothing about
   signing in changes. Pick *"A different admin"* to set a different username and
   password instead.
3. **Click "Create and prepare database".**
4. **Restart Conduit.**

That is the whole flow. **Test Connection** is still there, but it is only a
diagnostic — you do not have to run it first, and there is no longer a separate
"Create Database Now" button or a separate "Save" button.

### What "Create and prepare database" actually does

Four steps, in order, with per-step progress and per-step errors on screen:

1. **Create the database** if it does not already exist.
2. **Build the schema** — a full migration run from zero tables, using a *detached*
   `DatabaseConfig` aimed at the new connection.
3. **Set up the sign-in** — inserts exactly one row into the new database's
   `PortalAdmins`. The "Me" option copies your own row (username + password hash +
   salt), which is authorized because you are authenticated in the *source* database
   at that moment. Only your row is copied, never anyone else's. If the target
   database already has an active admin, this step is skipped rather than overwriting
   anything.
4. **Save the connection** — writes `ConnectionStrings:DefaultConnection` plus a
   `ConduitProvisionedConnection` marker to the secret store.

Two properties matter:

- **The save is inseparable from the rest.** There is no longer any way to be told
  "restart" without having saved, which is what used to silently drop operators back
  onto the old database.
- **A failed prepare never writes the connection string.** If any step fails, the run
  stops, the failing step and the driver's own error text are shown, and nothing is
  persisted — so a restart cannot strand you on a half-built database.

The running application keeps serving every active user from the **current** database
until you restart. The live `DatabaseConfig` is deliberately not repointed mid-flight.

### Only the login carries over — and it is a *copy*

Each database has its **own** `PortalAdmins` table. The prepare action copies your
sign-in and nothing else — **no agents, connections, sync projects, or history**. The
new database starts empty apart from schema and your admin row.

Be precise about what "copies your sign-in" means, because it is a credential
duplication and it is easy to under-read:

> Your username, password hash, and salt are written into the new database as a
> **second, independent account**. It is a copy that **outlives the original**.
> Changing or revoking your password in the old database later has **no effect** on
> the copy — the copy keeps working with the old password indefinitely.

So if the reason you are moving databases is that a credential was exposed, rotating
it in the source database is **not** enough. Rotate it in **both**, or use *"A
different admin"* and set a fresh credential on the new database instead.

The same applies in reverse: the old database keeps its admin row after you move, so
the old database remains signable-in with the same credential until you clean it up.

### Safety net: a configured-but-empty database migrates itself at boot

For the out-of-band cases — someone hand-edited `secrets.json`, set an environment
variable, deployed with a connection string, or a prepare run failed partway —
startup will build the schema itself, but **only** for the database an authenticated
admin explicitly designated.

`ConduitProvisionedConnection` in `secrets.json` pins a specific normalized
`DataSource` + `InitialCatalog`:

```json
"ConduitProvisionedConnection": {
  "DataSource": "localhost",
  "InitialCatalog": "conduit18",
  "MachineName": "conduit-host-01",
  "ProvisionedAtUtc": "2026-08-12T18:04:11.9310000Z"
}
```

At boot, when setup would otherwise be "required" purely because the schema is
absent, Conduit compares that marker against the currently-resolved connection
string. Server, database, **and issuing machine** must all match (case-insensitive,
trimmed), and the target must actually have no schema:

| Situation | Outcome |
|---|---|
| Marker matches the configured connection, on this machine, schema absent | Schema is built, then the status is re-probed |
| Marker names a different server or database | **Nothing happens** — routes to `/setup` |
| Marker was issued on a **different machine** | **Nothing happens** — routes to `/setup` |
| Marker has no `MachineName` (written by an older build) | **Nothing happens** — routes to `/setup` |
| Target database already has a schema | **Nothing happens** — refuses to migrate it unattended |
| No marker at all | **Nothing happens** — routes to `/setup` |
| Database unreachable | **Nothing happens** — routes to `/db-offline` |

The pin *is* the security control. It is not a boolean "auto-provision" flag, and it
must never be relaxed into one: that would let any connection string that happens to
be configured get a schema built into it unattended. Each outcome is logged
explicitly, so the startup log always says which branch was taken.

**Why the machine binding.** `localhost`, `.`, `(local)` and `(localdb)\…` are
host-relative — they name a different physical server depending on where they are
read. Without a machine identity, an install copied to another box (or a
`secrets.json` restored from a backup) carries a marker that matches on text alone and
authorizes an unattended schema build against whatever `localhost` means *there*. A
cross-machine copy now falls closed to the wizard. If you legitimately moved the
install, re-run **"Create and prepare database"** on the new host to re-issue the
marker.

**A marker never creates an administrator.** If the schema gets built this way, the
new database has zero portal admins, and that state routes to `/admin-recovery` — not
to the setup wizard. See the next section.

---

## Path C — `/admin-recovery` (nobody can sign in)

This covers a specific state: the database is **reachable and has a schema**, but
`PortalAdmins` has **no active row**. Nobody can sign in, so `/database-settings` is
unreachable, and `/db-offline` does not apply because the database is perfectly
healthy.

You can arrive here by having the safety net above build a fresh schema, by restoring
or copying a database, by a hand-run `UPDATE`, or by a prepare run whose admin-seed
step was skipped.

### Why this is not the setup wizard

It used to be. That was wrong, and the reasoning is worth keeping:

- Routing this state to `/setup` made the **anonymous** wizard reachable on installs
  that had already been set up. Before, the wizard was unreachable there; after, it
  was. That is a widening, whatever else is true.
- It was worse than "an anonymous visitor can create an admin." The wizard's admin
  step resolved an existing username **with no `Active` filter** and `UPDATE`d it with
  `Active = 1`. So typing a **deactivated** admin's username **reactivated that named
  identity and set its password** — a takeover of a real account, complete with
  whatever that identity carries in the audit history.
- The wizard also still walked through its JWT step, whose **"Generate New"** button
  let an anonymous visitor mint the install's signing key and have it persisted.

Network reachability is not authorization. An install is reachable from wherever it
is deployed. A loopback bind is not sufficient either — reverse proxies and container
port maps routinely present remote traffic as loopback.

### How recovery works

1. Conduit detects the state and writes a one-time token to
   **`%PROGRAMDATA%\Conduit\recovery.token`**, using the same ACL-restricted writer as
   `secrets.json` (owner + `LocalSystem` + `BUILTIN\Administrators`, inheritance
   protected). Issuing is idempotent — an outstanding token is never rotated out from
   under you.
2. `/admin-recovery` renders a dead end. It discloses **nothing** about the database:
   no server name, no database name, no login.
3. Sign in to the Conduit **host**, open that file, copy the `Token` value.
4. Paste it on the page and choose a username and password.
5. The token is **consumed** on success. One token, one recovery.

Reading that file is the same trust boundary as reading the connection string, which
is the correct boundary for minting the first administrator: anyone who can read it is
already inside.

### What recovery will not do

- **It never reactivates or reuses an existing account**, even a deactivated one. If
  the username collides with any row, it refuses and asks for a different name. Pick a
  new name; clean up the old rows once you can sign in.
- **It only touches `PortalAdmins`.** It cannot change the connection string, the JWT
  signing key, the Kestrel port, or the schema — unlike the setup wizard, which
  rewrites all of those.
- **It will not run once the install is healthy.** A valid token on an install with an
  active admin grants nothing.

### If the token file is not there

Restart Conduit — the token is re-issued whenever this state is detected. If it still
does not appear, check the application log for a token-write failure; recovery stays
**blocked** until a token can be written, which is the intended failure direction.

### Gotcha: you must still restart

The prepare action writes the new connection to the secret store, but the running
process keeps using the old one. The config file is loaded with `reloadOnChange:
false` (`Program.cs:32`), so nothing re-reads it in place.

Until you restart, every service in the app is still talking to the old database. The
success banner says so explicitly, and it names both databases.

---

## Path B — `/setup` (recovery path)

`/database-settings` is `[Authorize]`. If the database is unreachable you cannot
log in, so that page is unreachable exactly when you most need it.

`/setup` is the answer: it is anonymous and it is the **only** connection-entry page
reachable while the database is down. `SetupMiddleware` allowlists `/setup` and
`/db-offline`; `/database-settings` is deliberately not allowlisted.

If Conduit lands on `/db-offline`, click **"Reconnect to a different database"** to
reach the wizard, then enter the connection and continue.

### Forcing the first-run wizard deliberately

Only if you want a genuinely clean first-run experience:

1. Delete `src/Conduit.Web/setup.complete` **and** the copy in
   `src/Conduit.Web/bin/Debug/net8.0/setup.complete`.
2. Blank the `ConnectionStrings` block in `%PROGRAMDATA%\Conduit\secrets.json`.
3. Start Conduit — it drops you at `/setup`.

Prefer Path A for routine changes. This one is destructive to local state and
exercises a path most users never see.

---

## Where the connection string lives

**`%PROGRAMDATA%\Conduit\secrets.json`** → key `ConnectionStrings:DefaultConnection`
(normally `C:\ProgramData\Conduit\secrets.json`).

This is a machine-local secret store that lives **outside the repository**. Both
`/setup` and `/database-settings` write here, and it is the durable customer path.
It also holds `Jwt`, the installer's one-shot `Provision` section, and the
`ConduitProvisionedConnection` marker.

**The one thing that does not go here** is the Kestrel listening port. The setup
wizard writes that — and only that — to `appsettings.Development.json` or
`appsettings.Production.json` under the content root
(`SetupService.BuildEnvironmentConfigPath`). If you are looking for a *port*, it is
in the environment config file; if you are looking for a *connection string, JWT
secret, or provisioning marker*, it is in `secrets.json`. Conflating the two is what
made this page necessary.

### Why editing `appsettings.json` does nothing

Two mechanisms, either one sufficient:

1. **It is loaded last, so it wins.** `Program.cs:32` appends the secret store after
   every other provider (`AddJsonFile(SecretsFile.DefaultPath, …)`). In ASP.NET Core
   configuration, last provider registered wins. The comment at `Program.cs:26`
   states it "outranks the world-readable appsettings."

2. **A relocator actively strips it back out.** `Services/SecretsRelocator.cs:74-75`
   moves connection strings *out of* `appsettings.json` and *into* the secret store,
   and enforces that existing `secrets.json` values always win.

So an `appsettings.json` edit isn't merely ignored — it gets removed. The value you
see in the running app will not match the file you just edited, with no error to
tell you why.

> **If you are hunting a connection value that appears nowhere in the repo, stop
> grepping the repo.** Read the configuration provider registrations in
> `Program.cs` in order and check the last one first. Also check user-secrets
> (`%APPDATA%\Microsoft\UserSecrets\`), machine-scope environment variables, and
> environment variables set in the shell session that launched the process.

### Restart required

The secret store is loaded with `reloadOnChange: false`. **Any** change to it —
whether made through the UI or by editing the file — requires a full cold restart
of the process. An edit that "doesn't take" is expected behavior, not evidence that
you changed the wrong thing.

---

## Troubleshooting

**The page keeps reloading while I'm typing.**
Historically, `/db-offline` injected `<meta http-equiv="refresh" content="10">` into
the document head. Navigating to `/setup` from that page was a same-document Blazor
navigation, and **removing a meta-refresh element does not cancel the navigation the
browser has already scheduled** — so the browser hard-navigated ten seconds later and
wiped every field in the wizard. It fired on a wall clock, unrelated to typing.

This is fixed, but the fix cannot revoke a timer already armed in an open tab.
**Hard-reload any stale `/db-offline` tab** before concluding the fix failed.

**I changed the database but the app still uses the old one.**
You didn't restart. See [Restart required](#restart-required). The success banner on
`/database-settings` names both databases precisely so this is unambiguous.

**I restarted and landed on `/setup` anyway.**
The schema was absent and the provisioning marker did not match the configured
connection — check the startup log, which states the reason. Most likely the
connection string was changed by hand (or by an environment variable) after the last
prepare run, so it names a database no admin ever designated through the portal.

**I restarted and landed on `/admin-recovery`.**
The schema is present but the database has no active portal admin, so there is no
account to sign in with. This is not the setup wizard and it will not become the setup
wizard — see [Path C](#path-c--admin-recovery-nobody-can-sign-in). You need access to
the Conduit host to read the token file.

**The setup wizard no longer pre-fills my server and database.**
Deliberate. `/setup` is anonymous and it offers an unauthenticated visitor a
connection tester and a `CREATE DATABASE` against a server they name; rendering the
real hostname and database name there turns a blind probe into a targeted one. The SQL
login and password were already withheld for the same reason. Re-type them.

**I'm locked out because the database is down.**
Use `/setup`, not `/database-settings`. See [Path B](#path-b--setup-recovery-path).

---

## Security note

`%PROGRAMDATA%\Conduit\secrets.json` stores the SQL password **in plaintext**. It is
ACL-protected rather than encrypted, so:

- Edit it **in place** when editing by hand — don't delete and recreate it, or you
  will drop the ACL that protects it.
- Treat it as a credential file in any backup, imaging, or support-bundle process.
- Prefer a least-privilege SQL login over `sa`.

`%PROGRAMDATA%\Conduit\recovery.token` sits beside it under the same ACL and carries
the same weight: **anyone who can read it can create an administrator on this
install.** It only exists while an install has no active admin, and it is deleted the
moment it is used. The same in-place / backup / support-bundle cautions apply — and if
one appears on a host you did not expect it on, that host has lost all of its portal
admins and someone should find out why.

The config load order in `Program.cs` is also a security control, not a style choice.
The secret store is registered **after** `CreateBuilder`, so it outranks the
environment-variable provider — a `ConnectionStrings__DefaultConnection` env var
cannot override it. The provisioning marker's integrity depends on that: if a
lower-integrity source could win the connection string, an env var could redirect the
marker's authorization at a database no admin ever designated while the marker still
appeared to match.
