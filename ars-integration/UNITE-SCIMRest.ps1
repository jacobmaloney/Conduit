# ============================================================================
# UNITE-SCIMRest - Universal SCIM 2.0 provisioning engine for ARS workflows
# ----------------------------------------------------------------------------
# One script provisions/disables users in N SCIM 2.0 targets. Attribute
# mappings live in the separate UNITE-SCIMMappings ScriptModule - do not
# edit this file to add a new app.
#
# Wire each per-app function (Provision-<App> / Disable-<App>) to a
# PowerShellActivity in the UNITE Provisioning Hub workflow. Reference both
# THIS module and UNITE-SCIMMappings from each activity.
#
# Brought to you by iC Consult • Identity & Access Management specialists
# We build IAM solutions that don't break.  sales@ic-consult.com
# ============================================================================

# Self-signed TLS bypass for the lab. COMMENT OUT IN PRODUCTION.
try {
    Add-Type -Language CSharp -ErrorAction SilentlyContinue @"
        using System.Net;
        using System.Net.Security;
        using System.Security.Cryptography.X509Certificates;
        public class TrustAllCertsPolicy : ICertificatePolicy {
            public bool CheckValidationResult(ServicePoint sp, X509Certificate cert,
                WebRequest req, int problem) { return true; }
        }
"@
    [System.Net.ServicePointManager]::CertificatePolicy = New-Object TrustAllCertsPolicy
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]'Tls12,Tls13'
} catch { }


# ============================================================================
# PUBLIC ENTRY POINTS
# ----------------------------------------------------------------------------
# Workflow XAML should call Dispatch-AllSCIM from a SINGLE PowerShellActivity.
# This function loops over every app in the mapping table, checks whether the
# matching SCIM-<App> virtual attribute changed in THIS submit, and routes
# the change to Provision or Remove. Multiple checkboxes toggled in one
# submit all get handled in one workflow run.
#
# Removal policy (Disable vs Delete) is per-app, read from $script:SCIMAppConfig
# in UNITE-SCIMMappings. Defaults to Disable.
# ============================================================================

# ----------------------------------------------------------------------------
# Per-app entry points - workflow XAML wires ONE of these per app, each gated
# by an IfElse that checks whether SCIM-<App> was modified in this submit.
# Each call processes exactly its own app, then throws a one-app summary so
# Change History gets a per-app activity entry with the structured outcome.
# ----------------------------------------------------------------------------

function Dispatch-HRConnect        { _Dispatch-OneApp -AppKey "HRConnect"        -Request $Request }
function Dispatch-ITHelpdeskPortal { _Dispatch-OneApp -AppKey "ITHelpdeskPortal" -Request $Request }
function Dispatch-FinanceSuite     { _Dispatch-OneApp -AppKey "FinanceSuite"     -Request $Request }

function _Dispatch-OneApp {
    param([string]$AppKey, $Request)

    # Fresh buffer per activity so the per-app throw only contains THIS app's lines.
    $script:DispatchResultLines = New-Object System.Collections.ArrayList

    $newValue = "$($Request.Get("SCIM-$AppKey"))"
    if ([string]::IsNullOrEmpty($newValue)) {
        # IfElse upstream should have gated this; defensive no-op.
        return
    }

    $isProvision = ($newValue -ieq "true")
    $isRemove    = ($newValue -ieq "false")
    if (-not ($isProvision -or $isRemove)) {
        throw "[$AppKey] Unexpected SCIM-$AppKey value '$newValue' (expected 'true' or 'false')."
    }

    $appLabel = switch ($AppKey) {
        "HRConnect"         { "HR Connect" }
        "ITHelpdeskPortal"  { "IT Helpdesk Portal" }
        "FinanceSuite"      { "Finance Suite" }
        default             { $AppKey }
    }

    try {
        if ($isProvision) { _Do-Provision -AppKey $AppKey -Request $Request }
        else              { _Do-Remove    -AppKey $AppKey -Request $Request }
    } catch {
        $err = "$($_.Exception.Message)"
        $revertTo = -not $isProvision
        [void]$script:DispatchResultLines.Add("[ERROR] $appLabel - $err")
        try {
            Set-QADObject $Request.DN -ObjectAttributes @{ "SCIM-$AppKey" = $revertTo } | Out-Null
            [void]$script:DispatchResultLines.Add("[INFO]  $appLabel - SCIM-$AppKey auto-reverted to $revertTo; re-toggle to retry")
        } catch {
            [void]$script:DispatchResultLines.Add("[WARN]  $appLabel - could not auto-revert SCIM-$AppKey ($($_.Exception.Message)); uncheck + re-check manually")
        }
    }

    # Throw the per-app summary so it lands in this activity's Change History entry.
    # The activity has SuppressError=True so the throw does not abort the workflow.
    if ($script:DispatchResultLines.Count -gt 0) {
        throw ($script:DispatchResultLines -join "`r`n")
    }
}

