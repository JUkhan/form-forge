using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using FluentValidation;
using FormForge.Api.Common.Endpoints;
using FormForge.Api.Common.Endpoints.EndpointFilters;
using FormForge.Api.Common.Logging;
using FormForge.Api.Common.Security;
using FormForge.Api.Common.Spa;
using FormForge.Api.Common.OpenApi;
using FormForge.Api.Features.Audit;
using FormForge.Api.Features.Auth;
using FormForge.Api.Features.Auth.Dtos;
using FormForge.Api.Features.Auth.Validators;
using FormForge.Api.Features.Datasets;
using FormForge.Api.Features.Datasets.Dtos;
using FormForge.Api.Features.Datasets.Validators;
using FormForge.Api.Features.Designer;
using FormForge.Api.Features.Files;
using FormForge.Api.Features.Menus;
using FormForge.Api.Features.Menus.Dtos;
using FormForge.Api.Features.Menus.Validators;
using FormForge.Api.Features.Provisioning;
using FormForge.Api.Features.SchemaRegistry;
using FormForge.Api.Features.Designer.Dtos;
using FormForge.Api.Features.Designer.Validators;
using FormForge.Api.Features.DynamicCrud;
using FormForge.Api.Features.Permissions;
using FormForge.Api.Features.Roles;
using FormForge.Api.Features.Roles.Dtos;
using FormForge.Api.Features.Roles.Validators;
using FormForge.Api.Features.Users;
using FormForge.Api.Features.Users.Dtos;
using FormForge.Api.Features.Users.Validators;
using FormForge.Api.Infrastructure.EventBus;
using FormForge.Api.Infrastructure.HealthChecks;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

// QuestPDF requires a license acknowledgement before any document renders.
// "Community" is free for OSS / internal-tooling use; switch to Professional
// if this is ever bundled into a paid SaaS product.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Custom OTel meters — extend the pipeline set up by AddServiceDefaults().
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(AuthMetrics.MeterName));

builder.Services.AddHealthChecks()
    .AddNpgSql(
        connectionStringFactory: sp =>
            builder.Configuration.GetConnectionString("formforge")
            ?? throw new InvalidOperationException("Missing ConnectionStrings:formforge"),
        name: "postgres",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"])
    .Add(new HealthCheckRegistration(
        name: "minio",
        factory: sp => new MinioHealthCheck(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IConfiguration>()),
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"]));

builder.Services.AddHttpClient("minio-health")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
    });

builder.Services.AddSingleton<IHealthCheckPublisher, LoggingHealthCheckPublisher>();
builder.Services.Configure<HealthCheckPublisherOptions>(options =>
{
    options.Delay = TimeSpan.FromSeconds(30);
    options.Period = TimeSpan.FromSeconds(30);
});

builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    options.UseUtcTimestamp = true;
    options.JsonWriterOptions = new JsonWriterOptions { Indented = false };
});

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = ctx =>
    {
        var corr = ctx.HttpContext.GetCorrelationId();
        if (!string.IsNullOrEmpty(corr))
        {
            ctx.ProblemDetails.Extensions["correlationId"] = corr;
        }
    };
});

builder.Services.AddDbContext<FormForgeDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("formforge")));

builder.Services.AddMemoryCache();

// Story 2.13 — Data Protection backs IMfaService's IDataProtector for at-rest
// TOTP secret encryption (AR-56). ASP.NET Core does not auto-register Data
// Protection for JWT-only apps, so it must be added explicitly.
// Keys are persisted to the data_protection_keys table via EF Core so they
// survive container restarts and scale-out without invalidating stored secrets.
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<FormForgeDbContext>();

// Story 6.1 — register the DynamicRecord JSON converter so /api/data list
// responses serialize system columns as camelCase and user fieldKeys verbatim
// (AR-46 Option C). The converter is type-specific to DynamicRecord and does
// not affect any other response shape.
builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.Converters.Add(new FormForge.Api.Features.DynamicCrud.DynamicRecordJsonConverter());
});

// JWT options
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));

// Auth services
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
builder.Services.AddSingleton<AuthMetrics>();

