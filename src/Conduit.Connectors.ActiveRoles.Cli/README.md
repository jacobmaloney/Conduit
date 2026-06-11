# arscli — Active Roles connector proof harness

Standalone console that drives the `Conduit.Connectors.ActiveRoles` connector
against a live Active Roles (ARS) deployment **without** Conduit.Web or a Conduit
database. It exists to prove the connector end-to-end: bind through the AR ADSI
provider, read users *with their virtual attributes resolved*, and write role
virtual attributes so the AR **Separation-of-Duties policy denies the toxic
combination** and that denial surfaces as a failed write.

## Deployment constraint (READ THIS)

The connector talks to ARS through the **Active Roles ADSI provider** (the
`EDMS://` moniker over `System.DirectoryServices`). That provider ships with the
**Active Roles Management Tools** and is only present on:

- the Active Roles Administration Service host, or
- an admin workstation with AR Management Tools installed.

It is **not** on a generic Conduit host or a dev laptop. The project **compiles
anywhere** (`System.DirectoryServices` is a normal NuGet package), but `arscli`
(and the connector inside Conduit) only **runs** where the provider is installed.
Bind attempts elsewhere fail with a provider-not-found COM error.

## Configuration

Connection settings come from a local, **uncommitted** `appsettings.json` (copy
`appsettings.sample.json`) or environment variables:

| appsettings key        | env var        | required | meaning                                   |
|------------------------|----------------|----------|-------------------------------------------|
| `Ars:ServiceHost`      | `ARS_HOST`     | no       | AR Administration Service host for EDMS:// |
| `Ars:BindUser`         | `ARS_USER`     | yes      | `DOMAIN\user` or UPN                       |
| `Ars:BindPassword`     | `ARS_PASSWORD` | yes      | bind password                             |

`appsettings.json` is gitignored — never commit real credentials.

## Commands

```
arscli test
arscli read <objectClass> <baseDN> [count] [--va <attr>]
arscli write <userDN> <attr> <true|false>
```

- **test** — bind through the provider and confirm the AR service answers.
- **read** — subtree search through `EDMS://<baseDN>` for `objectClass`, printing
  each object's DN, `sAMAccountName`, and one virtual attribute (default
  `UNITE-HelpDeskAuditor`, override with `--va`). A resolved VA value proves the
  read went *through ARS*, not raw AD.
- **write** — bind `EDMS://<userDN>`, set `<attr>` to the value, `CommitChanges()`.
  A benign attribute succeeds (`Outcome=Updated`); a toxic role pairing makes the
  AR SoD policy throw, which the connector returns as `Outcome=Failed` with the
  policy message.

## Example (UNITE 2026 SoD demo)

```
arscli test
arscli read user "OU=UNITE-2026,DC=Domain,DC=Local" 3
arscli write "CN=Avery Stone,OU=UNITE-2026,DC=Domain,DC=Local" UNITE-HelpDeskAuditor true
arscli write "CN=Avery Stone,OU=UNITE-2026,DC=Domain,DC=Local" UNITE-HelpDeskAdministrator true
```

The last write (Administrator while Auditor is held) trips the SoD policy and
prints the deny reason as the failed-write `ErrorMessage`.

## Publish

```
dotnet publish -c Release -r win-x64 --self-contained false
```

Copy the publish output to the AR host, drop a real `appsettings.json` next to
`arscli.exe`, and run the commands there.
