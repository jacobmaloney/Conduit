namespace Conduit.Emulator.GoogleWorkspace.Repositories;

// Flat row shape matching gw_users. JSON bags stay strings until the repo hydrates GwUser.
public sealed class GwUserRow
{
    public string Id { get; set; } = string.Empty;
    public string CustomerId { get; set; } = string.Empty;
    public string PrimaryEmail { get; set; } = string.Empty;
    public string GivenName { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string OrgUnitPath { get; set; } = "/";
    public bool Suspended { get; set; }
    public string? SuspensionReason { get; set; }
    public bool Archived { get; set; }
    public bool IsAdmin { get; set; }
    public bool IsDelegatedAdmin { get; set; }
    public bool AgreedToTerms { get; set; } = true;
    public bool ChangePasswordAtNextLogin { get; set; }
    public bool IpWhitelisted { get; set; }
    public bool IsMailboxSetup { get; set; } = true;
    public bool IncludeInGlobalAddressList { get; set; } = true;
    public string? HashedPassword { get; set; }
    public string? RecoveryEmail { get; set; }
    public string? RecoveryPhone { get; set; }
    public string? ThumbnailPhotoUrl { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime? LastLoginTime { get; set; }
    public DateTime? DeletionTime { get; set; }
    public string Etag { get; set; } = string.Empty;
    public string? Emails_JSON { get; set; }
    public string? Phones_JSON { get; set; }
    public string? Addresses_JSON { get; set; }
    public string? Organizations_JSON { get; set; }
    public string? Relations_JSON { get; set; }
    public string? Websites_JSON { get; set; }
    public string? Languages_JSON { get; set; }
    public string? CustomSchemas_JSON { get; set; }
}