// Email service (Story 2.10 — welcome email on user creation, AR-53). The service
// holds only config + logger, so a singleton is safe; it news up a per-send
// SmtpClient internally (SmtpClient is not thread-safe).
builder.Services
    .AddOptions<SmtpOptions>()
    .Bind(builder.Configuration.GetSection(SmtpOptions.SectionName))
    .Validate(
        opts => string.IsNullOrEmpty(opts.Host) || !string.IsNullOrEmpty(opts.From),
        "Smtp__From must not be empty when Smtp__Host is configured.")
    .ValidateOnStart();
builder.Services.AddSingleton<IEmailService, MailKitEmailService>();

// FluentValidation
builder.Services.AddScoped<IValidator<LoginRequest>, LoginRequestValidator>();
// Story 2.11 — forgot/reset password request validators.
builder.Services.AddScoped<IValidator<ForgotPasswordRequest>, ForgotPasswordRequestValidator>();
builder.Services.AddScoped<IValidator<ResetPasswordRequest>, ResetPasswordRequestValidator>();
builder.Services.AddScoped(typeof(ValidationFilter<>));

// Role services (Story 2.4)
builder.Services.AddScoped<IRoleService, RoleService>();
builder.Services.AddScoped<IValidator<CreateRoleRequest>, CreateRoleRequestValidator>();
builder.Services.AddScoped<IValidator<UpdateRoleRequest>, UpdateRoleRequestValidator>();

// User services (Story 2.5)
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IValidator<AssignRolesRequest>, AssignRolesRequestValidator>();
// Admin user CRUD (Story 2.8)
builder.Services.AddScoped<IValidator<CreateUserRequest>, CreateUserRequestValidator>();
builder.Services.AddScoped<IValidator<UpdateUserRequest>, UpdateUserRequestValidator>();
// Story 7.3 — PUT /me/preferences
builder.Services.AddScoped<IValidator<UpdateMyPreferencesRequest>, UpdateMyPreferencesRequestValidator>();
// Story 2.12 — PUT /me/password (authenticated self password change).
builder.Services.AddScoped<IValidator<ChangePasswordRequest>, ChangePasswordRequestValidator>();
// Story 2.13 — TOTP MFA enrolment.
builder.Services.AddScoped<IValidator<MfaVerifyRequest>, MfaVerifyRequestValidator>();
builder.Services.AddScoped<IMfaService, MfaService>();
// Story 2.14 — TOTP MFA verification on login.
builder.Services.AddScoped<IValidator<MfaVerifyLoginRequest>, MfaVerifyLoginRequestValidator>();

// Permission infrastructure (Story 2.6) — Singletons. PermissionService subscribes
// to domain events in its constructor, so it must outlive any single request.
builder.Services.AddSingleton<IDomainEventBus, InProcessEventBus>();
builder.Services.AddSingleton<IPermissionService, PermissionService>();

// Provisioning pipeline (Story 5.2 — Decision 5.2: Channel + single-consumer BackgroundService).
// Bounded capacity 256: writes block briefly when full, which is acceptable for an admin-only
// action. WriteAsync (not TryWrite) — silent drops would lose binds. The reader/writer are
// resolved as singletons because the Channel itself must outlive every request, and the
// BackgroundService is a singleton too. MenuService injects IProvisioningService and uses
// the writer; ProvisioningBackgroundService injects the reader. Registered BEFORE IMenuService
// so the dependency order reads top-down (DI itself resolves lazily — order doesn't matter).
var provisioningChannel = System.Threading.Channels.Channel.CreateBounded<ProvisioningJob>(256);
builder.Services.AddSingleton(provisioningChannel.Reader);
builder.Services.AddSingleton(provisioningChannel.Writer);
builder.Services.AddSingleton<IProvisioningService, ProvisioningService>();
builder.Services.AddScoped<BindingDiffService>();
builder.Services.AddHostedService<ProvisioningBackgroundService>();
// Story 5.8 — startup recovery: re-enqueues any menus left Pending by a prior
// process crash or the Dapper-EF dual-write hazard. Registered after the consumer
// so the BackgroundService is already listening on the channel when recovery
// writes (the channel buffers regardless, but this order reads cleaner).
builder.Services.AddHostedService<ProvisioningRecoveryService>();

