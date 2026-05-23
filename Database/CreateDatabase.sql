/*
 * Conduit Database Schema (canonical bootstrap)
 *
 * This is the single source of truth for the initial database schema. It is
 * embedded into Conduit.DataAccess.dll and executed by DatabaseInitializer
 * on first run. It is also the file a DBA would run to manually provision a
 * database.
 *
 * Incremental schema changes after this baseline are applied by
 * Conduit.DataAccess.DatabaseMigrator (Migration v2+). Migrations are
 * idempotent, so running this script and then starting the app is safe.
 *
 * If you need to add a column or table:
 *   1. Add it here so fresh databases get it.
 *   2. Add a corresponding Migration entry in DatabaseMigrator.cs so
 *      existing databases pick it up on next startup.
 */

-- Users Table
CREATE TABLE [dbo].[Users] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [ExternalId] NVARCHAR(255) NULL,
    [UserName] NVARCHAR(255) NOT NULL,
    [Active] BIT NOT NULL DEFAULT 1,
    [Created] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [LastModified] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [Version] INT NOT NULL DEFAULT 1,
    -- Name attributes
    [FormattedName] NVARCHAR(500) NULL,
    [FamilyName] NVARCHAR(255) NULL,
    [GivenName] NVARCHAR(255) NULL,
    [MiddleName] NVARCHAR(255) NULL,
    [HonorificPrefix] NVARCHAR(50) NULL,
    [HonorificSuffix] NVARCHAR(50) NULL,
    -- Profile attributes
    [DisplayName] NVARCHAR(255) NULL,
    [NickName] NVARCHAR(255) NULL,
    [ProfileUrl] NVARCHAR(500) NULL,
    [Title] NVARCHAR(255) NULL,
    [UserType] NVARCHAR(255) NULL,
    [PreferredLanguage] NVARCHAR(10) NULL,
    [Locale] NVARCHAR(10) NULL,
    [Timezone] NVARCHAR(50) NULL,
    -- Password (PBKDF2 hashed)
    [PasswordHash] NVARCHAR(500) NULL,
    [PasswordSalt] NVARCHAR(255) NULL,
    -- Enterprise extension
    [EmployeeNumber] NVARCHAR(50) NULL,
    [CostCenter] NVARCHAR(50) NULL,
    [Organization] NVARCHAR(255) NULL,
    [Division] NVARCHAR(255) NULL,
    [Department] NVARCHAR(255) NULL,
    [ManagerId] UNIQUEIDENTIFIER NULL,
    -- Portal administrator flag
    [IsAdmin] BIT NOT NULL DEFAULT 0,
    CONSTRAINT [PK_Users] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_Users_Manager] FOREIGN KEY ([ManagerId]) REFERENCES [Users]([Id])
);

-- userName is unique PER TENANT, not globally. Different tenants are independent
-- SCIM identity stores; the same userName can legitimately exist in two of them.
CREATE UNIQUE NONCLUSTERED INDEX [UQ_Users_TenantId_UserName] ON [Users]([TenantId], [UserName]);

CREATE INDEX [IX_Users_UserName] ON [Users]([UserName]);
CREATE INDEX [IX_Users_ExternalId] ON [Users]([ExternalId]);
CREATE INDEX [IX_Users_Active] ON [Users]([Active]);
CREATE INDEX [IX_Users_ManagerId] ON [Users]([ManagerId]);
CREATE INDEX [IX_Users_IsAdmin] ON [Users]([IsAdmin]) WHERE [IsAdmin] = 1;

-- User Emails Table
CREATE TABLE [dbo].[UserEmails] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [UserId] UNIQUEIDENTIFIER NOT NULL,
    [Value] NVARCHAR(255) NOT NULL,
    [Type] NVARCHAR(50) NULL,
    [Primary] BIT NOT NULL DEFAULT 0,
    [Display] NVARCHAR(255) NULL,
    CONSTRAINT [PK_UserEmails] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_UserEmails_Users] FOREIGN KEY ([UserId]) REFERENCES [Users]([Id]) ON DELETE CASCADE
);

CREATE INDEX [IX_UserEmails_UserId] ON [UserEmails]([UserId]);
CREATE INDEX [IX_UserEmails_Value] ON [UserEmails]([Value]);

-- User Phone Numbers Table
CREATE TABLE [dbo].[UserPhoneNumbers] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [UserId] UNIQUEIDENTIFIER NOT NULL,
    [Value] NVARCHAR(50) NOT NULL,
    [Type] NVARCHAR(50) NULL,
    [Primary] BIT NOT NULL DEFAULT 0,
    [Display] NVARCHAR(255) NULL,
    CONSTRAINT [PK_UserPhoneNumbers] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_UserPhoneNumbers_Users] FOREIGN KEY ([UserId]) REFERENCES [Users]([Id]) ON DELETE CASCADE
);

CREATE INDEX [IX_UserPhoneNumbers_UserId] ON [UserPhoneNumbers]([UserId]);