# Legacy bulk dispatcher - kept for back-compat, calls all per-app handlers in
# sequence. Not used by the new workflow XAML.
function Dispatch-AllSCIM {
    if (-not $script:SCIMMappings) {
        throw "SCIMMappings not loaded - the mapping table from UNITE-SCIMMappings must be concatenated into this ScriptModule."
    }

    $anyFailure = $false

    foreach ($app in $script:SCIMMappings.Keys) {
        # $Request.Get returns the new value only when the attribute was modified
        # in this submit. Empty/null otherwise - so untouched apps are skipped.
        $newValue = "$($Request.Get("SCIM-$app"))"
        if ([string]::IsNullOrEmpty($newValue)) { continue }

        $isProvision = ($newValue -ieq "true")
        $isRemove    = ($newValue -ieq "false")
        if (-not ($isProvision -or $isRemove)) {
            Write-Output "[$app] Unexpected SCIM-$app value '$newValue' - skipping (expected 'true' or 'false')"
            continue
        }

        # Wrap each per-app dispatch in try/catch so one app's failure
        # does not block the others. On failure, revert the checkbox to its
        # previous value so the user can retry by re-toggling. Natural
        # idempotency in _Do-Provision / _Do-Remove handles the workflow
        # re-trigger that the revert causes (no infinite loop).
        try {
            if ($isProvision) { _Do-Provision -AppKey $app -Request $Request }
            else              { _Do-Remove    -AppKey $app -Request $Request }
        } catch {
            $anyFailure = $true
            $errMsg = "$($_.Exception.Message)"
            $revertTo = -not $isProvision   # if Provision failed, was false before

            # Buffer the failure line through _Report-style accounting so the
            # final summary includes it. The exception object from _ReportError
            # has already been formatted with [App/Action] - keep it whole.
            $appLabel = switch ($app) {
                "HRConnect"         { "HR Connect" }
                "ITHelpdeskPortal"  { "IT Helpdesk Portal" }
                "FinanceSuite"      { "Finance Suite" }
                default             { $app }
            }
            if (-not $script:DispatchResultLines) { $script:DispatchResultLines = New-Object System.Collections.ArrayList }
            [void]$script:DispatchResultLines.Add("[ERROR] $appLabel - $errMsg")

            # Revert the AD attribute so the user can retry by re-toggling.
            try {
                Set-QADObject $Request.DN -ObjectAttributes @{ "SCIM-$app" = $revertTo } | Out-Null
                [void]$script:DispatchResultLines.Add("[INFO]  $appLabel - SCIM-$app auto-reverted to $revertTo; re-toggle to retry")
            } catch {
                [void]$script:DispatchResultLines.Add("[WARN]  $appLabel - could not auto-revert SCIM-$app ($($_.Exception.Message)); uncheck + re-check manually")
            }
        }
    }

    # Compose the human-readable summary.
    $lines = if ($script:DispatchResultLines) { @($script:DispatchResultLines) } else { @() }
    if ($lines.Count -eq 0) {
        # Workflow fired on a non-SCIM property change (e.g. self-modify from
        # the failure auto-revert). Silent no-op.
        return
    }
    $summary = "SCIM Provisioning Hub - per-app outcomes:`r`n" + ($lines -join "`r`n")

    # Reliability matters more than aesthetics here. ARS 8.2 surfaces THREE
    # things from a PowerShellActivity into Change History:
    #   - thrown exceptions (rendered with the activity)
    #   - the script's return value (rendered as the activity's output but
    #     not always expanded by default)
    #   - explicit AddRecordToReport activities (static text only)
    #
    # The most reliable channel for showing the multi-line summary is a
    # final throw. The PowerShellActivity in the workflow is configured with
    # SuppressError="True" so the throw does not abort the workflow - it
    # only attaches the text to the activity entry in Change History.
    Write-Output $summary
    throw $summary
}

