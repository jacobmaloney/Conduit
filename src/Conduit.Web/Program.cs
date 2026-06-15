using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.IdentityModel.Tokens;
using Conduit.Core.Services;
using Conduit.DataAccess;
using Conduit.DataAccess.Repositories;
using Conduit.Web;
using Conduit.Web.Authentication;
using Conduit.Web.Services;
using Conduit.Web.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Run as a Windows Service when hosted by the SCM (no-op for console/dev runs).
builder.Host.UseWindowsService();

// Kestrel hardening — defends against slow-loris, body-bomb, and resource-exhaustion attacks.
// These are conservative defaults appropriate for an identity service; raise the body cap
// only if you legitimately push >256KB SCIM payloads (bulk operations will need their own).
builder.WebHost.ConfigureKestrel(opts =>
{
    opts.Limits.MaxConcurrentConnections = 1000;
    opts.Limits.MaxConcurrentUpgradedConnections = 100;
    opts.Limits.MaxRequestBodySize = 256 * 1024;            // 256 KB
    opts.Limits.MaxRequestHeadersTotalSize = 32 * 1024;     // 32 KB
    opts.Limits.MaxRequestLineSize = 8 * 1024;              // 8 KB
    opts.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60);
    opts.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
    opts.Limits.MinRequestBodyDataRate = new MinDataRate(bytesPerSecond: 100, gracePeriod: TimeSpan.FromSeconds(10));
    opts.Limits.MinResponseDataRate = new MinDataRate(bytesPerSecond: 100, gracePeriod: TimeSpan.FromSeconds(10));
    opts.AddServerHeader = false;
});

// Add services to the container
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddControllers()
    .AddNewtonsoftJson(); // Use Newtonsoft.Json for better SCIM compatibility

// Configure database
builder.Services.Configure<DatabaseConfig>(builder.Configuration.GetSection("Database"));
builder.Services.AddSingleton<DatabaseConfig>(sp =>
{
    var config = new DatabaseConfig();
    builder.Configuration.GetSection("Database").Bind(config);
    config.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
    return config;
});

// Data Protection — persist the keyring to disk so secrets encrypted at rest
// (notably OIDC ClientSecrets in IdentityProviders.Configuration) remain decryptable
// across restarts. Without persistence the keyring is per-process and stored secrets
// become unrecoverable on restart. Key directory is overridable via DataProtection:KeyPath.
var dpKeyPath = builder.Configuration["DataProtection:KeyPath"];
if (string.IsNullOrWhiteSpace(dpKeyPath))
{
    dpKeyPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "dp-keys");
}
Directory.CreateDirectory(dpKeyPath);
var dpBuilder = builder.Services.AddDataProtection()
    .SetApplicationName("Conduit")
    .PersistKeysToFileSystem(new DirectoryInfo(dpKeyPath));
// Encrypt the keyring at rest with Windows DPAPI so the stored keys (and thus every
// ClientSecret they protect) aren't readable as plaintext by anyone with file access.
// Windows-only deployment; on a non-Windows run skip DPAPI and keep plain file persistence.
if (OperatingSystem.IsWindows())
{
    dpBuilder.ProtectKeysWithDpapi();
}

// Configure JWT
builder.Services.Configure<JwtConfig>(builder.Configuration.GetSection("Jwt"));
var jwtConfig = new JwtConfig();
builder.Configuration.GetSection("Jwt").Bind(jwtConfig);

// If no JWT secret is configured, generate a random per-process key and log a loud
// warning. Tokens won't survive a restart, which is the correct behavior for an
// unconfigured install — the setup wizard persists a proper secret on first run.
if (string.IsNullOrEmpty(jwtConfig.SecretKey))
{
    var randomBytes = new byte[64];
    System.Security.Cryptography.RandomNumberGenerator.Fill(randomBytes);
    jwtConfig.SecretKey = Convert.ToBase64String(randomBytes);
    Console.Error.WriteLine(
        "WARNING: Jwt:SecretKey is not configured. A random per-process key has been " +
        "generated. Tokens will not survive process restart. Run /setup to persist a key.");
}