// Story 5.3 — Dapper-backed dynamic-schema DDL. DbConnectionFactory wraps raw
// NpgsqlConnection (singleton: holds no per-request state beyond the config).
// SchemaRegistry caches (designerId, version) → ColumnDefinition[] for Epic 6
// CRUD. DdlEmitter is scoped because it injects FormForgeDbContext which is
// scoped; ProvisioningBackgroundService resolves it via per-job IServiceScope.
builder.Services.AddSingleton<DbConnectionFactory>();
builder.Services.AddSingleton<ISchemaRegistry, SchemaRegistry>();
builder.Services.AddScoped<DdlEmitter>();
// "Table Provisioned" admin tab — provision/sync a CRUD designer's table without a
// menu binding. Scoped (injects FormForgeDbContext + CycleDetector, both scoped).
builder.Services.AddScoped<TableProvisioningService>();
// Story 6.3 — Layer 2 dynamic payload validator (AR-20 / Decision 3.3).
builder.Services.AddScoped<IDynamicPayloadValidator, DynamicPayloadValidator>();
// Story 5.5 — pre-bind Repeater cycle detector. Scoped (injects FormForgeDbContext
// which is scoped); resolved by MenuService.BindDesignerAsync at request time.
builder.Services.AddScoped<CycleDetector>();

// Menu services (Story 4.1)
builder.Services.AddScoped<IMenuService, MenuService>();
// Story 4.7 — real 5 s TTL navbar cache. Singleton so the shared CancellationTokenSource
// generation token is consistent across requests; IMemoryCache itself is thread-safe.
builder.Services.AddSingleton<IMenuCache, MenuCache>();
builder.Services.AddScoped<IValidator<CreateMenuRequest>, CreateMenuRequestValidator>();
builder.Services.AddScoped<IValidator<UpdateMenuRequest>, UpdateMenuRequestValidator>();
builder.Services.AddScoped<IValidator<AssignMenuRolesRequest>, AssignMenuRolesRequestValidator>();
builder.Services.AddScoped<IValidator<ReorderMenusRequest>, ReorderMenusRequestValidator>();
builder.Services.AddScoped<IValidator<ToggleMenuActiveRequest>, ToggleMenuActiveRequestValidator>();
builder.Services.AddScoped<IValidator<BindMenuDesignerRequest>, BindMenuDesignerRequestValidator>();
builder.Services.AddScoped<IValidator<SetMenuRoutePathRequest>, SetMenuRoutePathRequestValidator>();
// Icon storage (Story 4.3) — singleton so the lazy bucket-existence check runs once,
// not per request. The Minio SDK client is thread-safe.
builder.Services.AddSingleton<IIconStorageService, MinioIconStorageService>();

// Designer services (Story 3.2)
builder.Services.AddScoped<IDesignerService, DesignerService>();
// Story 5.6 — admin drift view: inspect + drop orphaned columns on provisioned tables.
builder.Services.AddScoped<SchemaDriftService>();
// Admin UNIQUE-constraint management for provisioned CRUD tables.
builder.Services.AddScoped<UniqueConstraintService>();
// Story 5.7 — schema audit log view: paginated DDL history per designer.
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<IValidator<CreateDesignerRequest>, CreateDesignerRequestValidator>();
builder.Services.AddScoped<IValidator<SaveVersionRequest>, SaveVersionRequestValidator>();
builder.Services.AddScoped<IValidator<UpdateVersionRequest>, UpdateVersionRequestValidator>();
builder.Services.AddScoped<IValidator<UpdateVersionStatusRequest>, UpdateVersionStatusRequestValidator>();
// Story 8.3 — dataset_name validators. Registered explicitly (the codebase wires
// validators per-type rather than by assembly scan). NOTE: the FluentValidation
// filter must NOT be wired to POST /api/datasets — it emits a 400 ValidationProblemDetails
// without a root `code` field, breaking the INVALID_DATASET_NAME envelope (see §7 in the
// story and DatasetEndpoints.cs). The validators remain available for testing and future use.
builder.Services.AddScoped<IValidator<CreateDatasetRequest>, CreateDatasetRequestValidator>();
builder.Services.AddScoped<IValidator<UpdateDatasetRequest>, UpdateDatasetRequestValidator>();
// Story 8.4 — Dataset lifecycle service and VIEW DDL manager.
builder.Services.AddScoped<DatasetViewManager>();
builder.Services.AddScoped<IDatasetService, DatasetService>();
// Story 9.1 (FR-63 / AR-62) — config-backed allowlist + 5-min catalog cache.
// Singleton: the effective allowlist is derived once at startup from IConfiguration,
// IMemoryCache is itself a singleton, and DbConnectionFactory is a singleton (see §4).
builder.Services.AddSingleton<IDatasetAllowlist, DatasetAllowlist>();
// Story 11.3 (FR-72 / AR-63) — read-only LIMIT-10 query preview. The connection factory is
// a singleton (stateless; reads the dedicated `formforge_preview` connection string, which
// owns its own bounded Npgsql pool); the per-request service is scoped like IDatasetService.
builder.Services.AddSingleton<IPreviewConnectionFactory, PreviewConnectionFactory>();
builder.Services.AddScoped<IPreviewService, PreviewService>();
// Dataset-backed Dropdown source — serves a dataset VIEW's columns (inspector) and
// its {value,label} options (runtime). Auth-only via the privileged pool; scoped
// like the other dataset services.
builder.Services.AddScoped<IDatasetDropdownService, DatasetDropdownService>();
// DatasetComponent runtime data-view — serves a dataset VIEW's rows (paginated, filtered,
// sorted) and the same result set as a CSV/XLSX/PDF export. Auth-only, privileged pool.
builder.Services.AddScoped<IDatasetRowQueryService, DatasetRowQueryService>();

