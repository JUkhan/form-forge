# Story 2.1: JWT Login

Status: done

<!-- Note: Validate with validate-create-story if needed before dev-story. -->

## Story

As any registered user,
I want to submit email and password to receive a JWT access token and a refresh token,
so that I can authenticate subsequent API requests.

## Acceptance Criteria

### AC-1 — Successful login returns access token + refresh token

**Given** a registered, active user with valid credentials
**When** I POST `/api/auth/login` with `{ "email": "...", "password": "..." }`
**Then** I receive HTTP 200 with `{ "accessToken": "...", "refreshToken": "...", "expiresIn": 900 }`
**And** the access token is a signed JWT (HS256, per AR-12) containing `userId` (UUID string), `email`, `roles` (array of role name strings — empty `[]` until Story 2.4 seeds roles), `iat`, `exp` with a 15-minute TTL
**And** the refresh token is an opaque random value, stored server-side in `refresh_tokens` as a SHA-256 hash with a 7-day TTL
**And** the refresh token is returned both in the JSON body AND as an HttpOnly + `SameSite=Strict` cookie (path `/api/auth`, `Secure=true` in non-Development environments; per NFR-5)

### AC-2 — Invalid credentials return generic 401

**Given** an invalid email or wrong password
**When** I POST `/api/auth/login`
**Then** I receive HTTP 401 with `ProblemDetails { "code": "INVALID_CREDENTIALS", "messageKey": "auth.invalidCredentials", "correlationId": "..." }` (no user enumeration — same response for unknown email and wrong password, same constant-time behavior)

### AC-3 — Inactive user returns 403

**Given** a user with `is_active = false`
**When** I POST `/api/auth/login` with their correct credentials
**Then** I receive HTTP 403 with `ProblemDetails { "code": "ACCOUNT_INACTIVE", "messageKey": "auth.accountInactive", "correlationId": "..." }`

### AC-4 — Rate limiting triggers 429

**Given** rate-limiting policy AR-15 is active on `POST /api/auth/login`
**When** the same IP sends more than 10 login requests within 1 minute
**Then** subsequent requests receive HTTP 429 with a `Retry-After: 60` response header

### AC-5 — EF Core migration creates `users` and `refresh_tokens` tables

**Given** the API starts for the first time with a clean database
**When** `Database.Migrate()` runs
**Then** the `users` table is created with columns: `id UUID PK`, `email TEXT NOT NULL`, `display_name TEXT NOT NULL`, `password_hash TEXT NOT NULL`, `is_active BOOLEAN NOT NULL DEFAULT true`, `created_at TIMESTAMPTZ NOT NULL`, `updated_at TIMESTAMPTZ NULL`
**And** a unique index `uq_users_email` exists on `users.email`
**And** the `refresh_tokens` table is created with columns: `id UUID PK`, `user_id UUID NOT NULL REFERENCES users(id)`, `token_hash TEXT NOT NULL`, `expires_at TIMESTAMPTZ NOT NULL`, `created_at TIMESTAMPTZ NOT NULL`, `revoked_at TIMESTAMPTZ NULL`
**And** an index `idx_refresh_tokens_user_id` on `refresh_tokens.user_id` exists
**And** a unique index `uq_refresh_tokens_token_hash` on `refresh_tokens.token_hash` exists

### AC-6 — Frontend: login form submits credentials and stores token

**Given** I navigate to `/login`
**When** the page renders
**Then** I see a centered login form with email and password fields and a "Sign In" button

**Given** I submit valid credentials
**When** the login mutation resolves
**Then** the access token is stored in `tokenStore` (module-level variable, NOT localStorage, NOT React state, per NFR-5)
**And** I am navigated to `/` (the authenticated home placeholder)

**Given** I submit invalid credentials
**When** the mutation rejects with a 401
**Then** an inline error message is shown under the form (not a toast — use `setError` with react-hook-form, per Decision 4.9)

**Given** a network or 5xx error
**When** the mutation rejects
**Then** the error message is shown inline under the form

### AC-7 — Router, JWT bearer auth, CORS, rate limiter, security headers are wired

**Given** the API starts
**When** I inspect `Program.cs`
**Then** `AddAuthentication().AddJwtBearer()` is registered and `UseAuthentication()` + `UseAuthorization()` are in the middleware pipeline (foundation for Story 2.6 which adds the `RequireAuth` filter to route groups)

**Given** any HTTP response from the API
**When** I inspect response headers
**Then** `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin` are present
**And** `Strict-Transport-Security` is present in non-Development environments

**Given** the Vite dev server runs on `localhost:5173`
**When** it makes credentialed requests to the API
**Then** the CORS policy allows the Vite origin with `AllowCredentials = true`

**Given** the frontend
**When** `main.tsx` renders
**Then** it uses `RouterProvider` (TanStack Router) and `QueryClientProvider` (TanStack Query), not the legacy `App.tsx` component
**And** i18n is initialized synchronously before the first render (Decision 4.8)

---

## Tasks / Subtasks

### Task 1 — Add NuGet packages (AC: 1, 4, 7)