// Configure authentication. Browser sessions use a cookie; API requests with a Bearer
// header forward to JWT. The SmartAuth policy scheme picks per-request based on headers,
// so existing [Authorize] controllers continue to validate JWTs unchanged.
const string SmartAuthScheme = "SmartAuth";
var authBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = SmartAuthScheme;
    options.DefaultChallengeScheme = SmartAuthScheme;
})
.AddPolicyScheme(SmartAuthScheme, SmartAuthScheme, options =>
{
    options.ForwardDefaultSelector = context =>
    {
        var authHeader = context.Request.Headers["Authorization"].ToString();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return JwtBearerDefaults.AuthenticationScheme;
        }
        return CookieAuthenticationDefaults.AuthenticationScheme;
    };
})
.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
{
    options.LoginPath = "/login";
    options.LogoutPath = "/logout";
    options.AccessDeniedPath = "/login";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Cookie.Name = "scim.admin";
    options.Cookie.HttpOnly = true;
    // SECURITY: SameSite=Lax (not Strict) is deliberate. Strict withholds the cookie
    // on the cross-site-initiated top-level navigation that follows an OIDC IdP redirect
    // back to our callback, which would bounce a freshly-SSO-authenticated user back to
    // /login once. Lax sends the cookie on top-level GET navigations (the SSO-return
    // shape) while still withholding it on cross-site POST/subresource requests — the
    // CSRF-relevant protection. HttpOnly blocks script access; SecurePolicy is
    // Always-over-HTTPS in production. Keep Lax to preserve the SSO return flow.
    options.Cookie.SameSite = SameSiteMode.Lax;
    // API callers (SCIM, generic REST, SQL emulator, ARS proxy) get a clean 401
    // with WWW-Authenticate: Bearer instead of an HTML login redirect. Browser
    // sessions still get the redirect.
    options.Events.OnRedirectToLogin = ctx =>
    {
        var p = ctx.Request.Path.Value ?? string.Empty;
        if (p.StartsWith("/scim/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/sql/v1/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/ars/v1/", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.Headers["WWW-Authenticate"] = "Bearer";
            return Task.CompletedTask;
        }
        ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = ctx =>
    {
        var p = ctx.Request.Path.Value ?? string.Empty;
        if (p.StartsWith("/scim/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/sql/v1/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/ars/v1/", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
        ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    };
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = jwtConfig.ValidateIssuer,
        ValidateAudience = jwtConfig.ValidateAudience,
        ValidateLifetime = jwtConfig.ValidateLifetime,
        ValidateIssuerSigningKey = jwtConfig.ValidateIssuerSigningKey,
        ValidIssuer = jwtConfig.Issuer,
        ValidAudience = jwtConfig.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtConfig.SecretKey))
    };
});

// Optional external-IdP (SSO): register one OpenIdConnect scheme per ENABLED row in
// the IdentityProviders table. These are ADDITIONAL named schemes — they are NOT the
// default and NOT a fallback, so the SmartAuth Bearer→JWT / else→Cookie selector and
// the SCIM/JWT API surface are untouched. OIDC is only ever entered via an explicit
// challenge on /login/sso. A missing/empty table just logs and continues (local login).
// Restart-required model after enabling/changing a provider (no hot reload) — acceptable.
{
    using var ssoLoggerFactory = LoggerFactory.Create(lb => lb.AddConsole());
    var ssoLogger = ssoLoggerFactory.CreateLogger("SSO.Registrar");
    // Build a standalone Data Protection provider over the SAME persisted key directory
    // and application name as the app's keyring, so secrets encrypted by the running app
    // can be decrypted here at startup. Avoids BuildServiceProvider (no duplicate-singleton
    // warning) while sharing the identical key material.
    var ssoProtector = new Conduit.Web.Authentication.DataProtectionSecretProtector(
        DataProtectionProvider.Create(new DirectoryInfo(dpKeyPath),
            configure =>
            {
                configure.SetApplicationName("Conduit");
                // Must match the app's keyring protection (above) so this standalone provider
                // can decrypt the now-DPAPI-encrypted keyring and unprotect stored ClientSecrets.
                if (OperatingSystem.IsWindows())
                {
                    configure.ProtectKeysWithDpapi();
                }
            }));
    authBuilder.AddDynamicExternalProviders(ssoProtector, builder.Configuration, ssoLogger);
}

// NOTE: No FallbackPolicy here. A fallback "require auth" policy also applies to /_blazor
// (the Blazor Server SignalR hub), which would break the interactive circuit on anonymous
// pages like /setup and /login. Each Blazor page uses @attribute [Authorize] explicitly;
// SCIM API controllers use [Authorize] explicitly.
builder.Services.AddAuthorization();