# Legacy per-app entry points kept for back-compat with workflow XAML that
# still wires individual Provision-/Disable- branches. New deployments should
# use the single Dispatch-AllSCIM activity above.
function Provision-HRConnect        { _Do-Provision -AppKey "HRConnect"        -Request $Request }
function Disable-HRConnect          { _Do-Remove    -AppKey "HRConnect"        -Request $Request }
function Provision-ITHelpdeskPortal { _Do-Provision -AppKey "ITHelpdeskPortal" -Request $Request }
function Disable-ITHelpdeskPortal   { _Do-Remove    -AppKey "ITHelpdeskPortal" -Request $Request }
function Provision-FinanceSuite     { _Do-Provision -AppKey "FinanceSuite"     -Request $Request }
function Disable-FinanceSuite       { _Do-Remove    -AppKey "FinanceSuite"     -Request $Request }


# ============================================================================
# CORE DISPATCH
# ============================================================================

function _Do-Provision {
    param([string]$AppKey, $Request)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $ctx  = _Get-SCIMContext -AppKey $AppKey
        $user = _Get-UserAttributes -Request $Request
        if (-not $user.sAMAccountName) {
            _ReportError -AppKey $AppKey -Action "Provision" -Message "Could not read sAMAccountName from $($Request.DN)"
            return
        }

        $sam = "$($user.sAMAccountName)"
        $appUserName = _Get-AppUserName -AppKey $AppKey -User $user
        if (-not $appUserName) {
            $src = _Describe-UserNameSource -AppKey $AppKey
            _ReportError -AppKey $AppKey -Action "Provision" -Message "Cannot determine SCIM userName for $sam - source $src is empty in AD. Populate it and re-toggle."
            return
        }
        $existing = _Find-SCIMUser -Ctx $ctx -UserName $appUserName
        if ($existing) {
            # 'active' missing OR true == already-active. Only explicit false
            # means the record exists but disabled, in which case we reactivate.
            if ($existing.active -ne $false) {
                $sw.Stop()
                _Report -AppKey $AppKey -Action "Provision" -Verb "AlreadyActive" -UserName $appUserName -ScimId $existing.id -ElapsedMs $sw.ElapsedMilliseconds
                return
            }
            $body = _Build-PatchActive -Active $true
            $resp = _Invoke-SCIM -Method "PATCH" -Url ("{0}/{1}" -f $ctx.Uri, $existing.id) -Token $ctx.Token -Body $body
            $sw.Stop()
            _Report -AppKey $AppKey -Action "Provision" -Verb "Reactivated" -UserName $appUserName -ScimId $existing.id -ElapsedMs $sw.ElapsedMilliseconds
        } else {
            $body = _Build-CreateUser -AppKey $AppKey -User $user
            try {
                $resp = _Invoke-SCIM -Method "POST" -Url $ctx.Uri -Token $ctx.Token -Body $body
                $sw.Stop()
                _Report -AppKey $AppKey -Action "Provision" -Verb "Created" -UserName $appUserName -ScimId $resp.id -ElapsedMs $sw.ElapsedMilliseconds
            } catch {
                # 409 Conflict on POST means the server says the user already
                # exists - our initial Find missed it (case sensitivity, soft-
                # delete, partial index, race). Re-find by userName and treat
                # as the AlreadyActive / Reactivated outcome instead of bouncing
                # back through the failure path.
                $isConflict = $false
                $r = $_.Exception.Response
                if ($r -and [int]$r.StatusCode -eq 409) { $isConflict = $true }
                if (-not $isConflict) { throw }

                $retry = _Find-SCIMUser -Ctx $ctx -UserName $appUserName
                if (-not $retry) {
                    # Server says 409 but we still can't find it - genuinely confused.
                    _ReportError -AppKey $AppKey -Action "Provision" -Message "Server reported 409 Conflict but a follow-up Find returned no user. Manual cleanup may be needed."
                    return
                }
                # Treat "active missing or true" as active. Some SCIM servers
                # omit the 'active' property when it equals the default (true).
                # Only an explicit false means we need to reactivate via PATCH.
                if ($retry.active -ne $false) {
                    $sw.Stop()
                    _Report -AppKey $AppKey -Action "Provision" -Verb "AlreadyActive" -UserName $appUserName -ScimId $retry.id -ElapsedMs $sw.ElapsedMilliseconds
                } else {
                    $patchBody = _Build-PatchActive -Active $true
                    [void](_Invoke-SCIM -Method "PATCH" -Url ("{0}/{1}" -f $ctx.Uri, $retry.id) -Token $ctx.Token -Body $patchBody)
                    $sw.Stop()
                    _Report -AppKey $AppKey -Action "Provision" -Verb "Reactivated" -UserName $appUserName -ScimId $retry.id -ElapsedMs $sw.ElapsedMilliseconds
                }
            }
        }
    } catch {
        $sw.Stop()
        _ReportError -AppKey $AppKey -Action "Provision" -Message (_Classify-NoResponse $_)
    }
}

