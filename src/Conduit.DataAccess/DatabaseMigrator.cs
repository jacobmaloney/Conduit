using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Conduit.DataAccess
{
    /// <summary>
    /// Handles database schema migrations and updates
    /// </summary>
    public class DatabaseMigrator
    {
        private readonly string _connectionString;
        private readonly ILogger<DatabaseMigrator> _logger;

        public DatabaseMigrator(string connectionString, ILogger<DatabaseMigrator> logger)
        {
            _connectionString = connectionString;
            _logger = logger;
        }

        /// <summary>
        /// Analyzes the current database schema and returns needed migrations
        /// </summary>
        public async Task<SchemaAnalysisResult> AnalyzeSchemaAsync()
        {
            var result = new SchemaAnalysisResult();
            
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            // Get all tables
            var tables = await connection.QueryAsync<string>(@"
                SELECT TABLE_NAME 
                FROM INFORMATION_SCHEMA.TABLES 
                WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_SCHEMA = 'dbo'
                ORDER BY TABLE_NAME");
            
            result.ExistingTables = tables.ToHashSet();

            // Get all columns for each table
            var columns = await connection.QueryAsync<ColumnInfo>(@"
                SELECT 
                    TABLE_NAME as TableName,
                    COLUMN_NAME as ColumnName,
                    DATA_TYPE as DataType,
                    CHARACTER_MAXIMUM_LENGTH as MaxLength,
                    IS_NULLABLE as IsNullable,
                    COLUMN_DEFAULT as DefaultValue
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = 'dbo'
                ORDER BY TABLE_NAME, ORDINAL_POSITION");

            foreach (var column in columns)
            {
                if (!result.TableColumns.ContainsKey(column.TableName))
                    result.TableColumns[column.TableName] = new HashSet<string>();
                
                result.TableColumns[column.TableName].Add(column.ColumnName);
            }

            // Check schema version
            if (result.ExistingTables.Contains("SchemaVersion"))
            {
                result.CurrentVersion = await connection.QuerySingleOrDefaultAsync<int?>(
                    "SELECT MAX(Version) FROM SchemaVersion") ?? 0;
            }

            return result;
        }

        /// <summary>
        /// Gets required migrations based on schema analysis
        /// </summary>
        public List<SchemaMigration> GetRequiredMigrations(SchemaAnalysisResult analysis)
        {
            var migrations = new List<SchemaMigration>();

            // Migration 1: Fix GroupMembers table
            if (analysis.ExistingTables.Contains("GroupMembers"))
            {
                var columns = analysis.TableColumns.GetValueOrDefault("GroupMembers", new HashSet<string>());
                
                if (columns.Contains("UserId") && !columns.Contains("Value"))
                {
                    migrations.Add(new SchemaMigration
                    {
                        Version = 2,
                        Name = "Fix GroupMembers table schema",
                        Description = "Rename UserId to Value and add Type/Primary columns",
                        SqlScript = @"
-- Fix GroupMembers table to match repository expectations
BEGIN TRANSACTION;

-- Drop constraints
DECLARE @constraint_name NVARCHAR(128);

-- Drop foreign key
SELECT @constraint_name = name FROM sys.foreign_keys 
WHERE parent_object_id = OBJECT_ID('dbo.GroupMembers') 
AND referenced_object_id = OBJECT_ID('dbo.Users');
IF @constraint_name IS NOT NULL
    EXEC('ALTER TABLE [dbo].[GroupMembers] DROP CONSTRAINT ' + @constraint_name);

-- Drop unique constraint
SELECT @constraint_name = name FROM sys.key_constraints 
WHERE parent_object_id = OBJECT_ID('dbo.GroupMembers') 
AND type = 'UQ';
IF @constraint_name IS NOT NULL
    EXEC('ALTER TABLE [dbo].[GroupMembers] DROP CONSTRAINT ' + @constraint_name);

-- Rename column
EXEC sp_rename 'dbo.GroupMembers.UserId', 'Value', 'COLUMN';

-- Add new columns
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GroupMembers') AND name = 'Type')
    ALTER TABLE [dbo].[GroupMembers] ADD [Type] NVARCHAR(50) NULL DEFAULT 'User';

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GroupMembers') AND name = 'Primary')
    ALTER TABLE [dbo].[GroupMembers] ADD [Primary] BIT NULL DEFAULT 0;

-- Drop unused column
IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GroupMembers') AND name = 'Added')
BEGIN
    -- Drop default constraint first
    SELECT @constraint_name = name FROM sys.default_constraints 
    WHERE parent_object_id = OBJECT_ID('dbo.GroupMembers') 
    AND parent_column_id = (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GroupMembers') AND name = 'Added');
    IF @constraint_name IS NOT NULL
        EXEC('ALTER TABLE [dbo].[GroupMembers] DROP CONSTRAINT ' + @constraint_name);
    
    ALTER TABLE [dbo].[GroupMembers] DROP COLUMN [Added];
END

-- Recreate constraints
ALTER TABLE [dbo].[GroupMembers] ADD CONSTRAINT [UQ_GroupMembers_GroupValue] UNIQUE ([GroupId], [Value]);

COMMIT TRANSACTION;"
                    });
                }
            }

            // Migration 2: Add Owner to Groups table
            if (analysis.ExistingTables.Contains("Groups"))
            {
                var columns = analysis.TableColumns.GetValueOrDefault("Groups", new HashSet<string>());
                
                if (!columns.Contains("OwnerId"))
                {
                    migrations.Add(new SchemaMigration
                    {
                        Version = 3,
                        Name = "Add Owner to Groups",
                        Description = "Add OwnerId column to Groups table",
                        SqlScript = @"
-- Add OwnerId column to Groups table
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Groups') AND name = 'OwnerId')
BEGIN
    ALTER TABLE [dbo].[Groups] ADD [OwnerId] UNIQUEIDENTIFIER NULL;
    ALTER TABLE [dbo].[Groups] ADD CONSTRAINT [FK_Groups_Owner] FOREIGN KEY ([OwnerId]) REFERENCES [Users]([Id]);
    CREATE INDEX [IX_Groups_OwnerId] ON [Groups]([OwnerId]);
END"
                    });
                }

                if (!columns.Contains("Type"))
                {
                    migrations.Add(new SchemaMigration
                    {
                        Version = 4,
                        Name = "Add Type to Groups",
                        Description = "Add Type column to Groups table",
                        SqlScript = @"
-- Add Type column to Groups table
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Groups') AND name = 'Type')
BEGIN
    ALTER TABLE [dbo].[Groups] ADD [Type] NVARCHAR(50) NULL;
END"
                    });
                }
            }

            // Migration 3: Add Manager to Users table
            if (analysis.ExistingTables.Contains("Users"))
            {
                var columns = analysis.TableColumns.GetValueOrDefault("Users", new HashSet<string>());
                
                if (!columns.Contains("ManagerId"))
                {
                    migrations.Add(new SchemaMigration
                    {
                        Version = 5,
                        Name = "Add Manager to Users",
                        Description = "Add ManagerId column to Users table",
                        SqlScript = @"
-- Add ManagerId column to Users table
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Users') AND name = 'ManagerId')
BEGIN
    ALTER TABLE [dbo].[Users] ADD [ManagerId] UNIQUEIDENTIFIER NULL;
    ALTER TABLE [dbo].[Users] ADD CONSTRAINT [FK_Users_Manager] FOREIGN KEY ([ManagerId]) REFERENCES [Users]([Id]);
    CREATE INDEX [IX_Users_ManagerId] ON [Users]([ManagerId]);
END"
                    });
                }
            }

            // Migration 4: Fix ApiTokens table schema
            if (analysis.ExistingTables.Contains("ApiTokens"))
            {
                var columns = analysis.TableColumns.GetValueOrDefault("ApiTokens", new HashSet<string>());
                
                if (!columns.Contains("CreatedAt") && columns.Contains("Created"))
                {
                    migrations.Add(new SchemaMigration
                    {
                        Version = 6,
                        Name = "Fix ApiTokens column names",
                        Description = "Rename columns in ApiTokens table to match repository",
                        SqlScript = @"
-- Fix ApiTokens table column names
BEGIN TRANSACTION;

-- Rename columns
IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ApiTokens') AND name = 'Created')
    EXEC sp_rename 'dbo.ApiTokens.Created', 'CreatedAt', 'COLUMN';

IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ApiTokens') AND name = 'LastUsed')
    EXEC sp_rename 'dbo.ApiTokens.LastUsed', 'LastUsedAt', 'COLUMN';

IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ApiTokens') AND name = 'Expires')
    EXEC sp_rename 'dbo.ApiTokens.Expires', 'ExpiresAt', 'COLUMN';

COMMIT TRANSACTION;"
                    });
                }
            }

            // Migration 5: Add IsAdmin to Users
            if (analysis.ExistingTables.Contains("Users"))
            {
                var columns = analysis.TableColumns.GetValueOrDefault("Users", new HashSet<string>());

                if (!columns.Contains("IsAdmin"))
                {
                    migrations.Add(new SchemaMigration
                    {
                        Version = 7,
                        Name = "Add IsAdmin to Users",
                        Description = "Add IsAdmin column for portal administrators",
                        SqlScript = @"
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Users') AND name = 'IsAdmin')
BEGIN
    ALTER TABLE [dbo].[Users] ADD [IsAdmin] BIT NOT NULL CONSTRAINT [DF_Users_IsAdmin] DEFAULT 0;
    CREATE INDEX [IX_Users_IsAdmin] ON [Users]([IsAdmin]) WHERE [IsAdmin] = 1;
END"
                    });
                }
            }

            // Migration 6 (v8): Multi-tenant — Connected Systems
            // Adds the Tenants table (UI label = "Connected Systems"), plus TenantId
            // foreign keys on Users, Groups, and ApiTokens, plus a Scope column on
            // ApiTokens. Seeds a default tenant so existing rows survive the NOT NULL
            // constraint. Idempotent at every step.
            migrations.Add(new SchemaMigration
            {
                Version = 8,
                Name = "Multi-tenant (Connected Systems) baseline",
                Description = "Add Tenants table + TenantId on Users/Groups/ApiTokens + Scope on ApiTokens",
                SqlScript = @"
-- 1. Tenants table
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Tenants')
BEGIN
    CREATE TABLE [dbo].[Tenants] (
        [Id]           UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
        [Name]         NVARCHAR(200)    NOT NULL,
        [Slug]         NVARCHAR(100)    NOT NULL,
        [Description]  NVARCHAR(500)    NULL,
        [SystemType]   NVARCHAR(20)     NOT NULL CONSTRAINT [DF_Tenants_SystemType] DEFAULT 'Emulator',
        [Domain]       NVARCHAR(300)    NULL,
        [IsActive]     BIT              NOT NULL CONSTRAINT [DF_Tenants_IsActive] DEFAULT 1,
        [Created]      DATETIME2        NOT NULL CONSTRAINT [DF_Tenants_Created] DEFAULT GETUTCDATE(),
        [LastModified] DATETIME2        NOT NULL CONSTRAINT [DF_Tenants_LastModified] DEFAULT GETUTCDATE(),
        CONSTRAINT [PK_Tenants] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [UQ_Tenants_Slug] UNIQUE ([Slug])
    );
    CREATE INDEX [IX_Tenants_IsActive] ON [Tenants]([IsActive]);
END

-- 2. Seed default tenant so existing Users/Groups/ApiTokens have a parent.
-- Fixed GUID so cross-DB references in code can be deterministic.
IF NOT EXISTS (SELECT 1 FROM [dbo].[Tenants] WHERE [Id] = '00000000-0000-0000-0000-000000000001')
BEGIN
    INSERT INTO [dbo].[Tenants] ([Id], [Name], [Slug], [Description], [SystemType], [IsActive])
    VALUES (
        '00000000-0000-0000-0000-000000000001',
        'Default',
        'default',
        'Default Connected System for pre-multi-tenant data',
        'Emulator',
        1
    );
END

-- 3. Users.TenantId
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Users') AND name = 'TenantId')
BEGIN
    ALTER TABLE [dbo].[Users]
        ADD [TenantId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_Users_TenantId] DEFAULT '00000000-0000-0000-0000-000000000001';
END
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE name = 'FK_Users_Tenants')
BEGIN
    ALTER TABLE [dbo].[Users] WITH CHECK
        ADD CONSTRAINT [FK_Users_Tenants] FOREIGN KEY ([TenantId]) REFERENCES [Tenants]([Id]);
END
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Users_TenantId' AND object_id = OBJECT_ID('dbo.Users'))
    CREATE INDEX [IX_Users_TenantId] ON [Users]([TenantId]);

-- 4. Groups.TenantId
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Groups') AND name = 'TenantId')
BEGIN
    ALTER TABLE [dbo].[Groups]
        ADD [TenantId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_Groups_TenantId] DEFAULT '00000000-0000-0000-0000-000000000001';
END
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE name = 'FK_Groups_Tenants')
BEGIN
    ALTER TABLE [dbo].[Groups] WITH CHECK
        ADD CONSTRAINT [FK_Groups_Tenants] FOREIGN KEY ([TenantId]) REFERENCES [Tenants]([Id]);
END
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Groups_TenantId' AND object_id = OBJECT_ID('dbo.Groups'))
    CREATE INDEX [IX_Groups_TenantId] ON [Groups]([TenantId]);

-- 5. ApiTokens.TenantId (nullable — NULL = admin/all-tenants)
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ApiTokens') AND name = 'TenantId')
BEGIN
    ALTER TABLE [dbo].[ApiTokens] ADD [TenantId] UNIQUEIDENTIFIER NULL;
END
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE name = 'FK_ApiTokens_Tenants')
BEGIN
    ALTER TABLE [dbo].[ApiTokens] WITH CHECK
        ADD CONSTRAINT [FK_ApiTokens_Tenants] FOREIGN KEY ([TenantId]) REFERENCES [Tenants]([Id]);
END
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ApiTokens_TenantId' AND object_id = OBJECT_ID('dbo.ApiTokens'))
    CREATE INDEX [IX_ApiTokens_TenantId] ON [ApiTokens]([TenantId]);

-- 6. ApiTokens.Scope ('Admin' | 'Tenant' | 'ArsProxy'; default 'Tenant')
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ApiTokens') AND name = 'Scope')
BEGIN
    ALTER TABLE [dbo].[ApiTokens]
        ADD [Scope] NVARCHAR(20) NOT NULL
        CONSTRAINT [DF_ApiTokens_Scope] DEFAULT 'Tenant';
END
"
            });

            // Migration 7 (v9): SqlAccounts tracking table for the /sql/v1/ emulator.
            // Holds metadata only — actual SQL login state lives in sys.sql_logins on the
            // configured target instance (see SystemConfiguration key SqlEmulator.ConnectionString).
            migrations.Add(new SchemaMigration
            {
                Version = 9,
                Name = "SqlAccounts tracking table",
                Description = "Adds the SqlAccounts table backing the /sql/v1/ emulator endpoint.",
                SqlScript = @"
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlAccounts')
BEGIN
    CREATE TABLE [dbo].[SqlAccounts] (
        [Id]       UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
        [TenantId] UNIQUEIDENTIFIER NOT NULL CONSTRAINT [DF_SqlAccounts_TenantId] DEFAULT '00000000-0000-0000-0000-000000000001',
        [Username] NVARCHAR(128)    NOT NULL,
        [Disabled] BIT              NOT NULL CONSTRAINT [DF_SqlAccounts_Disabled] DEFAULT 0,
        [Created]  DATETIME2        NOT NULL CONSTRAINT [DF_SqlAccounts_Created] DEFAULT GETUTCDATE(),
        CONSTRAINT [PK_SqlAccounts] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_SqlAccounts_Tenants] FOREIGN KEY ([TenantId]) REFERENCES [Tenants]([Id]),
        CONSTRAINT [UQ_SqlAccounts_TenantUsername] UNIQUE ([TenantId], [Username])
    );
    CREATE INDEX [IX_SqlAccounts_TenantId] ON [SqlAccounts]([TenantId]);
END
"
            });

            // Migration 8 (v10): PortalAdmins separation. Portal/web-UI admin accounts move
            // to their own table so SCIM data ops (Delete All Users, DELETE /scim/v2/Users/{id},
            // tenant resets) can never lock the operator out of their own server again. The
            // Users table keeps PasswordHash / PasswordSalt / IsAdmin columns for now so we
            // can roll back the credential read; a later migration can drop them once we're
            // confident nothing reads them.
            migrations.Add(new SchemaMigration
            {
                Version = 10,
                Name = "PortalAdmins separation",
                Description = "Creates PortalAdmins table and migrates existing IsAdmin=1 users into it. Users table is for SCIM only after this.",
                SqlScript = @"
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'PortalAdmins')
BEGIN
    CREATE TABLE [dbo].[PortalAdmins] (
        [Id]            UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
        [UserName]      NVARCHAR(128)    NOT NULL,
        [DisplayName]   NVARCHAR(256)    NULL,
        [PasswordHash]  NVARCHAR(512)    NOT NULL,
        [PasswordSalt]  NVARCHAR(512)    NOT NULL,
        [Active]        BIT              NOT NULL CONSTRAINT [DF_PortalAdmins_Active] DEFAULT 1,
        [Created]       DATETIME2        NOT NULL CONSTRAINT [DF_PortalAdmins_Created] DEFAULT SYSUTCDATETIME(),
        [LastModified]  DATETIME2        NOT NULL CONSTRAINT [DF_PortalAdmins_LastModified] DEFAULT SYSUTCDATETIME(),
        [LastLoginAt]   DATETIME2        NULL,
        CONSTRAINT [PK_PortalAdmins] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [UQ_PortalAdmins_UserName] UNIQUE ([UserName])
    );
END;

-- Copy any IsAdmin=1 user with a stored credential into PortalAdmins, skipping rows
-- that already exist in the destination (the migration is rerun-safe).
IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Users')
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Users') AND name = 'IsAdmin')
BEGIN
    INSERT INTO [dbo].[PortalAdmins] (Id, UserName, DisplayName, PasswordHash, PasswordSalt, Active, Created, LastModified)
    SELECT u.Id, u.UserName, u.DisplayName, u.PasswordHash, u.PasswordSalt,
           ISNULL(u.Active, 1), ISNULL(u.Created, SYSUTCDATETIME()), ISNULL(u.LastModified, SYSUTCDATETIME())
    FROM [dbo].[Users] u
    WHERE u.IsAdmin = 1
      AND u.PasswordHash IS NOT NULL
      AND u.PasswordSalt IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM [dbo].[PortalAdmins] pa WHERE LOWER(pa.UserName) = LOWER(u.UserName));

    -- Remove the admin rows from Users so they don't pollute SCIM /Users responses.
    -- FK references would block this — clean those out first.
    DELETE gm
    FROM [dbo].[GroupMembers] gm
    INNER JOIN [dbo].[Users] u ON u.Id = gm.Value
    WHERE u.IsAdmin = 1;

    UPDATE [dbo].[Groups]
       SET [OwnerId] = NULL
     WHERE [OwnerId] IN (SELECT Id FROM [dbo].[Users] WHERE IsAdmin = 1);

    UPDATE [dbo].[Users]
       SET [ManagerId] = NULL
     WHERE [ManagerId] IN (SELECT Id FROM [dbo].[Users] WHERE IsAdmin = 1);

    DELETE FROM [dbo].[Users] WHERE IsAdmin = 1;
END;
"
            });

            // Migration 9 (v11): Persistent brute-force protection state. Replaces the
            // in-memory LoginThrottle so failure counters survive process restarts and
            // multiple processes (eventually) can share the same lockout view.
            //
            // LoginAttempts is a rolling-window log of every attempt (success or failure)
            // per (UsernameLower, IpAddress). LoginLockouts records active hard-lockouts;
            // a row's LockedUntil is the wall-clock time at which the bucket reopens.
            migrations.Add(new SchemaMigration
            {
                Version = 11,
                Name = "LoginAttempts + LoginLockouts",
                Description = "Persistent brute-force protection state — survives restarts.",
                SqlScript = @"
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'LoginAttempts')
BEGIN
    CREATE TABLE [dbo].[LoginAttempts] (
        [Id]            UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
        [UsernameLower] NVARCHAR(256)    NOT NULL,
        [IpAddress]     NVARCHAR(64)     NOT NULL,
        [AttemptedAt]   DATETIME2        NOT NULL CONSTRAINT [DF_LoginAttempts_AttemptedAt] DEFAULT SYSUTCDATETIME(),
        [Success]       BIT              NOT NULL,
        CONSTRAINT [PK_LoginAttempts] PRIMARY KEY CLUSTERED ([Id])
    );
    CREATE INDEX [IX_LoginAttempts_UserIpTime]
        ON [LoginAttempts]([UsernameLower], [IpAddress], [AttemptedAt] DESC)
        INCLUDE ([Success]);
    CREATE INDEX [IX_LoginAttempts_AttemptedAt]
        ON [LoginAttempts]([AttemptedAt]);
END;

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'LoginLockouts')
BEGIN
    CREATE TABLE [dbo].[LoginLockouts] (
        [UsernameLower] NVARCHAR(256)    NOT NULL,
        [IpAddress]     NVARCHAR(64)     NOT NULL,
        [LockedUntil]   DATETIME2        NOT NULL,
        [LockedAt]      DATETIME2        NOT NULL CONSTRAINT [DF_LoginLockouts_LockedAt] DEFAULT SYSUTCDATETIME(),
        [FailureCount]  INT              NOT NULL CONSTRAINT [DF_LoginLockouts_FailureCount] DEFAULT 0,
        CONSTRAINT [PK_LoginLockouts] PRIMARY KEY CLUSTERED ([UsernameLower], [IpAddress])
    );
    CREATE INDEX [IX_LoginLockouts_LockedUntil] ON [LoginLockouts]([LockedUntil]);
