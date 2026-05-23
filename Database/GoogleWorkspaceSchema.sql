/*
 * Conduit.Emulator.GoogleWorkspace — Schema
 *
 * Google Admin SDK Directory API v1 emulator. All tables share the Conduit
 * database and are prefixed `gw_` to keep clear separation from SCIM tables.
 * Idempotent: safe to run repeatedly.
 */

-- =============================================
-- gw_customers (tenant root)
-- =============================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'gw_customers')
BEGIN
    CREATE TABLE [dbo].[gw_customers] (
        [CustomerId]         VARCHAR(32)  NOT NULL PRIMARY KEY,     -- e.g. C00acme01
        [CustomerDomain]     VARCHAR(255) NOT NULL,
        [AlternateEmail]     VARCHAR(255) NULL,
        [PhoneNumber]        VARCHAR(64)  NULL,
        [Language]           VARCHAR(16)  NOT NULL DEFAULT 'en',
        [PostalAddress_JSON] NVARCHAR(MAX) NULL,
        [Etag]               VARCHAR(64)  NOT NULL,
        [CreationTime]       DATETIME2    NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

-- =============================================
-- gw_domains
-- =============================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'gw_domains')
BEGIN
    CREATE TABLE [dbo].[gw_domains] (
        [CustomerId]   VARCHAR(32)  NOT NULL,
        [DomainName]   VARCHAR(255) NOT NULL,
        [IsPrimary]    BIT          NOT NULL DEFAULT 0,
        [Verified]     BIT          NOT NULL DEFAULT 1,
        [Etag]         VARCHAR(64)  NOT NULL,
        [CreationTime] DATETIME2    NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_gw_domains PRIMARY KEY ([CustomerId], [DomainName])
    );
END
GO

-- =============================================
-- gw_orgunits
-- =============================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'gw_orgunits')
BEGIN
    CREATE TABLE [dbo].[gw_orgunits] (
        [OrgUnitId]         VARCHAR(64)   NOT NULL PRIMARY KEY,     -- id:xxxxxxxx
        [CustomerId]        VARCHAR(32)   NOT NULL,
        [OrgUnitPath]       VARCHAR(1024) NOT NULL,                 -- /Engineering/Platform
        [ParentOrgUnitPath] VARCHAR(1024) NULL,
        [ParentOrgUnitId]   VARCHAR(64)   NULL,
        [Name]              NVARCHAR(255) NOT NULL,
        [Description]       NVARCHAR(MAX) NULL,
        [BlockInheritance]  BIT           NOT NULL DEFAULT 0,
        [Etag]              VARCHAR(64)   NOT NULL
    );
    CREATE UNIQUE INDEX UX_gw_orgunits_Path ON [dbo].[gw_orgunits]([CustomerId], [OrgUnitPath]);
END
GO