# Dispatch helper: read the per-app OnRemove policy and route to Disable or Delete.
function _Do-Remove {
    param([string]$AppKey, $Request)
    $cfg = Get-SCIMAppConfig -AppKey $AppKey
    $policy = if ($cfg -and $cfg.OnRemove) { $cfg.OnRemove } else { "Disable" }
    if ($policy -ieq "Delete") {
        _Do-Delete  -AppKey $AppKey -Request $Request
    } else {
        _Do-Disable -AppKey $AppKey -Request $Request
    }
}

function _Do-Disable {
    param([string]$AppKey, $Request)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $ctx  = _Get-SCIMContext -AppKey $AppKey
        $user = _Get-UserAttributes -Request $Request
        if (-not $user.sAMAccountName) {
            _ReportError -AppKey $AppKey -Action "Disable" -Message "Could not read sAMAccountName from $($Request.DN)"
            return
        }

        $sam = "$($user.sAMAccountName)"
        $appUserName = _Get-AppUserName -AppKey $AppKey -User $user
        if (-not $appUserName) {
            $src = _Describe-UserNameSource -AppKey $AppKey
            _ReportError -AppKey $AppKey -Action "Disable" -Message "Cannot determine SCIM userName for $sam - source $src is empty in AD."
            return
        }
        $existing = _Find-SCIMUser -Ctx $ctx -UserName $appUserName
        if (-not $existing) {
            $sw.Stop()
            _Report -AppKey $AppKey -Action "Disable" -Verb "SkippedNotPresent" -UserName $appUserName -ElapsedMs $sw.ElapsedMilliseconds
            return
        }
        if ($existing.active -eq $false) {
            $sw.Stop()
            _Report -AppKey $AppKey -Action "Disable" -Verb "AlreadyInactive" -UserName $appUserName -ScimId $existing.id -ElapsedMs $sw.ElapsedMilliseconds
            return
        }

        $body = _Build-PatchActive -Active $false
        $resp = _Invoke-SCIM -Method "PATCH" -Url ("{0}/{1}" -f $ctx.Uri, $existing.id) -Token $ctx.Token -Body $body
        $sw.Stop()
        _Report -AppKey $AppKey -Action "Disable" -Verb "Disabled" -UserName $appUserName -ScimId $existing.id -ElapsedMs $sw.ElapsedMilliseconds
    } catch {
        $sw.Stop()
        _ReportError -AppKey $AppKey -Action "Disable" -Message (_Classify-NoResponse $_)
    }
}

function _Do-Delete {
    param([string]$AppKey, $Request)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $ctx  = _Get-SCIMContext -AppKey $AppKey
        $user = _Get-UserAttributes -Request $Request
        if (-not $user.sAMAccountName) {
            _ReportError -AppKey $AppKey -Action "Delete" -Message "Could not read sAMAccountName from $($Request.DN)"
            return
        }

        $sam = "$($user.sAMAccountName)"
        $appUserName = _Get-AppUserName -AppKey $AppKey -User $user
        if (-not $appUserName) {
            $src = _Describe-UserNameSource -AppKey $AppKey
            _ReportError -AppKey $AppKey -Action "Delete" -Message "Cannot determine SCIM userName for $sam - source $src is empty in AD."
            return
        }
        $existing = _Find-SCIMUser -Ctx $ctx -UserName $appUserName
        if (-not $existing) {
            $sw.Stop()
            _Report -AppKey $AppKey -Action "Delete" -Verb "SkippedNotPresent" -UserName $appUserName -ElapsedMs $sw.ElapsedMilliseconds
            return
        }

        $resp = _Invoke-SCIM -Method "DELETE" -Url ("{0}/{1}" -f $ctx.Uri, $existing.id) -Token $ctx.Token -Body $null
        $sw.Stop()
        _Report -AppKey $AppKey -Action "Delete" -Verb "Deleted" -UserName $appUserName -ScimId $existing.id -ElapsedMs $sw.ElapsedMilliseconds
    } catch {
        $sw.Stop()
        _ReportError -AppKey $AppKey -Action "Delete" -Message (_Classify-NoResponse $_)
    }
}


