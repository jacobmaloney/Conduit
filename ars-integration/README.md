# UNITE Provisioning Hub — ARS → Conduit Integration

> **Brought to you by iC Consult • Identity & Access Management specialists**
> *We build IAM solutions that don't break.* — [sales@ic-consult.com](mailto:sales@ic-consult.com)

Active Roles workflow that provisions, re-enables, and disables users in any
number of SCIM 2.0 targets from a single PowerShell engine. Add the next SaaS
app by editing a hashtable — no new code per app.

This is the demo artifact for the UNITE 2026 talk
*"Descending into Madness with Advanced Scripting and Workflows"*
(Jacob Maloney, iC Consult • AJ Lindner, One Identity).

---

## What's in this folder

| File | What it is | Who edits it |
|---|---|---|
| `UNITE-SCIMMappings.ps1` | The mapping table — one hashtable entry per SCIM target | **Admins / IAM analysts** |
| `UNITE-SCIMRest.ps1`     | The universal SCIM engine — HTTP, retry, mapping engine | Developers (rarely) |
| `README.md`              | This file | — |

The two `.ps1` files live separately in the repo for clarity (admins edit the
mapping table without touching the engine), but the deploy tool
(`UNITE-Hub-Admin.ps1 -Verb ApplyMappingsToARS`) **concatenates them at push
time** into a single ARS ScriptModule called `UNITE-SCIMRest`. Workflow
`PowerShellActivity` entries reference only that one module — no XAML edits
needed when you change mappings.

---

## How it works

```
       ┌──────────────────────────┐
       │  User modified in ARS    │
       │  (or onPostModify hook)  │
       └────────────┬─────────────┘
                    ▼
       ┌──────────────────────────┐
       │  UNITE Provisioning Hub  │      Workflow IfElse tree:
       │       (workflow)         │      Which app? Provision or disable?
       └────────────┬─────────────┘
                    ▼
       ┌──────────────────────────┐
       │ Provision-<App>          │      Per-app entry point in
       │ Disable-<App>            │      UNITE-SCIMRest
       └────────────┬─────────────┘
                    ▼
       ┌──────────────────────────┐
       │ Get-SCIMMapping <App>    │      Looks up the hashtable in
       │  (UNITE-SCIMMappings)    │      UNITE-SCIMMappings
       └────────────┬─────────────┘
                    ▼
       ┌──────────────────────────┐
       │ _Build-CreateUser        │      Walks mapping → builds SCIM JSON
       │ _Invoke-SCIM             │      Sends POST/PATCH to Conduit
       └──────────────────────────┘
```

The engine reads `$script:SCIMMappings` from the mappings module, looks up the
entry for the AppKey, walks each `scimPath → adAttribute` pair, and assembles
a SCIM 2.0 payload. The enterprise schema extension is auto-added when any
`enterprise.*` path is mapped.

---

## Adding a new SCIM target — 5 steps

Let's say you're adding **Jira Cloud** with AppKey `JiraCloud`.

### 1) Add the mapping

In `UNITE-SCIMMappings.ps1`, add a block under `$script:SCIMMappings`:

```powershell
"JiraCloud" = @{
    "userName"          = "mail"             # Jira keys on email
    "name.givenName"    = "givenName"
    "name.familyName"   = "sn"
    "displayName"       = "displayName"
    "emails[0].value"   = "mail"
    "emails[0].type"    = "=work"
    "emails[0].primary" = "=true"
    "active"            = "=true"
}
```

### 2) Add the per-app functions

In `UNITE-SCIMRest.ps1`, add the two wrapper functions next to the existing ones:

```powershell
function Provision-JiraCloud { _Do-Provision -AppKey "JiraCloud" -Request $Request }
function Disable-JiraCloud   { _Do-Disable   -AppKey "JiraCloud" -Request $Request }
```

> **Why are these needed?** ARS workflow activities call functions by name.
> Two thin wrappers per app are the price of not needing a runtime parameter
> the workflow XAML doesn't reliably pass.

### 3) Add the workflow parameters

On the **UNITE Provisioning Hub** workflow, add two parameters:

| Parameter name | Type | Example value |
|---|---|---|
| `SCIM-JiraCloud-URI`   | String       | `http://192.168.1.137:5000/scim/v2/t/jira-cloud` |
| `SCIM-JiraCloud-Token` | SecureString | (paste raw token from Conduit, no `scim_` prefix — script adds it) |

> URIs are tolerant: with or without trailing `/Users` both work.

### 4) Push the updated module to ARS

From a workstation with the AR Management Shell installed:

```powershell
.\UNITE-Hub-Admin.ps1 -Verb ApplyMappingsToARS
```

This reads both `.ps1` files from this folder, concatenates them (mappings on
top, engine below), and overwrites the `UNITE-SCIMRest` ScriptModule in ARS.
No XAML or MMC step needed.

### 5) Add the branch to the workflow

Open the **UNITE Provisioning Hub** workflow in MMC and extend the outer
IfElse "Which app?" with a new branch for `JiraCloud`. Inside that branch,
mirror the inner IfElse "Which action?" with two `PowerShellActivity`
entries pointing at `Provision-JiraCloud` and `Disable-JiraCloud`. Both
activities reference only `UNITE-SCIMRest` (which already carries the
mapping table from step 4).