END;
"
            });

            // Migration 10 (v12): Legal-hold flag on Tenants. A Connected System with
            // LegalHold = 1 cannot be cleared or hard-deleted from the UI — the operator
            // has to explicitly take the flag off (an audit-logged action in itself)
            // before any destructive operation runs. The intended use is "this system
            // holds historical events that can't be reproduced; refuse to drop the data."
            migrations.Add(new SchemaMigration
            {
                Version = 12,
                Name = "Tenants.LegalHold",
                Description = "Adds the LegalHold flag to Tenants — destructive ops refuse when set.",
                SqlScript = @"
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.Tenants') AND name = 'LegalHold')
BEGIN
    ALTER TABLE [dbo].[Tenants]
        ADD [LegalHold] BIT NOT NULL
        CONSTRAINT [DF_Tenants_LegalHold] DEFAULT 0;
END;
"
            });

            // Migration 13: Make Users.UserName and Groups.DisplayName unique PER TENANT,
            // not globally. The original CreateDatabase.sql had:
            //   CONSTRAINT UQ_Users_UserName UNIQUE (UserName)
            //   CONSTRAINT UQ_Groups_DisplayName UNIQUE (DisplayName)
            // which broke SCIM multi-tenancy - a userName like "aaamaster" provisioned
            // into one Connected System could not be provisioned into another, since
            // the constraint was global. Tenant isolation is the whole point of the
            // /t/{slug}/ route. Replace with composite (TenantId, UserName / DisplayName)
            // uniqueness so different tenants are independent identity stores.
            migrations.Add(new SchemaMigration
            {
                Version = 13,
                Name = "Tenant-scoped uniqueness on Users.UserName + Groups.DisplayName",
                Description = "Replace global UQ_Users_UserName / UQ_Groups_DisplayName with composite (TenantId, ...) uniqueness so the same userName/displayName can legitimately exist in two tenants.",
                SqlScript = @"
-- Users: drop the global uniqueness in whichever form it exists (table
-- constraint via CONSTRAINT clause OR a free-standing UNIQUE INDEX created
-- by a different deploy path), then create the composite index.
IF EXISTS (SELECT 1 FROM sys.key_constraints
           WHERE name = 'UQ_Users_UserName' AND parent_object_id = OBJECT_ID('dbo.Users'))
BEGIN
    ALTER TABLE [dbo].[Users] DROP CONSTRAINT [UQ_Users_UserName];
END
ELSE IF EXISTS (SELECT 1 FROM sys.indexes
                WHERE name = 'UQ_Users_UserName' AND object_id = OBJECT_ID('dbo.Users'))
BEGIN
    DROP INDEX [UQ_Users_UserName] ON [dbo].[Users];
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UQ_Users_TenantId_UserName' AND object_id = OBJECT_ID('dbo.Users'))
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [UQ_Users_TenantId_UserName] ON [dbo].[Users]([TenantId], [UserName]);
END;

-- Groups: same treatment (constraint or index form).
IF EXISTS (SELECT 1 FROM sys.key_constraints
           WHERE name = 'UQ_Groups_DisplayName' AND parent_object_id = OBJECT_ID('dbo.Groups'))
BEGIN
    ALTER TABLE [dbo].[Groups] DROP CONSTRAINT [UQ_Groups_DisplayName];
END
ELSE IF EXISTS (SELECT 1 FROM sys.indexes
                WHERE name = 'UQ_Groups_DisplayName' AND object_id = OBJECT_ID('dbo.Groups'))
BEGIN
    DROP INDEX [UQ_Groups_DisplayName] ON [dbo].[Groups];
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UQ_Groups_TenantId_DisplayName' AND object_id = OBJECT_ID('dbo.Groups'))
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [UQ_Groups_TenantId_DisplayName] ON [dbo].[Groups]([TenantId], [DisplayName]);
END;
"
            });

            // Migration 14: V002 — Sync metadata (the symmetric-router schema).
            // Conduit owns five tables from V002 forward: SyncProjects, SyncRuns,
            // SyncRunLogs, AttributeMappings, ConnectionCredentials. Plus a sixth,
            // SyncProjectScopes, that holds per-project source-side scope filters
            // (e.g. LDAP base DN + filter for AD). Every table is workspace-capable
            // via WorkspaceId (nullable for now — single-workspace installer).
            //
            // Per the conduit-symmetric-router-architecture decision:
            //   - NO Objects table in Conduit ever.
            //   - Connector tenants don't persist row data; data flows through to the sink.
            //   - Emulator tenants keep using the existing Users/Groups/GroupMembers tables.
            migrations.Add(new SchemaMigration
            {
                Version = 14,
                Name = "V002 sync metadata",
                Description = "Adds the five sync-metadata tables (SyncProjects, SyncRuns, SyncRunLogs, AttributeMappings, ConnectionCredentials) plus SyncProjectScopes for source-side filters.",
                SqlScript = @"
-- 1. SyncProjects — every project flows SourceTenantId → SinkTenantId.
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SyncProjects')
BEGIN
    CREATE TABLE [dbo].[SyncProjects] (
        [Id]               UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
        [WorkspaceId]      UNIQUEIDENTIFIER NULL,
        [Name]             NVARCHAR(200)    NOT NULL,
        [Description]      NVARCHAR(1000)   NULL,
        [SourceTenantId]   UNIQUEIDENTIFIER NOT NULL,
        [SinkTenantId]     UNIQUEIDENTIFIER NOT NULL,
        [ObjectClass]      NVARCHAR(50)     NOT NULL CONSTRAINT [DF_SyncProjects_ObjectClass] DEFAULT 'User',
        [CronSchedule]     NVARCHAR(100)    NULL,
        [IsEnabled]        BIT              NOT NULL CONSTRAINT [DF_SyncProjects_IsEnabled] DEFAULT 1,
        [IsRunning]        BIT              NOT NULL CONSTRAINT [DF_SyncProjects_IsRunning] DEFAULT 0,
        [LastRunAt]        DATETIME2        NULL,
        [LastRunStatus]    NVARCHAR(50)     NULL,
        [LastRunId]        UNIQUEIDENTIFIER NULL,
        [NextScheduledRunAt] DATETIME2      NULL,
        [TotalRuns]        INT              NOT NULL CONSTRAINT [DF_SyncProjects_TotalRuns] DEFAULT 0,
        [SuccessfulRuns]   INT              NOT NULL CONSTRAINT [DF_SyncProjects_SuccessfulRuns] DEFAULT 0,
        [FailedRuns]       INT              NOT NULL CONSTRAINT [DF_SyncProjects_FailedRuns] DEFAULT 0,
        [CreatedAt]        DATETIME2        NOT NULL CONSTRAINT [DF_SyncProjects_CreatedAt] DEFAULT SYSUTCDATETIME(),
        [LastModified]     DATETIME2        NOT NULL CONSTRAINT [DF_SyncProjects_LastModified] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_SyncProjects] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_SyncProjects_SourceTenant] FOREIGN KEY ([SourceTenantId]) REFERENCES [Tenants]([Id]),
        CONSTRAINT [FK_SyncProjects_SinkTenant]   FOREIGN KEY ([SinkTenantId])   REFERENCES [Tenants]([Id])
    );
    CREATE INDEX [IX_SyncProjects_SourceTenantId] ON [SyncProjects]([SourceTenantId]);
    CREATE INDEX [IX_SyncProjects_SinkTenantId]   ON [SyncProjects]([SinkTenantId]);
    CREATE INDEX [IX_SyncProjects_IsEnabled]      ON [SyncProjects]([IsEnabled]) WHERE [IsEnabled] = 1;
    CREATE INDEX [IX_SyncProjects_WorkspaceId]    ON [SyncProjects]([WorkspaceId]);