-- User Addresses Table
CREATE TABLE [dbo].[UserAddresses] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [UserId] UNIQUEIDENTIFIER NOT NULL,
    [Type] NVARCHAR(50) NULL,
    [StreetAddress] NVARCHAR(500) NULL,
    [Locality] NVARCHAR(255) NULL,
    [Region] NVARCHAR(255) NULL,
    [PostalCode] NVARCHAR(50) NULL,
    [Country] NVARCHAR(255) NULL,
    [Formatted] NVARCHAR(1000) NULL,
    [Primary] BIT NOT NULL DEFAULT 0,
    CONSTRAINT [PK_UserAddresses] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_UserAddresses_Users] FOREIGN KEY ([UserId]) REFERENCES [Users]([Id]) ON DELETE CASCADE
);

CREATE INDEX [IX_UserAddresses_UserId] ON [UserAddresses]([UserId]);

-- Groups Table
CREATE TABLE [dbo].[Groups] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [ExternalId] NVARCHAR(255) NULL,
    [DisplayName] NVARCHAR(255) NOT NULL,
    [Description] NVARCHAR(1000) NULL,
    [Type] NVARCHAR(50) NULL,
    [OwnerId] UNIQUEIDENTIFIER NULL,
    [Created] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [LastModified] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [Version] INT NOT NULL DEFAULT 1,
    CONSTRAINT [PK_Groups] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_Groups_Owner] FOREIGN KEY ([OwnerId]) REFERENCES [Users]([Id])
);

CREATE INDEX [IX_Groups_DisplayName] ON [Groups]([DisplayName]);
CREATE INDEX [IX_Groups_ExternalId] ON [Groups]([ExternalId]);
CREATE INDEX [IX_Groups_OwnerId] ON [Groups]([OwnerId]);

-- Group Members Table
CREATE TABLE [dbo].[GroupMembers] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [GroupId] UNIQUEIDENTIFIER NOT NULL,
    [Value] UNIQUEIDENTIFIER NOT NULL,
    [Type] NVARCHAR(50) NULL DEFAULT 'User',
    [Primary] BIT NOT NULL DEFAULT 0,
    CONSTRAINT [PK_GroupMembers] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_GroupMembers_Groups] FOREIGN KEY ([GroupId]) REFERENCES [Groups]([Id]) ON DELETE CASCADE,
    CONSTRAINT [UQ_GroupMembers_GroupValue] UNIQUE ([GroupId], [Value])
);

CREATE INDEX [IX_GroupMembers_GroupId] ON [GroupMembers]([GroupId]);
CREATE INDEX [IX_GroupMembers_Value] ON [GroupMembers]([Value]);

-- Roles Table
CREATE TABLE [dbo].[Roles] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [ExternalId] NVARCHAR(255) NULL,
    [DisplayName] NVARCHAR(255) NOT NULL,
    [Description] NVARCHAR(1000) NULL,
    [Created] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [LastModified] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [Version] INT NOT NULL DEFAULT 1,
    CONSTRAINT [PK_Roles] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_Roles_DisplayName] UNIQUE ([DisplayName])
);

CREATE INDEX [IX_Roles_DisplayName] ON [Roles]([DisplayName]);

-- User Roles Table
CREATE TABLE [dbo].[UserRoles] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [UserId] UNIQUEIDENTIFIER NOT NULL,
    [RoleId] UNIQUEIDENTIFIER NOT NULL,
    [Value] NVARCHAR(255) NULL,
    [Display] NVARCHAR(255) NULL,
    [Type] NVARCHAR(50) NULL,
    [Primary] BIT NOT NULL DEFAULT 0,
    CONSTRAINT [PK_UserRoles] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_UserRoles_Users] FOREIGN KEY ([UserId]) REFERENCES [Users]([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_UserRoles_Roles] FOREIGN KEY ([RoleId]) REFERENCES [Roles]([Id]) ON DELETE CASCADE,
    CONSTRAINT [UQ_UserRoles_UserRole] UNIQUE ([UserId], [RoleId])
);

CREATE INDEX [IX_UserRoles_UserId] ON [UserRoles]([UserId]);
CREATE INDEX [IX_UserRoles_RoleId] ON [UserRoles]([RoleId]);

-- Custom Attributes Schema Table
CREATE TABLE [dbo].[CustomAttributeSchemas] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [SchemaUrn] NVARCHAR(500) NOT NULL,
    [Name] NVARCHAR(255) NOT NULL,
    [Description] NVARCHAR(1000) NULL,
    [Type] NVARCHAR(50) NOT NULL,
    [MultiValued] BIT NOT NULL DEFAULT 0,
    [Required] BIT NOT NULL DEFAULT 0,
    [CaseExact] BIT NOT NULL DEFAULT 0,
    [Mutability] NVARCHAR(50) NOT NULL DEFAULT 'readWrite',
    [Returned] NVARCHAR(50) NOT NULL DEFAULT 'default',
    [Uniqueness] NVARCHAR(50) NOT NULL DEFAULT 'none',
    [ResourceType] NVARCHAR(50) NOT NULL,
    [Created] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT [PK_CustomAttributeSchemas] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_CustomAttributeSchemas_SchemaName] UNIQUE ([SchemaUrn], [Name])
);