// JWT bearer authentication
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
        var signingKey = jwtSection["SigningKey"]
            ?? throw new InvalidOperationException("Jwt:SigningKey is required.");

        // Disable legacy short-name → long-URI mapping so our custom "roles" / "userId"
        // claims land on the principal under those exact names (otherwise the default
        // JwtSecurityTokenHandler may map them onto ClaimTypes.* URIs, breaking
        // RequireRole("platform-admin") and User.Identity.Name).
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSection["Issuer"] ?? "FormForge",
            ValidateAudience = true,
            ValidAudience = jwtSection["Audience"] ?? "FormForge",
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ClockSkew = TimeSpan.Zero,
            // Map our custom claim names so [Authorize(Roles=...)] and User.Identity.Name resolve.
            RoleClaimType = "roles",
            NameClaimType = "userId",
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Role claim type is "roles" (see JwtBearerOptions.RoleClaimType above). The
    // "platform-admin" policy is required by RequirePlatformAdmin() on /api/admin/*.
    options.AddPolicy("platform-admin", policy => policy.RequireRole("platform-admin"));
});

// CORS — allowlist Vite dev server; production origin injected as env var (AR-14).
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? [];
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(corsOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()
              .SetPreflightMaxAge(TimeSpan.FromHours(1)));
});

// Forwarded headers — required behind a reverse proxy so Request.IsHttps reflects the
// client's real scheme (drives the refresh cookie's Secure flag) and RemoteIpAddress is
// the real client (drives per-IP rate limiting, AR-15). The default trusts loopback only,
// so a proxy on another container IP (Traefik/nginx) has its X-Forwarded-* headers ignored.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    // When true, clear the loopback-only defaults so X-Forwarded-* is honored from any
    // peer. SAFE ONLY when the api container is NOT published to the host — its sole
    // ingress is then the proxy on the private Docker network, so headers cannot be
    // spoofed by an external client. This is the Compose/Dokploy case (browser -> Traefik
    // -> nginx -> api, all hops on trusted private networks). See appsettings.Compose.json.
    if (builder.Configuration.GetValue<bool>("Security:ForwardedHeaders:TrustAllProxies"))
    {
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.ForwardLimit = null; // allow the full proxy chain (Traefik + nginx)
    }
});