# ============================================================================
# MAPPING ENGINE - reads $script:SCIMMappings from UNITE-SCIMMappings
# ============================================================================

# Resolves the SCIM userName for this app by running the mapping's "userName"
# entry through _Resolve-MappingValue. Returns $null if the source AD attribute
# is empty - which is a hard-stop for both Find (no key to search on) and
# Provision (server will 400 with "userName is required").
function _Get-AppUserName {
    param([string]$AppKey, $User)
    $mapping = Get-SCIMMapping -AppKey $AppKey
    if (-not $mapping.ContainsKey("userName")) { return $null }
    $val = _Resolve-MappingValue -Source $mapping["userName"] -User $User
    if ($null -eq $val -or $val -eq "") { return $null }
    return "$val"
}

# Human-readable description of where the app's userName comes from, for error
# messages. "=literal" -> the literal; "{ ... }" scriptblock -> "<computed>";
# plain string -> the AD attribute name.
function _Describe-UserNameSource {
    param([string]$AppKey)
    $mapping = Get-SCIMMapping -AppKey $AppKey
    if (-not $mapping.ContainsKey("userName")) { return "<unmapped>" }
    $src = $mapping["userName"]
    if ($src -is [scriptblock]) { return "<computed>" }
    if ($src -is [string]) {
        if ($src.StartsWith("=")) { return "literal '$($src.Substring(1))'" }
        return "AD attribute '$src'"
    }
    return "$src"
}

function _Build-CreateUser {
    param([string]$AppKey, $User)

    $mapping = Get-SCIMMapping -AppKey $AppKey
    $payload = [ordered]@{
        schemas = @("urn:ietf:params:scim:schemas:core:2.0:User")
    }
    $hasEnterprise = $false

    foreach ($scimPath in $mapping.Keys) {
        $source = $mapping[$scimPath]
        $value  = _Resolve-MappingValue -Source $source -User $User
        if ($null -eq $value -or $value -eq "") { continue }

        # Normalize booleans coming through as strings (e.g. "=true")
        if ($value -is [string] -and ($value -eq "true" -or $value -eq "false")) {
            $value = [bool]::Parse($value)
        }

        if ($scimPath -like "enterprise.*") { $hasEnterprise = $true }
        _Set-SCIMPath -Payload $payload -Path $scimPath -Value $value
    }

    if ($hasEnterprise) {
        $payload.schemas += "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User"
    }
    return $payload
}

function _Resolve-MappingValue {
    param($Source, $User)

    $raw = $null
    if ($Source -is [scriptblock]) {
        try { $raw = (& $Source $User) } catch {
            # Don't swallow silently - a future scriptblock for a required
            # field (e.g. userName) failing this way would surface only as
            # "source <computed> is empty in AD" with no hint of the real
            # error. Write to the script trace so the operator can see it
            # without breaking the dispatch.
            Write-Output "[WARN] scriptblock mapping threw: $($_.Exception.Message)"
            return $null
        }
    }
    elseif ($Source -is [string]) {
        if ($Source.StartsWith("=")) { return $Source.Substring(1) }
        $raw = $User.$Source
    }
    else { $raw = $Source }

    # Quest AD cmdlets return ETS-decorated objects (e.g. ResultPropertyValueCollection)
    # rather than plain strings. ConvertTo-Json serializes those as nested objects
    # ({"Length":5,"name":"Jacob"}) which the SCIM server rejects with a 500.
    # Coerce to a clean string here so downstream JSON is always primitive-shaped.
    if ($null -eq $raw) { return $null }
    if ($raw -is [string]) { return $raw }
    if ($raw -is [bool])   { return $raw }
    if ($raw -is [System.Collections.IEnumerable] -and -not ($raw -is [string])) {
        # Multi-valued AD attr - take the first element and string-coerce.
        foreach ($v in $raw) { return "$v" }
        return $null
    }
    return "$raw"
}