CREATE INDEX [IX_CustomAttributeSchemas_ResourceType] ON [CustomAttributeSchemas]([ResourceType]);

-- Custom Attribute Values Table
CREATE TABLE [dbo].[CustomAttributeValues] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [SchemaId] UNIQUEIDENTIFIER NOT NULL,
    [ResourceId] UNIQUEIDENTIFIER NOT NULL,
    [Value] NVARCHAR(MAX) NOT NULL,
    [Created] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [LastModified] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT [PK_CustomAttributeValues] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_CustomAttributeValues_Schema] FOREIGN KEY ([SchemaId]) REFERENCES [CustomAttributeSchemas]([Id]) ON DELETE CASCADE
);

CREATE INDEX [IX_CustomAttributeValues_SchemaId] ON [CustomAttributeValues]([SchemaId]);
CREATE INDEX [IX_CustomAttributeValues_ResourceId] ON [CustomAttributeValues]([ResourceId]);

-- Departments Table
CREATE TABLE [dbo].[Departments] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [Name] NVARCHAR(255) NOT NULL,
    [ParentId] UNIQUEIDENTIFIER NULL,
    [Level] INT NOT NULL DEFAULT 0,
    [Created] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT [PK_Departments] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_Departments_Parent] FOREIGN KEY ([ParentId]) REFERENCES [Departments]([Id]),
    CONSTRAINT [UQ_Departments_Name] UNIQUE ([Name])
);

CREATE INDEX [IX_Departments_ParentId] ON [Departments]([ParentId]);

-- API Tokens Table
CREATE TABLE [dbo].[ApiTokens] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [Name] NVARCHAR(255) NOT NULL,
    [Description] NVARCHAR(1000) NULL,
    [TokenHash] NVARCHAR(500) NOT NULL,
    [CreatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [LastUsedAt] DATETIME2 NULL,
    [ExpiresAt] DATETIME2 NULL,
    [IsActive] BIT NOT NULL DEFAULT 1,
    CONSTRAINT [PK_ApiTokens] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_ApiTokens_TokenHash] UNIQUE ([TokenHash])
);

CREATE INDEX [IX_ApiTokens_TokenHash] ON [ApiTokens]([TokenHash]);
CREATE INDEX [IX_ApiTokens_IsActive] ON [ApiTokens]([IsActive]);

-- Audit Log Table
CREATE TABLE [dbo].[AuditLogs] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [Timestamp] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [Action] NVARCHAR(50) NOT NULL,
    [ResourceType] NVARCHAR(50) NOT NULL,
    [ResourceId] NVARCHAR(255) NULL,
    [UserId] NVARCHAR(255) NULL,
    [UserName] NVARCHAR(255) NULL,
    [IpAddress] NVARCHAR(50) NULL,
    [UserAgent] NVARCHAR(500) NULL,
    [StatusCode] INT NULL,
    [Details] NVARCHAR(MAX) NULL,
    [OldValue] NVARCHAR(MAX) NULL,
    [NewValue] NVARCHAR(MAX) NULL,
    [Duration] TIME NULL,
    CONSTRAINT [PK_AuditLogs] PRIMARY KEY CLUSTERED ([Id])
);

CREATE INDEX [IX_AuditLogs_Timestamp] ON [AuditLogs]([Timestamp]);
CREATE INDEX [IX_AuditLogs_ResourceType] ON [AuditLogs]([ResourceType]);
CREATE INDEX [IX_AuditLogs_Action] ON [AuditLogs]([Action]);
CREATE INDEX [IX_AuditLogs_UserId] ON [AuditLogs]([UserId]);

-- System Configuration Table
CREATE TABLE [dbo].[SystemConfiguration] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [Key] NVARCHAR(255) NOT NULL,
    [Value] NVARCHAR(MAX) NOT NULL,
    [Type] NVARCHAR(50) NOT NULL,
    [Description] NVARCHAR(1000) NULL,
    [LastModified] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT [PK_SystemConfiguration] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_SystemConfiguration_Key] UNIQUE ([Key])
);

INSERT INTO [SystemConfiguration] ([Key], [Value], [Type], [Description])
VALUES
    ('MaxPageSize', '1000', 'Integer', 'Maximum number of results per page'),
    ('DefaultPageSize', '100', 'Integer', 'Default number of results per page'),
    ('EnableCustomAttributes', 'true', 'Boolean', 'Enable custom attributes feature'),
    ('EnableAuditLog', 'true', 'Boolean', 'Enable audit logging'),
    ('TokenExpiration', '3600', 'Integer', 'Default token expiration in seconds'),
    ('EnableUserGeneration', 'true', 'Boolean', 'Enable user generation feature');