### 6) Add the Web Interface action button (optional)

If you want a one-click button for the helpdesk on the user object's task
list, add a Task entry to the WebInterface customization (see the SQL examples
in the talk repo's history — `UNITE_Assign_*` / `UNITE_Remove_*`).

---

## Mapping syntax reference

### Path syntax (the key in the hashtable)

| Path | Resulting SCIM JSON |
|---|---|
| `userName` | `{ "userName": "..." }` |
| `displayName` | `{ "displayName": "..." }` |
| `active` | `{ "active": true }` |
| `name.givenName` | `{ "name": { "givenName": "..." } }` |
| `name.familyName` | `{ "name": { "familyName": "..." } }` |
| `emails[0].value` | `{ "emails": [{ "value": "..." }] }` |
| `emails[0].type` | `{ "emails": [{ "type": "work" }] }` |
| `emails[0].primary` | `{ "emails": [{ "primary": true }] }` |
| `phoneNumbers[0].value` | `{ "phoneNumbers": [{ "value": "..." }] }` |
| `enterprise.department` | `{ "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User": { "department": "..." } }` |
| `enterprise.employeeNumber` | (same extension key) `employeeNumber` |
| `enterprise.costCenter` | (same extension key) `costCenter` |
| `enterprise.manager.value` | (same extension key) `{ "manager": { "value": "..." } }` |

### Value syntax (the right-hand side)

| Form | Meaning | Example |
|---|---|---|
| `"sAMAccountName"` | Plain string = name of an AD attribute. The engine reads `Get-QADUser`'s output property. | `"userName" = "sAMAccountName"` |
| `"=work"` | `=` prefix = emit this **literal** value. | `"emails[0].type" = "=work"` |
| `{ <scriptblock> }` | Computed at runtime. Receives `$u` = the AD user object. Return value goes into the payload. Return `$null` to omit. | See manager email recipe below |

### Common recipes

**Compute manager's email from manager DN:**
```powershell
"enterprise.manager.value" = {
    param($u)
    if ($u.manager) {
        try { (Get-QADUser $u.manager -DontUseDefaultIncludedProperties -IncludedProperties mail).mail }
        catch { $null }
    }
}
```

**Default a value when the AD attribute is empty:**
```powershell
"enterprise.department" = {
    param($u)
    if ($u.department) { $u.department } else { "Unassigned" }
}
```

**Concatenate first + last name into displayName:**
```powershell
"displayName" = {
    param($u)
    "$($u.givenName) $($u.sn)".Trim()
}
```

**Boolean from AD UAC accountDisabled state:**
```powershell
"active" = {
    param($u)
    -not $u.accountIsDisabled
}
```

**Ship a literal department for all users in a target:**
```powershell
"enterprise.department" = "=Finance"
```

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `401 Unauthorized` in workflow log | Token wrong, missing `Bearer ` prefix, or expired | Re-mint token in Conduit; re-paste into the workflow's `SCIM-<App>-Token` parameter. Engine adds `Bearer ` itself. |
| `Connection refused` / actively refused | Conduit not running, or bound to `localhost` not `0.0.0.0` | Set `Kestrel:Endpoints:Http:Url` to `http://0.0.0.0:5000` and open the firewall on the host. |
| `404 Not Found` | Slug in URI doesn't match a Connected System in Conduit | Check `/admin/connected-systems` in Conduit; copy the slug exactly. |
| `Could not read sAMAccountName from <DN>` | `$Request.Get('sAMAccountName')` only returns *modified* properties. The engine uses `Get-QADUser` to avoid this, but a stale workflow activity may still use the old pattern. | Update the activity to call the engine's `Provision-<App>` / `Disable-<App>` functions — they read all attributes correctly. |
| `No SCIM mapping defined for AppKey 'X'` | You added the workflow branch but not the mapping entry | Add `"X" = @{ ... }` to `$script:SCIMMappings` |
| Workflow trace is empty / silent failure | Activity not referencing the script modules, or `$Workflow.Parameter()` returned empty | Confirm both ScriptModules are referenced on the activity; confirm the workflow parameters are set (not just defined). |

---

## Demo configuration (UNITE 2026)

The talk ships with three Connected Systems pre-seeded in Conduit:

| App | Slug | Mapping highlight | Demo raw token |
|---|---|---|---|
| HR Connect | `hr-connect` | Minimal core schema | `demo-hr-2024` |
| IT Helpdesk Portal | `it-helpdesk-portal` | + department, employeeID, phone | `demo-it-2024` |
| Finance Suite | `finance-suite` | Keys on email, enterprise schema, computed manager | `demo-finance-2024` |

Paste each raw token (no `scim_` prefix — Conduit mints with that prefix
built in) into the matching `SCIM-<App>-Token` SecureString workflow
parameter.

---

## See also

- [Conduit README](../README.md) — server-side admin, multi-tenancy, token minting
- Talk: *Descending into Madness with Advanced Scripting and Workflows* — One Identity UNITE 2026, Grand Ballroom III, Thursday June 18 11:30 AM CDT

---

*Did this save you a week? Hire iC Consult for the next one.*
[sales@ic-consult.com](mailto:sales@ic-consult.com)