# Walks a path like "name.givenName", "emails[0].value", or "enterprise.department"
# and writes the value into the payload hashtable, creating intermediate
# nodes as needed.
function _Set-SCIMPath {
    param($Payload, [string]$Path, $Value)

    $enterpriseUrn = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User"

    # Re-key "enterprise.*" to the urn-prefixed extension key.
    if ($Path -like "enterprise.*") {
        $rest = $Path.Substring("enterprise.".Length)
        if (-not $Payload.Contains($enterpriseUrn)) {
            $Payload[$enterpriseUrn] = [ordered]@{}
        }
        _Set-NestedPath -Node $Payload[$enterpriseUrn] -Segments (_Tokenize-Path $rest) -Value $Value
        return
    }

    _Set-NestedPath -Node $Payload -Segments (_Tokenize-Path $Path) -Value $Value
}

# Tokenize "emails[0].value" -> @("emails", "[0]", "value")
function _Tokenize-Path {
    param([string]$Path)
    $tokens = @()
    foreach ($part in $Path.Split('.')) {
        $m = [regex]::Match($part, '^([^\[]+)(\[(\d+)\])?$')
        if ($m.Success) {
            $tokens += $m.Groups[1].Value
            if ($m.Groups[2].Success) { $tokens += "[$($m.Groups[3].Value)]" }
        } else {
            $tokens += $part
        }
    }
    return $tokens
}

function _Set-NestedPath {
    param($Node, [object[]]$Segments, $Value)

    for ($i = 0; $i -lt $Segments.Count; $i++) {
        $seg     = $Segments[$i]
        $isLast  = ($i -eq $Segments.Count - 1)
        $isIndex = $seg -match '^\[(\d+)\]$'

        if ($isIndex) {
            $idx = [int]$Matches[1]
            # $Node is expected to be a System.Collections.ArrayList from previous step
            while ($Node.Count -le $idx) { [void]$Node.Add([ordered]@{}) }
            if ($isLast) { $Node[$idx] = $Value } else { $Node = $Node[$idx] }
            continue
        }

        # Look ahead: is the next segment an array index?
        $nextIsIndex = (-not $isLast) -and ($Segments[$i+1] -match '^\[\d+\]$')

        if ($isLast) {
            $Node[$seg] = $Value
        } else {
            if (-not $Node.Contains($seg)) {
                if ($nextIsIndex) {
                    $Node[$seg] = New-Object System.Collections.ArrayList
                } else {
                    $Node[$seg] = [ordered]@{}
                }
            }
            $Node = $Node[$seg]
        }
    }
}

function _Build-PatchActive {
    param([bool]$Active)
    return [ordered]@{
        schemas    = @("urn:ietf:params:scim:api:messages:2.0:PatchOp")
        Operations = @(
            [ordered]@{ op = "replace"; path = "active"; value = $Active }
        )
    }
}


# ============================================================================
# AD READ + WORKFLOW CONFIG
# ============================================================================

