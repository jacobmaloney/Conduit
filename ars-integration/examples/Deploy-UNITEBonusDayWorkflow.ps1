# =============================================================================
# Deploy-UNITEExtendContractor - creates/updates:
#   1. VirtualSchema    : renames edsva-BonusDay* -> edsva-Extend*/AccessExtensions*
#   2. ScriptModule     : UNITE-ExtendContractor (loads UNITE-BonusDay.ps1)
#   3. Workflow         : "UNITE - Extend Contractor"
#
# File still named ...BonusDay... on disk for git history continuity.
# Re-runnable. Finds rows by NEW name first, falls back to OLD name + renames.
# After running, restart the Active Roles Administration Service so the
# virtual attribute schema cache picks up the new names. Update the
# Web Interface command button title to "Extend Contractor Access".
# =============================================================================

param(
    [string]$Server = "192.168.1.30",
    [string]$Database = "ActiveRoles820",
    [string]$User = "sa",
    [Parameter(Mandatory=$true)]
    [string]$Password,
    [string]$WorkflowName     = "UNITE - Extend Contractor",
    [string]$OldWorkflowName  = "UNITE Examples - Bonus Day Off",
    [string]$ScriptModuleName    = "UNITE-ExtendContractor",
    [string]$OldScriptModuleName = "UNITE-BonusDay",
    [string]$ScriptModuleParentGuid = "b184d443-23d5-4c99-b0d0-8cdc4ec1da37",
    [string]$WorkflowParentGuid     = "32e903fc-097e-49d3-9a7c-1f3eeedf3e4d",
    [string]$AdScopeContainerGuid   = "80a8172f-2d21-4035-b5d0-2675f24b66a1"
)

$ErrorActionPreference = "Stop"
$cs = "Server=$Server;Database=$Database;User Id=$User;Password=$Password;TrustServerCertificate=true;"

$scriptPath = Join-Path $PSScriptRoot "UNITE-BonusDay.ps1"
if (-not (Test-Path $scriptPath)) { Write-Error "Script file not found: $scriptPath"; exit 1 }
$scriptText = [System.IO.File]::ReadAllText($scriptPath)

# Parse-check before pushing
$tokens = $errors = $null
[System.Management.Automation.Language.Parser]::ParseInput($scriptText, [ref]$tokens, [ref]$errors) | Out-Null
if ($errors.Count -ne 0) {
    $errors | ForEach-Object { Write-Host "PARSE ERR line $($_.Extent.StartLineNumber): $($_.Message)" }
    exit 1
}

$conn = New-Object System.Data.SqlClient.SqlConnection $cs
$conn.Open()

# -----------------------------------------------------------------------------
# 1. Virtual attribute renames. Idempotent - only fires if old name still
#    present and new name not already in use.
#    The schemaIDGUID stays the same, so the workflow XAML's <AttributeNames>
#    references resolve via the same identity even after the user-visible
#    name flips. AR service restart picks up the new name in the in-memory
#    schema cache.
# -----------------------------------------------------------------------------