END;

-- 2. SyncRuns — one row per execution attempt. Lives forever (history).
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SyncRuns')
BEGIN
    CREATE TABLE [dbo].[SyncRuns] (
        [Id]               UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
        [WorkspaceId]      UNIQUEIDENTIFIER NULL,
        [SyncProjectId]    UNIQUEIDENTIFIER NOT NULL,
        [Status]           NVARCHAR(50)     NOT NULL CONSTRAINT [DF_SyncRuns_Status] DEFAULT 'Running',
        [TriggeredBy]      NVARCHAR(100)    NOT NULL CONSTRAINT [DF_SyncRuns_TriggeredBy] DEFAULT 'Manual',
        [StartedAt]        DATETIME2        NOT NULL CONSTRAINT [DF_SyncRuns_StartedAt] DEFAULT SYSUTCDATETIME(),
        [CompletedAt]      DATETIME2        NULL,
        [DurationMs]       BIGINT           NULL,
        [ObjectsRead]      INT              NOT NULL CONSTRAINT [DF_SyncRuns_ObjectsRead] DEFAULT 0,
        [ObjectsCreated]   INT              NOT NULL CONSTRAINT [DF_SyncRuns_ObjectsCreated] DEFAULT 0,
        [ObjectsUpdated]   INT              NOT NULL CONSTRAINT [DF_SyncRuns_ObjectsUpdated] DEFAULT 0,
        [ObjectsSkipped]   INT              NOT NULL CONSTRAINT [DF_SyncRuns_ObjectsSkipped] DEFAULT 0,
        [ObjectsFailed]    INT              NOT NULL CONSTRAINT [DF_SyncRuns_ObjectsFailed] DEFAULT 0,
        [ErrorMessage]     NVARCHAR(MAX)    NULL,
        CONSTRAINT [PK_SyncRuns] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_SyncRuns_SyncProjects] FOREIGN KEY ([SyncProjectId]) REFERENCES [SyncProjects]([Id])
    );
    CREATE INDEX [IX_SyncRuns_SyncProjectId] ON [SyncRuns]([SyncProjectId]);
    CREATE INDEX [IX_SyncRuns_StartedAt]     ON [SyncRuns]([StartedAt] DESC);
    CREATE INDEX [IX_SyncRuns_Status]        ON [SyncRuns]([Status]);