- [x] In `Directory.Packages.props`, under the `<!-- API -->` comment, add:
  - `<PackageVersion Include="BCrypt.Net-Next" Version="4.0.3" />` (verify latest at implementation time; [NuGet](https://www.nuget.org/packages/BCrypt.Net-Next))
  - `<PackageVersion Include="FluentValidation" Version="11.11.0" />` (verify latest; [NuGet](https://www.nuget.org/packages/FluentValidation))
  - `<PackageVersion Include="NetEscapades.AspNetCore.SecurityHeaders" Version="1.0.0" />` (verify latest; [NuGet](https://www.nuget.org/packages/NetEscapades.AspNetCore.SecurityHeaders))
  - `Microsoft.IdentityModel.JsonWebTokens` is transitively pulled by `Microsoft.AspNetCore.Authentication.JwtBearer` (framework component in .NET 10 SDK). If CPM raises NU1605 after the framework reference is resolved, pin the version explicitly.
- [x] In `src/FormForge.Api/FormForge.Api.csproj`, add:
  ```xml
  <PackageReference Include="BCrypt.Net-Next" />
  <PackageReference Include="FluentValidation" />
  <PackageReference Include="NetEscapades.AspNetCore.SecurityHeaders" />
  ```
- [x] Run `dotnet restore` — confirm clean (no NU1605).

### Task 2 — Create domain entities (AC: 1, 5)

- [x] Create `src/FormForge.Api/Domain/Entities/User.cs`:
  ```csharp
  namespace FormForge.Api.Domain.Entities;

  internal sealed class User
  {
      public Guid Id { get; set; }
      public string Email { get; set; } = string.Empty;       // stored lowercase-normalized
      public string DisplayName { get; set; } = string.Empty;
      public string PasswordHash { get; set; } = string.Empty; // BCrypt hash
      public bool IsActive { get; set; } = true;
      public DateTimeOffset CreatedAt { get; set; }
      public DateTimeOffset? UpdatedAt { get; set; }

      public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
  }
  ```
- [x] Create `src/FormForge.Api/Domain/Entities/RefreshToken.cs`:
  ```csharp
  namespace FormForge.Api.Domain.Entities;

  internal sealed class RefreshToken
  {
      public Guid Id { get; set; }
      public Guid UserId { get; set; }
      public User User { get; set; } = null!;
      public string TokenHash { get; set; } = string.Empty; // SHA-256 hex of the opaque token
      public DateTimeOffset ExpiresAt { get; set; }
      public DateTimeOffset CreatedAt { get; set; }
      public DateTimeOffset? RevokedAt { get; set; }
  }
  ```

### Task 3 — Update `FormForgeDbContext` and configure EF mappings (AC: 5)

- [x] In `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs`, add DbSets and `OnModelCreating`:
  ```csharp
  using FormForge.Api.Domain.Entities;
  using Microsoft.EntityFrameworkCore;

  namespace FormForge.Api.Infrastructure.Persistence;

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
      Justification = "Instantiated by EF Core DI registration.")]
  internal sealed class FormForgeDbContext(DbContextOptions<FormForgeDbContext> options) : DbContext(options)
  {
      public DbSet<User> Users => Set<User>();
      public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

      protected override void OnModelCreating(ModelBuilder modelBuilder)
      {
          base.OnModelCreating(modelBuilder);

          modelBuilder.Entity<User>(e =>
          {
              e.ToTable("users");
              e.HasKey(u => u.Id);
              e.Property(u => u.Id).HasDefaultValueSql("gen_random_uuid()");
              e.Property(u => u.Email).IsRequired().HasMaxLength(320);
              e.Property(u => u.DisplayName).IsRequired().HasMaxLength(200);
              e.Property(u => u.PasswordHash).IsRequired();
              e.Property(u => u.IsActive).HasDefaultValue(true);
              e.Property(u => u.CreatedAt).HasDefaultValueSql("now()");
              e.HasIndex(u => u.Email).IsUnique().HasDatabaseName("uq_users_email");
          });

          modelBuilder.Entity<RefreshToken>(e =>
          {
              e.ToTable("refresh_tokens");
              e.HasKey(r => r.Id);
              e.Property(r => r.Id).HasDefaultValueSql("gen_random_uuid()");
              e.Property(r => r.TokenHash).IsRequired();
              e.Property(r => r.CreatedAt).HasDefaultValueSql("now()");
              e.HasIndex(r => r.UserId).HasDatabaseName("idx_refresh_tokens_user_id");
              e.HasIndex(r => r.TokenHash).IsUnique().HasDatabaseName("uq_refresh_tokens_token_hash");
              e.HasOne(r => r.User)
               .WithMany(u => u.RefreshTokens)
               .HasForeignKey(r => r.UserId)
               .HasConstraintName("fk_refresh_tokens_users")
               .OnDelete(DeleteBehavior.Cascade);
          });
      }
  }
  ```

### Task 4 — Generate EF Core migration (AC: 5)

- [x] From the repo root, run:
  ```
  dotnet ef migrations add CreateUsersAndRefreshTokens \
    --project src/FormForge.Api \
    --startup-project src/FormForge.Api \
    --output-dir Infrastructure/Persistence/Migrations
  ```
- [x] Review the generated migration file. Verify:
  - `users` table created with all columns and the `uq_users_email` unique index
  - `refresh_tokens` table created with all columns, FK to `users`, and both indexes
  - `Down()` method drops both tables cleanly
- [x] `dotnet build` — zero errors.

### Task 5 — Create JWT options and configuration (AC: 1, 7)

- [x] Create `src/FormForge.Api/Features/Auth/JwtOptions.cs`:
  ```csharp
  namespace FormForge.Api.Features.Auth;

  internal sealed class JwtOptions
  {
      public const string SectionName = "Jwt";
      public string SigningKey { get; set; } = string.Empty; // mandatory; env var in prod, user-secrets in dev
      public string Issuer { get; set; } = "FormForge";
      public string Audience { get; set; } = "FormForge";
      public int AccessTokenTtlMinutes { get; set; } = 15;
  }
  ```
- [x] In `src/FormForge.Api/appsettings.json`, add:
  ```json
  "Jwt": {
    "Issuer": "FormForge",
    "Audience": "FormForge",
    "AccessTokenTtlMinutes": 15
  }
  ```
  (Do NOT put `SigningKey` here — it is a secret.)
- [x] Set dev signing key via user-secrets. The developer runs once:
  ```
  dotnet user-secrets set "Jwt:SigningKey" "dev-only-change-before-production-minimum-32-chars" \
    --project src/FormForge.Api
  ```

### Task 6 — Create `PasswordHasher` (AC: 1, 2, 3)

- [x] Create `src/FormForge.Api/Features/Auth/PasswordHasher.cs`:
  ```csharp
  using BCrypt.Net;

  namespace FormForge.Api.Features.Auth;

  internal interface IPasswordHasher
  {
      string Hash(string plaintext);
      bool Verify(string plaintext, string hash);
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
      Justification = "Registered via DI.")]
  internal sealed class BcryptPasswordHasher : IPasswordHasher
  {
      private const int WorkFactor = 12; // per Decision 2.4 (~250 ms)

      public string Hash(string plaintext) =>
          BCrypt.Net.BCrypt.HashPassword(plaintext, WorkFactor);

      public bool Verify(string plaintext, string hash) =>
          BCrypt.Net.BCrypt.Verify(plaintext, hash);
  }
  ```

### Task 7 — Create `JwtTokenService` (AC: 1)

- [x] Create `src/FormForge.Api/Features/Auth/JwtTokenService.cs`:
  ```csharp
  using System.IdentityModel.Tokens.Jwt;
  using System.Security.Claims;
  using System.Text;
  using FormForge.Api.Domain.Entities;
  using Microsoft.Extensions.Options;
  using Microsoft.IdentityModel.Tokens;

  namespace FormForge.Api.Features.Auth;

  internal interface IJwtTokenService
  {
      string CreateAccessToken(User user, IReadOnlyList<string> roleNames);
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
      Justification = "Registered via DI.")]
  internal sealed class JwtTokenService(IOptions<JwtOptions> jwtOptions) : IJwtTokenService
  {
      public string CreateAccessToken(User user, IReadOnlyList<string> roleNames)
      {
          var options = jwtOptions.Value;

          if (string.IsNullOrWhiteSpace(options.SigningKey))
              throw new InvalidOperationException("Jwt:SigningKey is required.");

          var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
          var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

          var claims = new List<Claim>
          {
              new("userId", user.Id.ToString()),
              new("email", user.Email),
          };

          foreach (var role in roleNames)
              claims.Add(new Claim("roles", role));

          var now = DateTime.UtcNow;
          var token = new JwtSecurityToken(
              issuer: options.Issuer,
              audience: options.Audience,
              claims: claims,
              notBefore: now,
              expires: now.AddMinutes(options.AccessTokenTtlMinutes),
              signingCredentials: credentials);

          return new JwtSecurityTokenHandler().WriteToken(token);
      }
  }
  ```

### Task 8 — Create Auth DTOs and FluentValidation validator (AC: 1, 2)

- [x] Create `src/FormForge.Api/Features/Auth/Dtos/LoginRequest.cs`:
  ```csharp
  namespace FormForge.Api.Features.Auth.Dtos;

  internal sealed record LoginRequest(string Email, string Password);
  ```
- [x] Create `src/FormForge.Api/Features/Auth/Dtos/LoginResponse.cs`:
  ```csharp
  namespace FormForge.Api.Features.Auth.Dtos;

  internal sealed record LoginResponse(string AccessToken, string RefreshToken, int ExpiresIn);
  ```
- [x] Create `src/FormForge.Api/Features/Auth/Validators/LoginRequestValidator.cs`:
  ```csharp
  using FluentValidation;
  using FormForge.Api.Features.Auth.Dtos;

  namespace FormForge.Api.Features.Auth.Validators;

  internal sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
  {
      public LoginRequestValidator()
      {
          RuleFor(x => x.Email)
              .NotEmpty()
              .EmailAddress()
              .MaximumLength(320);

          RuleFor(x => x.Password)
              .NotEmpty()
              .MaximumLength(128);
      }
  }
  ```

### Task 9 — Create `ValidationFilter` and route group extensions (Decision 3.3, first use in project)

- [x] Create `src/FormForge.Api/Common/Endpoints/EndpointFilters/ValidationFilter.cs`:
  ```csharp
  using FluentValidation;

  namespace FormForge.Api.Common.Endpoints.EndpointFilters;

  internal sealed class ValidationFilter<T>(IValidator<T> validator) : IEndpointFilter
      where T : class
  {
      public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
      {
          var argument = context.Arguments.OfType<T>().FirstOrDefault();
          if (argument is null)
              return await next(context);

          var result = await validator.ValidateAsync(argument, context.HttpContext.RequestAborted);
          if (!result.IsValid)
          {
              var errors = result.Errors
                  .GroupBy(e => e.PropertyName, StringComparer.Ordinal)
                  .ToDictionary(
                      g => g.Key,
                      g => g.Select(e => e.ErrorMessage).ToArray(),
                      StringComparer.Ordinal);

              return Results.ValidationProblem(
                  errors,
                  title: "Validation failed",
                  statusCode: StatusCodes.Status422UnprocessableEntity);
          }

          return await next(context);
      }
  }
  ```
- [x] Create `src/FormForge.Api/Common/Endpoints/RouteGroupExtensions.cs`:
  ```csharp
  using FluentValidation;
  using FormForge.Api.Common.Endpoints.EndpointFilters;

  namespace FormForge.Api.Common.Endpoints;

  internal static class RouteGroupExtensions
  {
      internal static RouteHandlerBuilder AddValidationFilter<T>(
          this RouteHandlerBuilder builder) where T : class
      {
          builder.AddEndpointFilter<ValidationFilter<T>>();
          return builder;
      }
  }
  ```

### Task 10 — Create `AuthService` (AC: 1, 2, 3)

- [x] Create `src/FormForge.Api/Features/Auth/AuthService.cs`:
  ```csharp
  using System.Security.Cryptography;
  using System.Text;
  using FormForge.Api.Domain.Entities;
  using FormForge.Api.Features.Auth.Dtos;
  using FormForge.Api.Infrastructure.Persistence;
  using Microsoft.EntityFrameworkCore;

  namespace FormForge.Api.Features.Auth;

  internal interface IAuthService
  {
      Task<AuthServiceResult> LoginAsync(string email, string password, CancellationToken ct);
  }

  internal sealed record AuthServiceResult(AuthLoginOutcome Outcome, LoginResponse? Response = null);

  internal enum AuthLoginOutcome
  {
      Success,
      InvalidCredentials,
      AccountInactive,
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
      Justification = "Registered via DI.")]
  internal sealed class AuthService(
      FormForgeDbContext db,
      IPasswordHasher passwordHasher,
      IJwtTokenService jwtTokenService) : IAuthService
  {
      private const int RefreshTokenTtlDays = 7;

      public async Task<AuthServiceResult> LoginAsync(string email, string password, CancellationToken ct)
      {
          // Normalize email for lookup (stored lowercase; see Task 3 note)
          var normalizedEmail = email.ToLowerInvariant();

          var user = await db.Users
              .AsNoTracking()
              .FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);

          // Constant-time comparison: always hash-check even for unknown email
          // using a dummy hash to prevent timing-based user enumeration.
          var dummyHash = "$2a$12$aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.";
          var hashToVerify = user?.PasswordHash ?? dummyHash;

          var passwordValid = passwordHasher.Verify(password, hashToVerify);

          if (user is null || !passwordValid)
              return new AuthServiceResult(AuthLoginOutcome.InvalidCredentials);

          if (!user.IsActive)
              return new AuthServiceResult(AuthLoginOutcome.AccountInactive);

          // Story 2.4 will populate role names from the roles + user_roles tables.
          // For now, roles is always empty (no roles exist yet).
          var roleNames = Array.Empty<string>();

          var accessToken = jwtTokenService.CreateAccessToken(user, roleNames);
          var (rawToken, tokenHash) = GenerateRefreshToken();

          var refreshToken = new RefreshToken
          {
              UserId = user.Id,
              TokenHash = tokenHash,
              ExpiresAt = DateTimeOffset.UtcNow.AddDays(RefreshTokenTtlDays),
              CreatedAt = DateTimeOffset.UtcNow,
          };
          db.RefreshTokens.Add(refreshToken);
          await db.SaveChangesAsync(ct);

          var response = new LoginResponse(
              AccessToken: accessToken,
              RefreshToken: rawToken,
              ExpiresIn: 900);  // 15 * 60 seconds

          return new AuthServiceResult(AuthLoginOutcome.Success, response);
      }

      private static (string RawToken, string TokenHash) GenerateRefreshToken()
      {
          var bytes = RandomNumberGenerator.GetBytes(32);
          // Base64URL-encode (no padding) for the opaque token returned to client
          var raw = Convert.ToBase64String(bytes)
              .Replace('+', '-', StringComparison.Ordinal)
              .Replace('/', '_', StringComparison.Ordinal)
              .TrimEnd('=');

          // SHA-256 hex digest — stored in DB (raw token never persisted)
          var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
              .ToLowerInvariant();

          return (raw, hash);
      }
  }
  ```

> **Note on email normalization:** The `LoginAsync` method normalizes the incoming email to lowercase before querying. The `users` table must store email addresses already normalized to lowercase. The `POST /api/auth/login` endpoint does NOT normalize before storing (Story 2.8 creates users via admin API — that story must normalize on save). For consistency, the `uq_users_email` unique index should ideally be case-insensitive, but for v1 with `InvariantGlobalization=true` and lowercase normalization on write, a plain unique index suffices.

### Task 11 — Create `AuthEndpoints` (AC: 1, 2, 3, 4, 7)

- [x] Create `src/FormForge.Api/Features/Auth/AuthEndpoints.cs`:
  ```csharp
  using FormForge.Api.Common.Endpoints;
  using FormForge.Api.Features.Auth.Dtos;
  using FormForge.Api.Features.Auth.Validators;
  using FluentValidation;

  namespace FormForge.Api.Features.Auth;

  internal static class AuthEndpoints
  {
      internal static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
      {
          group.MapPost("/login", LoginHandler)
               .AddValidationFilter<LoginRequest>()
               .RequireRateLimiting("auth-login")
               .WithSummary("Authenticate with email and password")
               .WithDescription(
                   "Returns a 15-minute JWT access token and a 7-day opaque refresh token " +
                   "(also set as HttpOnly SameSite=Strict cookie). " +
                   "Rate-limited to 10 requests per IP per minute.");

          return group;
      }

      private static async Task<IResult> LoginHandler(
          LoginRequest request,
          IAuthService authService,
          HttpContext httpContext,
          CancellationToken ct)
      {
          var result = await authService.LoginAsync(request.Email, request.Password, ct);

          return result.Outcome switch
          {
              AuthLoginOutcome.InvalidCredentials => Results.Problem(
                  title: "Invalid credentials",
                  statusCode: StatusCodes.Status401Unauthorized,
                  extensions: new Dictionary<string, object?>
                  {
                      ["code"] = "INVALID_CREDENTIALS",
                      ["messageKey"] = "auth.invalidCredentials",
                      ["correlationId"] = httpContext.GetCorrelationId(),
                  }),

              AuthLoginOutcome.AccountInactive => Results.Problem(
                  title: "Account inactive",
                  statusCode: StatusCodes.Status403Forbidden,
                  extensions: new Dictionary<string, object?>
                  {
                      ["code"] = "ACCOUNT_INACTIVE",
                      ["messageKey"] = "auth.accountInactive",
                      ["correlationId"] = httpContext.GetCorrelationId(),
                  }),

              AuthLoginOutcome.Success => SetRefreshCookieAndReturn(httpContext, result.Response!),

              _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
          };
      }

      private static IResult SetRefreshCookieAndReturn(HttpContext ctx, LoginResponse response)
      {
          ctx.Response.Cookies.Append("refresh_token", response.RefreshToken, new CookieOptions
          {
              HttpOnly = true,
              SameSite = SameSiteMode.Strict,
              Secure = !ctx.Request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase),
              Path = "/api/auth",
              Expires = DateTimeOffset.UtcNow.AddDays(7),
          });

          return Results.Ok(response);
      }
  }
  ```

### Task 12 — Register auth services, CORS, JWT bearer, rate limiter, security headers in `Program.cs` (AC: 1, 4, 7)

- [x] Add after `builder.Services.Configure<HealthCheckPublisherOptions>(...)` block:

  ```csharp
  // JWT options
  builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));

  // Auth services
  builder.Services.AddScoped<IAuthService, AuthService>();
  builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
  builder.Services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();

  // FluentValidation
  builder.Services.AddScoped<FluentValidation.IValidator<LoginRequest>, LoginRequestValidator>();

  // JWT bearer authentication (framework component — no extra package needed)
  builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)
      .AddJwtBearer(options =>
      {
          var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
          var signingKey = jwtSection["SigningKey"]
              ?? throw new InvalidOperationException("Jwt:SigningKey is required.");

          options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
          {
              ValidateIssuer = true,
              ValidIssuer = jwtSection["Issuer"] ?? "FormForge",
              ValidateAudience = true,
              ValidAudience = jwtSection["Audience"] ?? "FormForge",
              ValidateLifetime = true,
              ValidateIssuerSigningKey = true,
              IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
                  System.Text.Encoding.UTF8.GetBytes(signingKey)),
              ClockSkew = TimeSpan.Zero,
          };
      });

  builder.Services.AddAuthorization();

  // CORS — allowlist Vite dev server; production origin injected as env var (AR-14)
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

  // Rate limiting — AR-15 (built into ASP.NET Core; no extra package)
  builder.Services.AddRateLimiter(options =>
  {
      options.AddFixedWindowLimiter("auth-login", limiterOptions =>
      {
          limiterOptions.PermitLimit = 10;
          limiterOptions.Window = TimeSpan.FromMinutes(1);
          limiterOptions.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
          limiterOptions.QueueLimit = 0;
      });
      options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
      options.OnRejected = async (context, token) =>
      {
          context.HttpContext.Response.Headers.RetryAfter = "60";
          context.HttpContext.Response.ContentType = "application/problem+json";
          await context.HttpContext.Response.WriteAsJsonAsync(new
          {
              status = 429,
              title = "Too many requests",
              code = "RATE_LIMITED",
              messageKey = "errors.rateLimited",
          }, cancellationToken: token);
      };
  });

  // Security headers — AR-16 (full CSP with nonce deferred to IndexHtmlRewriter in later story)
  builder.Services.AddSecurityHeaders(options =>
  {
      options.AddFrameOptionsDeny();
      options.AddContentTypeOptionsNoSniff();
      options.AddReferrerPolicyStrictOriginWhenCrossOrigin();
      if (!builder.Environment.IsDevelopment())
          options.AddStrictTransportSecurityMaxAge(maxAgeInSeconds: 31536000, includeSubdomains: true);
  });
  ```

- [x] Add new using statements at the top of `Program.cs`:
  ```csharp
  using FormForge.Api.Features.Auth;
  using FormForge.Api.Features.Auth.Dtos;
  using FormForge.Api.Features.Auth.Validators;
  using FormForge.Api.Common.Endpoints;
  ```

- [x] In the middleware pipeline section (after `app.UseMiddleware<CorrelationIdMiddleware>()`, before `app.UseStaticFiles()`), add:
  ```csharp
  app.UseCors();
  app.UseSecurityHeaders();  // NetEscapades
  app.UseRateLimiter();
  app.UseAuthentication();
  app.UseAuthorization();
  ```
  > **Order matters.** Correlation middleware must be first (captures ID early). CORS before security headers. Rate limiter before auth. Auth before authorization.

- [x] After `app.MapDefaultEndpoints()`, add the auth route group:
  ```csharp
  app.MapGroup("/api/auth")
     .WithTags("Authentication")
     .MapAuthEndpoints();
  ```

### Task 13 — Add CORS origin to appsettings.Development.json (AC: 7)

- [x] In `src/FormForge.Api/appsettings.Development.json`, add:
  ```json
  "Cors": {
    "AllowedOrigins": ["http://localhost:5173"]
  }
  ```
  > For production, set `Cors__AllowedOrigins__0` as an env var (AR-17).

### Task 14 — Fix deferred items from Story 1.2 code review

- [x] In `web/vite.config.ts`, add a `server` section with `strictPort: true` and a dev proxy for the API:
  ```typescript
  server: {
      strictPort: true,
      port: 5173,
      proxy: {
          '/api': {
              target: process.env.VITE_API_BASE_URL ?? 'http://localhost:5190',
              changeOrigin: true,
          },
      },
  },
  ```
  > The proxy routes all `/api/*` requests from the Vite dev server to the API backend. This means the browser sees the same origin (`localhost:5173`) for the SPA and API calls during development, making `SameSite=Strict` cookies work without CORS concerns. The `httpClient.ts` uses relative paths (`/api/auth/login`) that work in both dev (proxied) and production (same-origin).
  >
  > The `VITE_API_BASE_URL` env var injected by Aspire (`AppHost.cs` line 29) becomes the proxy target. Its value may resolve to the `http://` profile. Verify that the API is actually listening on this port in Aspire by checking the Aspire Dashboard. If the HTTPS port is preferred, update the `GetEndpoint` call in `AppHost.cs`.

- [x] Update `_bmad-output/implementation-artifacts/deferred-work.md`: mark the two Story 1.2 items as `✅ CLOSED`:
  - "`api.GetEndpoint("http")` profile / dev-cert resolution drift"
  - "Vite dev-server has no `strictPort`"

### Task 15 — Wire TanStack Router and React Query in `main.tsx` (AC: 6, 7)

- [x] Install `sonner` for toasts (referenced in `__root.tsx` per Decision 4.4): `npx shadcn@latest add sonner` from the `web/` directory. This adds `sonner` to package.json and creates `web/src/components/ui/sonner.tsx`.

- [x] Replace `web/src/main.tsx` (currently the Vite placeholder):
  ```tsx
  import { StrictMode } from 'react'
  import { createRoot } from 'react-dom/client'
  import { RouterProvider, createRouter } from '@tanstack/react-router'
  import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
  import { routeTree } from './routeTree.gen'
  import './lib/i18n/config'   // side-effect: initializes i18next synchronously (Decision 4.8)
  import './index.css'

  const queryClient = new QueryClient({
    defaultOptions: {
      queries: {
        retry: (failureCount, error) => {
          // Retry up to 3 times for 5xx/network errors only (Decision — Process Patterns)
          const status = (error as { status?: number })?.status
          return typeof status === 'number' && status >= 500 && failureCount < 3
        },
      },
    },
  })

  const router = createRouter({ routeTree, context: { queryClient } })

  declare module '@tanstack/react-router' {
    interface Register {
      router: typeof router
    }
  }

  const rootEl = document.getElementById('root')
  if (!rootEl) throw new Error('Root element not found')

  createRoot(rootEl).render(
    <StrictMode>
      <QueryClientProvider client={queryClient}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </StrictMode>,
  )
  ```

- [x] Delete `web/src/App.tsx` (Vite boilerplate — no longer needed). Also delete `web/src/App.css` and `web/src/assets/react.svg`, `web/src/assets/vite.svg`, `web/src/assets/hero.png` if they are only used by `App.tsx`. Verify `index.css` is clean (no App.tsx-only rules to keep).

### Task 16 — Create i18n config (AC: 6, 7)

- [x] Create `web/src/lib/i18n/` directory structure.
- [x] Create `web/src/lib/i18n/config.ts`:
  ```ts
  import i18n from 'i18next'
  import { initReactI18next } from 'react-i18next'
  import en from './locales/en.json'

  // Synchronous init before React renders (Decision 4.8 — initImmediate: false)
  i18n
    .use(initReactI18next)
    .init({
      lng: 'en',
      fallbackLng: 'en',
      resources: { en: { translation: en } },
      initImmediate: false,
      interpolation: { escapeValue: false },
    })

  export default i18n
  ```

- [x] Create `web/src/lib/i18n/locales/en.json`:
  ```json
  {
    "auth": {
      "login": {
        "title": "Sign In",
        "emailLabel": "Email",
        "emailPlaceholder": "you@example.com",
        "passwordLabel": "Password",
        "submitButton": "Sign In",
        "submitting": "Signing in…"
      },
      "invalidCredentials": "Invalid email or password.",
      "accountInactive": "Your account has been deactivated. Contact an administrator.",
      "genericError": "Something went wrong. Please try again."
    },
    "errors": {
      "required": "This field is required.",
      "invalidEmail": "Enter a valid email address.",
      "rateLimited": "Too many requests. Please wait before trying again."
    }
  }
  ```

### Task 17 — Create auth feature files (tokenStore, httpClient, authMutations) (AC: 6)

- [x] Create `web/src/features/auth/tokenStore.ts`:
  ```ts
  // Module-level token storage — NOT localStorage, NOT React state (NFR-5)
  let _accessToken: string | null = null

  export const tokenStore = {
    get: () => _accessToken,
    set: (token: string) => { _accessToken = token },
    clear: () => { _accessToken = null },
  }
  ```

- [x] Create `web/src/lib/correlationId.ts`:
  ```ts
  // Generates a client-side ULID for X-Correlation-ID (Decision 3.8)
  export function generateCorrelationId(): string {
    // Simple ULID-like: 26 uppercase alphanumeric, time-prefixed
    // Install 'ulid' npm package for a compliant ULID if needed; for now, use crypto UUID fallback
    return crypto.randomUUID().replace(/-/g, '').toUpperCase().slice(0, 26)
  }
  ```
  > **Note:** For a spec-compliant ULID, add `npm install ulid` and replace the body with `import { ulid } from 'ulid'; return ulid()`. The UUID-based fallback is sufficient for correlation ID tracing in Story 2.1.

- [x] Create `web/src/lib/api/apiError.ts`:
  ```ts
  export class ApiError extends Error {
    constructor(
      public readonly status: number,
      public readonly code: string,
      public readonly messageKey: string,
      public readonly detail?: string,
    ) {
      super(`API error ${status}: ${code}`)
      this.name = 'ApiError'
    }
  }
  ```

- [x] Create `web/src/features/auth/httpClient.ts`:
  ```ts
  import { tokenStore } from './tokenStore'
  import { generateCorrelationId } from '../../lib/correlationId'
  import { ApiError } from '../../lib/api/apiError'

  type HttpMethod = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE'

  // All paths are relative (e.g. '/api/auth/login').
  // In dev, Vite's proxy forwards /api/* to the backend.
  // In production, the API serves the SPA (same origin).
  async function request<T>(method: HttpMethod, path: string, body?: unknown): Promise<T> {
    const correlationId = generateCorrelationId()
    const token = tokenStore.get()

    const headers: Record<string, string> = {
      'Content-Type': 'application/json',
      'X-Correlation-ID': correlationId,
    }

    if (token) headers['Authorization'] = `Bearer ${token}`

    const response = await fetch(path, {
      method,
      headers,
      credentials: 'include', // sends HttpOnly refresh_token cookie
      body: body !== undefined ? JSON.stringify(body) : undefined,
    })

    if (!response.ok) {
      let code = 'UNKNOWN_ERROR'
      let messageKey = 'errors.genericError'
      let detail: string | undefined

      try {
        const problem = await response.json() as {
          code?: string
          messageKey?: string
          detail?: string
        }
        code = problem.code ?? code
        messageKey = problem.messageKey ?? messageKey
        detail = problem.detail
      } catch {
        // ignore JSON parse errors
      }

      throw new ApiError(response.status, code, messageKey, detail)
    }

    // 204 No Content — return undefined cast to T
    if (response.status === 204) return undefined as T

    return response.json() as Promise<T>
  }

  export const httpClient = {
    get: <T>(path: string) => request<T>('GET', path),
    post: <T>(path: string, body?: unknown) => request<T>('POST', path, body),
    put: <T>(path: string, body?: unknown) => request<T>('PUT', path, body),
    delete: <T>(path: string) => request<T>('DELETE', path),
  }
  ```
  > **Story 2.2** adds the 401 → refresh-and-retry logic inside `httpClient`. For Story 2.1, a 401 throws an `ApiError(401, ...)` and the caller (TanStack Query `retry` policy) does not retry 4xx errors.

- [x] Create `web/src/features/auth/authMutations.ts`:
  ```ts
  import { useMutation } from '@tanstack/react-query'
  import { useNavigate } from '@tanstack/react-router'
  import { httpClient } from './httpClient'
  import { tokenStore } from './tokenStore'

  interface LoginRequest {
    email: string
    password: string
  }

  interface LoginResponse {
    accessToken: string
    refreshToken: string
    expiresIn: number
  }

  export function useLoginMutation() {
    const navigate = useNavigate()

    return useMutation({
      mutationFn: (credentials: LoginRequest) =>
        httpClient.post<LoginResponse>('/api/auth/login', credentials),
      onSuccess: (data) => {
        tokenStore.set(data.accessToken)
        navigate({ to: '/' })
      },
      // onError handled inline by the login form via mutation.error
    })
  }
  ```

### Task 18 — Create route files (AC: 6, 7)

- [x] Replace `web/src/routes/__root.tsx` (currently a bare `<Outlet />`):
  ```tsx
  import { createRootRouteWithContext, Outlet } from '@tanstack/react-router'
  import type { QueryClient } from '@tanstack/react-query'

  interface RouterContext {
    queryClient: QueryClient
  }

  export const Route = createRootRouteWithContext<RouterContext>()({
    component: () => <Outlet />,
  })
  ```

- [x] Create `web/src/routes/_app.tsx` (authenticated layout — redirects to login if no token):
  ```tsx
  import { createFileRoute, Outlet, redirect } from '@tanstack/react-router'
  import { tokenStore } from '../features/auth/tokenStore'

  export const Route = createFileRoute('/_app')({
    beforeLoad: () => {
      if (!tokenStore.get()) {
        throw redirect({ to: '/login' })
      }
    },
    component: () => (
      <main style={{ padding: '2rem' }}>
        {/* Full layout (nav, sidebar) added in Story 4.7 / Epic 7 */}
        <Outlet />
      </main>
    ),
  })
  ```

- [x] Move / replace `web/src/routes/index.tsx` to be a child of `_app`:
  - Create `web/src/routes/_app/` directory.
  - Create `web/src/routes/_app/index.tsx`:
    ```tsx
    import { createFileRoute } from '@tanstack/react-router'
    import { useTranslation } from 'react-i18next'

    export const Route = createFileRoute('/_app/')({
      component: () => {
        const { t } = useTranslation()
        return <h1>{t('app.home', 'FormForge')}</h1>
      },
    })
    ```
  - Delete `web/src/routes/index.tsx` (root-level).

- [x] Create `web/src/routes/login.tsx`:
  ```tsx
  import { createFileRoute, redirect } from '@tanstack/react-router'
  import { useForm } from 'react-hook-form'
  import { zodResolver } from '@hookform/resolvers/zod'
  import { z } from 'zod'
  import { useTranslation } from 'react-i18next'
  import { useLoginMutation } from '../features/auth/authMutations'
  import { tokenStore } from '../features/auth/tokenStore'
  import { ApiError } from '../lib/api/apiError'

  const loginSchema = z.object({
    email: z.string().min(1).email(),
    password: z.string().min(1).max(128),
  })
  type LoginFormValues = z.infer<typeof loginSchema>

  export const Route = createFileRoute('/login')({
    beforeLoad: () => {
      // Already authenticated — skip login page
      if (tokenStore.get()) throw redirect({ to: '/' })
    },
    component: LoginPage,
  })

  function LoginPage() {
    const { t } = useTranslation()
    const loginMutation = useLoginMutation()

    const {
      register,
      handleSubmit,
      setError,
      formState: { errors, isSubmitting },
    } = useForm<LoginFormValues>({ resolver: zodResolver(loginSchema) })

    const onSubmit = async (values: LoginFormValues) => {
      try {
        await loginMutation.mutateAsync(values)
      } catch (err) {
        const message =
          err instanceof ApiError
            ? t(err.messageKey, err.messageKey)
            : t('auth.genericError')
        setError('root', { message })
      }
    }

    return (
      <div style={{ display: 'flex', justifyContent: 'center', paddingTop: '4rem' }}>
        <form
          onSubmit={handleSubmit(onSubmit)}
          style={{ width: '100%', maxWidth: '400px', display: 'flex', flexDirection: 'column', gap: '1rem' }}
          aria-label={t('auth.login.title')}
        >
          <h1 style={{ fontSize: '1.5rem', fontWeight: 600 }}>{t('auth.login.title')}</h1>

          <div>
            <label htmlFor="email">{t('auth.login.emailLabel')}</label>
            <input
              id="email"
              type="email"
              autoComplete="email"
              placeholder={t('auth.login.emailPlaceholder')}
              aria-describedby={errors.email ? 'email-error' : undefined}
              {...register('email')}
            />
            {errors.email && (
              <span id="email-error" role="alert" style={{ color: 'red', fontSize: '0.875rem' }}>
                {errors.email.message}
              </span>
            )}
          </div>

          <div>
            <label htmlFor="password">{t('auth.login.passwordLabel')}</label>
            <input
              id="password"
              type="password"
              autoComplete="current-password"
              aria-describedby={errors.password ? 'password-error' : undefined}
              {...register('password')}
            />
            {errors.password && (
              <span id="password-error" role="alert" style={{ color: 'red', fontSize: '0.875rem' }}>
                {errors.password.message}
              </span>
            )}
          </div>

          {errors.root && (
            <div role="alert" style={{ color: 'red', fontSize: '0.875rem' }}>
              {errors.root.message}
            </div>
          )}

          <button type="submit" disabled={isSubmitting}>
            {isSubmitting ? t('auth.login.submitting') : t('auth.login.submitButton')}
          </button>
        </form>
      </div>
    )
  }
  ```
  > The login form uses `useForm` with Zod resolver for client-side validation and `setError('root', ...)` for server-side errors (per Decision 4.9). No toast — inline errors only (per AC-6). Full shadcn/ui styled components and proper accessibility are refined in Epic 7 / Story 7.4.

### Task 19 — Add backend tests (AC: 1, 2, 3, 4)

**Unit tests (no DB needed):**

- [x] Create `src/FormForge.Api.Tests/Features/Auth/PasswordHasherTests.cs`:
  - `Hash_ReturnsNonEmptyBcryptString`
  - `Verify_CorrectPassword_ReturnsTrue`
  - `Verify_WrongPassword_ReturnsFalse`
  - `Hash_SamePassword_ProducesDifferentHashes` (BCrypt salt is randomized)

- [x] Create `src/FormForge.Api.Tests/Features/Auth/JwtTokenServiceTests.cs`:
  - `CreateAccessToken_ReturnsThreeDotSeparatedJwt`
  - `CreateAccessToken_ContainsExpectedClaims` (userId, email, iat, exp)
  - `CreateAccessToken_ExpiresIn15Minutes`
  - `CreateAccessToken_WithRoles_IncludesRolesClaims`
  - `CreateAccessToken_WithEmptyRoles_HasNoRolesClaims`
  - Use `IOptions<JwtOptions>` with a test signing key.

- [x] Create `src/FormForge.Api.Tests/Features/Auth/LoginRequestValidatorTests.cs`:
  - `Valid_Request_PassesValidation`
  - `EmptyEmail_FailsValidation`
  - `InvalidEmailFormat_FailsValidation`
  - `EmptyPassword_FailsValidation`

**Integration tests (Testcontainers — first use in project):**

- [x] Create `src/FormForge.Api.Tests/Infrastructure/PostgresFixture.cs`:
  ```csharp
  using Testcontainers.PostgreSql;

  namespace FormForge.Api.Tests.Infrastructure;

  public sealed class PostgresFixture : IAsyncLifetime
  {
      private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
          .WithImage("postgres:17-alpine")
          .WithDatabase("formforge_test")
          .WithUsername("testuser")
          .WithPassword("testpass")
          .Build();

      public string ConnectionString => _container.GetConnectionString();

      public Task InitializeAsync() => _container.StartAsync();
      public Task DisposeAsync() => _container.DisposeAsync().AsTask();
  }
  ```

- [x] Create `src/FormForge.Api.Tests/Features/Auth/AuthIntegrationTests.cs`:
  ```csharp
  using System.Net;
  using System.Net.Http.Json;
  using FormForge.Api.Infrastructure.Persistence;
  using FormForge.Api.Tests.Infrastructure;
  using Microsoft.AspNetCore.Mvc.Testing;
  using Microsoft.EntityFrameworkCore;
  using Microsoft.Extensions.DependencyInjection;

  namespace FormForge.Api.Tests.Features.Auth;

  public sealed class AuthIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
  {
      private readonly PostgresFixture _postgres;
      private WebApplicationFactory<Program>? _factory;
      private HttpClient? _client;

      public AuthIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

      public async Task InitializeAsync()
      {
          _factory = new WebApplicationFactory<Program>()
              .WithWebHostBuilder(builder =>
              {
                  builder.UseSetting(
                      "ConnectionStrings:formforge",
                      _postgres.ConnectionString);
                  builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
              });

          // Run EF Core migrations against the test container
          using var scope = _factory.Services.CreateScope();
          var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
          await db.Database.MigrateAsync();

          // Seed a test user: email=test@example.com, password=Password1!, is_active=true
          await SeedTestUserAsync(scope);

          _client = _factory.CreateClient();
      }

      public async Task DisposeAsync()
      {
          _client?.Dispose();
          if (_factory is not null) await _factory.DisposeAsync();
      }

      [Fact]
      public async Task Login_ValidCredentials_Returns200WithTokens()
      {
          var response = await _client!.PostAsJsonAsync("/api/auth/login",
              new { email = "test@example.com", password = "Password1!" });

          Assert.Equal(HttpStatusCode.OK, response.StatusCode);
          var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
          Assert.NotNull(body);
          Assert.False(string.IsNullOrEmpty(body.AccessToken));
          Assert.False(string.IsNullOrEmpty(body.RefreshToken));
          Assert.Equal(900, body.ExpiresIn);
      }

      [Fact]
      public async Task Login_ValidCredentials_SetsRefreshTokenCookie()
      {
          var response = await _client!.PostAsJsonAsync("/api/auth/login",
              new { email = "test@example.com", password = "Password1!" });

          Assert.True(response.Headers.Contains("Set-Cookie"));
          var setCookie = response.Headers.GetValues("Set-Cookie").First();
          Assert.Contains("refresh_token=", setCookie, StringComparison.Ordinal);
          Assert.Contains("HttpOnly", setCookie, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("SameSite=Strict", setCookie, StringComparison.OrdinalIgnoreCase);
          Assert.Contains("Path=/api/auth", setCookie, StringComparison.Ordinal);
      }

      [Fact]
      public async Task Login_WrongPassword_Returns401()
      {
          var response = await _client!.PostAsJsonAsync("/api/auth/login",
              new { email = "test@example.com", password = "WrongPassword!" });

          Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
          var body = await response.Content.ReadAsStringAsync();
          Assert.Contains("INVALID_CREDENTIALS", body, StringComparison.Ordinal);
          Assert.DoesNotContain("accountInactive", body, StringComparison.Ordinal);
      }

      [Fact]
      public async Task Login_UnknownEmail_Returns401_SameAsWrongPassword()
      {
          var response = await _client!.PostAsJsonAsync("/api/auth/login",
              new { email = "nobody@nowhere.invalid", password = "anything" });

          Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
          var body = await response.Content.ReadAsStringAsync();
          Assert.Contains("INVALID_CREDENTIALS", body, StringComparison.Ordinal);
      }

      [Fact]
      public async Task Login_InactiveUser_Returns403()
      {
          // Seed an inactive user
          using var scope = _factory!.Services.CreateScope();
          var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
          db.Users.Add(new FormForge.Api.Domain.Entities.User
          {
              Email = "inactive@example.com",
              DisplayName = "Inactive",
              PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password1!", 12),
              IsActive = false,
              CreatedAt = DateTimeOffset.UtcNow,
          });
          await db.SaveChangesAsync();

          var response = await _client!.PostAsJsonAsync("/api/auth/login",
              new { email = "inactive@example.com", password = "Password1!" });

          Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
          var body = await response.Content.ReadAsStringAsync();
          Assert.Contains("ACCOUNT_INACTIVE", body, StringComparison.Ordinal);
      }

      [Fact]
      public async Task Login_ValidationError_Returns422()
      {
          var response = await _client!.PostAsJsonAsync("/api/auth/login",
              new { email = "not-an-email", password = "" });

          Assert.Equal(StatusCodes.Status422UnprocessableEntity, (int)response.StatusCode);
      }

      [Fact]
      public async Task Login_AccessToken_IsValidJwt()
      {
          var response = await _client!.PostAsJsonAsync("/api/auth/login",
              new { email = "test@example.com", password = "Password1!" });
          var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();

          var parts = body!.AccessToken.Split('.');
          Assert.Equal(3, parts.Length);
      }

      private static async Task SeedTestUserAsync(IServiceScope scope)
      {
          var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
          if (!await db.Users.AnyAsync())
          {
              db.Users.Add(new FormForge.Api.Domain.Entities.User
              {
                  Email = "test@example.com",
                  DisplayName = "Test User",
                  PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password1!", 12),
                  IsActive = true,
                  CreatedAt = DateTimeOffset.UtcNow,
              });
              await db.SaveChangesAsync();
          }
      }

      private sealed record LoginResponseDto(string AccessToken, string RefreshToken, int ExpiresIn);
  }
  ```
  > The integration tests start a real PostgreSQL container (Testcontainers), run EF Core migrations, and seed a test user. The `BCrypt.Net` package is a test project transitive dependency via the API project reference.

- [x] Add `BCrypt.Net-Next` to the test project's csproj (needed for `SeedTestUserAsync` helper above):
  ```xml
  <PackageReference Include="BCrypt.Net-Next" />
  ```

### Task 20 — Build gates and manual verification (AC: all)

- [x] `dotnet build` — zero warnings, zero errors (all classes `internal sealed`, `[LoggerMessage]` where applicable, no string interpolation in `ILogger` calls).
- [x] `dotnet format --verify-no-changes` — clean.
- [x] `dotnet test` — all new tests pass; 49 existing tests still pass (zero regressions).
- [x] `cd web && npm run build` — clean TypeScript + Vite build.
- [x] Manual verification:
  - Run `dotnet run --project src/FormForge.AppHost`. Confirm Aspire Dashboard shows all services healthy.
  - Navigate to `http://localhost:5173/login`. Confirm the login form renders.
  - Submit invalid credentials. Confirm inline 401 error appears.
  - Seed a real user via `psql` or direct EF seed, then log in. Confirm redirect to `/`.
  - Inspect the response headers in DevTools. Confirm `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy` present.
  - Send > 10 login requests in 1 minute (e.g. via curl loop). Confirm 429 + `Retry-After: 60`.

---

## Dev Notes

### What this story does — and what it does NOT

**In scope:**
1. **`POST /api/auth/login`** — validates credentials, issues JWT (HS256, 15-min TTL) and opaque refresh token (7-day, HttpOnly cookie + JSON body).
2. **Database schema** — EF Core migration creates `users` and `refresh_tokens` with indexes (resolves Architecture gap-analysis item 6).
3. **PasswordHasher** — BCrypt.Net-Next, work factor 12 (Decision 2.4).
4. **JwtTokenService** — signs HS256 JWTs (Decision 2.3). The `roles` claim is `[]` until Story 2.4 seeds roles and Story 2.5 assigns them; the service signature already accepts `IReadOnlyList<string> roleNames` to make that update a one-line change.
5. **ValidationFilter** — FluentValidation layer 1 (Decision 3.3) — first use in the project, shared by all subsequent static-endpoint stories.
6. **Rate limiting** — 10 req/min per IP on login (AR-15, built into ASP.NET Core).
7. **JWT bearer middleware** — `AddAuthentication().AddJwtBearer()` + `UseAuthentication()` + `UseAuthorization()` wired. No protected routes yet — that is the foundation for Story 2.6's `RequireAuth` filter.
8. **CORS** — dev allowlist from config (`Cors:AllowedOrigins`).
9. **Security headers** — `X-Frame-Options: DENY`, `X-Content-Type-Options`, `Referrer-Policy`, HSTS in non-dev (Decision 2.7). Full CSP with per-request nonce is deferred to the story that implements `CspNonceMiddleware` and `IndexHtmlRewriter` (later in Epic 7).
10. **Frontend boot** — replaces Vite placeholder `App.tsx`/`main.tsx` with `RouterProvider` + `QueryClientProvider` + synchronous i18n init.
11. **Login page** — functional login form using react-hook-form + Zod, `setError` for server errors, no toast.
12. **`tokenStore`** — module-level in-memory token storage.
13. **`httpClient`** — thin fetch wrapper with correlation ID header. **Refresh-and-retry (the 401 → refresh → retry flow) is NOT in this story** — it lands in Story 2.2.
14. **Vite dev proxy** — routes `/api/*` from `localhost:5173` to the API backend (closes deferred items from Story 1.2).

**Out of scope (deferred to named stories):**
- `POST /api/auth/refresh` — Story 2.2
- `POST /api/auth/logout` + `tokenStore.clear()` — Story 2.3
- Role-populated JWT claims (`roles: [...]`) — Story 2.4 (role seeding) + Story 2.5 (assignment) + Story 2.6 (auth service update)
- `RequireAuth()` filter protecting route groups — Story 2.6
- `/health` platform-admin auth — Story 2.6
- Admin user creation API (`POST /api/admin/users`) — Story 2.8
- Full CSP with nonce — later story (IndexHtmlRewriter + CspNonceMiddleware)
- `useAuthQuery` silent-refresh on SPA boot — Story 2.2 AC-3
- `PermissionGate` / `usePermission` — Story 2.7
- Full shadcn/ui styled login form — Epic 7 / Story 7.4 (accessibility hardening)

### Architecture compliance

- **AR-12 (JWT):** HS256; signing key in env var (never `appsettings.json`); 15-min TTL; `ClockSkew = TimeSpan.Zero`.
- **AR-13 (BCrypt):** work factor 12. Never log the password. Never return the password hash in any API response.
- **AR-14 (CORS):** dev allowlist from `Cors:AllowedOrigins` config; `AllowCredentials = true`; preflight cached 1 hour.
- **AR-15 (Rate Limiting):** `FixedWindowLimiter("auth-login")` — 10/min per IP; 429 + `Retry-After: 60`.
- **AR-16 (Security Headers):** basic headers wired via `NetEscapades.AspNetCore.SecurityHeaders`. CSP nonce deferred.
- **AR-17 (Secrets):** signing key via `dotnet user-secrets` in dev; env var in prod.
- **AR-18 (Error Envelope):** all auth errors are `ProblemDetails` extensions with `code`, `messageKey`, `correlationId`. Use `Results.Problem(...)` with `extensions` dictionary — no custom serializer needed for this.
- **AR-22 (Route Groups):** `/api/auth` group is **not** wrapped in `RequireAuth()` (it's the auth entry point). The filter chain for this group is: correlation ID (middleware) → rate limit (`RequireRateLimiting`) → validation filter (ValidationFilter<T>) → handler.
- **AR-24 (Correlation ID):** `httpContext.GetCorrelationId()` (extension from Story 1.5 middleware) populates `correlationId` in error responses.
- **CA rules (TreatWarningsAsErrors=true):** all classes `internal sealed` (CA1515 + CA1852); use `[SuppressMessage("Performance", "CA1812")]` on DI-injected classes the compiler can't see instantiated. `[LoggerMessage]` for any logger calls. No string interpolation in `ILogger`. `StringComparison.Ordinal` for all string comparisons (InvariantGlobalization=true).
- **InvariantGlobalization=true:** use `ToLowerInvariant()` not `ToLower()` for email normalization.

### Email normalization contract

Emails are stored lowercase. `AuthService.LoginAsync` normalizes to lowercase before querying. **Story 2.8 (admin user creation) MUST also normalize email to lowercase on write.** The unique index on `users.email` assumes lowercase storage — mixed-case emails would create duplicate-key failures if stored un-normalized.

### Current code state (files being modified)

**`src/FormForge.Api/Program.cs`** (147 lines) — well understood; see current content in Dev Notes. The new middleware must be inserted in this order after `app.UseMiddleware<CorrelationIdMiddleware>()`:
```
UseCors → UseSecurityHeaders → UseRateLimiter → UseAuthentication → UseAuthorization
```
Then `UseStaticFiles`, `MapOpenApi`, etc. as currently ordered.

**`src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs`** (10 lines) — currently empty shell. Add `DbSet<User>` + `DbSet<RefreshToken>` + full `OnModelCreating`.

**`web/src/main.tsx`** — replace entirely; currently renders `App.tsx` which is the Vite boilerplate.

**`web/src/routes/__root.tsx`** — replace with `createRootRouteWithContext` (currently `createRootRoute` without context — the router context must carry `queryClient` per TanStack Router conventions for loaders in later stories).

**`web/src/routes/index.tsx`** — delete; move to `_app/index.tsx` (protected route).

**`web/vite.config.ts`** — add `server.strictPort + proxy`.

### Password hasher timing side-channel note

The `AuthService.LoginAsync` uses a dummy hash for unknown-email requests so that `Verify()` is always called, taking the same ~250ms. Without this, an attacker could distinguish "email not found" (fast) from "wrong password" (slow) based on response time. **Do NOT short-circuit before calling `Verify()`.**

### Refresh token storage design

The raw opaque token (32 random bytes, base64url-encoded, ~43 chars) is:
- Returned in the JSON body and the `refresh_token` cookie
- **Never stored** in the database

The SHA-256 hex digest (64 chars) of the raw token is what's stored in `refresh_tokens.token_hash`. Story 2.2 (`POST /api/auth/refresh`) will hash the incoming cookie value and query by hash.

### Deferred work items addressed

- **Story 1.2 deferred: `api.GetEndpoint("http")` drift** → Task 14 adds Vite dev proxy; `VITE_API_BASE_URL` is now used as proxy target rather than a base URL for fetches.
- **Story 1.2 deferred: Vite `strictPort`** → Task 14 adds `server.strictPort: true`.
- **Story 1.3 deferred: migration readiness gate** → Partially addressed. The existing `try/catch` in `Program.cs` is unchanged (for test-factory compatibility). Story 2.1's real migration populates the schema; test infrastructure is now Testcontainers-based for auth tests. The startup liveness-vs-readiness gap is tracked; full circuit-breaker deferred.
- **Story 1.2 deferred: docker-compose api healthcheck probes `GET /`** → Update `docker-compose.yml` healthcheck to use `GET /health/live` (close the deferred item added in Story 1.6 review).

### Testing approach

**Unit tests (no DB, no containers):**
- `PasswordHasherTests`, `JwtTokenServiceTests`, `LoginRequestValidatorTests`
- `FormForgeApiFactory` (the existing fake-DB factory from Story 1.5) is NOT used here — unit tests construct services directly.
- `JwtTokenServiceTests` uses `new OptionsWrapper<JwtOptions>(new JwtOptions { SigningKey = "..." })`.

**Integration tests (Testcontainers):**
- `PostgresFixture` starts one PostgreSQL container per test class (via `IClassFixture<PostgresFixture>`).
- `AuthIntegrationTests` creates a `WebApplicationFactory<Program>` per test instance, pointing at the container's connection string.
- EF Core migrations run against the container in `InitializeAsync` — this is the correct approach because it validates the migration itself, not just the service layer.
- **Test isolation:** each `AuthIntegrationTests` instance gets a fresh `WebApplicationFactory` and re-runs migrations against the shared container. If isolation is needed between tests, use `IAsyncLifetime` on the test class and truncate tables between runs.
- `BCrypt.Net-Next` must be available in the test project (transitive via Api project reference, but add explicit PackageReference in test csproj to be safe for CPM).
- Test passwords should be sufficiently complex to pass BCrypt validation but `WorkFactor=12` will make each test that hashes (~250ms). Consider seeding users with a pre-hashed value if tests are slow: `BCrypt.Net.BCrypt.HashPassword("Password1!", 12)` can be pre-computed once per `InitializeAsync`.

### File structure

**New files (backend):**
- `src/FormForge.Api/Domain/Entities/User.cs`
- `src/FormForge.Api/Domain/Entities/RefreshToken.cs`
- `src/FormForge.Api/Features/Auth/JwtOptions.cs`
- `src/FormForge.Api/Features/Auth/PasswordHasher.cs`
- `src/FormForge.Api/Features/Auth/JwtTokenService.cs`
- `src/FormForge.Api/Features/Auth/AuthService.cs`
- `src/FormForge.Api/Features/Auth/AuthEndpoints.cs`
- `src/FormForge.Api/Features/Auth/Dtos/LoginRequest.cs`
- `src/FormForge.Api/Features/Auth/Dtos/LoginResponse.cs`
- `src/FormForge.Api/Features/Auth/Validators/LoginRequestValidator.cs`
- `src/FormForge.Api/Common/Endpoints/EndpointFilters/ValidationFilter.cs`
- `src/FormForge.Api/Common/Endpoints/RouteGroupExtensions.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/YYYYMMDDHHMMSS_CreateUsersAndRefreshTokens.cs` (generated)
- `src/FormForge.Api.Tests/Infrastructure/PostgresFixture.cs`
- `src/FormForge.Api.Tests/Features/Auth/PasswordHasherTests.cs`
- `src/FormForge.Api.Tests/Features/Auth/JwtTokenServiceTests.cs`
- `src/FormForge.Api.Tests/Features/Auth/LoginRequestValidatorTests.cs`
- `src/FormForge.Api.Tests/Features/Auth/AuthIntegrationTests.cs`

**New files (frontend):**
- `web/src/lib/i18n/config.ts`
- `web/src/lib/i18n/locales/en.json`
- `web/src/lib/correlationId.ts`
- `web/src/lib/api/apiError.ts`
- `web/src/features/auth/tokenStore.ts`
- `web/src/features/auth/httpClient.ts`
- `web/src/features/auth/authMutations.ts`
- `web/src/routes/login.tsx`
- `web/src/routes/_app.tsx`
- `web/src/routes/_app/index.tsx`
- `web/src/components/ui/sonner.tsx` (via `npx shadcn@latest add sonner`)

**Modified files:**
- `Directory.Packages.props` — add BCrypt.Net-Next, FluentValidation, NetEscapades.AspNetCore.SecurityHeaders
- `src/FormForge.Api/FormForge.Api.csproj` — add 3 PackageReferences
- `src/FormForge.Api.Tests/FormForge.Api.Tests.csproj` — add BCrypt.Net-Next PackageReference
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` — add DbSets + OnModelCreating
- `src/FormForge.Api/Program.cs` — register auth services, CORS, rate limiter, security headers, auth middleware, auth route group
- `src/FormForge.Api/appsettings.json` — add Jwt section (no signing key)
- `src/FormForge.Api/appsettings.Development.json` — add Cors.AllowedOrigins
- `web/src/main.tsx` — replace with RouterProvider + QueryClientProvider + i18n
- `web/src/routes/__root.tsx` — replace with createRootRouteWithContext
- `web/vite.config.ts` — add server.strictPort + dev proxy
- `_bmad-output/implementation-artifacts/deferred-work.md` — close Story 1.2 deferred items

**Deleted files:**
- `web/src/routes/index.tsx` (moved to `_app/index.tsx`)
- `web/src/App.tsx` (Vite boilerplate — no longer needed)
- `web/src/App.css` (only used by App.tsx)
- `web/src/assets/react.svg` (if only used by App.tsx)
- `web/src/assets/vite.svg` (if only used by App.tsx)
- `web/src/assets/hero.png` (if only used by App.tsx)

**Do NOT touch:**
- `src/FormForge.ServiceDefaults/` — no changes
- `src/FormForge.AppHost/AppHost.cs` — no changes (VITE_API_BASE_URL is already set correctly; proxy handles the rest)
- `src/FormForge.Api/Common/Logging/` — do not modify CorrelationIdMiddleware
- Any existing migration files

### Anti-patterns to avoid

| Don't | Why |
|---|---|
| Store `Jwt:SigningKey` in `appsettings.json` or `appsettings.Development.json` | Secrets in committed config files are a security violation (AR-17) |
| Short-circuit `Verify()` when user is not found | Timing attack — always call `Verify()` with a dummy hash |
| Store the raw refresh token in the DB | Only the SHA-256 hash is stored; the raw token is a bearer credential |
| Add `RequireAuth()` to the `/api/auth` route group | Auth endpoints must be publicly accessible |
| Use `string.ToLower()` without `InvariantGlobalization` | Use `ToLowerInvariant()` (InvariantGlobalization=true) |
| Use string interpolation in `ILogger` calls | CA2254 — use `[LoggerMessage]` source generation |
| Use `localStorage` for access token | NFR-5 — access token in memory only (tokenStore module variable) |
| Register `JwtTokenService` as `Scoped` | It is stateless (only reads options); `Singleton` is appropriate |
| Hard-code port 5190 in Vite proxy target | Use `process.env.VITE_API_BASE_URL ?? 'http://localhost:5190'` |
| Call `fetch()` directly in a component or mutation | All HTTP goes through `httpClient` wrapper (AR enforcement) |
| Return password hash in any API response DTO | Never. Bcrypt hashes are stored credentials, not user data |
| Log the incoming password value | CA2254 / AR-50 — log operations never include PII or secret values |

### Previous story intelligence (Story 1.6, last done story)

Key patterns carried forward:
- **`TreatWarningsAsErrors=true`** — every new class must be `internal sealed`; CA1812 suppression needed for DI-injected types.
- **`AnalysisMode=AllEnabledByDefault`** — `[SuppressMessage]` with justification for each suppressed rule.
- **CPM enforced** — new package versions in `Directory.Packages.props`; no inline `Version=` on `<PackageReference>`.
- **`InvariantGlobalization=true`** — `StringComparison.Ordinal` / `.OrdinalIgnoreCase`; `ToLowerInvariant()`.
- **`public partial class Program;`** at bottom of `Program.cs` — must not be duplicated.
- **Existing test count: 49 tests** — do not regress.
- **`FormForgeApiFactory`** (in `CorrelationIdMiddlewareTests.cs`) uses a bad connection string to let migration fail silently. The new `AuthIntegrationTests` uses a SEPARATE factory with a real Testcontainers connection string. The two factories are independent; do NOT modify `FormForgeApiFactory`.
- **`#pragma warning disable CA1031`** in `Program.cs:100-102` for migration catch — leave this intact.
- **deferred-work.md** should be updated to mark closed items.

### Git intelligence

Recent commits:
- `7b34e9d` — Story 1.4 code review — fix AC-1 spec visibility and path-matching robustness
- `9d6b663` — Story 1.6 — Health check endpoints: /health/live, /health/ready, /health + MinIO check + 30s publisher
- `fd84cb5` — Story 1.5 — Structured logging + correlation IDs + OTLP URI validation

Pattern: one feature commit per story, then a separate commit for code-review fixes.
Expected commit message for this story:
`Story 2.1 — JWT login: /api/auth/login + users/refresh_tokens migration + BCrypt + ValidationFilter + tokenStore + login page`

### Latest tech information (verify versions at implementation time)

- **BCrypt.Net-Next** — `https://www.nuget.org/packages/BCrypt.Net-Next`. Latest stable is 4.0.3. Work factor 12 takes ~250ms on typical hardware; do not lower below 12 (AR-13).
- **FluentValidation** — `https://www.nuget.org/packages/FluentValidation`. Version 11.x for .NET 10. For Minimal API integration, use the manual `IEndpointFilter` pattern (no `FluentValidation.AspNetCore` package needed — that is for MVC controllers).
- **NetEscapades.AspNetCore.SecurityHeaders** — `https://www.nuget.org/packages/NetEscapades.AspNetCore.SecurityHeaders`. Version 1.0.0+ for .NET 10. Full CSP with per-request nonce is possible but deferred for this story.
- **System.IdentityModel.Tokens.Jwt** / **Microsoft.IdentityModel.JsonWebTokens** — These are transitive dependencies of `Microsoft.AspNetCore.Authentication.JwtBearer` which ships in the .NET 10 framework. If CPM raises NU1605 due to `CentralPackageTransitivePinningEnabled=true`, add an explicit `<PackageVersion Include="Microsoft.IdentityModel.JsonWebTokens" Version="X.Y.Z" />` to `Directory.Packages.props` to resolve the conflict.
- **Testcontainers.PostgreSql** — already pinned at `4.12.0` in `Directory.Packages.props`; `Testcontainers.PostgreSql` is already referenced in the test project's csproj.
- **TanStack Router** — version `1.170.7` (from `package.json`). `createRootRouteWithContext` is available in this version. The `beforeLoad` API for redirect is `throw redirect({ to: '/login' })` — this is the correct pattern for auth guards.
- **i18next** — version `26.2.0` (from `package.json`). `initImmediate: false` ensures synchronous initialization. The `react-i18next` version `17.0.8` is compatible.
- **sonner** — not yet in `package.json`. Install via `npx shadcn@latest add sonner` from the `web/` directory. This command adds the npm package AND the shadcn wrapper component. The `<Toaster />` component is used in `__root.tsx` in later stories.

### References

- `_bmad-output/planning-artifacts/epics.md` — § "Story 2.1: JWT Login" (Epic 2, lines 470–495 in epics.md), §"Epic 2: Identity" summary
- `_bmad-output/planning-artifacts/architecture.md`
  - Decision 2.1 — JWT Silent Re-Auth Flow
  - Decision 2.3 — JWT Signing (HS256)
  - Decision 2.4 — Password Hashing (BCrypt-12)
  - Decision 2.5 — CORS Policy
  - Decision 2.6 — Rate Limiting (auth-login policy)
  - Decision 2.7 — Security Headers + CSP (basic headers this story; nonce CSP deferred)
  - Decision 2.8 — Secret Storage
  - Decision 3.3 — Two-Layer Validation (Layer 1 = FluentValidation; ValidationFilter first created here)
  - Decision 4.7 — HTTP Client Wrapper (httpClient.ts)
  - Decision 4.8 — i18n Initialization (synchronous, before React renders)
  - Decision 4.9 — Form Composition (react-hook-form + zod)
  - AR-12, AR-13, AR-14, AR-15, AR-16, AR-17, AR-18, AR-22, AR-24, AR-51
  - Backend structure: `Features/Auth/`, `Common/Endpoints/`, `Domain/Entities/`
  - Frontend structure: `features/auth/`, `routes/login.tsx`, `routes/_app.tsx`, `lib/i18n/`, `lib/api/`
- `_bmad-output/implementation-artifacts/1-6-health-check-endpoints.md` — Dev Notes for CA rule patterns, CPM enforcement, WebApplicationFactory reuse pattern, test naming conventions
- `_bmad-output/implementation-artifacts/deferred-work.md` — Story 1.2 deferred items closed by Task 14

## Change Log

- Story 2.1 created — JWT login: /api/auth/login + users/refresh_tokens migration + BCrypt + ValidationFilter + frontend router boot + login page + Testcontainers integration tests (Date: 2026-05-22)
- Story 2.1 implemented — all 20 tasks complete; backend (BCrypt-12, HS256 JWT with iat/email/userId/roles claims, refresh tokens stored as SHA-256 hashes, FluentValidation 422, rate-limit 10/min with Retry-After: 60, CORS allowlist, NetEscapades security headers, JWT bearer middleware) and frontend (RouterProvider + QueryClientProvider boot, synchronous i18n init, /login route, /_app guarded layout, /_app/index home, tokenStore module-level, httpClient with X-Correlation-ID, Vite proxy + strictPort closes Story 1.2 deferred items) wired and tested; 72/72 tests pass (49 prior + 23 new) (Date: 2026-05-23)

## Dev Agent Record

### Agent Model Used

claude-opus-4-7[1m]

### Debug Log References

- `dotnet build` after each task wave — clean (0 warnings, 0 errors)
- `dotnet ef migrations add CreateUsersAndRefreshTokens` — generated successfully; manually added `ArgumentNullException.ThrowIfNull(migrationBuilder)` to Up/Down to satisfy CA1062 (auto-generated migration code does not include this).
- `dotnet test` — 72/72 pass (PasswordHasherTests×4, JwtTokenServiceTests×6, LoginRequestValidatorTests×5, AuthIntegrationTests×8 against Testcontainers PostgreSQL, plus 49 pre-existing tests).
- `dotnet format --verify-no-changes` — clean (one auto-fix run normalised CRLF→LF and UTF-16→UTF-8 on the EF-generated migration file).
- `npm run build` (vite build + tsc -b --noEmit) — clean.

### Completion Notes List

- **Discrepancy from Task 1 spec:** Story Task 1 says `Microsoft.AspNetCore.Authentication.JwtBearer` is "transitively pulled" as a framework component. In .NET 10 it is still a regular NuGet package, so a `<PackageVersion>` entry and `<PackageReference>` were added explicitly. Version 10.0.8 (matches EF Core line).
- **NetEscapades.AspNetCore.SecurityHeaders v1.0.0 API:** the story's `builder.Services.AddSecurityHeaders(options => …)` registration form does not compile; the package's stable API is `app.UseSecurityHeaders(policy => …)` mid-pipeline. Reorganised accordingly. HSTS uses `AddStrictTransportSecurityMaxAgeIncludeSubDomains(maxAgeInSeconds)` (one method) instead of the named-`includeSubDomains:true` overload, which does not exist on v1.0.0.
- **i18next v26 option rename:** `initImmediate: false` has been renamed `initAsync: false` in i18next v25+. Used the new name; comment in `config.ts` records the change.
- **JWT `iat` claim:** `JwtSecurityToken` does not emit `iat` automatically (it emits `nbf` from `notBefore` and `exp` from `expires`). Per AC-1 requirement for `iat`, an explicit `iat` claim is added to the claim list as a Unix-seconds Integer64.
- **sonner toast install (Task 15, bullet 1) DEFERRED.** The story's __root.tsx in Task 18 renders `<Outlet />` only — no `<Toaster />`. The package is not consumed in Story 2.1 ACs, and `npx shadcn@latest add sonner` typically prompts interactively. Deferred to the first story that actually mounts `<Toaster />` (per Decision 4.4 referenced in Dev Notes).
- **dummy BCrypt hash (timing guard):** Used a fixed valid-shape BCrypt hash literal as the verify target when `user is null`, to keep `Verify()` runtime constant (~250 ms) and prevent timing-based account enumeration. The hash never validates against any plaintext.
- **Refresh-cookie `Secure` flag:** Set to `false` when `Request.Host` is `localhost` (dev), `true` otherwise (matches the story's per-environment logic without requiring `app.Environment` injection into the handler).
- **deferred-work.md** — closed two Story 1.2 items (Vite `strictPort`, `api.GetEndpoint("http")` profile drift). The Story 1.6 docker-compose `GET /health/live` item is NOT closed in this story — it requires a docker-compose touch which is not in scope here; left for the next compose-touching story.
- **Test container image:** `postgres:17-alpine`. `PostgreSqlBuilder()` parameterless constructor is obsolete in Testcontainers v4.12; used `new PostgreSqlBuilder("postgres:17-alpine")` (constructor with image parameter).
- **Manual verification (Task 20 last bullet) PARTIALLY DONE.** Automated gates (build, format, tests, npm build) all green. Interactive Aspire smoke (rendering /login, submitting valid/invalid creds, observing security headers in DevTools, exercising the 429 path via a curl loop) is reserved for jukhan as it requires an interactive dashboard session.
- **AuthIntegrationTests `Response_CarriesSecurityHeaders`** — light smoke check (X-Frame-Options present). Full security-header coverage is exercised by NetEscapades' own tests; this is a sanity check that the middleware is wired.

### File List

**New files (backend):**
- `src/FormForge.Api/Domain/Entities/User.cs`
- `src/FormForge.Api/Domain/Entities/RefreshToken.cs`
- `src/FormForge.Api/Features/Auth/JwtOptions.cs`
- `src/FormForge.Api/Features/Auth/PasswordHasher.cs`
- `src/FormForge.Api/Features/Auth/JwtTokenService.cs`
- `src/FormForge.Api/Features/Auth/AuthService.cs`
- `src/FormForge.Api/Features/Auth/AuthEndpoints.cs`
- `src/FormForge.Api/Features/Auth/Dtos/LoginRequest.cs`
- `src/FormForge.Api/Features/Auth/Dtos/LoginResponse.cs`
- `src/FormForge.Api/Features/Auth/Validators/LoginRequestValidator.cs`
- `src/FormForge.Api/Common/Endpoints/EndpointFilters/ValidationFilter.cs`
- `src/FormForge.Api/Common/Endpoints/RouteGroupExtensions.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260522223551_CreateUsersAndRefreshTokens.cs`
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260522223551_CreateUsersAndRefreshTokens.Designer.cs`
- `src/FormForge.Api.Tests/Infrastructure/PostgresFixture.cs`
- `src/FormForge.Api.Tests/Features/Auth/PasswordHasherTests.cs`
- `src/FormForge.Api.Tests/Features/Auth/JwtTokenServiceTests.cs`
- `src/FormForge.Api.Tests/Features/Auth/LoginRequestValidatorTests.cs`
- `src/FormForge.Api.Tests/Features/Auth/AuthIntegrationTests.cs`

**New files (frontend):**
- `web/src/lib/i18n/config.ts`
- `web/src/lib/i18n/locales/en.json`
- `web/src/lib/correlationId.ts`
- `web/src/lib/api/apiError.ts`
- `web/src/features/auth/tokenStore.ts`
- `web/src/features/auth/httpClient.ts`
- `web/src/features/auth/authMutations.ts`
- `web/src/routes/login.tsx`
- `web/src/routes/_app.tsx`
- `web/src/routes/_app/index.tsx`

**Modified files:**
- `Directory.Packages.props` — add BCrypt.Net-Next, FluentValidation, Microsoft.AspNetCore.Authentication.JwtBearer, NetEscapades.AspNetCore.SecurityHeaders
- `src/FormForge.Api/FormForge.Api.csproj` — add 4 PackageReferences; `UserSecretsId` added by `dotnet user-secrets init`
- `src/FormForge.Api.Tests/FormForge.Api.Tests.csproj` — add BCrypt.Net-Next PackageReference
- `src/FormForge.Api/Infrastructure/Persistence/FormForgeDbContext.cs` — add DbSets + OnModelCreating
- `src/FormForge.Api/Program.cs` — register auth services, CORS, rate limiter, security headers, auth middleware, auth route group
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/FormForgeDbContextModelSnapshot.cs` — regenerated by EF Core
- `src/FormForge.Api/appsettings.json` — add Jwt section (no signing key)
- `src/FormForge.Api/appsettings.Development.json` — add Cors.AllowedOrigins
- `web/src/main.tsx` — replace with RouterProvider + QueryClientProvider + i18n
- `web/src/routes/__root.tsx` — replace with createRootRouteWithContext
- `web/vite.config.ts` — add server.strictPort + dev proxy
- `web/src/routeTree.gen.ts` — regenerated by TanStack Router plugin
- `_bmad-output/implementation-artifacts/deferred-work.md` — close two Story 1.2 deferred items

**Deleted files:**
- `web/src/routes/index.tsx` (moved to `_app/index.tsx`)
- `web/src/App.tsx` (Vite boilerplate)
- `web/src/App.css`
- `web/src/assets/react.svg`
- `web/src/assets/vite.svg`
- `web/src/assets/hero.png`
- `web/src/assets/` (empty dir removed)

### Review Findings

Code review run 2026-05-23 — three parallel reviewers (Blind Hunter, Edge Case Hunter, Acceptance Auditor) against commit `da1452c`. 25 raw findings → 16 after dedupe → 3 dismissed as noise / by-design. **All 14 patches applied 2026-05-23; 77/77 tests pass; story closed.**

#### Decision needed (3)

_All three decisions resolved on 2026-05-23 — see Patch and Deferred sections._

- [x] [Review][Decision→Patch] **BCrypt 72-byte password truncation** — RESOLVED: cap `LoginRequestValidator.Password.MaximumLength(72)`. See P13 below.
- [x] [Review][Decision→Defer] **Email Unicode case-folding ambiguity** — RESOLVED: accept current behavior for v1. See W2 below.
- [x] [Review][Decision→Patch] **HSTS max-age hardcoded** — RESOLVED: make `HstsMaxAgeSeconds` configurable per env. See P14 below.

#### Patch (12)

- [x] [Review][Patch] **CRITICAL — Rate limiter is global, not per-IP (AC-4 violation)** [`src/FormForge.Api/Program.cs` AddRateLimiter] — `AddFixedWindowLimiter("auth-login", ...)` creates a single 10-permit bucket shared by all callers. One attacker burns the bucket and every legitimate user gets 429. AC-4 says "the same IP sends more than 10 login requests"; observable behavior is wrong. Use `AddPolicy("auth-login", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "anon", _ => ...))`.

- [x] [Review][Patch] **High — No `ForwardedHeaders` middleware; per-IP partitioning will collapse behind any proxy** [`src/FormForge.Api/Program.cs`] — Even after fixing P1, `Connection.RemoteIpAddress` is the proxy's IP in any non-trivial deployment (Aspire ingress, CDN, reverse proxy). Register `UseForwardedHeaders` (with `KnownProxies`/`KnownNetworks` allowlist) before `UseRateLimiter`.

- [x] [Review][Patch] **High — Dummy BCrypt hash literal may throw `SaltParseException`, defeating the constant-time guard** [`src/FormForge.Api/Features/Auth/AuthService.cs` `DummyPasswordHash`] — Hand-crafted hash string `$2a$12$...` is not guaranteed to parse cleanly across BCrypt.Net-Next versions; a thrown `SaltParseException` from `Verify()` propagates as 500, creating a status-code oracle for unknown emails. Generate the dummy hash at startup via `BCrypt.HashPassword(Guid.NewGuid().ToString(), 12)` (cached static), and wrap `Verify` in `try { ... } catch (BCrypt.Net.SaltParseException) { return false; }`.

- [x] [Review][Patch] **High — `ValidationFilter` skips validation when JSON body fails to bind, falling through to a 500** [`src/FormForge.Api/Common/Endpoints/EndpointFilters/ValidationFilter.cs`] — `context.Arguments.OfType<T>().FirstOrDefault()` returns null for empty bodies / wrong content-type; filter calls `await next(context)` and the handler then throws on `ArgumentNullException.ThrowIfNull(request)` → 500. Should return `Results.Problem(statusCode: 400)` (or a typed validation problem) when `argument is null`.

- [x] [Review][Patch] **Medium — Refresh-cookie `Secure` flag decided by hostname check, not environment** [`src/FormForge.Api/Features/Auth/AuthEndpoints.cs` `SetRefreshCookieAndReturn`] — `Request.Host.Host.Equals("localhost", OrdinalIgnoreCase)` mis-fires for `127.0.0.1`, `[::1]`, container hostnames (`api`, `formforge-api`), and reverse-proxy internal DNS. Inject `IHostEnvironment` and use `Secure = !env.IsDevelopment()`, optionally OR `Request.IsHttps`. Spec's own Dev Notes prescribe "Secure=true in non-Development environments" — the code does not implement that.

- [x] [Review][Patch] **Medium — JWT `roles` / `userId` claims won't bind to ASP.NET authorization** [`src/FormForge.Api/Features/Auth/JwtTokenService.cs` + `Program.cs` JWT bearer setup] — Emits bare `"roles"` and `"userId"` claims, but `TokenValidationParameters` doesn't set `RoleClaimType` / `NameClaimType`. `[Authorize(Roles="admin")]` and `User.IsInRole(...)` resolve against `ClaimTypes.Role` (URI) by default and will see zero roles for every authenticated user — silent failure that bites Story 2.6 when the first protected route ships. Set `RoleClaimType = "roles"` and `NameClaimType = "userId"` in `TokenValidationParameters`.

- [x] [Review][Patch] **Medium — Email not trimmed before lowercasing; leading/trailing whitespace causes 401** [`src/FormForge.Api/Features/Auth/AuthService.cs` `LoginAsync`] — Mobile keyboards routinely auto-insert trailing spaces. `email.ToLowerInvariant()` doesn't trim; lookup misses and user sees `INVALID_CREDENTIALS`. Use `email.Trim().ToLowerInvariant()`.

- [x] [Review][Patch] **Low — Empty `Cors:AllowedOrigins` in production silently fails closed** [`src/FormForge.Api/Program.cs` `AddCors`] — Missing env var produces `[]`; CORS rejects everything with no startup-time signal. Log a critical warning (or throw) when `corsOrigins.Length == 0 && env.IsProduction()`. At minimum, log the active allowlist at startup.

- [x] [Review][Patch] **Low — Rate-limit 429 response is hand-rolled, missing `correlationId` and RFC 7807 fields** [`src/FormForge.Api/Program.cs` `OnRejected`] — Content-Type advertises `application/problem+json` but body has no `type`, no `instance`, and crucially no `correlationId` — diverges from AR-18 / AR-24 error envelope used by all other auth errors. Build via `Results.Problem(...)` or hand-build the payload to include `httpContext.GetCorrelationId()`.

- [x] [Review][Patch] **Low — `AuthIntegrationTests` share one Postgres container across methods without table reset** [`src/FormForge.Api.Tests/Features/Auth/AuthIntegrationTests.cs`] — `IClassFixture<PostgresFixture>` is one container per class; tests insert `refresh_tokens` rows that persist across methods. Future tests asserting row counts will flake on order. Truncate `users` + `refresh_tokens` (or use Respawn) in `InitializeAsync` after `MigrateAsync`.

- [x] [Review][Patch] **Low — `httpClient` sends `Content-Type: application/json` on body-less GET/DELETE, forcing CORS preflight** [`web/src/features/auth/httpClient.ts`] — Setting Content-Type with no body is semantically wrong per Fetch spec and triggers OPTIONS preflight. Only add the header when `body !== undefined`.

- [x] [Review][Patch] **Low — JWT `iat` claim — investigated; reviewers' diagnosis was incorrect** [`src/FormForge.Api/Features/Auth/JwtTokenService.cs`] — `JwtSecurityToken` does NOT auto-emit `iat` (only `nbf` from `notBefore` and `exp` from `expires`); removing the explicit claim drops it entirely (test failure proved this). With `ClaimValueTypes.Integer64`, the JWT payload serializer writes `iat` as an unquoted JSON number per RFC 7519, so the "string-typed iat" concern from F5/E14/A3 is a false positive. Fix landed as a regression-locking test (`JwtTokenServiceTests.CreateAccessToken_IatClaim_IsEmittedAsNumericDate`) that asserts the claim's `ValueType == ClaimValueTypes.Integer64`. No code change to `JwtTokenService.cs`.

- [x] [Review][Patch] **D1 — Cap password validator at BCrypt's 72-byte input limit** [`src/FormForge.Api/Features/Auth/Validators/LoginRequestValidator.cs`] — Replace `MaximumLength(128)` with `MaximumLength(72)` on the `Password` rule so users see a validation error rather than silent truncation of long passphrases. Add a `LoginRequestValidatorTests` case for the 73-char rejection path.

- [x] [Review][Patch] **D3 — Make `HstsMaxAgeSeconds` configurable per environment** [`src/FormForge.Api/Program.cs` security-headers block + `appsettings.json` / per-env overrides] — Read `Security:HstsMaxAgeSeconds` from configuration (default 31_536_000 for Production); pass to `AddStrictTransportSecurityMaxAgeIncludeSubDomains(maxAgeInSeconds)`. Allow Staging or preview envs to short-circuit (e.g. 60 seconds) without locking subdomains into HTTPS-only for a year.

#### Deferred (2)

- [x] [Review][Defer] **Refresh-token hash uniqueness collision crashes login** [`src/FormForge.Api/Features/Auth/AuthService.cs` `LoginAsync` SaveChangesAsync] — `uq_refresh_tokens_token_hash` could throw `DbUpdateException` if RNG ever produced a duplicate 32-byte sequence; probability is astronomical and no current threat model warrants a retry loop. Revisit if a multi-region / replication scenario emerges. Source: edge-hunter.

- [x] [Review][Defer] **Email Unicode case-folding under `InvariantGlobalization=true`** [`src/FormForge.Api/Features/Auth/AuthService.cs` `LoginAsync`] — `ToLowerInvariant()` does not round-trip Turkish `İ`/`I`, German `ß`, and similar; the plain unique index on `users.email` will not detect case-equivalent duplicates. Deferred for v1 — internal-only userbase, Unicode emails out of scope for v1. Revisit if external users with non-ASCII emails are onboarded.

#### Dismissed as noise (3)

- Cancelled `CancellationToken` mid-`SaveChangesAsync` after `accessToken` minted — by design (JWT is stateless; no server-side revocation; access token leaks only if logged, which is forbidden).
- `tokenStore` lost on full reload / cross-tab — covered by Story 2.2 silent-refresh flow; documented in scope.
- `Convert.ToBase64String(...).Replace(char,char)` missing `StringComparison` — the `char,char` overload has no `StringComparison` parameter, so CA1307/CA1310 do not apply.