// Register services
builder.Services.AddTransient<DatabaseInitializer>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddScoped<TenantRepository>();
builder.Services.AddScoped<UserRepository>();
builder.Services.AddScoped<GroupRepository>();
builder.Services.AddScoped<TenantSnapshotService>();
builder.Services.AddScoped<PortalAdminRepository>();
builder.Services.AddScoped<ApiTokenRepository>();
builder.Services.AddScoped<AuditLogRepository>();
builder.Services.AddScoped<LoginThrottleRepository>();
builder.Services.AddScoped<SystemConfigurationRepository>();
builder.Services.AddScoped<SetupRepository>();
builder.Services.AddScoped<SqlAccountRepository>();
builder.Services.AddScoped<EmulatorSinkRepository>();
// Optional external-IdP (SSO) support. Repository + secret-at-rest protector.
// SSO is OPTIONAL — zero enabled providers means local login only.
builder.Services.AddScoped<IdentityProviderRepository>();
builder.Services.AddSingleton<Conduit.Web.Authentication.ISecretProtector,
    Conduit.Web.Authentication.DataProtectionSecretProtector>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<ApiTokenService>();
builder.Services.AddScoped<AuditLogService>();
builder.Services.AddScoped<SetupService>();
builder.Services.AddScoped<SystemConfigurationService>();
builder.Services.AddScoped<DemoSeedService>();
builder.Services.AddScoped<ActiveSystemState>();
builder.Services.AddScoped<LoginService>();
builder.Services.AddScoped<Conduit.Core.Services.UserGenerationService>();
builder.Services.AddSingleton<ApplicationLogService>();
builder.Services.AddSingleton<GenerationService>();
builder.Services.AddSingleton<DataChangeNotifier>();
// LoginThrottle reads/writes the DB on every check — Scoped so it can take the
// DatabaseConfig the rest of the data layer uses. Pruner is a hosted background
// service that keeps the throttle tables bounded.
builder.Services.AddScoped<LoginThrottle>();
builder.Services.AddHostedService<LoginThrottlePruner>();

// Portal-only open-access mode (demo / local testing). Singleton holds the
// in-process cache so [Authorize] doesn't take a DB hit per request.
builder.Services.AddSingleton<OpenAccessState>();
builder.Services.AddHostedService<StartupService>();

// Resilient DB startup: bounded retry-with-backoff inline (handles a brief blip), then a
// background self-heal service that keeps retrying if the DB is still down at boot so the
// app recovers without a manual restart. The hosted service is a no-op unless the inline
// retry flips the signal (so zero cost on the happy path / on a fatal auth-or-schema error).
builder.Services.AddSingleton(StartupRetryOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<DatabaseSelfHealSignal>();
builder.Services.AddHostedService<DatabaseSelfHealService>();

// CORS is driven by Cors:AllowedOrigins in configuration. If no origins are
// configured, the policy allows nothing cross-origin — which is the right default
// for an identity service. Same-origin browser sessions and SCIM clients with
// Bearer tokens are unaffected by CORS.
var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("SCIMPolicy", policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .WithExposedHeaders("Location");
        }
        // else: empty policy — no cross-origin requests permitted
    });
});