END;

-- 3. SyncRunLogs — per-run line log. One row per log entry.
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SyncRunLogs')
BEGIN
    CREATE TABLE [dbo].[SyncRunLogs] (
        [Id]          BIGINT           IDENTITY(1,1) NOT NULL,
        [SyncRunId]   UNIQUEIDENTIFIER NOT NULL,
        [Level]       NVARCHAR(20)     NOT NULL CONSTRAINT [DF_SyncRunLogs_Level] DEFAULT 'Info',
        [Message]     NVARCHAR(MAX)    NOT NULL,
        [Timestamp]   DATETIME2        NOT NULL CONSTRAINT [DF_SyncRunLogs_Timestamp] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_SyncRunLogs] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_SyncRunLogs_SyncRuns] FOREIGN KEY ([SyncRunId]) REFERENCES [SyncRuns]([Id]) ON DELETE CASCADE
    );
    CREATE INDEX [IX_SyncRunLogs_SyncRunId]  ON [SyncRunLogs]([SyncRunId], [Id]);
END;

-- 4. AttributeMappings — per-project source-attr → sink-attr mapping.
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'AttributeMappings')
BEGIN
    CREATE TABLE [dbo].[AttributeMappings] (
        [Id]              UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
        [SyncProjectId]   UNIQUEIDENTIFIER NOT NULL,
        [SourceAttribute] NVARCHAR(200)    NOT NULL,
        [SinkAttribute]   NVARCHAR(200)    NOT NULL,
        [TransformExpr]   NVARCHAR(MAX)    NULL,
        [IsRequired]      BIT              NOT NULL CONSTRAINT [DF_AttributeMappings_IsRequired] DEFAULT 0,
        [SortOrder]       INT              NOT NULL CONSTRAINT [DF_AttributeMappings_SortOrder] DEFAULT 0,
        CONSTRAINT [PK_AttributeMappings] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_AttributeMappings_SyncProjects] FOREIGN KEY ([SyncProjectId]) REFERENCES [SyncProjects]([Id]) ON DELETE CASCADE
    );
    CREATE INDEX [IX_AttributeMappings_SyncProjectId] ON [AttributeMappings]([SyncProjectId]);
