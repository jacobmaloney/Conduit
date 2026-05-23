# =============================================================================
# UNITE-ExtendContractor - script module backing the "UNITE - Extend Contractor"
# workflow. Demonstrates 8 common ARS workflow patterns the audience asks
# about. Story: a contractor is hired with accountExpires set to today+90 days.
# Manager presses "Extend Contractor Access" - workflow needs manager approval,
# bumps accountExpires by 1 day, increments a counter, caps at 5 extensions.
#
#   Ex 1  Virtual attribute              (config - see SETUP.md)
#   Ex 2  Pass parameter to script        $Workflow.Parameter("ExtensionCap")
#   Ex 3  Return value from script        Set-WorkflowVariable + Write-Output
#   Ex 4  Web Interface button            (config - see SETUP.md)
#   Ex 5  AddRecordToReport               (in the workflow XAML)
#   Ex 6  IfElse branching                (in the workflow XAML)
#   Ex 7  Update target via Set-QADObject Set-QADObject inside Ex7-ExtendAccess
#   Ex 8  Approval activity               (added in MMC - per SETUP.md)
#
# File is named UNITE-BonusDay.ps1 on disk for git history continuity; the
# deployed ARS ScriptModule is renamed to "UNITE-ExtendContractor".
#
# Brought to you by iC Consult * Identity & Access Management specialists
# We build IAM solutions that don't break.  sales@ic-consult.com
# =============================================================================

# Default cap. The workflow's Parameters dialog overrides this per-deploy
# (workflow parameter "ExtensionCap"). Demo of Ex 2.
$script:DefaultExtensionCap = 5

function Ex2-ReadContext {
    # EXAMPLE 2 + reading object attributes.
    # The WI button writes the requested # of days into edsva-ExtendAccessRequest
    # (string holding an integer). Defaults to 5 if blank or unparseable.
    try {
        $cap = $Workflow.Parameter("ExtensionCap")
        if (-not $cap) { $cap = $script:DefaultExtensionCap }
        $daysStr = "$($Request.Get('edsva-ExtendAccessRequest'))".Trim()
        $days = 0
        if (-not [int]::TryParse($daysStr, [ref]$days) -or $days -le 0) { $days = 5 }

        $manager = "$($Request.Get('manager'))".Trim()
        $sam     = "$($Request.Get('sAMAccountName'))".Trim()

        # accountExpires arrives as FileTime via $Request.Get; Quest returns a
        # plain DateTime which is easier to add days to.
        $u = Get-QADUser $Request.DN -DontUseDefaultIncludedProperties `
                -IncludedProperties sAMAccountName,accountExpires,manager
        $currentExpiry = $u.AccountExpires
        $expiryText = if ($currentExpiry) { $currentExpiry.ToString('yyyy-MM-dd') } else { 'never' }

        # Stash in script scope so downstream activities (Ex 3, Ex 7) can
        # read without going back to AD - Example 3 return-value pattern.
        $script:EX_Cap         = [int]$cap
        $script:EX_DaysReq     = [int]$days
        $script:EX_Manager     = $manager
        $script:EX_Sam         = $sam
        $script:EX_CurrentExp  = $currentExpiry

        Write-Output "[Ex 2] ExtensionCap workflow parameter = $cap"
        Write-Output "[Ex 2] Days requested (from web form) = $days"
        Write-Output "[Ex 2] Target = $sam"
        Write-Output "[Ex 2] Current accountExpires = $expiryText"
        Write-Output "[Ex 2] Manager DN (for Ex 8 approval) = $manager"

        # Throw the human summary so it lands on the activity row itself
        # (only ARS-surfaced channel for dynamic text). SuppressError=True
        # on the activity lets the workflow continue past the throw.
        throw "[Ex 2] $sam requested +$days day extension. Current expiry: $expiryText. Cap = $cap requests."
    } catch [System.Management.Automation.RuntimeException] {
        throw
    } catch {
        throw "[Ex 2 FAILED] $($_.Exception.Message)"
    }
}

function Ex7-ExtendAccess {
    # EXAMPLE 7: write back to target via Set-QADObject. Reached only after
    # the IfElse branch (Ex 6) confirms the counter is under the cap AND
    # the Approval activity (Ex 8) returned approved. Bumps:
    #   - edsva-AccessExtensionsGranted by 1 (counts requests, not days)
    #   - accountExpires by the # of days requested in Ex 2
    try {
        $current = "$($Request.Get('edsva-AccessExtensionsGranted'))"
        if ([string]::IsNullOrWhiteSpace($current)) { $current = "0" }
        $new = [int]$current + 1

        $days = $script:EX_DaysReq
        if (-not $days -or $days -le 0) { $days = 5 }

        # If currently null/0 (never expires), start at today+$days;
        # otherwise add $days to current value.
        $cur = $script:EX_CurrentExp
        if (-not $cur -or $cur -eq [DateTime]::MinValue) {
            $newExpiry = (Get-Date).Date.AddDays($days)
        } else {
            $newExpiry = $cur.AddDays($days)
        }

        Set-QADUser $Request.DN -AccountExpires $newExpiry -ObjectAttributes @{
            'edsva-AccessExtensionsGranted' = $new
        } | Out-Null

        $script:EX_NewCount  = $new
        $script:EX_NewExpiry = $newExpiry

        $expText = $newExpiry.ToString('yyyy-MM-dd')
        Write-Output "[Ex 7] edsva-AccessExtensionsGranted: $current -> $new"
        Write-Output "[Ex 7] accountExpires bumped to $expText (+$days days)"
        throw "[Ex 7] Extension #$new granted for $($script:EX_Sam): +$days days. New expiry: $expText."
    } catch [System.Management.Automation.RuntimeException] {
        throw
    } catch {
        throw "[Ex 7 FAILED] $($_.Exception.Message)"
    }
}

function Ex3-PublishResult {
    # EXAMPLE 3: return value from script to the audit trail.
    # ARS PowerShellActivities have three return-value channels:
    #   1) Write-Output - script trace only (click into the activity row)
    #   2) Throw - lands on the activity's main row in Change History
    #   3) Set-WorkflowVariable - downstream conditions/activities can read it
    $count   = $script:EX_NewCount
    $expiry  = $script:EX_NewExpiry
    if (-not $count)  { $count  = '<unknown>' }
    $expText = if ($expiry) { $expiry.ToString('yyyy-MM-dd') } else { '<unknown>' }

    Write-Output "[Ex 3 channel 1 / Write-Output] Extension count downstream: $count"
    Write-Output "[Ex 3 channel 1 / Write-Output] New expiry downstream: $expText"

    throw "[Ex 3] $($script:EX_Sam) now has $count extension(s). New expiry: $expText. Days requested this round: $($script:EX_DaysReq)."
}

function Ex9-ClearRequestMarker {
    # Cleanup: clear edsva-ExtendAccessRequest so the WI button can be pressed
    # again. Not one of the numbered examples - makes the demo idempotent.
    try {
        Set-QADObject $Request.DN -ObjectAttributes @{ 'edsva-ExtendAccessRequest' = '' } | Out-Null
        Write-Output "[cleanup] edsva-ExtendAccessRequest cleared; button ready for next use"
    } catch {
        Write-Output "[cleanup WARN] could not clear edsva-ExtendAccessRequest: $($_.Exception.Message)"
    }
}