// Rate limiting — AR-15. Per-IP partitioned bucket so one attacker cannot starve all clients.
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("auth-login", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
            }));
    options.AddPolicy("auth-refresh", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
            }));
    // Story 2.11 — forgot-password: 5 req/min/IP. Tight limit because each request
    // can trigger an outbound email.
    options.AddPolicy("auth-forgot-password", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
            }));
    // Story 2.11 — reset-password: 10 req/min/IP. Slightly higher to tolerate a
    // user retrying a mistyped password while consuming a valid token.
    options.AddPolicy("auth-reset-password", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
            }));
    // Story 2.14 — mfa-verify: 10 req/min/IP. Mirrors the login rate limiter; the
    // 5-consecutive-wrong-codes session eviction provides an additional application-layer guard.
    options.AddPolicy("auth-mfa-verify", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
            }));
    // Story 2.12 — PUT /api/users/me/password: 5 req/min per authenticated user.
    // Sliding window per userId so brute-forcing currentPassword from a stolen JWT
    // is rate-limited. Falls back to IP for any unauthenticated edge case (rejected
    // by the auth layer anyway, so the IP fallback is defensive only).
    options.AddPolicy("user-change-password", ctx =>
    {
        var userId = ctx.User.FindFirst("userId")?.Value;
        return !string.IsNullOrEmpty(userId)
            ? RateLimitPartition.GetSlidingWindowLimiter(
                userId,
                _ => new SlidingWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                    PermitLimit = 5,
                    QueueLimit = 0,
                })
            : RateLimitPartition.GetSlidingWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new SlidingWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                    PermitLimit = 5,
                    QueueLimit = 0,
                });
    });
    // /api/admin/* — Decision 2.6: sliding window, 120 req/min per user. Falls
    // back to IP for any unauthenticated traffic that bypasses RequireAuth() (it
    // will be rejected by the auth layer anyway, so IP fallback is defensive only).
    options.AddPolicy("admin", httpContext =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: httpContext.User.FindFirst("userId")?.Value
                          ?? httpContext.Connection.RemoteIpAddress?.ToString()
                          ?? "unknown",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
            }));
    // /api/data/{designerId} — AR-15: POST (write) 60 req/min, all others 300 req/min.
    options.AddPolicy("data-write", httpContext =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: httpContext.User.FindFirst("userId")?.Value
                          ?? httpContext.Connection.RemoteIpAddress?.ToString()
                          ?? "unknown",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
            }));
    options.AddPolicy("data-read", httpContext =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: httpContext.User.FindFirst("userId")?.Value
                          ?? httpContext.Connection.RemoteIpAddress?.ToString()
                          ?? "unknown",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
            }));
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        var correlationId = context.HttpContext.GetCorrelationId();
        context.HttpContext.Response.Headers.RetryAfter = "60";
        context.HttpContext.Response.ContentType = "application/problem+json";
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            type = "https://datatracker.ietf.org/doc/html/rfc6585#section-4",
            status = 429,
            title = "Too many requests",
            code = "RATE_LIMITED",
            messageKey = "errors.rateLimited",
            correlationId,
        }, cancellationToken: token).ConfigureAwait(false);
    };
});

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, cancellationToken) =>
    {
        document.Info = new OpenApiInfo
        {
            Title = "FormForge API",
            Version = "v1",
            Description = "FormForge dynamic forms platform. All endpoints except /api/auth/* require a valid JWT Bearer token.",
        };
        var components = document.Components ??= new OpenApiComponents();
        components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
        components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "JWT access token. Obtain via POST /api/auth/login. 15-minute TTL; refresh via POST /api/auth/refresh.",
        };
        return Task.CompletedTask;
    });
    options.AddOperationTransformer<DynamicEndpointDocumentTransformer>();
});

var app = builder.Build();

try
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
    db.Database.Migrate();

    // Story 11.3 (FR-72 / AR-63) — the dataset migration creates the least-privileged
    // `formforge_preview` LOGIN role WITHOUT a password (secrets never live in migrations;
    // the role comment documents that ops/Aspire sets it). Set/rotate it here from
    // configuration so the read-only preview connection pool can authenticate. Idempotent
    // and skipped when no password is configured (e.g. integration tests, which point the
    // preview pool at the superuser connection string). ALTER ROLE … PASSWORD cannot use a
    // bind parameter, so the config-sourced value is single-quote-escaped into the literal.
    var previewRolePassword = builder.Configuration["DatasetManager:PreviewRolePassword"];
    if (!string.IsNullOrEmpty(previewRolePassword))
    {
        // ALTER ROLE … PASSWORD cannot bind a parameter, so the config-sourced (operator,
        // not request) value is single-quote-escaped into the literal. EF1002 is suppressed
        // for this one call because the value is not user input and cannot be parameterized.
        var escaped = previewRolePassword.Replace("'", "''", StringComparison.Ordinal);
        var alterRoleSql = $"ALTER ROLE formforge_preview WITH LOGIN PASSWORD '{escaped}'";
#pragma warning disable EF1002 // see comment above — non-parameterizable DDL, config-sourced value
        db.Database.ExecuteSqlRaw(alterRoleSql);
#pragma warning restore EF1002
    }

    // Bootstrap: create a default platform-admin user if the users table is empty.
    // Credentials are emitted as a startup warning so the operator can retrieve them.
    // Skipped on every subsequent restart once any user exists.
    if (!db.Users.Any())
    {
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        const string adminEmail = "admin@formforge.local";
        const string adminPassword = "Admin1234!";
        var platformAdminRoleId = new Guid("00000000-0000-0000-0000-000000000001");
        var now = DateTimeOffset.UtcNow;
        var adminUser = new FormForge.Api.Domain.Entities.User
        {
            Id = Guid.NewGuid(),
            Email = adminEmail,
            DisplayName = "Admin",
            PasswordHash = hasher.Hash(adminPassword),
            IsActive = true,
            CreatedAt = now,
        };
        adminUser.UserRoles.Add(new FormForge.Api.Domain.Entities.UserRole
        {
            UserId = adminUser.Id,
            RoleId = platformAdminRoleId,
            CreatedAt = now,
        });
        db.Users.Add(adminUser);
        db.SaveChanges();
        StartupLog.BootstrapAdminCreated(app.Logger, adminEmail, adminPassword);
    }
}
#pragma warning disable CA1031
catch (Exception ex)
#pragma warning restore CA1031
{
    StartupLog.MigrationFailed(app.Logger, ex);
}