END;

-- 5. ConnectionCredentials — AES-GCM ciphertext keyed by TenantId.
-- One row per (TenantId, CredentialName). The ciphertext blob is opaque to SQL;
-- the encryption key comes from configuration at runtime (see CredentialProtector).
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ConnectionCredentials')
BEGIN
    CREATE TABLE [dbo].[ConnectionCredentials] (
        [Id]               UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
        [TenantId]         UNIQUEIDENTIFIER NOT NULL,
        [CredentialName]   NVARCHAR(100)    NOT NULL,
        [Ciphertext]       VARBINARY(MAX)   NOT NULL,
        [Nonce]            VARBINARY(12)    NOT NULL,
        [Tag]              VARBINARY(16)    NOT NULL,
        [CreatedAt]        DATETIME2        NOT NULL CONSTRAINT [DF_ConnectionCredentials_CreatedAt] DEFAULT SYSUTCDATETIME(),
        [LastModified]     DATETIME2        NOT NULL CONSTRAINT [DF_ConnectionCredentials_LastModified] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_ConnectionCredentials] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_ConnectionCredentials_Tenants] FOREIGN KEY ([TenantId]) REFERENCES [Tenants]([Id]) ON DELETE CASCADE,
        CONSTRAINT [UQ_ConnectionCredentials_TenantName] UNIQUE ([TenantId], [CredentialName])
    );
    CREATE INDEX [IX_ConnectionCredentials_TenantId] ON [ConnectionCredentials]([TenantId]);
