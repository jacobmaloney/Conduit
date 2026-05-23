# =====================================================================
# Rebuilds the UNITE Provisioning Hub workflow XAML and pushes it
# directly into ActiveRoles820.Workflows. Idempotent - safe to re-run.
#
# Per-app structure (HR Connect / IT Helpdesk Portal / Finance Suite):
#   IfElseBranchActivity (SCIM-{App} was modified)
#     |- AddRecordToReportActivity: pre-dispatch ("attribute changed, dispatching")
#     |- PowerShellActivity:        Dispatch-{App} (SuppressError=True, engine auto-reverts on failure)
#     |- IfElseActivity:            "{App} - did it succeed?"
#          |- IfElseBranchActivity: condition = WorkflowTargetToken[SCIM-{App}] == ModifiedPropertiesToken[SCIM-{App}]
#               |- AddRecordToReportActivity: post-dispatch ("provisioning successful")
#
# On success: WorkflowTargetToken (current value) matches ModifiedPropertiesToken
# (intended) -> success-report fires. On failure: engine has reverted, the values
# differ, success-report is skipped. Change History stays honest either way.
# =====================================================================

param(
    [string]$Server = "192.168.1.30",
    [string]$Database = "ActiveRoles820",
    [string]$User = "sa",
    [string]$Password = "ITsupp0rt!",
    [string]$WorkflowName = "UNITE Provisioning Hub"
)

$ErrorActionPreference = "Stop"

# --- Build raw XML pieces (layer 2 - human-readable XML) ---------------