// Fail-loud check: a production deploy with no CORS allowlist will silently reject
// every SPA request. Surface this at startup instead of as a confusing CORS error.
if (app.Environment.IsProduction() && corsOrigins.Length == 0)
{
    StartupLog.EmptyCorsAllowlistInProduction(app.Logger);
}

// Middleware ordering: forwarded headers FIRST so RemoteIpAddress reflects the real client
// before correlation/rate-limit see it; correlation ID next; then CORS, security headers,
// rate limiter, authentication, authorization, then static/files.
app.UseForwardedHeaders();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<CspNonceMiddleware>();
app.UseExceptionHandler();
app.UseCors();

// Security headers — non-CSP headers stay here; CSP is emitted per-request below (nonce required).
var hstsMaxAgeSeconds = builder.Configuration.GetValue<int?>("Security:HstsMaxAgeSeconds") ?? 31_536_000;
app.UseSecurityHeaders(policy =>
{
    policy.AddFrameOptionsDeny();
    policy.AddContentTypeOptionsNoSniff();
    policy.AddReferrerPolicyStrictOriginWhenCrossOrigin();
    if (!app.Environment.IsDevelopment())
    {
        policy.AddStrictTransportSecurityMaxAgeIncludeSubDomains(maxAgeInSeconds: hstsMaxAgeSeconds);
    }
});

// Per-request CSP with nonce — development omitted so Vite HMR inline scripts are not blocked.
// The header is set via Response.OnStarting so it survives any downstream middleware that
// flushes headers before `await next()` returns (auth challenges, early short-circuit
// responses). Without OnStarting, an early flush silently drops the CSP header.
if (!app.Environment.IsDevelopment())
{
    app.Use(async (ctx, next) =>
    {
        ctx.Response.OnStarting(() =>
        {
            var nonce = CspNonceMiddleware.GetNonce(ctx);
            var minioHost = builder.Configuration["Security:MinioPresignedHost"] ?? "";
            var imgSrcMinio = string.IsNullOrEmpty(minioHost) ? "" : " " + minioHost;
            ctx.Response.Headers["Content-Security-Policy"] =
                $"default-src 'self'; " +
                $"img-src 'self' data: blob:{imgSrcMinio}; " +
                $"style-src 'self' 'unsafe-inline'; " +
                $"script-src 'self' 'nonce-{nonce}'; " +
                $"connect-src 'self'";
            return Task.CompletedTask;
        });
        await next().ConfigureAwait(false);
    });
}

app.UseAuthentication();
app.UseAuthorization();
// Rate limiter must run AFTER authentication so the "admin" policy's partition
// factory can read the authenticated userId claim. Otherwise httpContext.User
// is anonymous and per-user partitioning collapses to per-IP. (Story 2.4 review.)
app.UseRateLimiter();
// Static files: defense-in-depth — index.html must NEVER be served raw (it contains
// the literal __CSP_NONCE__ placeholder; only IndexHtmlRewriter can serve a nonce-injected
// version on the MapFallback path). If a future change ever calls UseDefaultFiles or maps
// index.html to "/", this OnPrepareResponse short-circuits the static handler to 404.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (string.Equals(ctx.File.Name, "index.html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.StatusCode = StatusCodes.Status404NotFound;
            ctx.Context.Response.ContentLength = 0;
        }
    },
});