// Rate limiting — three policies + a global per-IP fallback.
//
//   "auth"     — login and token-mint endpoints. Slow on purpose; brute-force resistance
//                comes from the LoginService lockout AND this bucket.
//   "scim"     — bearer-token-authenticated API surface. Per-token bucket; large enough
//                for real provisioners but cheap to cap a runaway client.
//   "anon"     — anonymous discovery endpoints (ServiceProviderConfig, Schemas, etc).
//   "global"   — final per-IP guardrail so an attacker hitting unauthenticated routes
//                can't tie up all available connections.
//
// Limits are tunable from configuration via the RateLimits:* keys (see appsettings.json).
builder.Services.AddRateLimiter(opts =>
{
    opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    opts.OnRejected = async (ctx, token) =>
    {
        if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            ctx.HttpContext.Response.Headers["Retry-After"] =
                ((int)retryAfter.TotalSeconds).ToString();
        }
        ctx.HttpContext.Response.ContentType = "application/json";
        await ctx.HttpContext.Response.WriteAsync(
            "{\"error\":\"rate_limited\",\"detail\":\"Too many requests. Slow down and retry.\"}",
            cancellationToken: token);
    };

    opts.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }));

    opts.AddPolicy("scim", httpContext =>
    {
        // Partition by token if present, otherwise by IP — so one shared IP across
        // many tokens doesn't starve everyone.
        var auth = httpContext.Request.Headers["Authorization"].ToString();
        var key = !string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer scim_", StringComparison.OrdinalIgnoreCase)
            ? "tok:" + auth[..Math.Min(auth.Length, 24)]    // token-prefix only — never log the full value
            : "ip:" + (httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        return RateLimitPartition.GetTokenBucketLimiter(
            partitionKey: key,
            factory: _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 200,
                TokensPerPeriod = 100,
                ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                QueueLimit = 0,
                AutoReplenishment = true,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });
    });

    opts.AddPolicy("anon", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    opts.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        // The SSO OIDC callback (/signin-oidc/*) is handled inside UseAuthentication, before
        // endpoint routing resolves an endpoint, so the SsoController's [EnableRateLimiting("auth")]
        // attribute never attaches to it. Bound the callback here at the global layer with the
        // SAME strict per-IP window local login uses, so an attacker can't hammer the token
        // exchange + OnTokenValidated gate. /login/sso (the challenge) is additionally bounded by
        // the controller attribute. Everything else (SCIM, /api, etc.) keeps the 600/min guardrail.
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var path = httpContext.Request.Path.Value ?? string.Empty;
        var isSso = path.StartsWith("/signin-oidc", StringComparison.OrdinalIgnoreCase)
                 || path.StartsWith("/login/sso", StringComparison.OrdinalIgnoreCase);
        if (isSso)
        {
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: "sso:" + ip,
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                });
        }
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ip,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 600,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });
});

// Conduit lightweight scheduler
builder.Services.AddSingleton<Conduit.Scheduling.IScheduledJob, Conduit.Scheduling.Jobs.ConnectorHealthCheckJob>();