function Rename-VirtualAttr {
    param(
        [System.Data.SqlClient.SqlConnection]$Conn,
        [string]$OldName, [string]$NewName,
        [string]$NewLdap, [string]$NewDisplay
    )
    $c = $Conn.CreateCommand()
    $c.CommandText = "SELECT COUNT(*) FROM VirtualSchema WHERE name = @n"
    [void]$c.Parameters.AddWithValue("@n", $NewName)
    if (([int]$c.ExecuteScalar()) -gt 0) {
        Write-Host "VirtualAttr '$NewName' already exists - skipping rename."
        return
    }
    $c2 = $Conn.CreateCommand()
    $c2.CommandText = "SELECT COUNT(*) FROM VirtualSchema WHERE name = @n"
    [void]$c2.Parameters.AddWithValue("@n", $OldName)
    if (([int]$c2.ExecuteScalar()) -eq 0) {
        Write-Host "VirtualAttr '$OldName' not found - nothing to rename."
        return
    }
    $u = $Conn.CreateCommand()
    $u.CommandText = @"
UPDATE VirtualSchema
SET name = @new,
    distinguishedName = 'CN=' + @new + ',CN=Virtual Attributes,CN=Server Configuration,CN=Configuration',
    lDAPDisplayName = @ldap,
    displayName = @disp,
    whenChanged = SYSDATETIME()
WHERE name = @old
"@
    [void]$u.Parameters.AddWithValue("@new",  $NewName)
    [void]$u.Parameters.AddWithValue("@old",  $OldName)
    [void]$u.Parameters.AddWithValue("@ldap", $NewLdap)
    [void]$u.Parameters.AddWithValue("@disp", $NewDisplay)
    [void]$u.ExecuteNonQuery()
    Write-Host "Renamed VirtualAttr '$OldName' -> '$NewName'."
}

Rename-VirtualAttr -Conn $conn -OldName 'edsva-BonusDayRequest' -NewName 'edsva-ExtendAccessRequest' `
    -NewLdap 'ExtendAccessRequest' -NewDisplay 'Extend Access Request'
Rename-VirtualAttr -Conn $conn -OldName 'edsva-BonusDaysGranted' -NewName 'edsva-AccessExtensionsGranted' `
    -NewLdap 'edsva-AccessExtensionsGranted' -NewDisplay 'Access Extensions Granted'

# -----------------------------------------------------------------------------
# 2. ScriptModule - rename old row if present, then upsert body.
# -----------------------------------------------------------------------------

$r = $conn.CreateCommand()
$r.CommandText = "UPDATE ScriptModules SET name = @new, distinguishedName = REPLACE(distinguishedName, 'CN=' + @old + ',', 'CN=' + @new + ',') WHERE name = @old AND NOT EXISTS (SELECT 1 FROM ScriptModules WHERE name = @new)"
[void]$r.Parameters.AddWithValue("@old", $OldScriptModuleName)
[void]$r.Parameters.AddWithValue("@new", $ScriptModuleName)
$renamed = $r.ExecuteNonQuery()
if ($renamed -gt 0) { Write-Host "Renamed ScriptModule '$OldScriptModuleName' -> '$ScriptModuleName'." }

$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT CAST(objectGUID AS UNIQUEIDENTIFIER) FROM ScriptModules WHERE name = @n"
[void]$cmd.Parameters.AddWithValue("@n", $ScriptModuleName)
$existingGuid = $cmd.ExecuteScalar()

if ($existingGuid) {
    $scriptModuleGuid = [Guid]$existingGuid
    Write-Host "ScriptModule '$ScriptModuleName' exists (GUID $scriptModuleGuid); updating body."
    $u = $conn.CreateCommand()
    $u.CommandText = "UPDATE ScriptModules SET edsaScriptText = @t, whenChanged = SYSDATETIME() WHERE objectGUID = @g"
    $pt = $u.Parameters.Add("@t", [System.Data.SqlDbType]::NVarChar, -1); $pt.Value = $scriptText
    [void]$u.Parameters.AddWithValue("@g", $scriptModuleGuid)
    [void]$u.ExecuteNonQuery()
} else {
    $scriptModuleGuid = [Guid]::NewGuid()
    Write-Host "Creating ScriptModule '$ScriptModuleName' (GUID $scriptModuleGuid)."
    $dn = "CN=$ScriptModuleName,CN=UNITE-2026,CN=Script Modules,CN=Configuration"
    $i = $conn.CreateCommand()
    $i.CommandText = @"
INSERT INTO ScriptModules
    (objectGUID, ParentObjectGUID, name, distinguishedName, objectClass,
     edsaScriptText, edsaScriptLanguage, edsaScriptType,
     whenCreated, whenChanged, edsaIsPredefined, edsaSystemObject)
VALUES (@g, @p, @n, @d, 'edsScriptModule', @t, 'PowerShell', 0,
        SYSDATETIME(), SYSDATETIME(), 0, 0);
"@
    [void]$i.Parameters.AddWithValue("@g", $scriptModuleGuid)
    [void]$i.Parameters.AddWithValue("@p", [Guid]$ScriptModuleParentGuid)
    [void]$i.Parameters.AddWithValue("@n", $ScriptModuleName)
    [void]$i.Parameters.AddWithValue("@d", $dn)
    $pt = $i.Parameters.Add("@t", [System.Data.SqlDbType]::NVarChar, -1); $pt.Value = $scriptText
    [void]$i.ExecuteNonQuery()
}