app.MapOpenApi();

if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/openapi/v1.json", "FormForge API v1"));
}

app.MapDefaultEndpoints();

// /health — detailed, all checks. AR-25: requires platform-admin role.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = HealthCheckJsonWriter.WriteResponse,
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
    },
})
.RequireAuthorization("platform-admin");

app.MapGroup("/api/auth")
   .WithTags("Authentication")
   .MapAuthEndpoints();

app.MapGroup("/api/admin")
   .RequireAuth()
   .RequirePlatformAdmin()
   .RequireRateLimiting("admin")
   .WithTags("Admin")
   .MapAdminEndpoints();

// /api/users — self-service endpoints for the calling user. Uses "admin" sliding
// window (120/min/user) since AR-15 doesn't define a separate limit for self reads.
app.MapGroup("/api/users")
   .RequireAuth()
   .RequireRateLimiting("admin")
   .WithTags("Users")
   .MapUserSelfEndpoints()
   .MapActiveUserEndpoints()
   .MapMePreferencesEndpoints();

// /api/menus — Story 4.7. Public navbar tree, permission + isActive filtered,
// 5 s in-memory cache per user. Every authenticated user can read their own tree;
// the server filters per-user inside the handler. Same 120/min "admin" rate-limit
// bucket as other authenticated reads.
app.MapGroup("/api/menus")
   .RequireAuth()
   .RequireRateLimiting("admin")
   .WithTags("Menus")
   .MapMenuEndpoints();

// /api/data/{designerId} — group default is "data-read" (300/min). Story 3.x POST
// handlers will override individually with "data-write" (60/min).
app.MapGroup("/api/data/{designerId}")
   .RequireAuth()
   .RequireRateLimiting("data-read")
   .WithTags("Dynamic Data")
   .MapDynamicDataEndpoints();

// /api/files — presign and file-serving helpers. RequireAuth so unauthenticated
// callers cannot enumerate object keys; the resulting presigned URL is public-facing.
app.MapGroup("/api/files")
   .RequireAuth()
   .RequireRateLimiting("admin")
   .WithTags("Files")
   .MapFilesEndpoints();

// /api/designers — Designer CRUD (Story 3.2). POST requires platform-admin enforced
// per-endpoint; GET endpoints are open to all authenticated users so the Epic 6
// DynamicComponent renderer can read schema versions without an admin role.
app.MapGroup("/api/designers")
   .RequireAuth()
   .RequireRateLimiting("admin")
   .WithTags("Designers")
   .MapDesignerEndpoints();

// /api/datasets — Dataset Manager (Story 8.2, FR-56). Read endpoints are auth-only;
// write endpoints enforce dataset-management via RequireDatasetManagement() per-endpoint.
// Handlers are stubs replaced in Stories 8.4–8.7 / 11.3. Same "admin" sliding-window
// rate-limit bucket as the other authenticated admin-tooling reads.
app.MapGroup("/api/datasets")
   .RequireAuth()
   .RequireRateLimiting("admin")
   .WithTags("Datasets")
   .MapDatasetEndpoints();

// SPA fallback — must be LAST so all API routes win first.
app.MapFallback(IndexHtmlRewriter.HandleAsync);

app.Run();

internal static partial class StartupLog
{
    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Warning,
        Message = "Database migration failed on startup — will retry or fail on next request")]
    public static partial void MigrationFailed(Microsoft.Extensions.Logging.ILogger logger, Exception ex);

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Warning,
        Message = "Bootstrap: created default admin user. Email={Email} Password={Password} — change after first login.")]
    public static partial void BootstrapAdminCreated(Microsoft.Extensions.Logging.ILogger logger, string email, string password);

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Critical,
        Message = "Cors:AllowedOrigins is empty in Production — all cross-origin requests will be rejected. Configure Cors__AllowedOrigins__0=https://your-spa-host before deployment.")]
    public static partial void EmptyCorsAllowlistInProduction(Microsoft.Extensions.Logging.ILogger logger);
}

#pragma warning disable CA1515 // Program is public so Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> can locate the entry point.
public partial class Program;
#pragma warning restore CA1515