// Sync engine — repositories, orchestrator, connector adapters, credential protector.
builder.Services.AddScoped<Conduit.DataAccess.Repositories.SyncProjectRepository>();
builder.Services.AddScoped<Conduit.DataAccess.Repositories.SyncRunRepository>();
builder.Services.AddScoped<Conduit.DataAccess.Repositories.SyncRunAsyncJobRepository>();
builder.Services.AddScoped<Conduit.DataAccess.Repositories.WorkflowRepository>();
builder.Services.AddScoped<Conduit.DataAccess.Repositories.SinkRecordHashRepository>();
builder.Services.AddScoped<Conduit.DataAccess.Repositories.SinkConnectionCredentialMapRepository>();
builder.Services.AddScoped<Conduit.DataAccess.Repositories.ConnectionCredentialRepository>();
builder.Services.AddScoped<Conduit.Sync.Security.CredentialProtector>();
builder.Services.AddScoped<Conduit.Sync.Connectors.IConnectorAdapter, Conduit.Connectors.ActiveDirectory.ActiveDirectoryAdapter>();
builder.Services.AddScoped<Conduit.Sync.Connectors.IConnectorAdapter, Conduit.Connectors.Emulator.EmulatorAdapter>();
// Phase 1.5 connectors
builder.Services.AddScoped<Conduit.Sync.Connectors.IConnectorAdapter, Conduit.Connectors.EntraID.EntraIDAdapter>();
builder.Services.AddScoped<Conduit.Sync.Connectors.IConnectorAdapter, Conduit.Connectors.Okta.OktaAdapter>();
builder.Services.AddScoped<Conduit.Sync.Connectors.IConnectorAdapter, Conduit.Connectors.GoogleWorkspace.GoogleWorkspaceAdapter>();
builder.Services.AddScoped<Conduit.Sync.Connectors.IConnectorAdapter, Conduit.Connectors.GenericLdap.GenericLdapAdapter>();
builder.Services.AddScoped<Conduit.Sync.Connectors.IConnectorAdapter, Conduit.Connectors.Scim.ScimAdapter>();
builder.Services.AddScoped<Conduit.Sync.Connectors.IConnectorAdapter, Conduit.Connectors.Database.DatabaseAdapter>();
builder.Services.AddScoped<Conduit.Sync.Connectors.IConnectorAdapter, Conduit.Connectors.Csv.CsvAdapter>();
builder.Services.AddScoped<Conduit.Sync.Connectors.IConnectorAdapter, Conduit.Connectors.Aws.AwsAdapter>();
builder.Services.AddScoped<Conduit.Sync.Connectors.IConnectorAdapter, Conduit.Connectors.Aws.AwsSsoAdapter>();
builder.Services.AddScoped<Conduit.Sync.Connectors.IConnectorAdapter, Conduit.Connectors.SharePoint.SharePointAdapter>();
// Phase 2 — IdentityCenter as a Conduit connector (paired with IC's /api/objects/{query,bulk}).
builder.Services.AddScoped<Conduit.Sync.Connectors.IConnectorAdapter, Conduit.Connectors.IdentityCenter.IdentityCenterAdapter>();
// SQL Discovery — source-only license-inventory scanner (SPN/instance-list enumeration → IC sink).
builder.Services.AddScoped<Conduit.Sync.Connectors.IConnectorAdapter, Conduit.Connectors.SqlDiscovery.SqlDiscoveryAdapter>();
// Active Roles — writes route THROUGH the AR Administration Service (EDMS://) so
// policies/workflows/virtual-attributes fire (e.g. SoD denies a toxic role pairing
// mid-sync). Requires the AR ADSI provider on the host that runs the connector.
builder.Services.AddScoped<Conduit.Sync.Connectors.IConnectorAdapter, Conduit.Connectors.ActiveRoles.ActiveRolesAdapter>();
// Azure Resource Graph — source-only cloud-inventory reader (subscriptions + resources
// via the ARG KQL endpoint over raw HTTP; surfaces Azure Hybrid Benefit markers).
builder.Services.AddScoped<Conduit.Sync.Connectors.IConnectorAdapter, Conduit.Connectors.AzureResourceGraph.AzureResourceGraphAdapter>();
builder.Services.AddScoped<Conduit.Sync.Connectors.ConnectorRegistry>();
// Singleton: shared across the controller's fire-and-forget Run-Now task, the
// scheduler, and every Blazor circuit so "Stop Sync" can trip the in-flight run.
builder.Services.AddSingleton<Conduit.Sync.Orchestration.SyncCancellationRegistry>();
builder.Services.AddScoped<Conduit.Sync.Orchestration.SyncProjectOrchestrator>();
// Phase 2 — per-connector attribute template catalog + canonical source→sink resolver.
builder.Services.AddSingleton<Conduit.Sync.Templates.IAttributeMapService, Conduit.Sync.Templates.AttributeMapService>();
// Phase 3 — per-connector object-class sets → in-memory sync-project graph generator.
builder.Services.AddSingleton<Conduit.Sync.Templates.ISyncProjectGenerator, Conduit.Sync.Templates.SyncProjectGenerator>();

// Scheduled sync runner is Scoped-by-dependency but the scheduler instantiates
// IScheduledJob singletons; wire it as a transient that resolves its scoped deps
// from a per-execute scope. Simpler approach: register as singleton with an
// IServiceScopeFactory shim — but for Phase 1A we instead register it as singleton
// directly because all its dependencies (repos, orchestrator) are also stateless
// per-call. SqlConnection is created per BaseRepository call.
builder.Services.AddSingleton<Conduit.Scheduling.IScheduledJob>(sp =>
{
    // Build a scope on each tick by capturing the IServiceScopeFactory. The job
    // ExecuteAsync method runs sync work which itself opens DB connections.
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    return new ScheduledJobScopedWrapper(scopeFactory,
        s => new Conduit.Sync.Orchestration.ScheduledSyncRunnerJob(
            s.GetRequiredService<Conduit.DataAccess.Repositories.SyncProjectRepository>(),
            // Fix 5: the job receives the scope FACTORY, not a scoped orchestrator.
            // Fired runs outlive the tick scope (the wrapper disposes it as soon as
            // the tick returns), so each run creates + owns + disposes its own scope
            // and resolves a fresh orchestrator inside it.
            s.GetRequiredService<IServiceScopeFactory>(),
            s.GetRequiredService<ILogger<Conduit.Sync.Orchestration.ScheduledSyncRunnerJob>>()));
});