END;

-- 6. SyncProjectScopes — per-project source-side scope filters.
-- For AD: BaseDN + LdapFilter. For SCIM/HTTP connectors: query expression.
-- Single row per project for now; a future migration can multi-row this if multiple scopes are needed.
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SyncProjectScopes')
BEGIN
    CREATE TABLE [dbo].[SyncProjectScopes] (
        [Id]             UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
        [SyncProjectId]  UNIQUEIDENTIFIER NOT NULL,
        [BaseDN]         NVARCHAR(2000)   NULL,
        [LdapFilter]     NVARCHAR(2000)   NULL,
        [QueryExpression] NVARCHAR(MAX)   NULL,
        [PageSize]       INT              NOT NULL CONSTRAINT [DF_SyncProjectScopes_PageSize] DEFAULT 1000,
        [MaxObjects]     INT              NULL,
        [IncludeDeleted] BIT              NOT NULL CONSTRAINT [DF_SyncProjectScopes_IncludeDeleted] DEFAULT 0,
        [CreatedAt]      DATETIME2        NOT NULL CONSTRAINT [DF_SyncProjectScopes_CreatedAt] DEFAULT SYSUTCDATETIME(),
        [LastModified]   DATETIME2        NOT NULL CONSTRAINT [DF_SyncProjectScopes_LastModified] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_SyncProjectScopes] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_SyncProjectScopes_SyncProjects] FOREIGN KEY ([SyncProjectId]) REFERENCES [SyncProjects]([Id]) ON DELETE CASCADE,
        CONSTRAINT [UQ_SyncProjectScopes_SyncProjectId] UNIQUE ([SyncProjectId])
    );