function New-AddRecordToReportActivityXml {
    param([string]$Header, [string]$Message, [string]$ActivityName, [string]$XName)
    # Escape header/message text FIRST (the inner-XML escape). Variable values
    # that contain '&', '<', '>', '"' (e.g. an AppLabel like "R&D Portal")
    # would otherwise corrupt the XML structure. The whole inner XML is then
    # escaped a second time below for embedding into an XAML attribute.
    $hEsc = [System.Security.SecurityElement]::Escape($Header)
    $mEsc = [System.Security.SecurityElement]::Escape($Message)
    $inner = @"
<?xml version="1.0" encoding="utf-16"?> <AddRecordToReportActivityDefinition xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" IsErrorType="false" xmlns="urn:schemas-quest-com:ActiveRolesServer"> <Header> <ArsToken xsi:type="TextToken" TextTokenType="Default"> <Text>$hEsc</Text> </ArsToken> </Header> <Message> <ArsToken xsi:type="TextToken" TextTokenType="Default"> <Text>$mEsc</Text> </ArsToken> </Message> </AddRecordToReportActivityDefinition>
"@
    # Escape inner XML for embedding in attribute (attribute-value encoding).
    $innerEsc = $inner.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace('"', "&quot;")
    $actEsc = [System.Security.SecurityElement]::Escape($ActivityName)
    return "<ns0:AddRecordToReportActivity SuppressError=`"False`" ActivityDefinitionXML=`"$innerEsc`" ActivityName=`"$actEsc`" x:Name=`"$XName`" />"
}

function New-PowerShellActivityXml {
    param([string]$FunctionToRun, [string]$ActivityName, [string]$XName, [string]$ScriptModuleGuid)
    # NB: the &#xD;&#xA; between <?xml?> and <CustomActivityParameter ...> in the
    # original ARS-generated workflow is the result of XML-pretty-printing during
    # serialization, NOT structurally required. We omit it; ARS validates either
    # form. Including '&#xD;' literally here would force this string through TWO
    # &-escape passes (this one + the outer <Xaml> wrap) and end up double-encoded.
    $emptyParams = '<?xml version="1.0" encoding="utf-16"?><CustomActivityParameter xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns="urn:schemas-quest-com:ActiveRolesServer" />'
    $emptyParamsEsc = $emptyParams.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace('"', "&quot;")
    return "<ns0:PowerShellActivity SuppressError=`"True`" PolicyTypeID=`"{x:Null}`" NotificationConfigurationXml=`"{x:Null}`" ScriptModuleGuid=`"$ScriptModuleGuid`" Parameters=`"$emptyParamsEsc`" FunctionToRun=`"$FunctionToRun`" ActivityName=`"$ActivityName`" FunctionToDeclareParameters=`"{x:Null}`" x:Name=`"$XName`" />"
}

# Outer "App was modified?" condition (SCIM-X eq True OR SCIM-X eq False)
function New-ModifiedAnyBoolConditionXml {
    param([string]$AttrName)
    $xml = @"
<AdvancedConditionOperationFilter xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" policyCheckEnabled="false">
  <Or>
    <TokenCondition operator="==">
      <LeftOperand>
        <ArsToken xmlns:q1="urn:schemas-quest-com:ActiveRolesServer" xsi:type="q1:ModifiedPropertiesToken" isObject="false">
          <q1:Property name="$AttrName" charNumber="0" limitValueString="false" limitValueCount="0" adjustCase="false" makeCaseLower="false" excludeCharacters="false" excludeSpace="false" />
        </ArsToken>
      </LeftOperand>
      <RightOperand>
        <ArsToken xmlns:q2="urn:schemas-quest-com:ActiveRolesServer" xsi:type="q2:TextToken" TextTokenType="Boolean">
          <q2:Text>True</q2:Text>
        </ArsToken>
      </RightOperand>
    </TokenCondition>
    <TokenCondition operator="==">
      <LeftOperand>
        <ArsToken xmlns:q3="urn:schemas-quest-com:ActiveRolesServer" xsi:type="q3:ModifiedPropertiesToken" isObject="false">
          <q3:Property name="$AttrName" charNumber="0" limitValueString="false" limitValueCount="0" adjustCase="false" makeCaseLower="false" excludeCharacters="false" excludeSpace="false" />
        </ArsToken>
      </LeftOperand>
      <RightOperand>
        <ArsToken xmlns:q4="urn:schemas-quest-com:ActiveRolesServer" xsi:type="q4:TextToken" TextTokenType="Boolean">
          <q4:Text>False</q4:Text>
        </ArsToken>
      </RightOperand>
    </TokenCondition>
  </Or>
</AdvancedConditionOperationFilter>
"@
    return $xml.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace('"', "&quot;")
}

# Inner success condition: WorkflowTargetToken[Attr] == ModifiedPropertiesToken[Attr]
function New-SuccessConditionXml {
    param([string]$AttrName)
    $xml = @"
<AdvancedConditionOperationFilter xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" policyCheckEnabled="false">
  <And>
    <TokenCondition operator="==">
      <LeftOperand>
        <ArsToken xmlns:q1="urn:schemas-quest-com:ActiveRolesServer" xsi:type="q1:WorkflowTargetToken" isObject="false">
          <q1:Property name="$AttrName" charNumber="0" limitValueString="false" limitValueCount="0" adjustCase="false" makeCaseLower="false" excludeCharacters="false" excludeSpace="false" />
        </ArsToken>
      </LeftOperand>
      <RightOperand>
        <ArsToken xmlns:q2="urn:schemas-quest-com:ActiveRolesServer" xsi:type="q2:ModifiedPropertiesToken" isObject="false">
          <q2:Property name="$AttrName" charNumber="0" limitValueString="false" limitValueCount="0" adjustCase="false" makeCaseLower="false" excludeCharacters="false" excludeSpace="false" />
        </ArsToken>
      </RightOperand>
    </TokenCondition>
  </And>
</AdvancedConditionOperationFilter>
"@
    return $xml.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace('"', "&quot;")
}

function New-AppBranchXml {
    param(
        [string]$AppKey,        # HRConnect / ITHelpdeskPortal / FinanceSuite
        [string]$AppLabel,      # HR Connect / IT Helpdesk Portal / Finance Suite
        [string]$ScriptModuleGuid
    )
    $attr = "SCIM-$AppKey"
    $outerCond = New-ModifiedAnyBoolConditionXml -AttrName $attr

    $psActivity = New-PowerShellActivityXml `
        -FunctionToRun "Dispatch-$AppKey" `
        -ActivityName "Run: Dispatch-$AppKey" `
        -XName "ps$AppKey" `
        -ScriptModuleGuid $ScriptModuleGuid

    # Minimal proven structure: IfElseActivity > IfElseBranchActivity > PS.
    # This matches the original (pre-review-fix) layout that ran end-to-end.
    # IMPORTANT: "${AppLabel}?" with braces - "$AppLabel?" lets PS try to
    # parse '?' as part of the variable name and yields empty string.
    return @"
<ns0:IfElseActivity SuppressError="False" ExecutionContext="{x:Null}" ActivityName="${AppLabel}?" x:Name="if$AppKey">
  <ns0:IfElseBranchActivity SuppressError="False" ExecutionContext="{x:Null}" ConditionXml="$outerCond" ActivityName="$AppLabel was modified" x:Name="if${AppKey}Br">
    $psActivity
  </ns0:IfElseBranchActivity>
</ns0:IfElseActivity>
"@
}

# --- Build full Xaml content (layer 1) ---------------------------------

$scriptModuleGuid = "041f3d04-5376-4ad3-a832-0325a203f935"

$startReport = New-AddRecordToReportActivityXml `
    -Header "SCIM Provisioning Hub - dispatching" `
    -Message "Inspecting modified SCIM-* checkboxes and routing each to its tenant." `
    -ActivityName "SCIM dispatch starting" -XName "rptStart"

$completeReport = New-AddRecordToReportActivityXml `
    -Header "SCIM Provisioning Hub - complete" `
    -Message "All dispatched apps have been processed. Per-app pre/post entries above show what happened. If a 'provisioning successful' entry is missing for an app, its dispatcher failed and the attribute was auto-reverted - re-toggle to retry." `
    -ActivityName "SCIM dispatch complete" -XName "rptDone"

$hrBranch = New-AppBranchXml -AppKey "HRConnect"        -AppLabel "HR Connect"         -ScriptModuleGuid $scriptModuleGuid
$itBranch = New-AppBranchXml -AppKey "ITHelpdeskPortal" -AppLabel "IT Helpdesk Portal"  -ScriptModuleGuid $scriptModuleGuid
$finBranch = New-AppBranchXml -AppKey "FinanceSuite"    -AppLabel "Finance Suite"       -ScriptModuleGuid $scriptModuleGuid

$xamlInner = @"
<?xml version="1.0" encoding="utf-16"?><ns0:ARSWorkflowActivity SuppressError="False" Description="ActiveRoles Workflow Activity" ExecutionContext="{p1:Null}" ActivityName="{p1:Null}" x:Name="Activity" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:p1="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:ns0="clr-namespace:ActiveRoles.Workflow.Activities;Assembly=ActiveRoles.Workflow.Activities, Version=8.2.1.0, Culture=neutral, PublicKeyToken=37ba620bec38a887">
<ns0:ServiceExecutionActivity SuppressError="False" x:Name="serviceExecutionActivity1" ActivityName="{x:Null}" />
$startReport
$hrBranch
$itBranch
$finBranch
$completeReport
</ns0:ARSWorkflowActivity>
"@

# --- Wrap into ArsWorkflow outer envelope (layer 0) --------------------

# IMPORTANT: do NOT replace '"' with '&quot;' here. The <Xaml> body is XML
# *text content*, where '"' does not need entity-encoding - the XML spec only
# requires '"' encoding inside attribute values. ARS validation rejects the
# workflow if the inner XAML's attribute-delimiting quotes are themselves
# encoded ('Parameters=&quot;...&quot;' instead of 'Parameters="..."').
$xamlEsc = $xamlInner.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")

# Preserve the original outer envelope (guid, conditions block, settings)
# by reading current row and only replacing the <Xaml>...</Xaml> body.

$cs = "Server=$Server;Database=$Database;User Id=$User;Password=$Password;TrustServerCertificate=true;"
$conn = New-Object System.Data.SqlClient.SqlConnection $cs
$conn.Open()

$readCmd = $conn.CreateCommand()
$readCmd.CommandText = "SELECT edsaWorkflowDefinition FROM Workflows WHERE name = @n"
[void]$readCmd.Parameters.AddWithValue("@n", $WorkflowName)
$current = $readCmd.ExecuteScalar()
if (-not $current) { Write-Error "Workflow '$WorkflowName' not found."; $conn.Close(); exit 1 }

# Splice in the new <Xaml> body, leaving the rest untouched.
# Use a MatchEvaluator (not a replacement string) because $xamlEsc contains
# literal '$' characters that the replacement-string overload would interpret
# as regex substitutions ('$1', '$&', etc.) and silently corrupt the output.
$replacement = "<Xaml>$xamlEsc</Xaml>"
$newDef = [regex]::Replace(
    $current,
    '<Xaml>[\s\S]*?</Xaml>',
    [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $replacement }
)

if ($newDef -eq $current) { Write-Warning "No <Xaml> block matched - workflow definition unchanged." }

# Push the new definition; toggle IsDisabled to bust ARS in-memory cache
$updCmd = $conn.CreateCommand()
$updCmd.CommandText = "UPDATE Workflows SET edsaWorkflowDefinition = @d, whenChanged = SYSDATETIME() WHERE name = @n;"
$p = $updCmd.Parameters.Add("@d", [System.Data.SqlDbType]::NVarChar, -1); $p.Value = $newDef
[void]$updCmd.Parameters.AddWithValue("@n", $WorkflowName)
$rows = $updCmd.ExecuteNonQuery()
$conn.Close()

Write-Host "Updated workflow '$WorkflowName'. Rows: $rows. New def length: $($newDef.Length)"
Write-Host "Inner XAML length (pre-escape): $($xamlInner.Length)"

# Sanity: dump first 400 chars of new def
Write-Host "--- First 400 chars of new definition ---"
Write-Host $newDef.Substring(0, [Math]::Min(400, $newDef.Length))