// Phase 4: AsyncJobPollerService — advances SyncRunAsyncJobs rows out-of-band.
// Registered through the same scoped wrapper so per-tick repositories are fresh.
builder.Services.AddSingleton<Conduit.Scheduling.IScheduledJob>(sp =>
{
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    return new ScheduledJobScopedWrapper(scopeFactory,
        s => new Conduit.Sync.Orchestration.AsyncJobPollerService(
            s.GetRequiredService<Conduit.DataAccess.Repositories.SyncRunAsyncJobRepository>(),
            s.GetRequiredService<Conduit.DataAccess.Repositories.SyncRunRepository>(),
            s.GetRequiredService<Conduit.Sync.Connectors.ConnectorRegistry>(),
            s.GetRequiredService<ILogger<Conduit.Sync.Orchestration.AsyncJobPollerService>>()));
});

builder.Services.AddHostedService<Conduit.Scheduling.SchedulerService>();

// Outbound-only "Scan now": polls IC's agent-command API (when an IdentityCenter
// connection is configured) and triggers Run-Now of the SQL Discovery project.
// 404 from IC = feature not deployed yet — polled quietly, never crashes the host.
// Hardened: 1 MB response cap, and no auto-redirect — X-API-Key is a default
// request header, so following a 302 would replay the key to another host.
builder.Services.AddHttpClient("IcAgentCommandPoller")
    .ConfigureHttpClient(c => c.MaxResponseContentBufferSize = 1024 * 1024)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
// ApplyObjectWrite executor — validates + allow-lists an IC-routed AD write and
// drives the AD sink. Scoped: it resolves the scoped connector adapters + tenant
// repo + credential protector. The poller resolves it per-command from its scope.
builder.Services.AddScoped<Conduit.Web.Services.AdAgentWriteExecutor>();
// ApplySqlWrite executor — validates + allow-lists an IC-routed SQL security DDL
// change (CREATE/DROP user/login, role membership) and runs it through parameterized
// QUOTENAME dynamic SQL with the per-connection scan credential. Scoped: it resolves
// the scoped CredentialProtector. The poller resolves it per-command from its scope.
builder.Services.AddScoped<Conduit.Web.Services.SqlAgentWriteExecutor>();
// ApplyAwsWrite executor — validates + allow-lists an IC-routed AWS IAM change
// (tag, group membership, managed-policy attach/detach, access-key status, console-
// access removal) and drives AwsIamWriter; ALL AWS SDK calls stay in the connector.
// Scoped: it resolves the scoped CredentialProtector. The poller resolves it per-
// command from its scope.
builder.Services.AddScoped<Conduit.Web.Services.AwsAgentWriteExecutor>();
// Enrollment/heartbeat status shared between the poller (writer) and the Configuration page (reader).
builder.Services.AddSingleton<Conduit.Web.Services.IcAgentStatusService>();
builder.Services.AddHostedService<Conduit.Web.Services.IcAgentCommandPollerService>();

var app = builder.Build();

// Check if setup is required before initializing database
using (var scope = app.Services.CreateScope())
{
    var setupService = scope.ServiceProvider.GetRequiredService<SetupService>();
    bool setupRequired;
    try
    {
        setupRequired = await setupService.IsSetupRequiredAsync();
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(ex, "Could not check setup status, assuming setup required");
        setupRequired = true;
    }

    // Only initialize database if setup is complete. If this fails (e.g. the server
    // moved or credentials changed since last run), do NOT crash startup — the user
    // needs the app running to reach /setup and fix the connection.
    if (!setupRequired)
    {
        var dbInitializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        var retryOptions = app.Services.GetRequiredService<StartupRetryOptions>();

        // Bounded inline retry-with-backoff. Retries ONLY connectivity-class failures; a fatal
        // (auth/permission/schema) error surfaces immediately and is NOT retried.
        var initResult = await DatabaseStartup.TryInitializeWithRetryAsync(dbInitializer, retryOptions, logger);

        if (initResult == StartupInitResult.StillUnreachable)
        {
            // DB down at boot. Do NOT crash and do NOT block — start the host so /db-offline can
            // serve, and arm the background self-heal service to keep retrying until the DB returns.
            app.Services.GetRequiredService<DatabaseSelfHealSignal>().Request();
            SetupMiddleware.ClearCache();
        }
        else if (initResult == StartupInitResult.Fatal)
        {
            // Misconfiguration that will not fix itself by waiting. Already logged as ERROR.
            // Fall through to setup/error routing rather than self-healing forever.
            SetupMiddleware.ClearCache();
        }

        // Load Portal.OpenAccess from SystemConfigurations into the singleton cache. Skipped
        // when the DB is unreachable (the self-heal service warms it on success instead).
        if (initResult == StartupInitResult.Initialized)
        {
            try { await app.Services.GetRequiredService<OpenAccessState>().InitializeAsync(); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not load Portal.OpenAccess at startup; defaulting to disabled.");
            }
        }
    }
}