END;
"
            });

            // Migration 15: Phase 2 — incremental sync (cursors) + multi-credential
            // selection on SyncProjects. Additive only; existing rows default cleanly.
            //   - SyncRuns.Cursor + IsIncremental: opaque resume token persisted at
            //     end of run; orchestrator passes back to source on next run.
            //   - SyncProjects.SourceCredentialName / SinkCredentialName: optional
            //     pointer into ConnectionCredentials(TenantId, CredentialName) so a
            //     tenant can host multiple credentials (e.g. AWS IAM + AWS SSO).
            migrations.Add(new SchemaMigration
            {
                Version = 15,
                Name = "Phase 2 cursor + multi-credential pointers",
                Description = "Adds SyncRuns.Cursor + IsIncremental for delta sync; SyncProjects.SourceCredentialName + SinkCredentialName for multi-credential-per-tenant UX.",
                SqlScript = @"
IF COL_LENGTH('dbo.SyncRuns','Cursor') IS NULL
BEGIN
    ALTER TABLE [dbo].[SyncRuns] ADD [Cursor] NVARCHAR(MAX) NULL;
END;
IF COL_LENGTH('dbo.SyncRuns','IsIncremental') IS NULL
BEGIN
    ALTER TABLE [dbo].[SyncRuns] ADD [IsIncremental] BIT NOT NULL CONSTRAINT [DF_SyncRuns_IsIncremental] DEFAULT 0;
END;
IF COL_LENGTH('dbo.SyncProjects','SourceCredentialName') IS NULL
BEGIN
    ALTER TABLE [dbo].[SyncProjects] ADD [SourceCredentialName] NVARCHAR(100) NULL;
END;
IF COL_LENGTH('dbo.SyncProjects','SinkCredentialName') IS NULL
BEGIN
    ALTER TABLE [dbo].[SyncProjects] ADD [SinkCredentialName] NVARCHAR(100) NULL;
END;
"
            });

            // Migration 16: Phase 4 — async-job framework. Some sinks (notably AWS SSO
            // Admin's CreateAccountAssignment) return an opaque RequestId instead of
            // acting synchronously. The orchestrator persists a row here per submission;
            // AsyncJobPollerService advances each row to Succeeded/Failed by calling the
            // adapter's IConnectorAsyncJobResolver.
            migrations.Add(new SchemaMigration
            {
                Version = 16,
                Name = "Phase 4 async-job framework",
                Description = "Adds SyncRunAsyncJobs table for tracking adapter-submitted async jobs (e.g. AWS SSO assignment creations) that the AsyncJobPollerService advances out-of-band.",
                SqlScript = @"
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SyncRunAsyncJobs')
BEGIN
    CREATE TABLE [dbo].[SyncRunAsyncJobs] (
        [Id]               BIGINT IDENTITY(1,1) NOT NULL,
        [SyncRunId]        UNIQUEIDENTIFIER NOT NULL,
        [SyncProjectId]    UNIQUEIDENTIFIER NOT NULL,
        [TenantId]         UNIQUEIDENTIFIER NOT NULL,
        [SystemType]       NVARCHAR(100)    NOT NULL,
        [JobType]          NVARCHAR(100)    NOT NULL,
        [JobId]            NVARCHAR(500)    NOT NULL,
        [ObjectExternalId] NVARCHAR(500)    NULL,
        [State]            NVARCHAR(20)     NOT NULL CONSTRAINT [DF_SyncRunAsyncJobs_State] DEFAULT 'Pending',
        [ErrorMessage]     NVARCHAR(MAX)    NULL,
        [PayloadJson]      NVARCHAR(MAX)    NULL,
        [ResultJson]       NVARCHAR(MAX)    NULL,
        [SubmittedAt]      DATETIME2        NOT NULL CONSTRAINT [DF_SyncRunAsyncJobs_SubmittedAt] DEFAULT SYSUTCDATETIME(),
        [LastPolledAt]     DATETIME2        NULL,
        [CompletedAt]      DATETIME2        NULL,
        [PollAttempts]     INT              NOT NULL CONSTRAINT [DF_SyncRunAsyncJobs_PollAttempts] DEFAULT 0,
        CONSTRAINT [PK_SyncRunAsyncJobs] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_SyncRunAsyncJobs_SyncRuns] FOREIGN KEY ([SyncRunId]) REFERENCES [SyncRuns]([Id])
    );
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SyncRunAsyncJobs_State_SubmittedAt' AND object_id = OBJECT_ID('dbo.SyncRunAsyncJobs'))
BEGIN
    CREATE INDEX [IX_SyncRunAsyncJobs_State_SubmittedAt] ON [dbo].[SyncRunAsyncJobs]([State], [SubmittedAt]);
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SyncRunAsyncJobs_SyncRunId' AND object_id = OBJECT_ID('dbo.SyncRunAsyncJobs'))
BEGIN
    CREATE INDEX [IX_SyncRunAsyncJobs_SyncRunId] ON [dbo].[SyncRunAsyncJobs]([SyncRunId]);
END;
"
            });

            // Migration 17: Phase 7 — Workflows + WorkflowSteps tree per Sync Project.
            // Adds a workflow/step hierarchy (mirrors IC's SyncProjectWizard model) on
            // top of the existing SyncProjects table. AttributeMappings + SyncProjectScopes
            // get an optional WorkflowStepId so a mapping/scope can attach to a specific
            // step rather than the project as a whole.
            //
            // Backfill: every existing SyncProject gets ONE Default workflow and ONE
            // Default Mapping step. Existing AttributeMappings + SyncProjectScopes are
            // re-pointed at that step so projects keep working unchanged after upgrade.
            // The orchestrator's step-routing walk treats a one-workflow / one-step
            // project as identical to the Phase 6 source→mapping→sink loop.
            migrations.Add(new SchemaMigration
            {
                Version = 17,
                Name = "Phase 7 workflows + steps tree",
                Description = "Adds Workflows + WorkflowSteps tables, optional WorkflowStepId FK on AttributeMappings + SyncProjectScopes, and backfills a Default workflow + step per existing SyncProject.",
                SqlScript = @"
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Workflows')
BEGIN
    CREATE TABLE [dbo].[Workflows] (
        [Id]            UNIQUEIDENTIFIER NOT NULL,
        [SyncProjectId] UNIQUEIDENTIFIER NOT NULL,
        [Name]          NVARCHAR(200)    NOT NULL,
        [Description]   NVARCHAR(1000)   NULL,
        [Ordinal]       INT              NOT NULL CONSTRAINT [DF_Workflows_Ordinal] DEFAULT 0,
        [Enabled]       BIT              NOT NULL CONSTRAINT [DF_Workflows_Enabled] DEFAULT 1,
        [CreatedAt]     DATETIME2        NOT NULL CONSTRAINT [DF_Workflows_CreatedAt] DEFAULT SYSUTCDATETIME(),
        [ModifiedAt]    DATETIME2        NOT NULL CONSTRAINT [DF_Workflows_ModifiedAt] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_Workflows] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_Workflows_SyncProjects] FOREIGN KEY ([SyncProjectId]) REFERENCES [SyncProjects]([Id])
    );
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Workflows_SyncProjectId' AND object_id = OBJECT_ID('dbo.Workflows'))
BEGIN
    CREATE INDEX [IX_Workflows_SyncProjectId] ON [dbo].[Workflows]([SyncProjectId], [Ordinal]);
END;

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'WorkflowSteps')
BEGIN
    CREATE TABLE [dbo].[WorkflowSteps] (
        [Id]              UNIQUEIDENTIFIER NOT NULL,
        [WorkflowId]      UNIQUEIDENTIFIER NOT NULL,
        [Name]            NVARCHAR(200)    NOT NULL,
        [StepType]        NVARCHAR(50)     NOT NULL CONSTRAINT [DF_WorkflowSteps_StepType] DEFAULT 'Mapping',
        [Ordinal]         INT              NOT NULL CONSTRAINT [DF_WorkflowSteps_Ordinal] DEFAULT 0,
        [Enabled]         BIT              NOT NULL CONSTRAINT [DF_WorkflowSteps_Enabled] DEFAULT 1,
        [Configuration]   NVARCHAR(MAX)    NULL,
        [CreatedAt]       DATETIME2        NOT NULL CONSTRAINT [DF_WorkflowSteps_CreatedAt] DEFAULT SYSUTCDATETIME(),
        [ModifiedAt]      DATETIME2        NOT NULL CONSTRAINT [DF_WorkflowSteps_ModifiedAt] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_WorkflowSteps] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_WorkflowSteps_Workflows] FOREIGN KEY ([WorkflowId]) REFERENCES [Workflows]([Id])
    );
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WorkflowSteps_WorkflowId' AND object_id = OBJECT_ID('dbo.WorkflowSteps'))
BEGIN
    CREATE INDEX [IX_WorkflowSteps_WorkflowId] ON [dbo].[WorkflowSteps]([WorkflowId], [Ordinal]);