-- =============================================
-- gw_users
-- =============================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'gw_users')
BEGIN
    CREATE TABLE [dbo].[gw_users] (
        [Id]                       VARCHAR(32)   NOT NULL PRIMARY KEY,     -- 21-digit numeric string
        [CustomerId]               VARCHAR(32)   NOT NULL,
        [PrimaryEmail]             VARCHAR(255)  NOT NULL,
        [GivenName]                NVARCHAR(128) NOT NULL,
        [FamilyName]               NVARCHAR(128) NOT NULL,
        [FullName]                 NVARCHAR(256) NOT NULL,
        [OrgUnitPath]              VARCHAR(1024) NOT NULL DEFAULT '/',
        [Suspended]                BIT           NOT NULL DEFAULT 0,
        [SuspensionReason]         NVARCHAR(512) NULL,
        [Archived]                 BIT           NOT NULL DEFAULT 0,
        [IsAdmin]                  BIT           NOT NULL DEFAULT 0,
        [IsDelegatedAdmin]         BIT           NOT NULL DEFAULT 0,
        [AgreedToTerms]            BIT           NOT NULL DEFAULT 1,
        [ChangePasswordAtNextLogin] BIT          NOT NULL DEFAULT 0,
        [IpWhitelisted]            BIT           NOT NULL DEFAULT 0,
        [IsMailboxSetup]           BIT           NOT NULL DEFAULT 1,
        [IncludeInGlobalAddressList] BIT         NOT NULL DEFAULT 1,
        [HashedPassword]           VARCHAR(512)  NULL,
        [RecoveryEmail]            VARCHAR(255)  NULL,
        [RecoveryPhone]            VARCHAR(64)   NULL,
        [ThumbnailPhotoUrl]        VARCHAR(1024) NULL,
        [CreationTime]             DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        [LastLoginTime]            DATETIME2     NULL,
        [DeletionTime]             DATETIME2     NULL,
        [Etag]                     VARCHAR(64)   NOT NULL,
        -- Variable JSON bags
        [Emails_JSON]              NVARCHAR(MAX) NULL,
        [Phones_JSON]              NVARCHAR(MAX) NULL,
        [Addresses_JSON]           NVARCHAR(MAX) NULL,
        [Organizations_JSON]       NVARCHAR(MAX) NULL,
        [Relations_JSON]           NVARCHAR(MAX) NULL,
        [Websites_JSON]            NVARCHAR(MAX) NULL,
        [Languages_JSON]           NVARCHAR(MAX) NULL,
        [CustomSchemas_JSON]       NVARCHAR(MAX) NULL
    );
    CREATE UNIQUE INDEX UX_gw_users_PrimaryEmail ON [dbo].[gw_users]([PrimaryEmail]) WHERE [DeletionTime] IS NULL;
    CREATE INDEX IX_gw_users_OrgUnitPath ON [dbo].[gw_users]([OrgUnitPath]);
    CREATE INDEX IX_gw_users_Suspended ON [dbo].[gw_users]([Suspended]);
    CREATE INDEX IX_gw_users_FamilyName ON [dbo].[gw_users]([FamilyName]);
END
GO

-- =============================================
-- gw_groups
-- =============================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'gw_groups')
BEGIN
    CREATE TABLE [dbo].[gw_groups] (
        [Id]                 VARCHAR(32)   NOT NULL PRIMARY KEY,
        [CustomerId]         VARCHAR(32)   NOT NULL,
        [Email]              VARCHAR(255)  NOT NULL,
        [Name]               NVARCHAR(256) NOT NULL,
        [Description]        NVARCHAR(MAX) NULL,
        [DirectMembersCount] INT           NOT NULL DEFAULT 0,
        [AdminCreated]       BIT           NOT NULL DEFAULT 1,
        [Etag]               VARCHAR(64)   NOT NULL,
        [CreationTime]       DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
    );
    CREATE UNIQUE INDEX UX_gw_groups_Email ON [dbo].[gw_groups]([Email]);
END
GO

-- =============================================
-- gw_members
-- =============================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'gw_members')
BEGIN
    CREATE TABLE [dbo].[gw_members] (
        [GroupId]  VARCHAR(32)  NOT NULL,
        [MemberId] VARCHAR(32)  NOT NULL,
        [Email]    VARCHAR(255) NOT NULL,
        [Role]     VARCHAR(16)  NOT NULL DEFAULT 'MEMBER',   -- OWNER | MANAGER | MEMBER
        [Type]     VARCHAR(16)  NOT NULL DEFAULT 'USER',     -- USER | GROUP | EXTERNAL | CUSTOMER
        [Status]   VARCHAR(16)  NOT NULL DEFAULT 'ACTIVE',
        [DeliverySettings] VARCHAR(16) NOT NULL DEFAULT 'ALL_MAIL',
        [Etag]     VARCHAR(64)  NOT NULL,
        CONSTRAINT PK_gw_members PRIMARY KEY ([GroupId], [MemberId])
    );
    CREATE INDEX IX_gw_members_MemberId ON [dbo].[gw_members]([MemberId]);
END
GO

-- =============================================
-- gw_aliases
-- =============================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'gw_aliases')
BEGIN
    CREATE TABLE [dbo].[gw_aliases] (
        [Alias]        VARCHAR(255) NOT NULL PRIMARY KEY,
        [TargetId]     VARCHAR(32)  NOT NULL,
        [TargetKind]   VARCHAR(16)  NOT NULL,      -- user | group
        [PrimaryEmail] VARCHAR(255) NOT NULL,
        [Editable]     BIT          NOT NULL DEFAULT 1,
        [Etag]         VARCHAR(64)  NOT NULL
    );
    CREATE INDEX IX_gw_aliases_TargetId ON [dbo].[gw_aliases]([TargetId]);