// Configure the HTTP request pipeline.
//
// Pipeline order matters — security middleware runs before the routes that need
// protecting. Specifically:
//   1. Production error handler (no stack-trace leakage)
//   2. HSTS + HTTPS redirect (only when HTTPS is actually configured)
//   3. Security headers (HSTS, X-Frame-Options, X-Content-Type-Options, CSP, …)
//   4. Static files (gets the headers too)
//   5. Routing (resolves the endpoint)
//   6. Setup check (block requests until /setup is complete)
//   7. CORS (after routing so per-endpoint CORS can work)
//   8. Rate limiter (block abusive traffic before we do any DB work)
//   9. Token + authentication
//  10. Authorization
//  11. SCIM connection logger
if (!app.Environment.IsDevelopment())
{
    // Two-track exception handler:
    //   - HTML routes get the /Error page (generic, log-correlation Request ID).
    //   - API routes (/scim, /api, /sql, /ars, /health) get a JSON envelope with
    //     the same correlation id and no stack-trace details.
    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            var requestId = System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier;
            var path = context.Request.Path.Value ?? string.Empty;
            var isApi = path.StartsWith("/scim/", StringComparison.OrdinalIgnoreCase)
                     || path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                     || path.StartsWith("/sql/", StringComparison.OrdinalIgnoreCase)
                     || path.StartsWith("/ars/", StringComparison.OrdinalIgnoreCase)
                     || path.StartsWith("/health", StringComparison.OrdinalIgnoreCase);

            // The ExceptionHandlerPathFeature carries the original exception; log it
            // but never let it reach the wire.
            var ex = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>()?.Error;
            if (ex is not null)
            {
                var log = context.RequestServices.GetRequiredService<ILogger<Program>>();
                log.LogError(ex, "Unhandled exception on {Path} (request {RequestId})", path, requestId);
            }

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            if (isApi)
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    $"{{\"error\":\"internal_error\",\"detail\":\"An unexpected error occurred. Reference id {requestId} in your logs.\",\"requestId\":\"{requestId}\"}}");
            }
            else
            {
                context.Response.Redirect("/Error?requestId=" + Uri.EscapeDataString(requestId));
            }
        });
    });
    app.UseHsts();
}

// Only redirect to HTTPS when we actually have an HTTPS port to redirect to —
// avoids the "Failed to determine the https port for redirect" warning in HTTP-only
// dev runs while still enforcing HTTPS in any real deployment.
var httpsPort = app.Configuration["HTTPS_PORT"]
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_HTTPS_PORT")
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_HTTPS_PORTS");
var hasHttpsUrl = (Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "").Contains("https://");
if (!string.IsNullOrEmpty(httpsPort) || hasHttpsUrl || !app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseStaticFiles();
app.UseRouting();

// Check if setup is required before processing requests
app.UseSetupCheck();

app.UseCors("SCIMPolicy");

// Rate limiting MUST come before auth so that a bad token doesn't drain the bucket
// by spinning ApiTokenAuthMiddleware repeatedly.
app.UseRateLimiter();

// Authenticate scim_ API tokens before the JWT handler runs
app.UseMiddleware<ApiTokenAuthMiddleware>();

app.UseAuthentication();

// If portal open-access mode is on, swap in a synthetic admin principal for
// any portal request that's not already signed in. Runs AFTER UseAuthentication
// so a real cookie still wins.
app.UseMiddleware<OpenAccessSignInMiddleware>();

app.UseAuthorization();

// Add SCIM connection logging middleware
app.UseMiddleware<ScimConnectionLoggingMiddleware>();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");
app.MapControllers();
// Razor Pages — /login and /logout. Apply the auth bucket so the login form
// itself sits behind a per-IP brute-force limiter on top of the in-LoginService
// per-(user,IP) throttle.
app.MapRazorPages().RequireRateLimiting("auth");

// Add a simple health check endpoint. Includes Portal.OpenAccess so a monitor
// can spot when the demo mode is left on.
app.MapGet("/health", (OpenAccessState openAccess) => Results.Ok(new
{
    status = "healthy",
    timestamp = DateTime.UtcNow,
    portalOpenAccess = openAccess.IsEnabled
}));

app.Run();