# -----------------------------------------------------------------------------
# 3. Workflow XAML - rename existing row if present, then upsert def + params.
# -----------------------------------------------------------------------------

$rw = $conn.CreateCommand()
$rw.CommandText = "UPDATE Workflows SET name = @new, displayName = @new, distinguishedName = REPLACE(distinguishedName, 'CN=' + @old + ',', 'CN=' + @new + ',') WHERE name = @old AND NOT EXISTS (SELECT 1 FROM Workflows WHERE name = @new)"
[void]$rw.Parameters.AddWithValue("@old", $OldWorkflowName)
[void]$rw.Parameters.AddWithValue("@new", $WorkflowName)
$wrenamed = $rw.ExecuteNonQuery()
if ($wrenamed -gt 0) { Write-Host "Renamed Workflow '$OldWorkflowName' -> '$WorkflowName'." }

# Helpers ---------------------------------------------------------------------

function Add-Report {
    param([string]$Header, [string]$Message, [string]$ActivityName, [string]$XName)
    $defL1 = "&lt;?xml version=&quot;1.0&quot; encoding=&quot;utf-16&quot;?&gt;&lt;AddRecordToReportActivityDefinition xmlns:xsd=&quot;http://www.w3.org/2001/XMLSchema&quot; xmlns:xsi=&quot;http://www.w3.org/2001/XMLSchema-instance&quot; IsErrorType=&quot;false&quot; xmlns=&quot;urn:schemas-quest-com:ActiveRolesServer&quot;&gt;&lt;Header&gt;&lt;ArsToken xsi:type=&quot;TextToken&quot; TextTokenType=&quot;Default&quot;&gt;&lt;Text&gt;$Header&lt;/Text&gt;&lt;/ArsToken&gt;&lt;/Header&gt;&lt;Message&gt;&lt;ArsToken xsi:type=&quot;TextToken&quot; TextTokenType=&quot;Default&quot;&gt;&lt;Text&gt;$Message&lt;/Text&gt;&lt;/ArsToken&gt;&lt;/Message&gt;&lt;/AddRecordToReportActivityDefinition&gt;"
    return "<ns0:AddRecordToReportActivity SuppressError=`"False`" ActivityDefinitionXML=`"$defL1`" ActivityName=`"$ActivityName`" x:Name=`"$XName`" />"
}

function PS-Activity {
    param([string]$FunctionToRun, [string]$ActivityName, [string]$XName, [string]$Guid, [bool]$Suppress = $true)
    $paramsL1 = "&lt;?xml version=&quot;1.0&quot; encoding=&quot;utf-16&quot;?&gt;&lt;CustomActivityParameter xmlns:xsd=&quot;http://www.w3.org/2001/XMLSchema&quot; xmlns:xsi=&quot;http://www.w3.org/2001/XMLSchema-instance&quot; xmlns=&quot;urn:schemas-quest-com:ActiveRolesServer&quot; /&gt;"
    $suppressStr = if ($Suppress) { "True" } else { "False" }
    return "<ns0:PowerShellActivity SuppressError=`"$suppressStr`" PolicyTypeID=`"{x:Null}`" NotificationConfigurationXml=`"{x:Null}`" ScriptModuleGuid=`"$Guid`" Parameters=`"$paramsL1`" FunctionToRun=`"$FunctionToRun`" ActivityName=`"$ActivityName`" FunctionToDeclareParameters=`"{x:Null}`" x:Name=`"$XName`" />"
}

function Condition-TargetCmpLiteral {
    param([string]$AttrName, [string]$Op, [int]$LiteralInt)
    $opEnc = $Op.Replace('<', '&amp;lt;').Replace('>', '&amp;gt;')
    return "&lt;AdvancedConditionOperationFilter xmlns:xsd=&quot;http://www.w3.org/2001/XMLSchema&quot; xmlns:xsi=&quot;http://www.w3.org/2001/XMLSchema-instance&quot; policyCheckEnabled=&quot;false&quot;&gt;&lt;And&gt;&lt;TokenCondition operator=&quot;$opEnc&quot;&gt;&lt;LeftOperand&gt;&lt;ArsToken xmlns:q1=&quot;urn:schemas-quest-com:ActiveRolesServer&quot; xsi:type=&quot;q1:WorkflowTargetToken&quot; isObject=&quot;false&quot;&gt;&lt;q1:Property name=&quot;$AttrName&quot; charNumber=&quot;0&quot; limitValueString=&quot;false&quot; limitValueCount=&quot;0&quot; adjustCase=&quot;false&quot; makeCaseLower=&quot;false&quot; excludeCharacters=&quot;false&quot; excludeSpace=&quot;false&quot; /&gt;&lt;/ArsToken&gt;&lt;/LeftOperand&gt;&lt;RightOperand&gt;&lt;ArsToken xmlns:q2=&quot;urn:schemas-quest-com:ActiveRolesServer&quot; xsi:type=&quot;q2:TextToken&quot; TextTokenType=&quot;Default&quot;&gt;&lt;q2:Text&gt;$LiteralInt&lt;/q2:Text&gt;&lt;/ArsToken&gt;&lt;/RightOperand&gt;&lt;/TokenCondition&gt;&lt;/And&gt;&lt;/AdvancedConditionOperationFilter&gt;"
}

# Activities ------------------------------------------------------------------

$smGuid = $scriptModuleGuid.ToString()

$rptStart = Add-Report `
    -Header "Ex 5a: AddRecordToReport - request received" `
    -Message "Web Interface 'Extend Contractor' button set edsva-ExtendAccessRequest = days. See Ex 2 row for parsed value, Ex 8 for the approval result." `
    -ActivityName "Ex 5a: AddRecordToReport" -XName "rptStart"

$ex2 = PS-Activity -FunctionToRun "Ex2-ReadContext" `
    -ActivityName "Ex 2: PS read workflow parameter + AD attributes" -XName "psEx2" `
    -Guid $smGuid -Suppress $true

$ex7 = PS-Activity -FunctionToRun "Ex7-ExtendAccess" `
    -ActivityName "Ex 7: PS Set-QADObject - extend accountExpires +1d" -XName "psEx7" `
    -Guid $smGuid -Suppress $true

$ex3 = PS-Activity -FunctionToRun "Ex3-PublishResult" `
    -ActivityName "Ex 3: PS return new value to next step" -XName "psEx3" `
    -Guid $smGuid -Suppress $true

$rptOK = Add-Report `
    -Header "Ex 5b: AddRecordToReport - granted" `
    -Message "Contractor access extended by the requested number of days. New expiry + counter visible in the Ex 3 row above." `
    -ActivityName "Ex 5b: AddRecordToReport (granted)" -XName "rptOK"

$rptDenied = Add-Report `
    -Header "Ex 5c: AddRecordToReport - denied (at cap)" `
    -Message "Counter has hit the ExtensionCap. No further extensions allowed without resetting edsva-AccessExtensionsGranted in MMC." `
    -ActivityName "Ex 5c: AddRecordToReport (denied)" -XName "rptDenied"

$cleanup = PS-Activity -FunctionToRun "Ex9-ClearRequestMarker" `
    -ActivityName "cleanup: clear edsva-ExtendAccessRequest" -XName "psCleanup" `
    -Guid $smGuid -Suppress $true

$capCondUnder = Condition-TargetCmpLiteral -AttrName "edsva-AccessExtensionsGranted" -Op "<"  -LiteralInt 5
$capCondAt    = Condition-TargetCmpLiteral -AttrName "edsva-AccessExtensionsGranted" -Op ">=" -LiteralInt 5

$ifElse = @"
<ns0:IfElseActivity SuppressError="False" ExecutionContext="{x:Null}" ActivityName="Ex 6: IfElse - under cap?" x:Name="ifCap">
  <ns0:IfElseBranchActivity SuppressError="False" ExecutionContext="{x:Null}" ConditionXml="$capCondUnder" ActivityName="Under cap" x:Name="ifCapUnder">
    $ex7
    $ex3
    $rptOK
  </ns0:IfElseBranchActivity>
  <ns0:IfElseBranchActivity SuppressError="False" ExecutionContext="{x:Null}" ConditionXml="$capCondAt" ActivityName="At cap" x:Name="ifCapAt">
    $rptDenied
  </ns0:IfElseBranchActivity>
</ns0:IfElseActivity>
"@

$xamlInner = @"
<?xml version="1.0" encoding="utf-16"?><ns0:ARSWorkflowActivity SuppressError="False" Description="ActiveRoles Workflow Activity" ExecutionContext="{p1:Null}" ActivityName="{p1:Null}" x:Name="Activity" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:p1="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:ns0="clr-namespace:ActiveRoles.Workflow.Activities;Assembly=ActiveRoles.Workflow.Activities, Version=8.2.1.0, Culture=neutral, PublicKeyToken=37ba620bec38a887">
<ns0:ServiceExecutionActivity SuppressError="False" x:Name="serviceExecutionActivity1" ActivityName="{x:Null}" />
$rptStart
$ex2
$ifElse
$cleanup
</ns0:ARSWorkflowActivity>
"@

$xamlEsc = $xamlInner.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")

$workflowGuid = [Guid]::NewGuid()
$workflowDef = @"
<ArsWorkflow xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" guid="$workflowGuid">
  <Xaml>$xamlEsc</Xaml>
  <InitializationScript />
  <Conditions>
    <Operation xsi:type="ModifyObject" policyCheckEnabled="false" objectClass="user">
      <AttributeNames>
        <string>edsva-ExtendAccessRequest</string>
      </AttributeNames>
    </Operation>
    <InitiatorsAndScopes>
      <InitiatorAndScopeFilter>
        <Initiator xsi:type="SecurityIdentifierInitiatorFilter" sid="S-1-1-0" />
        <Scope xsi:type="IncludeContainer">
          <Container guid="$AdScopeContainerGuid" />
        </Scope>
      </InitiatorAndScopeFilter>
    </InitiatorsAndScopes>
    <AdvancedConditions policyCheckEnabled="false">
      <And />
    </AdvancedConditions>
  </Conditions>
  <Settings>
    <AccountType>ServiceAccount</AccountType>
    <EnforceApproval>false</EnforceApproval>
  </Settings>
</ArsWorkflow>
"@

$workflowParamsXml = @"
<ParameterDefinitions xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns="urn:schemas-quest-com:ActiveRolesServer:WorkflowParameters"><ParameterDefinition name="ExtensionCap" syntax="String" multiValued="false" required="false" runtime="false"><DisplayName>Extension Cap (max grants per contractor)</DisplayName><DefaultValue isScript="false"><ScriptGuid xsi:nil="true" /><Values><Value isEncrypted="false"><RawValue>5</RawValue></Value></Values></DefaultValue><PossibleValues isScript="false"><ScriptGuid xsi:nil="true" /><Values /></PossibleValues></ParameterDefinition></ParameterDefinitions>
"@

$cmd2 = $conn.CreateCommand()
$cmd2.CommandText = "SELECT CAST(objectGUID AS UNIQUEIDENTIFIER) FROM Workflows WHERE name = @n"
[void]$cmd2.Parameters.AddWithValue("@n", $WorkflowName)
$existingWf = $cmd2.ExecuteScalar()

if ($existingWf) {
    Write-Host "Workflow '$WorkflowName' exists (GUID $existingWf); updating definition + parameters."
    $upd = $conn.CreateCommand()
    $upd.CommandText = @"
UPDATE Workflows
SET edsaWorkflowDefinition = @d,
    edsaWorkflowParameters = @pp,
    displayName            = @dn,
    objectClass            = 'edsWorkflowDefinition',
    whenChanged            = SYSDATETIME()
WHERE objectGUID = @g
"@
    $pd  = $upd.Parameters.Add("@d",  [System.Data.SqlDbType]::NVarChar, -1); $pd.Value  = $workflowDef
    $ppp = $upd.Parameters.Add("@pp", [System.Data.SqlDbType]::NVarChar, -1); $ppp.Value = $workflowParamsXml
    [void]$upd.Parameters.AddWithValue("@dn", $WorkflowName)
    [void]$upd.Parameters.AddWithValue("@g",  $existingWf)
    [void]$upd.ExecuteNonQuery()
} else {
    Write-Host "Creating Workflow '$WorkflowName' (GUID $workflowGuid)."
    $dn = "CN=$WorkflowName,CN=UNITE-2026,CN=Workflow,CN=Policies,CN=Configuration"
    $ins = $conn.CreateCommand()
    $ins.CommandText = @"
INSERT INTO Workflows
    (objectGUID, ParentObjectGUID, name, displayName, distinguishedName, objectClass,
     edsaWorkflowDefinition, edsaWorkflowParameters,
     whenCreated, whenChanged, edsaIsPredefined, edsaSystemObject, edsaWorkflowIsDisabled)
VALUES (@g, @p, @n, @dn, @d, 'edsWorkflowDefinition',
        @def, @pp,
        SYSDATETIME(), SYSDATETIME(), 0, 0, 0);
"@
    [void]$ins.Parameters.AddWithValue("@g",  $workflowGuid)
    [void]$ins.Parameters.AddWithValue("@p",  [Guid]$WorkflowParentGuid)
    [void]$ins.Parameters.AddWithValue("@n",  $WorkflowName)
    [void]$ins.Parameters.AddWithValue("@dn", $WorkflowName)
    [void]$ins.Parameters.AddWithValue("@d",  $dn)
    $pdef = $ins.Parameters.Add("@def", [System.Data.SqlDbType]::NVarChar, -1); $pdef.Value = $workflowDef
    $ppp  = $ins.Parameters.Add("@pp",  [System.Data.SqlDbType]::NVarChar, -1); $ppp.Value  = $workflowParamsXml
    [void]$ins.ExecuteNonQuery()
}

$conn.Close()

Write-Host ""
Write-Host "=== Done ==="
Write-Host "ScriptModule: $ScriptModuleName ($scriptModuleGuid)"
Write-Host "Workflow:     $WorkflowName"
Write-Host ""
Write-Host "Manual steps remaining:"
Write-Host "  1. Restart 'Active Roles Administration Service' so the virtual"
Write-Host "     attribute schema cache picks up the rename."
Write-Host "  2. In Web Interface customization: update the command button"
Write-Host "     title from 'Grant Bonus Day' to 'Extend Contractor Access',"
Write-Host "     and change the attribute it sets from edsva-BonusDayRequest"
Write-Host "     to edsva-ExtendAccessRequest."
Write-Host "  3. iisreset to clear Web Interface cache."
Write-Host "  4. Test: Set-QADUser -Identity <contractor> -ObjectAttributes @{ 'edsva-ExtendAccessRequest' = 'birthday weekend' }"