END;

-- Optional per-step FKs. Null = legacy/project-scoped (Phase 6 and earlier).
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'WorkflowStepId' AND Object_ID = Object_ID('dbo.AttributeMappings'))
BEGIN
    ALTER TABLE [dbo].[AttributeMappings] ADD [WorkflowStepId] UNIQUEIDENTIFIER NULL;
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AttributeMappings_WorkflowStepId' AND object_id = OBJECT_ID('dbo.AttributeMappings'))
BEGIN
    CREATE INDEX [IX_AttributeMappings_WorkflowStepId] ON [dbo].[AttributeMappings]([WorkflowStepId]);
END;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = 'WorkflowStepId' AND Object_ID = Object_ID('dbo.SyncProjectScopes'))
BEGIN
    ALTER TABLE [dbo].[SyncProjectScopes] ADD [WorkflowStepId] UNIQUEIDENTIFIER NULL;
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SyncProjectScopes_WorkflowStepId' AND object_id = OBJECT_ID('dbo.SyncProjectScopes'))
BEGIN
    CREATE INDEX [IX_SyncProjectScopes_WorkflowStepId] ON [dbo].[SyncProjectScopes]([WorkflowStepId]);
END;

-- Backfill: one Default workflow + one Default Mapping step per existing project
-- that doesn't already have one. Re-points existing mappings + scopes at the
-- backfilled step. Idempotent — re-running this migration manually is a no-op.
DECLARE @projId UNIQUEIDENTIFIER;
DECLARE proj_cur CURSOR LOCAL FAST_FORWARD FOR
    SELECT p.Id FROM dbo.SyncProjects p
    WHERE NOT EXISTS (SELECT 1 FROM dbo.Workflows w WHERE w.SyncProjectId = p.Id);
OPEN proj_cur;
FETCH NEXT FROM proj_cur INTO @projId;
WHILE @@FETCH_STATUS = 0
BEGIN
    DECLARE @wfId UNIQUEIDENTIFIER = NEWID();
    DECLARE @stepId UNIQUEIDENTIFIER = NEWID();
    INSERT INTO dbo.Workflows (Id, SyncProjectId, Name, Description, Ordinal, Enabled, CreatedAt, ModifiedAt)
        VALUES (@wfId, @projId, N'Default', N'Backfilled by V17 migration.', 0, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
    INSERT INTO dbo.WorkflowSteps (Id, WorkflowId, Name, StepType, Ordinal, Enabled, Configuration, CreatedAt, ModifiedAt)
        VALUES (@stepId, @wfId, N'Default', N'Mapping', 0, 1, NULL, SYSUTCDATETIME(), SYSUTCDATETIME());
    UPDATE dbo.AttributeMappings   SET WorkflowStepId = @stepId WHERE SyncProjectId = @projId AND WorkflowStepId IS NULL;
    UPDATE dbo.SyncProjectScopes  SET WorkflowStepId = @stepId WHERE SyncProjectId = @projId AND WorkflowStepId IS NULL;
    FETCH NEXT FROM proj_cur INTO @projId;
END;
CLOSE proj_cur;
DEALLOCATE proj_cur;
"
            });

            // Filter migrations that haven't been applied yet
            return migrations.Where(m => m.Version > analysis.CurrentVersion).OrderBy(m => m.Version).ToList();
        }

        /// <summary>
        /// Applies migrations to the database
        /// </summary>
        public async Task<bool> ApplyMigrationsAsync(List<SchemaMigration> migrations)
        {
            if (!migrations.Any())
            {
                _logger.LogInformation("No migrations to apply");
                return true;
            }

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            foreach (var migration in migrations)
            {
                _logger.LogInformation($"Applying migration {migration.Version}: {migration.Name}");
                
                using var transaction = connection.BeginTransaction();
                try
                {
                    // Split by GO statements and execute each batch
                    var batches = migration.SqlScript.Split(new[] { "\nGO\n", "\nGO\r\n", "\r\nGO\r\n", "\r\nGO\n" }, 
                        StringSplitOptions.RemoveEmptyEntries);

                    foreach (var batch in batches)
                    {
                        if (!string.IsNullOrWhiteSpace(batch))
                        {
                            await connection.ExecuteAsync(batch, transaction: transaction);
                        }
                    }

                    // Record the migration
                    await connection.ExecuteAsync(@"
                        INSERT INTO SchemaVersion (Version, AppliedOn, Description)
                        VALUES (@Version, @AppliedOn, @Description)",
                        new { migration.Version, AppliedOn = DateTime.UtcNow, migration.Description },
                        transaction);

                    transaction.Commit();
                    _logger.LogInformation($"Migration {migration.Version} applied successfully");
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    _logger.LogError(ex, $"Error applying migration {migration.Version}: {migration.Name}");
                    throw new Exception($"Migration {migration.Version} failed: {ex.Message}", ex);
                }
            }

            return true;
        }

        /// <summary>
        /// Ensures SchemaVersion table exists
        /// </summary>
        public async Task EnsureSchemaVersionTableAsync()
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            var exists = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'SchemaVersion'") > 0;

            if (!exists)
            {
                await connection.ExecuteAsync(@"
                    CREATE TABLE [dbo].[SchemaVersion] (
                        [Version] INT NOT NULL,
                        [AppliedOn] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                        [Description] NVARCHAR(500) NULL,
                        CONSTRAINT [PK_SchemaVersion] PRIMARY KEY CLUSTERED ([Version])
                    )");

                // Mark initial schema as version 1
                await connection.ExecuteAsync(
                    "INSERT INTO SchemaVersion (Version, Description) VALUES (1, 'Initial schema creation')");
            }
        }
    }

    public class SchemaAnalysisResult
    {
        public HashSet<string> ExistingTables { get; set; } = new();
        public Dictionary<string, HashSet<string>> TableColumns { get; set; } = new();
        public int CurrentVersion { get; set; } = 0;
    }

    public class ColumnInfo
    {
        public string TableName { get; set; } = "";
        public string ColumnName { get; set; } = "";
        public string DataType { get; set; } = "";
        public int? MaxLength { get; set; }
        public string IsNullable { get; set; } = "";
        public string? DefaultValue { get; set; }
    }

    public class SchemaMigration
    {
        public int Version { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string SqlScript { get; set; } = "";
    }
}