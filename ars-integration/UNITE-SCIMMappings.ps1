# ============================================================================
# UNITE-SCIMMappings - Attribute mapping table for the UNITE Provisioning Hub
# ----------------------------------------------------------------------------
# This is the ONLY file you edit to add a new SCIM target. The universal SCIM
# engine in UNITE-SCIMRest reads this table at runtime.
#
# Adding a new app:
#   1) Add an entry to $script:SCIMMappings keyed by your AppKey (no spaces,
#      e.g. "JiraCloud", "Workday", "SnowflakeAdmin")
#   2) Map SCIM payload paths to AD attribute names (or literals, or computed
#      values - see README.md for full syntax)
#   3) Register the matching workflow parameters (SCIM-<AppKey>-URI,
#      SCIM-<AppKey>-Token) on the UNITE Provisioning Hub workflow
#   4) Add the per-app function (Provision-<AppKey> / Disable-<AppKey>) and
#      wire it into the workflow's IfElse branches
#
# Brought to you by iC Consult • Identity & Access Management specialists
# We build IAM solutions that don't break.  sales@ic-consult.com
# ============================================================================

# ---- Mapping value types ---------------------------------------------------
#  "sAMAccountName"            Plain string  -> read the named AD attribute
#  "=work"                     '=' prefix    -> emit this literal value
#  { (Get-QADUser $u.Manager).mail }   ScriptBlock -> compute at runtime;
#                                                     $u = AD user object
#
# ---- Path syntax (left-hand side) ------------------------------------------
#  userName                            top-level scalar
#  displayName                         top-level scalar
#  active                              top-level scalar (boolean)
#  name.givenName                      nested object property
#  name.familyName                     nested object property
#  emails[0].value                     first element of multi-valued array
#  emails[0].type                      "
#  emails[0].primary                   "
#  phoneNumbers[0].value               "
#  phoneNumbers[0].type                "
#  enterprise.department               enterprise schema extension
#  enterprise.employeeNumber           "
#  enterprise.costCenter               "
#  enterprise.division                 "
#  enterprise.organization             "
#  enterprise.manager.value            manager reference (string id)
# ============================================================================

$script:SCIMMappings = @{

    # ------------------------------------------------------------------------
    # HR Connect - minimal core schema, just identity + email
    # ------------------------------------------------------------------------
    "HRConnect" = @{
        "userName"          = "sAMAccountName"
        "name.givenName"    = "givenName"
        "name.familyName"   = "sn"
        "displayName"       = "displayName"
        "emails[0].value"   = "mail"
        "emails[0].type"    = "=work"
        "emails[0].primary" = "=true"
        "active"            = "=true"
    }

    # ------------------------------------------------------------------------
    # IT Helpdesk Portal - adds department + title for ticket routing
    # ------------------------------------------------------------------------
    "ITHelpdeskPortal" = @{
        "userName"                  = "sAMAccountName"
        "name.givenName"            = "givenName"
        "name.familyName"           = "sn"
        "displayName"               = "displayName"
        "emails[0].value"           = "mail"
        "emails[0].type"            = "=work"
        "emails[0].primary"         = "=true"
        "phoneNumbers[0].value"     = "telephoneNumber"
        "phoneNumbers[0].type"      = "=work"
        "enterprise.department"     = "department"
        "enterprise.employeeNumber" = "employeeID"
        "active"                    = "=true"
    }

    # ------------------------------------------------------------------------
    # Finance Suite - keys on email (not sam), needs cost center + manager
    # ------------------------------------------------------------------------
    "FinanceSuite" = @{
        "userName"                = "mail"              # Finance keys on email
        "name.givenName"          = "givenName"
        "name.familyName"         = "sn"
        "displayName"             = "displayName"
        "emails[0].value"         = "mail"
        "emails[0].type"          = "=work"
        "emails[0].primary"       = "=true"
        "enterprise.department"   = "department"
        "enterprise.costCenter"   = "extensionAttribute3"
        "enterprise.organization" = "company"
        # NOTE: enterprise.manager.value in SCIM 2.0 is a REFERENCE to another
        # SCIM user (by their SCIM id, a GUID) - not an email or DN. To wire
        # this correctly, the mapping would need to call Conduit first to
        # look up the manager's SCIM id (or accept missing manager linkage on
        # first-time provisioning and reconcile later). For the talk demo we
        # surface the manager's display name instead, which is informational
        # and harmless on the server side.
        "enterprise.manager.displayName" = {
            param($u)
            if ($u.manager) {
                try { (Get-QADUser $u.manager -DontUseDefaultIncludedProperties -IncludedProperties displayName).displayName }
                catch { $null }
            }
        }
        "active" = "=true"
    }

    # ------------------------------------------------------------------------
    # Example: add your next app here. Uncomment + customize.
    # ------------------------------------------------------------------------
    # "JiraCloud" = @{
    #     "userName"        = "mail"
    #     "name.givenName"  = "givenName"
    #     "name.familyName" = "sn"
    #     "displayName"     = "displayName"
    #     "emails[0].value" = "mail"
    #     "emails[0].type"  = "=work"
    #     "active"          = "=true"
    # }
}

# ============================================================================
# Per-app behavior config. Separate from the mapping table because this is
# about LIFECYCLE policy, not field shape.
#
# Recognized keys:
#   OnRemove = "Disable" (default) | "Delete"
#     What to do when the SCIM-<App> virtual attribute flips to false.
#     "Disable" -> PATCH active=false (row stays, marked inactive)
#     "Delete"  -> HTTP DELETE on the SCIM user (row removed)
#
# Defaults: any app not listed here gets OnRemove="Disable".
# ============================================================================

$script:SCIMAppConfig = @{
    "HRConnect"        = @{ OnRemove = "Disable" }
    "ITHelpdeskPortal" = @{ OnRemove = "Disable" }
    "FinanceSuite"     = @{ OnRemove = "Delete"  }   # per-seat pricing - actually remove on offboard
}

# Convenience accessors for the engine.
function Get-SCIMMapping {
    param([Parameter(Mandatory=$true)][string]$AppKey)
    if (-not $script:SCIMMappings.ContainsKey($AppKey)) {
        throw "No SCIM mapping defined for AppKey '$AppKey'. Add an entry to `$script:SCIMMappings in UNITE-SCIMMappings."
    }
    return $script:SCIMMappings[$AppKey]
}

function Get-SCIMAppConfig {
    param([Parameter(Mandatory=$true)][string]$AppKey)
    if ($script:SCIMAppConfig -and $script:SCIMAppConfig.ContainsKey($AppKey)) {
        return $script:SCIMAppConfig[$AppKey]
    }
    return @{}   # empty -> all defaults apply
}
