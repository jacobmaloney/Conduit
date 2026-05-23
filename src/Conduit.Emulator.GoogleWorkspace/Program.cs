using Microsoft.AspNetCore.Authentication;
using Newtonsoft.Json;
using Conduit.DataAccess;
using Conduit.Emulator.GoogleWorkspace.Auth;
using Conduit.Emulator.GoogleWorkspace.Infrastructure;
using Conduit.Emulator.GoogleWorkspace.Repositories;
using Conduit.Emulator.GoogleWorkspace.Seed;

var builder = WebApplication.CreateBuilder(args);

// --- Config -----------------------------------------------------------------
builder.Services.Configure<GoogleWorkspaceOptions>(builder.Configuration.GetSection("GoogleWorkspace"));

// Shared SQL DB with the SCIM side.
builder.Services.AddSingleton<DatabaseConfig>(_ =>
{
    var cfg = new DatabaseConfig();
    builder.Configuration.GetSection("Database").Bind(cfg);
    cfg.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
    return cfg;
});

// --- Controllers + JSON -----------------------------------------------------
builder.Services.AddControllers()
    .AddNewtonsoftJson(o =>
    {
        o.SerializerSettings.NullValueHandling = NullValueHandling.Ignore;
        o.SerializerSettings.DateFormatHandling = DateFormatHandling.IsoDateFormat;
        o.SerializerSettings.DateTimeZoneHandling = DateTimeZoneHandling.Utc;
    });

// --- Data & infra -----------------------------------------------------------
builder.Services.AddSingleton<GwDatabaseInitializer>();
builder.Services.AddScoped<GwUserRepository>();
builder.Services.AddScoped<GwGroupRepository>();
builder.Services.AddScoped<GwMemberRepository>();
builder.Services.AddScoped<GwCustomerRepository>();
builder.Services.AddScoped<GwDomainRepository>();
builder.Services.AddScoped<GwOrgUnitRepository>();
builder.Services.AddScoped<GwAliasRepository>();

// --- Auth -------------------------------------------------------------------
builder.Services.AddScoped<ServiceAccountStore>();
builder.Services.AddScoped<ServiceAccountValidator>();

builder.Services.AddAuthentication(GoogleBearerDefaults.Scheme)
    .AddScheme<GoogleBearerOptions, GoogleBearerHandler>(GoogleBearerDefaults.Scheme, _ => { });
builder.Services.AddAuthorization();

// --- Seed + schema (hosted service applies schema then seeds if empty) ------
builder.Services.AddScoped<TenantSeeder>();
builder.Services.AddHostedService<TenantSeederHostedService>();

// --- CORS -------------------------------------------------------------------
builder.Services.AddCors(options =>
{
    options.AddPolicy("GoogleEmulator", policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

app.UseRouting();
app.UseCors("GoogleEmulator");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// --- Discovery-ish endpoints ------------------------------------------------
app.MapGet("/", () => Results.Ok(new
{
    service = "Conduit.Emulator.GoogleWorkspace",
    kind = "emulator",
    note = "Google Admin SDK Directory API v1 emulator. Use /oauth2/v4/token then /admin/directory/v1/*.",
    serviceAccounts = "/emulator/service-accounts"
}));

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.Run();
