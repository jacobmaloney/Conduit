# UNITE - Extend Contractor (setup)

Single ARS workflow that demonstrates eight common patterns the audience
asks about. Story: a contractor was hired with `accountExpires` set to
today+90 days. Manager clicks **Extend Contractor** on the user's page,
types how many days (default 5), the workflow needs **manager approval**,
and on approve bumps `accountExpires` by that many days. A counter caps
total requests at 5 so the demo gets to the "denied" branch quickly.

| # | Example | Where it lives |
|---|---|---|
| 1 | Virtual attribute | MMC config - see below |
| 2 | Pass parameter to script | Workflow Parameter `ExtensionCap`, read by `Ex2-ReadContext` |
| 3 | Return value from script | `Ex3-PublishResult` - Write-Output + throw channels |
| 4 | Web Interface button | "Extend Contractor" command - see below |
| 5 | AddRecordToReport | Three activities in the workflow XAML (5a/5b/5c) |
| 6 | IfElse branching | "Ex 6: IfElse - under cap?" |
| 7 | Set-QADObject write-back | `Ex7-ExtendAccess` - bumps `accountExpires` + counter |
| 8 | Approval | Added in MMC after the rest deploys - see below |

---

## Prerequisites

### Ex 1 - Create the virtual attributes (MMC)

Two attributes on the user class, **both string** so the WI prompts render
naturally and the script parses what it needs:

1. Active Roles Console > **Configuration > Server Configuration >
   Schema > Active Directory > user**
2. Right-click `user` > **New > Active Roles Virtual Attribute**
3. First attribute (holds the # of days requested per press):
   - Name: `edsva-ExtendAccessRequest`
   - Display name: `Extend Access Request (days)`
   - Syntax: **Unicode String**, single-valued
4. Second attribute (the counter):
   - Name: `edsva-AccessExtensionsGranted`
   - Display name: `Access Extensions Granted`
   - Syntax: **Integer**, single-valued
5. Restart the **Active Roles Administration Service** so the new
   attributes are available to workflows.

### Ex 4 - "Extend Contractor" command button on the Web Interface

1. Active Roles Configuration Center > **Web Interface > [your site] >
   Customize Web Interface**
2. Navigate **Directory Management > Users**, find the **Commands** /
   **Actions** section.
3. Add a new command:
   - Title: **Extend Contractor**
   - Description: `How many days should this contractor's access be extended? Manager approval required.`
   - Action: **Modify Object** > attribute `edsva-ExtendAccessRequest`
   - Value: **prompt user** with default `5` and label "How many days?"
4. Save. Run `iisreset` to clear the customization cache.

---

## Deploy the script module + workflow

```powershell
& "C:\Users\jacob\source\repos\Conduit\ars-integration\examples\Deploy-UNITEBonusDayWorkflow.ps1"
```

(File name still `Deploy-UNITEBonusDayWorkflow.ps1` for git-history
continuity. Internally deploys "UNITE - Extend Contractor".)

The script:
- Renames `edsva-BonusDay*` virtual attrs in place if they're still around.
- Upserts the `UNITE-ExtendContractor` script module from `UNITE-BonusDay.ps1`.
- Upserts the `UNITE - Extend Contractor` workflow row with the
  `ExtensionCap = 5` workflow parameter.
- Trigger: `ModifyObject` on `edsva-ExtendAccessRequest` on `user`, scoped
  to the UNITE-2026 container.

Re-runnable. Finds rows by the new name first, falls back to the old name
and renames.

### Ex 8 - Add the Approval activity (MMC)

`ApprovalActivity` XAML has a per-tenant `mailConfiguration` GUID that's
brittle to script. Add it through the UI:

1. AR Console > **Configuration > Policies > Workflow > UNITE-2026 > UNITE - Extend Contractor**
2. Open the **Workflow Designer** (right-click > Edit Workflow).
3. Drag an **Approval Activity** onto the canvas **between Ex 2 and Ex 6**.
4. Activity name: `Ex 8: Approval - manager approves`
5. **Approvers:** add "Manager of the workflow target object" (built-in
   token). This routes the approval task to whoever is in the contractor's
   `manager` AD attribute.
6. Approval text (what the approver sees): `Approve extension request for
   <target>? Requested days: see the workflow target's
   edsva-ExtendAccessRequest attribute.` (Optional - default text is fine.)
7. **Save**.

Doing this through MMC is a stronger demo beat anyway - the audience sees
that adding manager approval is one drag-and-drop, no code.

---

## Demo flow at the talk

1. **Setup:** demo contractor "Joe Contractor" has `accountExpires = today + 90 days`
   and a manager attribute pointing at a user you can log in as.
2. In the Web Interface (logged in as Joe's manager), open Joe's user page.
   Point out the **Extend Contractor** command.
3. Click it. A "How many days?" prompt appears with **5** pre-filled.
   Submit.
4. Switch to the manager's **Tasks** view (or a second tab logged in as
   the same manager). A new approval task is waiting: "Approve extension
   request for Joe Contractor?". Click **Approve**.
5. Switch to AR Console > **Reports / Change History**. Walk the audience
   through:

   | Activity | Concept demoed |
   |---|---|
   | `Ex 5a: AddRecordToReport` | Static text in Change History |
   | `Ex 2: PS read workflow parameter + AD attributes` | Workflow param + $Request.Get reads |
   | `Ex 8: Approval - manager approves` | Out-of-band approval |
   | `Ex 6: IfElse - under cap?` | Token-condition branching |
   | `Ex 7: PS Set-QADObject - extend accountExpires +Nd` | Write-back to AD |
   | `Ex 3: PS return new value to next step` | Return-value channels |
   | `Ex 5b: AddRecordToReport (granted)` OR `Ex 5c: (denied)` | Branch outcome |

6. **Reset between runs:** in MMC, clear `edsva-AccessExtensionsGranted`
   (or set to 0) and optionally roll `accountExpires` back. The Extend
   Contractor command works again.

---

## Troubleshooting

- **"The workflow failed validation"** - missing/mismatched virtual
  attribute name, or AR Admin Service wasn't restarted after a rename.
- **WI button prompts but workflow doesn't fire** - the `edsva-ExtendAccessRequest`
  attribute might be set to the SAME value twice (ARS only fires on actual
  change). Set it to blank first, then try again. Or clear and resubmit.
- **Approval task doesn't appear in Tasks view** - the demo user's
  `manager` attribute isn't set, or the manager isn't an active AR user.
- **MMC IfElse Branch Properties shows blank "AND group"** - MMC's UI
  cannot always render a `WorkflowTargetToken`-based condition that was
  scripted in. The workflow still EXECUTES correctly. Rebuild through the
  token picker if you need the dialog to display.
- **Counter doesn't reset** - by design. Use MMC to clear
  `edsva-AccessExtensionsGranted` between demo runs.
- **WI button missing after retitle** - `iisreset` and clear the cache at
  `...\Web Interface\<site>\App_Data\Cache`.