END
GO

-- =============================================
-- gw_roles
-- =============================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'gw_roles')
BEGIN
    CREATE TABLE [dbo].[gw_roles] (
        [RoleId]           BIGINT        NOT NULL PRIMARY KEY,
        [CustomerId]       VARCHAR(32)   NOT NULL,
        [RoleName]         NVARCHAR(255) NOT NULL,
        [RoleDescription]  NVARCHAR(MAX) NULL,
        [IsSystemRole]     BIT           NOT NULL DEFAULT 0,
        [IsSuperAdminRole] BIT           NOT NULL DEFAULT 0,
        [Privileges_JSON]  NVARCHAR(MAX) NULL,
        [Etag]             VARCHAR(64)   NOT NULL
    );
END
GO

-- =============================================
-- gw_role_assignments
-- =============================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'gw_role_assignments')
BEGIN
    CREATE TABLE [dbo].[gw_role_assignments] (
        [RoleAssignmentId] BIGINT       NOT NULL PRIMARY KEY,
        [CustomerId]       VARCHAR(32)  NOT NULL,
        [RoleId]           BIGINT       NOT NULL,
        [AssignedToId]     VARCHAR(32)  NOT NULL,    -- user or group id
        [ScopeType]        VARCHAR(16)  NOT NULL,    -- CUSTOMER | ORG_UNIT
        [OrgUnitId]        VARCHAR(64)  NULL,
        [Etag]             VARCHAR(64)  NOT NULL
    );
END
GO

-- =============================================
-- gw_schemas (custom user schemas)
-- =============================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'gw_schemas')
BEGIN
    CREATE TABLE [dbo].[gw_schemas] (
        [SchemaId]    VARCHAR(64)   NOT NULL PRIMARY KEY,
        [CustomerId]  VARCHAR(32)   NOT NULL,
        [SchemaName]  VARCHAR(255)  NOT NULL,
        [DisplayName] NVARCHAR(256) NOT NULL,
        [Fields_JSON] NVARCHAR(MAX) NOT NULL,
        [Etag]        VARCHAR(64)   NOT NULL
    );
END
GO

-- =============================================
-- gw_service_accounts (realistic OAuth2 JWT-bearer)
-- =============================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'gw_service_accounts')
BEGIN
    CREATE TABLE [dbo].[gw_service_accounts] (
        [ClientEmail]   VARCHAR(255)  NOT NULL PRIMARY KEY,   -- sa@project.iam.gserviceaccount.com
        [ClientId]      VARCHAR(64)   NOT NULL,
        [PrivateKeyId]  VARCHAR(64)   NOT NULL,
        [PublicKeyPem]  NVARCHAR(MAX) NOT NULL,
        [PrivateKeyPem] NVARCHAR(MAX) NOT NULL,                -- so emulator can hand out the .json key
        [ProjectId]     VARCHAR(128)  NOT NULL,
        [AllowedScopes] NVARCHAR(MAX) NOT NULL,                -- space-delimited
        [Disabled]      BIT           NOT NULL DEFAULT 0,
        [CreatedAt]     DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

-- =============================================
-- gw_access_tokens (issued by /oauth2/v4/token)
-- =============================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'gw_access_tokens')
BEGIN
    CREATE TABLE [dbo].[gw_access_tokens] (
        [Token]       VARCHAR(512)  NOT NULL PRIMARY KEY,
        [ClientEmail] VARCHAR(255)  NOT NULL,
        [Subject]     VARCHAR(255)  NULL,                      -- DWD sub claim
        [Scopes]      NVARCHAR(MAX) NOT NULL,
        [IssuedAt]    DATETIME2     NOT NULL,
        [ExpiresAt]   DATETIME2     NOT NULL
    );
    CREATE INDEX IX_gw_access_tokens_Expiry ON [dbo].[gw_access_tokens]([ExpiresAt]);
END
GO

PRINT 'Google Workspace emulator schema ready.';
GO