function _Get-UserAttributes {
    param($Request)
    $u = Get-QADUser $Request.DN -DontUseDefaultIncludedProperties `
        -IncludedProperties sAMAccountName,givenName,sn,mail,displayName,department,title,employeeID,
                             telephoneNumber,mobile,company,manager,extensionAttribute1,extensionAttribute2,
                             extensionAttribute3,extensionAttribute4,extensionAttribute5
    if (-not $u) { throw "Get-QADUser returned nothing for $($Request.DN)" }
    return $u
}

function _Get-SCIMContext {
    param([string]$AppKey)

    $uriParam   = "SCIM-$AppKey-URI"
    $tokenParam = "SCIM-$AppKey-Token"

    $uri   = $Workflow.Parameter($uriParam)
    $token = $Workflow.Parameter($tokenParam)

    # Trim BOTH ends - MMC sometimes saves values with a leading space.
    if ($uri)   { $uri   = $uri.Trim() }
    if ($token) { $token = $token.Trim() }

    if (-not $uri)   { throw "Workflow parameter '$uriParam' is empty. Open the workflow's Parameters dialog in MMC and set it." }
    if (-not $token) { throw "Workflow parameter '$tokenParam' is empty. Open the workflow's Parameters dialog in MMC, click in the value field, paste the FULL bearer token (exactly as your SCIM provider gave you - Conduit mints with a 'scim_' prefix, Okta/Auth0/etc. don't), and OK. If you see ******** but get this error, the previous save did not persist - re-enter it." }

    # NOTE: the engine deliberately does NOT munge the token. Whatever the
    # admin pasted is what gets sent verbatim after 'Bearer '. Conduit
    # happens to mint tokens with a 'scim_' prefix; other SCIM providers
    # don't. The token format is the SCIM provider's contract, not ours.

    # Tolerate trailing /Users - script appends it itself.
    $uri = $uri.TrimEnd('/')
    if ($uri.EndsWith("/Users")) { $uri = $uri.Substring(0, $uri.Length - "/Users".Length) }

    # Warn (don't block) when the URI is plain HTTP. The cert-trust callback
    # at the top of this script makes http silently work, which is convenient
    # for the lab but dangerous in production - tokens travel in cleartext.
    if ($uri -notmatch '^https://') {
        Write-Output "[WARN] $AppKey URI is not HTTPS ('$uri') - bearer token will travel in cleartext. Use only for lab/demo."
    }

    return @{
        AppKey = $AppKey
        Uri    = "$uri/Users"
        Token  = $token
        DryRun = ($Workflow.Parameter("SCIM-DryRun") -eq "true")
    }
}


# ============================================================================
# SCIM HTTP
# ============================================================================

function _Find-SCIMUser {
    param($Ctx, [string]$UserName)
    if ($Ctx.DryRun) { return $null }

    # SCIM filter strings escape backslash and double-quote per RFC 7644 sec 3.4.2.2.
    # Without this, a userName containing " or \ produces a syntactically invalid
    # filter (e.g. `userName eq "foo"bar"`) - URL-encoding can't fix bad SCIM syntax.
    # NB: in PowerShell -replace, the replacement string is .NET regex
    # replacement syntax where backslash is NOT special; only '$' is. So we
    # write '\\' (2 chars) to emit two backslashes, and '\"' (2 chars) to emit
    # backslash + quote.
    $escaped = $UserName -replace '\\','\\' -replace '"','\"'
    $filter  = [System.Web.HttpUtility]::UrlEncode(("userName eq `"{0}`"" -f $escaped))
    $url     = "$($Ctx.Uri)?filter=$filter"
    $resp   = _Invoke-SCIM -Method "GET" -Url $url -Token $Ctx.Token -Body $null
    if ($resp.Resources -and $resp.Resources.Count -gt 0) { return $resp.Resources[0] }
    return $null
}

function _Invoke-SCIM {
    param([string]$Method, [string]$Url, [string]$Token, $Body)

    $headers = @{
        "Authorization" = "Bearer $Token"
        "Accept"        = "application/scim+json"
    }
    $params = @{
        Method  = $Method
        Uri     = $Url
        Headers = $headers
        UseBasicParsing = $true
        ErrorAction = "Stop"
    }
    if ($Body) {
        $params["ContentType"] = "application/scim+json"
        $params["Body"]        = ($Body | ConvertTo-Json -Depth 10 -Compress)
    }

    $raw = Invoke-WebRequest @params

    # In PowerShell 5.1 (ARS host), Invoke-WebRequest returns $raw.Content as
    # byte[] for any Content-Type that is not "text/*". SCIM 2.0 mandates
    # "application/scim+json", which means Content arrives as bytes and piping
    # it into ConvertFrom-Json silently fails (no exception, no warning - you
    # just get $null back). That is the root cause of Find-misses on records
    # that DO exist server-side. Decode bytes -> UTF-8 string explicitly so
    # parsing is reliable across PS 5.1, PS 7, and every SCIM 2.0 vendor.
    $body = $raw.Content
    if ($body -is [byte[]]) {
        if ($body.Length -eq 0) { return $null }
        $body = [System.Text.Encoding]::UTF8.GetString($body)
    }
    if ([string]::IsNullOrWhiteSpace($body)) { return $null }
    return ($body | ConvertFrom-Json)
}


# ============================================================================
# REPORTING / ERROR CLASSIFICATION
# ============================================================================

function _Report {
    param(
        [string]$AppKey,
        [string]$Action,
        [string]$Verb,           # Created, Reactivated, AlreadyActive, Disabled, AlreadyInactive, Deleted, SkippedNotPresent
        [string]$UserName,
        [string]$ScimId = $null,
        [string]$Note   = $null,
        [long]  $ElapsedMs = 0
    )
    $appLabel = switch ($AppKey) {
        "HRConnect"         { "HR Connect" }
        "ITHelpdeskPortal"  { "IT Helpdesk Portal" }
        "FinanceSuite"      { "Finance Suite" }
        default             { $AppKey }
    }
    # Consistent shape: "[ICON]  AppLabel - action for user, context"
    $line = switch ($Verb) {
        "Created"           { "[OK]    $appLabel - new account provisioned for $UserName, AD identity linked" }
        "Reactivated"       { "[OK]    $appLabel - existing account reactivated for $UserName, was disabled" }
        "AlreadyActive"     { "[INFO]  $appLabel - account already exists for $UserName, linked to AD identity - no change needed" }
        "Disabled"          { "[OK]    $appLabel - account disabled for $UserName, assignment removed (record retained)" }
        "AlreadyInactive"   { "[INFO]  $appLabel - account already disabled for $UserName - no change needed" }
        "Deleted"           { "[OK]    $appLabel - account deleted for $UserName per tenant policy" }
        "SkippedNotPresent" { "[INFO]  $appLabel - no account found for $UserName, nothing to disable - assignment marker cleared" }
        default             { "[OK]    $appLabel - $Verb for $UserName" }
    }
    if ($ScimId)          { $line += "  (scimId=$ScimId)" }
    if ($Note)            { $line += "  ($Note)" }
    if ($ElapsedMs -gt 0) { $line += "  [${ElapsedMs}ms]" }

    # Successes and idempotent no-ops (AlreadyActive / SkippedNotPresent /
    # AlreadyInactive) go to the script trace - visible when you click into
    # the activity in Change History, but NOT thrown. ARS only renders thrown
    # text in the main Change History row, so throwing here would mark every
    # happy-path dispatch as "Activity encountered an error". Only genuine
    # failures (network/auth/5xx) should land in $script:DispatchResultLines
    # and produce a throw - that work happens in _ReportError + the catch
    # block in _Dispatch-OneApp.
    Write-Output $line
}

function _ReportError {
    param([string]$AppKey, [string]$Action, [string]$Message)
    $appLabel = switch ($AppKey) {
        "HRConnect"         { "HR Connect" }
        "ITHelpdeskPortal"  { "IT Helpdesk Portal" }
        "FinanceSuite"      { "Finance Suite" }
        default             { $AppKey }
    }
    # throw is the ONLY thing ARS surfaces as an error in Change History.
    throw "[$appLabel/$Action] $Message"
}

function _Classify-NoResponse {
    param($Err)
    $msg = if ($Err.Exception.Message) { $Err.Exception.Message } else { "$Err" }
    $resp = $Err.Exception.Response

    # Capture the SCIM error response body when there is one - its 'detail'
    # field names the real failure (e.g. "NullReferenceException: ...").
    $body = ""
    if ($resp) {
        try {
            $sr = New-Object System.IO.StreamReader($resp.GetResponseStream())
            $body = $sr.ReadToEnd(); $sr.Close()
            if ($body.Length -gt 400) { $body = $body.Substring(0, 400) + "..." }
        } catch { }
    }
    $bodyTail = if ($body) { " - server said: $body" } else { "" }

    if ($resp) {
        switch ([int]$resp.StatusCode) {
            401 { return "401 Unauthorized - token rejected. Paste the FULL 'scim_<value>' mint string into the workflow token parameter.$bodyTail" }
            403 { return "403 Forbidden - token is valid but scoped to a different Connected System.$bodyTail" }
            404 { return "404 Not Found - the URI's tenant slug does not match any Connected System.$bodyTail" }
            409 { return "409 Conflict - user already exists; benign race between Find and Create.$bodyTail" }
            429 { return "429 Rate limited - back off and retry.$bodyTail" }
            default {
                $code = [int]$resp.StatusCode
                if ($code -ge 500) { return "$code Server error.$bodyTail" }
                return "$code $msg.$bodyTail"
            }
        }
    }
    if ($msg -match "actively refused|No connection could be made") { return "Connection refused - is Conduit running on the configured host:port?" }
    if ($msg -match "remote name could not be resolved")             { return "DNS failure - check the host in the URI" }
    if ($msg -match "timed out")                                     { return "Timeout - Conduit did not respond" }
    if ($msg -match "SSL|TLS|certificate")                           { return "TLS error - self-signed cert; enable the TrustAllCertsPolicy block at top of script for lab use" }
    return $msg
}
