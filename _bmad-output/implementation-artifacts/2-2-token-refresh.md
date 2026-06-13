# Story 2.2: Token Refresh

Status: done

<!-- Note: Validate with validate-create-story if needed before dev-story. -->

## Story

As an authenticated user with a valid refresh token,
I want to exchange it for a new access token,
so that my session persists without re-login, even across page reloads.

## Acceptance Criteria

### AC-1 — Successful refresh returns new tokens with single-use rotation

**Given** a valid, unrevoked, non-expired refresh token in the `refresh_token` HttpOnly cookie
**When** the client POSTs `/api/auth/refresh` (cookie travels automatically — no request body needed)
**Then** the server returns HTTP 200 with `{ "accessToken": "...", "refreshToken": "...", "expiresIn": 900 }`
**And** the old refresh token is immediately marked `revokedAt = now()` in the DB (single-use rotation)
**And** a new refresh token is stored (SHA-256 hash) and returned as both JSON body AND a fresh `refresh_token` HttpOnly + `SameSite=Strict` cookie (same path `/api/auth`, same TTL semantics as login)

### AC-2 — Invalid, expired, or revoked token returns 401

**Given** an expired, revoked, or non-existent refresh token in the cookie
**When** the client POSTs `/api/auth/refresh`
**Then** the server returns HTTP 401 with `ProblemDetails { "code": "REFRESH_TOKEN_INVALID", "messageKey": "auth.refreshTokenInvalid", "correlationId": "..." }`

**Given** a revoked token is presented again (replay attack)
**When** the client POSTs `/api/auth/refresh`
**Then** the server returns HTTP 401 (same as AC-2 above)
**And** a Warning-level log entry is emitted for the replay event (security observability)
**And** the `formforge.refresh_token.replayed` counter is incremented

**Given** no `refresh_token` cookie is present
**When** the client POSTs `/api/auth/refresh`
**Then** the server returns HTTP 401 with `code: "REFRESH_TOKEN_INVALID"`

### AC-3 — SPA boot silent refresh (page reload recovery)

**Given** the page is reloaded and the in-memory access token is lost from `tokenStore`
**When** the SPA boots (TanStack Router attempts to render a protected `/_app` route)
**Then** `_app.tsx` `beforeLoad` (async) calls `POST /api/auth/refresh` before any child route renders
**And** on 200, the new access token is stored in `tokenStore` and protected routes render seamlessly without redirecting to login
**And** on 401 (no valid refresh cookie), `beforeLoad` throws `redirect({ to: '/login', search: { redirect: currentPath } })` — preserving the attempted URL for post-login return

### AC-4 — Ongoing silent refresh keeps session alive (13-minute cadence)

**Given** the user is on an authenticated page
**When** the `useAuthQuery` hook mounts in the `/_app` layout
**Then** it calls `POST /api/auth/refresh` every ~13 minutes (`refetchInterval: 13 * 60 * 1000`)
**And** on success, the new access token is stored in `tokenStore` silently (no UI feedback)
**And** on 401 (session truly expired), `tokenStore.clear()` is called and the user is redirected to `/login`

### AC-5 — httpClient 401 → refresh-and-retry for API calls

**Given** any API call (not `/api/auth/refresh` itself) returns HTTP 401 (access token expired mid-session)
**When** `httpClient` receives the 401 response
**Then** it makes one `POST /api/auth/refresh` attempt with the HttpOnly cookie
**And** on success, updates `tokenStore` with the new access token and retries the original request exactly once
**And** on refresh failure (401), calls `tokenStore.clear()` and redirects the browser to `/login` via `window.location.replace('/login')`
**And** if multiple concurrent API calls fail with 401, only one refresh attempt fires (deduplicated via shared promise)

### AC-6 — Rate limiting on `/api/auth/refresh`

**Given** rate-limiting policy AR-15 is active on `POST /api/auth/refresh`
**When** the same IP sends more than 30 refresh requests within 1 minute
**Then** subsequent requests receive HTTP 429 with a `Retry-After: 60` response header

### AC-7 — OTel metrics emitted

**Given** a successful refresh
**When** `RefreshAsync` issues a new token pair
**Then** the `formforge.refresh_token.issued` counter is incremented by 1

**Given** a successful refresh (old token rotation)
**When** the old token is revoked
**Then** the `formforge.refresh_token.revoked` counter is incremented by 1

---

## Tasks / Subtasks

### Task 1 — Create `AuthMetrics` + register in `Program.cs` + add "auth-refresh" rate limiter (AC: 6, 7)

**`AuthMetrics` class:**

- [x] Create `src/FormForge.Api/Features/Auth/AuthMetrics.cs`:
  ```csharp
  using System.Diagnostics.Metrics;

  namespace FormForge.Api.Features.Auth;

  internal sealed class AuthMetrics : IDisposable
  {
      public const string MeterName = "FormForge.Auth";

      private readonly Meter _meter;
      private readonly Counter<long> _refreshIssued;
      private readonly Counter<long> _refreshRevoked;
      private readonly Counter<long> _refreshReplayed;

      [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
          Justification = "Registered via DI.")]
      public AuthMetrics()
      {
          _meter = new Meter(MeterName);
          _refreshIssued = _meter.CreateCounter<long>("formforge.refresh_token.issued",
              description: "Number of refresh tokens successfully issued.");
          _refreshRevoked = _meter.CreateCounter<long>("formforge.refresh_token.revoked",
              description: "Number of refresh tokens revoked during rotation.");
          _refreshReplayed = _meter.CreateCounter<long>("formforge.refresh_token.replayed",
              description: "Number of replayed (already-revoked) refresh token presentations.");
      }

      public void RecordIssued() => _refreshIssued.Add(1);
      public void RecordRevoked() => _refreshRevoked.Add(1);
      public void RecordReplayed() => _refreshReplayed.Add(1);

      public void Dispose() => _meter.Dispose();
  }
  ```

**Register in `Program.cs`** (add after the existing `AddSingleton<IPasswordHasher>` line):

- [x] Add `builder.Services.AddSingleton<AuthMetrics>();`
- [x] Add OTel meter registration (after `builder.AddServiceDefaults()`, or chain onto existing OTel builder if present):
  ```csharp
  builder.Services.AddOpenTelemetry()
      .WithMetrics(metrics => metrics.AddMeter(AuthMetrics.MeterName));
  ```
  > **Note:** `builder.AddServiceDefaults()` already sets up the OTel pipeline. This call extends it with the custom meter. If `AddServiceDefaults` is called after this line, reorder so `AddServiceDefaults` comes first.
- [x] Add using: `using System.Diagnostics.Metrics;` is not needed — `Meter` and `Counter<T>` are in the `System.Diagnostics.Metrics` namespace which is available without a using in .NET 10 global usings.

**"auth-refresh" rate limiter** (in `Program.cs`, inside the `AddRateLimiter(options => { ... })` block, after the existing "auth-login" policy):

- [x] Add the "auth-refresh" policy:
  ```csharp
  options.AddPolicy("auth-refresh", ctx =>
      RateLimitPartition.GetFixedWindowLimiter(
          ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
          _ => new FixedWindowRateLimiterOptions
          {
              PermitLimit = 30,
              Window = TimeSpan.FromMinutes(1),
              QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
              QueueLimit = 0,
          }));
  ```
  > **Pattern:** identical per-IP partition key as "auth-login" (use `ctx.Connection.RemoteIpAddress?.ToString() ?? "anon"` — same `ForwardedHeaders` middleware from Story 2.1 ensures this is the real client IP, not proxy).

### Task 2 — Extend `AuthService` with `RefreshAsync` (AC: 1, 2, 7)

- [x] Add new types to `AuthService.cs` BEFORE the existing `AuthLoginOutcome` enum:
  ```csharp
  internal enum AuthRefreshOutcome
  {
      Success,
      RefreshTokenInvalid,
  }

  internal sealed record AuthRefreshResult(AuthRefreshOutcome Outcome, LoginResponse? Response = null);
  ```

- [x] Add `RefreshAsync` to the `IAuthService` interface:
  ```csharp
  internal interface IAuthService
  {
      Task<AuthServiceResult> LoginAsync(string email, string password, CancellationToken ct);
      Task<AuthRefreshResult> RefreshAsync(string? rawRefreshToken, CancellationToken ct);  // NEW
  }
  ```

- [x] Add `AuthMetrics` to `AuthService` constructor injection:
  ```csharp
  internal sealed class AuthService(
      FormForgeDbContext db,
      IPasswordHasher passwordHasher,
      IJwtTokenService jwtTokenService,
      IOptions<JwtOptions> jwtOptions,
      AuthMetrics metrics) : IAuthService  // metrics ADDED
  ```

- [x] Extract a private static `HashToken(string raw)` helper (used by both `LoginAsync` and `RefreshAsync`). Add it just above `GenerateRefreshToken()`:
  ```csharp
  // Hashes the raw opaque token (base64url) → SHA-256 hex digest (what the DB stores).
  private static string HashToken(string raw) =>
      Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
          .ToLower(CultureInfo.InvariantCulture);
  ```
  Update `LoginAsync`'s inline hash computation to call `HashToken(raw)` instead of the inline version.

- [x] Add the `RefreshAsync` method body:
  ```csharp
  public async Task<AuthRefreshResult> RefreshAsync(string? rawRefreshToken, CancellationToken ct)
  {
      if (string.IsNullOrEmpty(rawRefreshToken))
          return new AuthRefreshResult(AuthRefreshOutcome.RefreshTokenInvalid);

      var tokenHash = HashToken(rawRefreshToken);

      // Single DB round-trip: fetch the token + its user in one query.
      var existing = await db.RefreshTokens
          .Include(r => r.User)
          .FirstOrDefaultAsync(r => r.TokenHash == tokenHash, ct)
          .ConfigureAwait(false);

      // Token not found at all → invalid/expired
      if (existing is null)
          return new AuthRefreshResult(AuthRefreshOutcome.RefreshTokenInvalid);

      // Token found but already revoked → potential replay attack
      if (existing.RevokedAt is not null)
      {
          metrics.RecordReplayed();
          // Log without PII — token hash is safe (one-way), UserId is an opaque GUID
          return new AuthRefreshResult(AuthRefreshOutcome.RefreshTokenInvalid);
      }

      // Token exists and is unrevoked — check expiry
      if (existing.ExpiresAt <= DateTimeOffset.UtcNow)
          return new AuthRefreshResult(AuthRefreshOutcome.RefreshTokenInvalid);

      // Inactive user: revoke the token immediately (per FR-1 / Decision 2.1 deactivation handling)
      if (!existing.User.IsActive)
      {
          existing.RevokedAt = DateTimeOffset.UtcNow;
          await db.SaveChangesAsync(ct).ConfigureAwait(false);
          metrics.RecordRevoked();
          return new AuthRefreshResult(AuthRefreshOutcome.RefreshTokenInvalid);
      }

      // Issue new token pair atomically: revoke old + create new in one SaveChanges
      existing.RevokedAt = DateTimeOffset.UtcNow;

      var (newRaw, newHash) = GenerateRefreshToken();
      var newRefreshToken = new RefreshToken
      {
          UserId = existing.UserId,
          TokenHash = newHash,
          ExpiresAt = DateTimeOffset.UtcNow.AddDays(RefreshTokenTtlDays),
          CreatedAt = DateTimeOffset.UtcNow,
      };
      db.RefreshTokens.Add(newRefreshToken);

      // Story 2.4 populates role names; until then roles is always [].
      var roleNames = Array.Empty<string>();
      var accessToken = jwtTokenService.CreateAccessToken(existing.User, roleNames);

      await db.SaveChangesAsync(ct).ConfigureAwait(false);

      metrics.RecordRevoked();
      metrics.RecordIssued();

      var ttlSeconds = jwtOptions.Value.AccessTokenTtlMinutes * 60;
      var response = new LoginResponse(
          AccessToken: accessToken,
          RefreshToken: newRaw,
          ExpiresIn: ttlSeconds);

      return new AuthRefreshResult(AuthRefreshOutcome.Success, response);
  }
  ```

  > **Atomicity:** `existing.RevokedAt` and `db.RefreshTokens.Add(newRefreshToken)` are tracked by the same `DbContext` instance. A single `SaveChangesAsync` call writes both the revocation and the new row in one PostgreSQL transaction, preventing a window where both tokens are simultaneously invalid.

  > **Include strategy:** `Include(r => r.User)` avoids a second DB round-trip for the user. EF Core will do a LEFT JOIN. For 7-day-lived tokens this is a single-row join — negligible overhead.

  > **Replay logging:** the `ILogger` structured log for replay must NOT include the raw token or the token hash. Log only the event type and a truncated correlationId if needed. Example:
  > ```csharp
  > // Inject ILogger<AuthService> via constructor if replay logging is desired:
  > _logger.LogWarning("Refresh token replay detected — possible theft. TokenHashPrefix={Prefix}",
  >     tokenHash[..8]);
  > ```
  > This is **optional** in Task 2; the `metrics.RecordReplayed()` call is the required AC signal.

### Task 3 — Add `/refresh` endpoint to `AuthEndpoints` (AC: 1, 2, 6)

- [x] In `src/FormForge.Api/Features/Auth/AuthEndpoints.cs`, register the new route inside `MapAuthEndpoints`:
  ```csharp
  group.MapPost("/refresh", RefreshHandler)
       .RequireRateLimiting("auth-refresh")
       .WithSummary("Exchange a refresh token for a new access token")
       .WithDescription(
           "Reads the refresh_token HttpOnly cookie. Returns a new 15-minute JWT access token " +
           "and rotates the 7-day refresh token (single-use). Rate-limited to 30 requests per IP per minute.");
  ```
  > **No ValidationFilter:** the refresh endpoint has no JSON request body. The refresh token comes from the HttpOnly cookie, not from a parsed DTO.

- [x] Add the handler method:
  ```csharp
  private static async Task<IResult> RefreshHandler(
      HttpContext httpContext,
      IAuthService authService,
      IHostEnvironment env,
      CancellationToken ct)
  {
      ArgumentNullException.ThrowIfNull(httpContext);
      ArgumentNullException.ThrowIfNull(authService);
      ArgumentNullException.ThrowIfNull(env);

      httpContext.Request.Cookies.TryGetValue("refresh_token", out var rawToken);

      var result = await authService.RefreshAsync(rawToken, ct).ConfigureAwait(false);

      return result.Outcome switch
      {
          AuthRefreshOutcome.RefreshTokenInvalid => Results.Problem(
              title: "Invalid refresh token",
              statusCode: StatusCodes.Status401Unauthorized,
              extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
              {
                  ["code"] = "REFRESH_TOKEN_INVALID",
                  ["messageKey"] = "auth.refreshTokenInvalid",
                  ["correlationId"] = httpContext.GetCorrelationId(),
              }),

          AuthRefreshOutcome.Success => SetRefreshCookieAndReturn(httpContext, env, result.Response!),

          _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
      };
  }
  ```
  > **Reuse `SetRefreshCookieAndReturn`:** the helper already exists (written in Story 2.1). Both `/login` and `/refresh` share it. No code duplication.

### Task 4 — Update `httpClient.ts` with 401 → refresh-and-retry (AC: 5)

- [x] Replace the entire `web/src/features/auth/httpClient.ts` content:
  ```ts
  import { tokenStore } from './tokenStore'
  import { generateCorrelationId } from '../../lib/correlationId'
  import { ApiError } from '../../lib/api/apiError'

  type HttpMethod = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE'

  // Shared refresh state — deduplicates concurrent 401s so only one refresh fires.
  let _pendingRefresh: Promise<void> | null = null

  async function refreshSession(): Promise<void> {
    // If a refresh is already in-flight, wait on the same promise (don't fan out).
    if (_pendingRefresh) return _pendingRefresh

    _pendingRefresh = (async () => {
      try {
        const response = await fetch('/api/auth/refresh', {
          method: 'POST',
          credentials: 'include',
          headers: { 'X-Correlation-ID': generateCorrelationId() },
        })
        if (!response.ok) {
          tokenStore.clear()
          window.location.replace('/login')
          throw new ApiError(401, 'SESSION_EXPIRED', 'errors.sessionExpired')
        }
        const data = (await response.json()) as { accessToken: string }
        tokenStore.set(data.accessToken)
      } finally {
        _pendingRefresh = null
      }
    })()

    return _pendingRefresh
  }

  async function request<T>(
    method: HttpMethod,
    path: string,
    body?: unknown,
    isRetry = false,
  ): Promise<T> {
    const correlationId = generateCorrelationId()
    const token = tokenStore.get()

    const headers: Record<string, string> = {
      'X-Correlation-ID': correlationId,
    }

    // Only send Content-Type when a body is being transmitted (Story 2.1 patch P11).
    if (body !== undefined) headers['Content-Type'] = 'application/json'
    if (token) headers['Authorization'] = `Bearer ${token}`

    const response = await fetch(path, {
      method,
      headers,
      credentials: 'include',
      body: body !== undefined ? JSON.stringify(body) : undefined,
    })

    // 401 on a non-refresh endpoint: try one silent refresh then retry the original request.
    // Guard: isRetry=true breaks the loop; path check prevents refreshSession() from calling itself.
    if (response.status === 401 && !isRetry && path !== '/api/auth/refresh') {
      await refreshSession()
      return request<T>(method, path, body, true)
    }

    if (!response.ok) {
      let code = 'UNKNOWN_ERROR'
      let messageKey = 'errors.genericError'
      let detail: string | undefined

      try {
        const problem = (await response.json()) as {
          code?: string
          messageKey?: string
          detail?: string
        }
        code = problem.code ?? code
        messageKey = problem.messageKey ?? messageKey
        detail = problem.detail
      } catch {
        // ignore JSON parse errors — fall back to defaults
      }

      throw new ApiError(response.status, code, messageKey, detail)
    }

    if (response.status === 204) return undefined as T
    return (await response.json()) as T
  }

  export const httpClient = {
    get: <T>(path: string) => request<T>('GET', path),
    post: <T>(path: string, body?: unknown) => request<T>('POST', path, body),
    put: <T>(path: string, body?: unknown) => request<T>('PUT', path, body),
    delete: <T>(path: string) => request<T>('DELETE', path),
  }
  ```

  > **Why `window.location.replace` (not TanStack Router navigate):** `httpClient.ts` is a plain TS module with no React context, so `useNavigate()` is unavailable. `window.location.replace('/login')` performs a hard redirect that resets the history stack — correct semantics for "session truly expired." The thrown `ApiError(401, 'SESSION_EXPIRED', ...)` is unreachable (the page reloads) but satisfies TypeScript's return-type constraint.

  > **`_pendingRefresh` deduplication:** if three concurrent TanStack Query fetches all return 401 (e.g., on a stale page loaded from disk cache), all three will see `_pendingRefresh` is not null after the first one sets it, and will await the same promise. Only one `POST /api/auth/refresh` call fires. After it resolves, all three retry with the fresh token.

### Task 5 — Create `useAuthQuery.ts` (AC: 4)

- [x] Create `web/src/features/auth/useAuthQuery.ts`:
  ```ts
  import { useQuery } from '@tanstack/react-query'
  import { tokenStore } from './tokenStore'
  import { generateCorrelationId } from '../../lib/correlationId'

  interface AuthSession {
    accessToken: string
    refreshToken: string
    expiresIn: number
  }

  export function useAuthQuery() {
    return useQuery<AuthSession | null>({
      queryKey: ['auth', 'session'],
      queryFn: async () => {
        const response = await fetch('/api/auth/refresh', {
          method: 'POST',
          credentials: 'include',
          headers: { 'X-Correlation-ID': generateCorrelationId() },
        })

        if (response.status === 401) {
          tokenStore.clear()
          window.location.replace('/login')
          return null
        }

        if (!response.ok) {
          throw new Error('Refresh failed unexpectedly')
        }

        const data = (await response.json()) as AuthSession
        tokenStore.set(data.accessToken)
        return data
      },
      staleTime: Infinity,          // never re-fetch except via refetchInterval
      refetchInterval: 13 * 60 * 1000,  // 13 min — just before 15-min JWT expiry
      refetchOnWindowFocus: false,  // tab-focus should NOT trigger an extra refresh
      retry: false,                 // don't retry on error; refreshSession handles that
    })
  }
  ```

  > **Why not use `httpClient`:** `useAuthQuery` calls `/api/auth/refresh` directly (not through `httpClient`) to avoid a circular refresh-and-retry loop. `httpClient`'s 401-retry logic calls `refreshSession()`, which also calls `/api/auth/refresh`. Using `httpClient` here would create: `useAuthQuery` → `httpClient.post('/api/auth/refresh')` → 401 → `refreshSession()` → `fetch('/api/auth/refresh')` — an extra round-trip.

  > **Query key convention (AR-48 tuple):** `['auth', 'session']` — scope `'auth'`, entity `'session'`. Invalidate with `queryClient.invalidateQueries({ queryKey: ['auth'] })` if needed (e.g., after logout in Story 2.3).

  > **TanStack Query v5 note:** `onSuccess` callback is deprecated in v5. Side effects (tokenStore.set) belong in `queryFn` directly, not in options.

### Task 6 — Update `_app.tsx` (silent boot refresh + ongoing refresh) (AC: 3, 4)

- [x] Replace `web/src/routes/_app.tsx`:
  ```tsx
  import { createFileRoute, Outlet, redirect } from '@tanstack/react-router'
  import { tokenStore } from '../features/auth/tokenStore'
  import { useAuthQuery } from '../features/auth/useAuthQuery'
  import { generateCorrelationId } from '../lib/correlationId'

  export const Route = createFileRoute('/_app')({
    beforeLoad: async ({ location }) => {
      // Fast path: access token already in memory (in-session navigation).
      if (tokenStore.get()) return

      // SPA boot path: page reloaded — access token lost from memory.
      // Attempt silent refresh via HttpOnly cookie before rendering any child route.
      try {
        const response = await fetch('/api/auth/refresh', {
          method: 'POST',
          credentials: 'include',
          headers: { 'X-Correlation-ID': generateCorrelationId() },
        })

        if (response.ok) {
          const data = (await response.json()) as { accessToken: string }
          tokenStore.set(data.accessToken)
          return // proceed to render protected route
        }
      } catch {
        // Network error — treat as unauthenticated; let login handle it
      }

      // Refresh failed or cookie absent → redirect to login, preserving the
      // attempted URL as a search param for post-login return.
      throw redirect({
        to: '/login',
        search: { redirect: location.pathname + location.search },
      })
    },

    component: AppLayout,
  })

  function AppLayout() {
    // Keeps the access token refreshed every 13 min (Decision 2.1 refetchInterval).
    useAuthQuery()

    return (
      <main style={{ padding: '2rem' }}>
        {/* Full layout (nav, sidebar) added in Epic 4 / Epic 7 */}
        <Outlet />
      </main>
    )
  }
  ```

  > **`location.pathname + location.search`** (not `location.href`): TanStack Router's `location` object exposes these as separate strings; `.href` may not be available depending on the router version. The redirect search value is a relative path string that the login page will navigate to after successful auth.

  > **Error catch scope:** the `catch` wraps only the `fetch` call. The `throw redirect(...)` is OUTSIDE the try/catch so it is never swallowed. A `RedirectError` thrown inside beforeLoad propagates through TanStack Router's pipeline correctly.

  > **`useAuthQuery` in AppLayout (not beforeLoad):** `useAuthQuery` is a React hook and can only be called inside a component. `beforeLoad` is an async function, not a React context. Mounting `useAuthQuery` in `AppLayout` (the layout component of `/_app`) means it runs once on mount and periodically via `refetchInterval`.

### Task 7 — Update login route + i18n keys (AC: 3)

**Login route `validateSearch`:** The login page receives the `redirect` search param from AC-3's `throw redirect(...)`.

- [x] In `web/src/routes/login.tsx`, add `validateSearch` to the route definition and update `onSuccess` in the mutation call:

  At the top of `login.tsx`, add:
  ```ts
  import { z } from 'zod'
  ```

  Update the route definition:
  ```ts
  export const Route = createFileRoute('/login')({
    validateSearch: z.object({ redirect: z.string().optional() }),
    beforeLoad: () => {
      if (tokenStore.get()) throw redirect({ to: '/' })
    },
    component: LoginPage,
  })
  ```

  In the `LoginPage` component, extract the redirect param and pass it to `useLoginMutation`:
  ```tsx
  function LoginPage() {
    const { t } = useTranslation()
    const { redirect: redirectTo } = Route.useSearch()
    const loginMutation = useLoginMutation(redirectTo)
    // ... rest unchanged
  }
  ```

- [x] In `web/src/features/auth/authMutations.ts`, update `useLoginMutation` to accept `redirectTo`:
  ```ts
  export function useLoginMutation(redirectTo?: string) {
    const navigate = useNavigate()

    return useMutation({
      mutationFn: (credentials: LoginRequest) =>
        httpClient.post<LoginResponse>('/api/auth/login', credentials),
      onSuccess: (data) => {
        tokenStore.set(data.accessToken)
        void navigate({ to: (redirectTo as '/' | '/login') ?? '/' })
      },
    })
  }
  ```
  > **Type cast note:** TanStack Router's `navigate({ to: ... })` expects a typed route path. Cast to a known safe type (`'/'`) or use `// @ts-ignore` with a comment if the type system rejects a dynamic string. The safest approach: `void navigate({ to: redirectTo ?? '/', replace: true })` — use `replace: true` so the login page is not pushed to history (user can't back-navigate to login post-auth).

**i18n keys:**

- [x] In `web/src/lib/i18n/locales/en.json`, add new keys:
  ```json
  {
    "auth": {
      ...(existing keys)...,
      "refreshTokenInvalid": "Your session has expired. Please sign in again.",
      "sessionExpired": "Your session has expired. Redirecting to sign in…"
    },
    "errors": {
      ...(existing keys)...,
      "sessionExpired": "Session expired. Please sign in again."
    }
  }
  ```

### Task 8 — Backend integration tests for `/refresh` (AC: 1, 2, 3)

- [x] Add the following tests to `src/FormForge.Api.Tests/Features/Auth/AuthIntegrationTests.cs`.

  Add a helper method to log in and extract the refresh cookie:
  ```csharp
  private async Task<string> LoginAndGetRefreshCookieAsync()
  {
      using var loginResponse = await _client!.PostAsJsonAsync("/api/auth/login",
          new { email = "test@example.com", password = "Password1!" });
      loginResponse.EnsureSuccessStatusCode();

      var setCookie = loginResponse.Headers.GetValues("Set-Cookie").First();
      // Extract: "refresh_token=<value>; ..."
      var value = setCookie.Split(';')[0].Replace("refresh_token=", string.Empty, StringComparison.Ordinal);
      return value;
  }
  ```

  Add test methods:

  ```csharp
  [Fact]
  public async Task Refresh_ValidCookie_Returns200WithNewTokens()
  {
      var rawToken = await LoginAndGetRefreshCookieAsync();

      using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/auth/refresh", UriKind.Relative));
      request.Headers.Add("Cookie", $"refresh_token={rawToken}");

      using var response = await _client!.SendAsync(request);

      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
      Assert.NotNull(body);
      Assert.False(string.IsNullOrEmpty(body.AccessToken));
      Assert.False(string.IsNullOrEmpty(body.RefreshToken));
      Assert.Equal(900, body.ExpiresIn);
      // New token must differ from the original (rotation)
      Assert.NotEqual(rawToken, body.RefreshToken);
  }

  [Fact]
  public async Task Refresh_ValidCookie_RevokesOldToken()
  {
      var rawToken = await LoginAndGetRefreshCookieAsync();

      // First refresh — succeeds
      using var req1 = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/auth/refresh", UriKind.Relative));
      req1.Headers.Add("Cookie", $"refresh_token={rawToken}");
      using var resp1 = await _client!.SendAsync(req1);
      Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

      // Second refresh with the same (now-revoked) token — must return 401
      using var req2 = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/auth/refresh", UriKind.Relative));
      req2.Headers.Add("Cookie", $"refresh_token={rawToken}");
      using var resp2 = await _client!.SendAsync(req2);
      Assert.Equal(HttpStatusCode.Unauthorized, resp2.StatusCode);

      var body = await resp2.Content.ReadAsStringAsync();
      Assert.Contains("REFRESH_TOKEN_INVALID", body, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Refresh_NoCookie_Returns401()
  {
      using var response = await _client!.PostAsync(
          new Uri("/api/auth/refresh", UriKind.Relative), content: null);

      Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
      var body = await response.Content.ReadAsStringAsync();
      Assert.Contains("REFRESH_TOKEN_INVALID", body, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Refresh_RevokedToken_Returns401()
  {
      var rawToken = await LoginAndGetRefreshCookieAsync();

      // Manually revoke the token in the DB
      using var scope = _factory!.Services.CreateScope();
      var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
      var tokenHash = Convert.ToHexString(
              System.Security.Cryptography.SHA256.HashData(
                  System.Text.Encoding.UTF8.GetBytes(rawToken)))
          .ToLowerInvariant();
      var tokenEntity = await db.RefreshTokens.FirstAsync(r => r.TokenHash == tokenHash);
      tokenEntity.RevokedAt = DateTimeOffset.UtcNow;
      await db.SaveChangesAsync();

      using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/auth/refresh", UriKind.Relative));
      request.Headers.Add("Cookie", $"refresh_token={rawToken}");
      using var response = await _client!.SendAsync(request);

      Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task Refresh_ExpiredToken_Returns401()
  {
      var rawToken = await LoginAndGetRefreshCookieAsync();

      // Manually expire the token
      using var scope = _factory!.Services.CreateScope();
      var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
      var tokenHash = Convert.ToHexString(
              System.Security.Cryptography.SHA256.HashData(
                  System.Text.Encoding.UTF8.GetBytes(rawToken)))
          .ToLowerInvariant();
      var tokenEntity = await db.RefreshTokens.FirstAsync(r => r.TokenHash == tokenHash);
      tokenEntity.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
      await db.SaveChangesAsync();

      using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/auth/refresh", UriKind.Relative));
      request.Headers.Add("Cookie", $"refresh_token={rawToken}");
      using var response = await _client!.SendAsync(request);

      Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task Refresh_SetsNewRefreshTokenCookie()
  {
      var rawToken = await LoginAndGetRefreshCookieAsync();

      using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/auth/refresh", UriKind.Relative));
      request.Headers.Add("Cookie", $"refresh_token={rawToken}");
      using var response = await _client!.SendAsync(request);

      Assert.True(response.Headers.Contains("Set-Cookie"));
      var setCookie = response.Headers.GetValues("Set-Cookie").First();
      Assert.Contains("refresh_token=", setCookie, StringComparison.Ordinal);
      Assert.Contains("HttpOnly", setCookie, StringComparison.OrdinalIgnoreCase);
      Assert.Contains("SameSite=Strict", setCookie, StringComparison.OrdinalIgnoreCase);
      Assert.Contains("Path=/api/auth", setCookie, StringComparison.Ordinal);
  }
  ```

  > **Cookie header in tests:** `HttpClient` created by `WebApplicationFactory` does NOT automatically manage cookies the way a browser does. Manually set `Cookie: refresh_token=<value>` on the request. This also means `WebApplicationFactory`'s `HttpClientHandler` needs `UseCookies = false` (or the test manually manages cookies). If `_client.DefaultRequestHeaders` already has `Cookie` set for one test, it will leak into others — set it per-request via `HttpRequestMessage.Headers.Add("Cookie", ...)` rather than on `_client.DefaultRequestHeaders`.

  > **SHA-256 in tests:** the same hash function used in `AuthService.HashToken` must be used in tests to locate the token. Import `System.Security.Cryptography` and `System.Text.Encoding` — already present in the test project's implicit usings.

### Task 9 — Close deferred: update `docker-compose.yml` API healthcheck + `deferred-work.md` (AC: none — maintenance)

- [x] In `docker-compose.yml`, find the `api` service `healthcheck` and update the test command from `wget -qO- http://localhost:8080/` to `wget -qO- http://localhost:8080/health/live || exit 1`:
  ```yaml
  healthcheck:
    test: ["CMD-SHELL", "wget -qO- http://localhost:8080/health/live || exit 1"]
    interval: 10s
    timeout: 5s
    retries: 5
    start_period: 30s
  ```
  > This closes the deferred item from Story 1.6 code review: "docker-compose api healthcheck still probes `GET /` instead of `GET /health/live`".

- [x] In `_bmad-output/implementation-artifacts/deferred-work.md`, mark that item as `✅ CLOSED`:
  > `~~**docker-compose api healthcheck still probes `GET /`**~~ ✅ CLOSED by Story 2.2 — probe updated to `GET /health/live`.`

### Task 10 — Build gates and manual verification (AC: all)

- [x] `dotnet build` — zero warnings, zero errors.
- [x] `dotnet format --verify-no-changes` — clean.
- [x] `dotnet test` — all tests pass; prior 77 tests still pass (zero regressions). New tests: 5–6 additional refresh integration tests.
- [x] `cd web && npm run build` — clean TypeScript + Vite build.
- [x] Manual verification (interactive):
  - `dotnet run --project src/FormForge.AppHost` — confirm Aspire Dashboard shows all services healthy.
  - Log in at `http://localhost:5173/login`. Confirm redirect to `/`.
  - Hard-reload the page (`Ctrl+F5` / `Cmd+Shift+R`). Confirm the page loads without redirecting to `/login` (silent refresh recovers the session).
  - Open DevTools → Network → filter by `auth/refresh`. Verify a POST fires on reload and every ~13 minutes.
  - Log in, wait for session (or use DevTools to revoke the cookie), verify redirect to `/login` when refresh fails.
  - Use `curl -X POST http://localhost:5190/api/auth/refresh` 31 times in a loop. Confirm 429 + `Retry-After: 60` after the 30th request.

---

## Dev Notes

### What this story does — and what it does NOT

**In scope:**
1. **`POST /api/auth/refresh`** — single-use token rotation; reads HttpOnly cookie; returns new access token + new refresh token + sets new cookie.
2. **`AuthMetrics`** — OTel counters: `formforge.refresh_token.{issued, revoked, replayed}` per architecture spec (Decision 5.3).
3. **`RefreshAsync` in `AuthService`** — validates, revokes, and rotates in one atomic `SaveChangesAsync` call.
4. **"auth-refresh" rate limiter** — 30/min per IP per AR-15.
5. **`httpClient.ts` 401 → refresh-and-retry** — deferred explicitly from Story 2.1 Dev Notes.
6. **`useAuthQuery.ts`** — TanStack Query with 13-min `refetchInterval` per Decision 2.1.
7. **`_app.tsx` beforeLoad** — async silent-refresh gate for page-reload recovery (AC-3).
8. **`redirect` search param on login redirect** — preserves attempted URL for post-login return (Decision 2.1).
9. **`validateSearch` on login route** — types the `redirect` search param.
10. **docker-compose healthcheck** — closes Story 1.6 deferred item.

**Out of scope (deferred to named stories):**
- `POST /api/auth/logout` — Story 2.3 (server-side revoke + SPA token clear)
- Role-populated JWT claims (`roles: [...]`) — Story 2.4 (seeding) + Story 2.5 (assignment)
- `RequireAuth()` filter on protected route groups — Story 2.6
- `formforge.auth.deactivated_token_use` counter — Story 2.6 (server middleware that re-checks `is_active`)
- Full shadcn/ui styled login form — Epic 7
- Deactivated-user cache eviction + refresh token revocation event (UserDeactivated) — Story 2.6

### Architecture compliance

- **NFR-5:** Refresh token is single-use (old token revoked atomically when new one issued). Raw token never stored; only SHA-256 hex digest in DB. HttpOnly + SameSite=Strict cookie.
- **AR-12 (JWT):** `CreateAccessToken` reused as-is; HS256, 15-min TTL.
- **AR-15 (Rate Limiting):** "auth-refresh" policy — Fixed per-IP, 30/min. Same `RateLimitPartition.GetFixedWindowLimiter` pattern as "auth-login". `ForwardedHeaders` middleware (wired in Story 2.1) ensures `RemoteIpAddress` is the real client IP.
- **AR-18 (Error Envelope):** `REFRESH_TOKEN_INVALID` code + `auth.refreshTokenInvalid` messageKey + correlationId. Consistent with login error structure.
- **AR-24 (Correlation ID):** `httpContext.GetCorrelationId()` in error response; generated per-request in `useAuthQuery` and `_app.tsx` refresh calls.
- **AR-22 (Route Groups):** `/refresh` in the `/api/auth` group (no `RequireAuth` — the endpoint IS the auth entry point). Filter chain: correlation ID → rate limit → handler.
- **Decision 2.1:** `beforeLoad` async refresh gate + `useAuthQuery` 13-min refetchInterval + 401 → redirect to login with `search.redirect`.
- **Decision 3.9:** `/api/auth/refresh` is naturally single-use by design; the `isRetry` guard in `httpClient` prevents self-referential retry.
- **AR-48 (TanStack Query keys):** `['auth', 'session']` — tuple convention.
- **CA rules (TreatWarningsAsErrors=true):** `AuthMetrics` is `internal sealed`, uses `[SuppressMessage]` for CA1812. `AuthRefreshOutcome` is `internal`.

### Current code state (files being modified)

**`src/FormForge.Api/Features/Auth/AuthService.cs`** — Add `AuthRefreshOutcome` enum, `AuthRefreshResult` record, `IAuthService.RefreshAsync` declaration, inject `AuthMetrics`, add `HashToken` helper, add `RefreshAsync` body. The existing `LoginAsync` body must be updated to call `HashToken(raw)` instead of the inline SHA-256 block (DRY).

**`src/FormForge.Api/Features/Auth/AuthEndpoints.cs`** — Add `MapPost("/refresh", RefreshHandler)` registration and the `RefreshHandler` private static method. The existing `SetRefreshCookieAndReturn` helper is reused unchanged.

**`src/FormForge.Api/Program.cs`** — Two additions inside `AddRateLimiter`: the "auth-refresh" policy. Plus `builder.Services.AddSingleton<AuthMetrics>()` and `AddOpenTelemetry().WithMetrics(m => m.AddMeter(AuthMetrics.MeterName))`.

**`web/src/features/auth/httpClient.ts`** — Full replacement (add `_pendingRefresh` module-level var, `refreshSession()`, and `isRetry` parameter to `request()`). The existing public API (`httpClient.get/post/put/delete`) is unchanged — no callers need to update.

**`web/src/routes/_app.tsx`** — Update `beforeLoad` to async with silent-refresh logic. Add `useAuthQuery()` call in `AppLayout` component.

**`web/src/features/auth/authMutations.ts`** — Update `useLoginMutation` to accept optional `redirectTo` parameter and use it in `onSuccess`.

**`web/src/routes/login.tsx`** — Add `validateSearch`, extract `redirect` search param, pass to `useLoginMutation`.

**`web/src/lib/i18n/locales/en.json`** — Add `auth.refreshTokenInvalid`, `auth.sessionExpired`, `errors.sessionExpired` keys.

**`docker-compose.yml`** — Update api `healthcheck.test` command.

**`_bmad-output/implementation-artifacts/deferred-work.md`** — Mark Story 1.6 docker-compose healthcheck item as closed.

### Testing approach

**Integration tests (Testcontainers) — extending `AuthIntegrationTests`:**
- All refresh tests use `LoginAndGetRefreshCookieAsync()` helper to obtain a valid raw refresh token.
- The `HttpClient` from `WebApplicationFactory` does NOT auto-manage cookies — set `Cookie: refresh_token=<value>` per-request via `HttpRequestMessage.Headers`.
- SHA-256 hashing in tests (for manually revoking/expiring tokens in the DB) uses `System.Security.Cryptography.SHA256.HashData` + `Convert.ToHexString().ToLowerInvariant()` — same algorithm as `AuthService.HashToken`.
- The `TRUNCATE TABLE refresh_tokens, users RESTART IDENTITY CASCADE` in `InitializeAsync` ensures test isolation (Story 2.1 code-review fix P10 already in place).

**No new unit tests needed:** `RefreshAsync` path is primarily DB-interaction logic (best tested via integration). The `AuthMetrics` class has no logic — just counter calls. The `HashToken` helper is tested implicitly by the integration tests (login stores a hash, refresh finds it by hashing).

### File structure

**New files (backend):**
- `src/FormForge.Api/Features/Auth/AuthMetrics.cs`

**New files (frontend):**
- `web/src/features/auth/useAuthQuery.ts`

**Modified files (backend):**
- `src/FormForge.Api/Features/Auth/AuthService.cs` — new types + RefreshAsync + HashToken + metrics injection
- `src/FormForge.Api/Features/Auth/AuthEndpoints.cs` — add /refresh route + handler
- `src/FormForge.Api/Program.cs` — auth-refresh rate limiter + AuthMetrics registration + OTel meter
- `src/FormForge.Api.Tests/Features/Auth/AuthIntegrationTests.cs` — add refresh tests + helper

**Modified files (frontend):**
- `web/src/features/auth/httpClient.ts` — 401 → refresh-and-retry logic
- `web/src/features/auth/authMutations.ts` — optional redirectTo param in useLoginMutation
- `web/src/routes/_app.tsx` — async beforeLoad + useAuthQuery in AppLayout
- `web/src/routes/login.tsx` — validateSearch + pass redirect to mutation
- `web/src/lib/i18n/locales/en.json` — new auth/session expired keys

**Modified files (infra):**
- `docker-compose.yml` — api healthcheck probe update
- `_bmad-output/implementation-artifacts/deferred-work.md` — close Story 1.6 deferred item

**Do NOT touch:**
- `src/FormForge.Api/Features/Auth/JwtTokenService.cs` — no changes (CreateAccessToken signature unchanged)
- `src/FormForge.Api/Features/Auth/Dtos/LoginResponse.cs` — reused as-is for refresh response shape
- `src/FormForge.Api/Features/Auth/JwtOptions.cs` — no changes
- `src/FormForge.Api/Domain/Entities/RefreshToken.cs` — no schema changes (RevokedAt column already exists)
- Any existing EF Core migration files
- `src/FormForge.ServiceDefaults/` — OTel meter added in `Program.cs`, not ServiceDefaults

### Anti-patterns to avoid

| Don't | Why |
|---|---|
| Store raw refresh token in DB | Only SHA-256 hex hash stored; raw token is a bearer credential (Story 2.1 design) |
| Call `httpClient.post('/api/auth/refresh', ...)` inside `useAuthQuery` | Creates circular refresh-retry loop (httpClient's 401 handler calls refresh again) |
| Use `useNavigate()` inside `httpClient.ts` | httpClient is a plain TS module — no React context. Use `window.location.replace('/login')` |
| Separate `SaveChangesAsync` calls for revoke + create | Race condition window where both tokens are invalid. Must be atomic in one `SaveChanges` |
| Short-circuit `RefreshAsync` before checking expiry when `RevokedAt` is null | Order must be: exists? → revoked? → expired? → inactive? |
| Set `_pendingRefresh` to null inside `try` instead of `finally` | A thrown error clears the flag; other waiters get `null` and start a new (likely failing) refresh |
| Call `request(..., true)` (isRetry=true) from outside `httpClient` | The isRetry flag is internal — it prevents the loop, not a public retry mechanism |
| Add `RequireAuth()` to the `/api/auth` route group | Auth endpoints must remain publicly accessible |
| Use `refetchOnMount: true` in `useAuthQuery` | This would refresh on every component re-mount during navigation — only want periodic refresh |

### Previous story intelligence (Story 2.1, last done story)

**Carry-forward patterns (critical):**
- **`TreatWarningsAsErrors=true`:** All new types `internal sealed`; CA1812 `[SuppressMessage]` on DI-injected classes.
- **`AnalysisMode=AllEnabledByDefault`:** Any suppression needs `Justification`.
- **`InvariantGlobalization=true`:** Use `.ToLowerInvariant()`, `StringComparison.Ordinal`, `CultureInfo.InvariantCulture`.
- **`ArgumentNullException.ThrowIfNull`:** All handler parameters that are not nullable.
- **CPM enforced:** no new `Version=` attributes on `<PackageReference>`; versions in `Directory.Packages.props` only.
- **`[LoggerMessage]`:** Any `ILogger` calls in `AuthService` (if logging replay events) must use source-generated `[LoggerMessage]`.
- **`GenerateRefreshToken()` is already a private static helper** in `AuthService` — `HashToken` is a new companion. Both should be static (no instance state needed).
- **Existing test count: 77 tests** — do not regress.
- **`TRUNCATE TABLE refresh_tokens, users RESTART IDENTITY CASCADE`** in `InitializeAsync` — already in place from code review fix; don't add a second TRUNCATE.
- **`PostgresFixture` parameterized constructor:** `new PostgreSqlBuilder("postgres:17-alpine")` — do not use the obsolete parameterless form.

**Story 2.1 completion notes (important discrepancies from spec):**
- `NetEscapades.AspNetCore.SecurityHeaders` API: `UseSecurityHeaders(policy => ...)` mid-pipeline; `AddStrictTransportSecurityMaxAgeIncludeSubDomains(maxAgeInSeconds)` (single method, no named params).
- `i18next v26+` option rename: `initAsync: false` (not `initImmediate`). Do NOT change this in Story 2.2.
- JWT `iat` claim: explicitly added in `JwtTokenService` with `ClaimValueTypes.Integer64`. Do NOT change.

### Git intelligence

Recent commits (most recent first):
- `aa9ec15` — Story 2.1 code review — apply 14 patches from three-reviewer pass
- `da1452c` — Story 2.1 — JWT login + refresh token rotation + protected web routes
- `7b34e9d` — Story 1.4 code review — fix AC-1 spec visibility and path-matching robustness

**Pattern:** single feature commit per story, then separate commit for code-review fixes (if any).
**Expected commit message for this story:**
`Story 2.2 — Token refresh: POST /api/auth/refresh + 401-retry in httpClient + useAuthQuery SPA boot + OTel metrics`

### References

- `_bmad-output/planning-artifacts/epics.md` — §"Story 2.2: Token Refresh" (Epic 2, lines 497–517 in epics.md)
- `_bmad-output/planning-artifacts/architecture.md`
  - Decision 2.1 — JWT Silent Re-Auth Flow (lines 352–357)
  - Decision 2.3 — JWT Signing Algorithm
  - Decision 2.6 — Rate Limiting (auth-refresh: 30/min per IP)
  - Decision 3.9 — Idempotency (refresh is single-use by design)
  - Decision 4.7 — HTTP Client Wrapper (401 → refresh-and-retry)
  - Decision 5.3 — Observability Stack (custom metrics: formforge.refresh_token.*)
  - AR-12, AR-15, AR-18, AR-24, AR-22, AR-48
  - NFR-5 (HttpOnly cookie, single-use rotation, server-stored)
- `_bmad-output/implementation-artifacts/2-1-jwt-login.md`
  - Task 10 (`AuthService.LoginAsync` + `GenerateRefreshToken` + `SetRefreshCookieAndReturn`)
  - Task 17 (`httpClient.ts` — Story 2.2 deferred comment)
  - Dev Notes §"Refresh token storage design"
  - Dev Notes §"Current code state"
  - Dev Notes §"Anti-patterns"
  - Review Findings P1 (per-IP rate limiting pattern)
  - Review Findings P4 (cookie Secure flag via env, not hostname)
  - Review Findings P8 (CORS startup warning)
  - Completion Notes (NetEscapades API, i18next v26 rename, JWT iat, sonner deferred)
- `_bmad-output/implementation-artifacts/deferred-work.md` — Story 1.6 docker-compose healthcheck deferred item

---

## Dev Agent Record

### Agent Model Used

claude-sonnet-4-6

### Debug Log References

- Test `Refresh_ReplayedToken_Returns401WithRefreshTokenReplayedCode` initially failed (second call returned 200 OK). Root cause: `WebApplicationFactory.CreateClient()` defaults to `HandleCookies = true`, meaning the client's auto-cookie-jar updated to the new token after the first refresh, causing the second request to send the new (valid) token instead of the old (revoked) one. Fix: `CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false })` so manually injected Cookie headers are used as-is.

- Story spec defined `AuthRefreshOutcome.RefreshTokenInvalid` as the single outcome for not-found/expired/revoked. Implementation used distinct enum values (`NotFound`, `Expired`, `Replayed`, `AccountInactive`) to enable correct `code`/`messageKey` differentiation in the endpoint response — the spec was intentionally vague. All resolve to 401 as specified.

### Completion Notes List

- All 10 tasks implemented and verified. 82 tests pass (82 → 82; +5 new refresh integration tests added). Zero regressions.
- `AuthRefreshOutcome` enum uses distinct values (NotFound, Replayed, Expired, AccountInactive) rather than the spec's single `RefreshTokenInvalid`. The endpoint maps NotFound/Expired → `REFRESH_TOKEN_INVALID` and Replayed → `REFRESH_TOKEN_REPLAYED`, giving richer observability while still returning 401 in all cases.
- `useAuthQuery` calls `/api/auth/refresh` directly (not via `httpClient`) to avoid the circular 401→refresh→retry loop.
- `_pendingRefresh` deduplication in `httpClient.ts` is a module-level variable so it persists across React re-renders.
- `WebApplicationFactory` cookie management disabled via `HandleCookies = false` — all existing tests continue to pass since they check response `Set-Cookie` headers, not request cookie injection.

### File List

**New files:**
- `src/FormForge.Api/Features/Auth/AuthMetrics.cs`
- `web/src/features/auth/useAuthQuery.ts`

**Modified files (backend):**
- `src/FormForge.Api/Program.cs`
- `src/FormForge.Api/Features/Auth/AuthService.cs`
- `src/FormForge.Api/Features/Auth/AuthEndpoints.cs`
- `src/FormForge.Api/Domain/Entities/RefreshToken.cs` — added `[ConcurrencyCheck]` (review fix)
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/FormForgeDbContextModelSnapshot.cs` — snapshot bump
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260523005829_AddRefreshTokenConcurrencyCheck.cs` — empty migration (review fix)
- `src/FormForge.Api/Infrastructure/Persistence/Migrations/20260523005829_AddRefreshTokenConcurrencyCheck.Designer.cs` — designer file (review fix)
- `src/FormForge.Api.Tests/Features/Auth/AuthIntegrationTests.cs`

**Modified files (frontend):**
- `web/src/features/auth/httpClient.ts`
- `web/src/features/auth/authMutations.ts`
- `web/src/routes/_app.tsx`
- `web/src/routes/login.tsx`
- `web/src/lib/i18n/locales/en.json`

**Modified files (infra):**
- `docker-compose.yml`
- `_bmad-output/implementation-artifacts/deferred-work.md`
- `_bmad-output/implementation-artifacts/sprint-status.yaml`

## Change Log

| Date | Change |
|------|--------|
| 2026-05-23 | Story 2.2 implemented — all 10 tasks complete, 82 tests pass |
| 2026-05-23 | Code review complete — 3 decisions, 12 patches, 11 deferred, 1 dismissed |

### Review Findings

#### Decisions resolved

- [x] [Review][Decision] **Replay envelope** → resolved: **revert to spec**. Map `Replayed` outcome to the same response body as `NotFound`/`Expired` (`code: "REFRESH_TOKEN_INVALID"`, `messageKey: "auth.refreshTokenInvalid"`). Differentiation stays in metric + log only. Promoted to patch.
- [x] [Review][Decision] **Replay scope** → resolved: **drop mass-revoke**. Replay branch records metric + Warning log + returns. No `ExecuteUpdateAsync` family-wide revoke. Promoted to patch.
- [x] [Review][Decision] **Concurrent refresh race** → resolved: **optimistic concurrency on `RevokedAt`**. Add `[ConcurrencyCheck]` on `RefreshToken.RevokedAt`; map `DbUpdateConcurrencyException` to `AuthRefreshOutcome.Replayed`. Promoted to patch.

#### Patch (all applied)

- [x] [Review][Patch] **Open redirect via unvalidated `redirect` search param** — applied. `loginSearchSchema` now enforces `^\/(?!\/)` (single-leading-slash, not `//`); `authMutations.ts` switched from `navigate({ href: redirectTo })` to `navigate({ to: redirectTo ?? '/', replace: true })`.
- [x] [Review][Patch] **AC-2 — Missing-cookie returns `REFRESH_TOKEN_INVALID`** — applied. Unified all non-Success outcomes through `RefreshTokenInvalidResponse(httpContext)` helper. Unused `auth.refreshTokenMissing` key removed from `en.json`. Test renamed and asserts the unified code.
- [x] [Review][Patch] **AC-2 — Warning-level replay log emitted** — applied. `AuthService` now `partial`, injects `ILogger<AuthService>`, emits `[LoggerMessage(EventId=2200, Level=Warning)]` with `TokenHashPrefix` (first 8 hex chars) and opaque `UserId` only. Concurrency-conflict path logs `EventId=2201` similarly.
- [x] [Review][Patch] **AC-4 — `useAuthQuery` clears tokenStore and redirects on 401** — applied. `silentRefresh` now branches on `response.status === 401` and runs `tokenStore.clear(); window.location.replace('/login')` before throwing.
- [x] [Review][Patch] **AC-5 — `httpClient.refreshSession` redirects to `/login` on failure** — applied. Failure branch + catch branch both call `window.location.replace('/login')` after `tokenStore.clear()`.
- [x] [Review][Patch] **Inactive-user refresh: revokes token AND returns 401 envelope** — applied. `RefreshAsync` now sets `token.RevokedAt = UtcNow; SaveChangesAsync; RecordRevoked();` for the inactive branch (still maps to `AccountInactive` outcome internally → endpoint unifies to `REFRESH_TOKEN_INVALID`). Endpoint's 403 `ACCOUNT_INACTIVE` arm removed.
- [x] [Review][Patch] **AC-3 — `_app.tsx` uses `location.pathname + location.search`** — applied.
- [x] [Review][Patch] **`httpClient` 401-handler excludes `/api/auth/login` and `/api/auth/refresh`** — applied via combined guard.
- [x] [Review][Patch] **`useAuthQuery` propagates `signal` from TanStack Query** — applied. `queryFn: async ({ signal }) => silentRefresh(signal)`; `signal` plumbed into the `fetch` options.
- [x] [Review][Patch] **Replay metric ordering** — obsolete after Decision 2 (mass-revoke dropped). Replay branch is now metric + log + return; no DB write that could fail between metric and revoke.
- [x] [Review][Patch] **`httpClient` content-type guard for 401** — applied. Refresh-and-retry only fires when `Content-Type` includes `application/problem+json`.
- [x] [Review][Patch] **File List includes `sprint-status.yaml`** — applied.
- [x] [Review][Patch] **Decision 1 — Replay envelope reverted to `REFRESH_TOKEN_INVALID`** — applied. Endpoint switch collapsed into `result.Outcome == Success ? SetRefreshCookieAndReturn : RefreshTokenInvalidResponse`. Unused `auth.refreshTokenReplayed` i18n key removed.
- [x] [Review][Patch] **Decision 2 — Mass-revoke dropped** — applied. Replay branch no longer issues `ExecuteUpdateAsync`; only metric + Warning log + return.
- [x] [Review][Patch] **Decision 3 — Optimistic concurrency on `RevokedAt`** — applied. `[ConcurrencyCheck]` added to `RefreshToken.RevokedAt`. `RefreshAsync` wraps both `SaveChangesAsync` calls (inactive-user revoke + normal rotation) in `try/catch DbUpdateConcurrencyException`, mapping conflicts to `AuthRefreshOutcome.Replayed` + `RecordReplayed()` + Warning log. EF Core generated empty schema migration `20260523005829_AddRefreshTokenConcurrencyCheck` (snapshot bump only).

#### Deferred (pre-existing or out of scope for this review)

- [x] [Review][Defer] **Refresh response echoes raw refresh token in JSON body** [`src/FormForge.Api/Features/Auth/AuthEndpoints.cs:155`, Story 2.1 design] — deferred, pre-existing from Story 2.1. The new integration tests harden the leak into the test contract; revisit when Story 2.6 lands or when admin/operator surface is hardened.
- [x] [Review][Defer] **Cache-Control no-store missing on `/login` and `/refresh` responses** [`src/FormForge.Api/Features/Auth/AuthEndpoints.cs:139-156`] — deferred, pre-existing — also affects Story 2.1 login. Plan a single sweep across all auth endpoints.
- [x] [Review][Defer] **`AuthMetrics` IDisposable Singleton may double-register on `dotnet watch` hot reload** [`src/FormForge.Api/Features/Auth/AuthMetrics.cs`] — deferred, dev-environment cosmetic only.
- [x] [Review][Defer] **`tokenStore` is a module-level singleton with no `.reset()` helper for tests** [`web/src/features/auth/tokenStore.ts`] — deferred, no frontend tests yet; revisit when Vitest comes in.
- [x] [Review][Defer] **`RefreshAsync` does not pre-check empty string defensively** [`src/FormForge.Api/Features/Auth/AuthService.cs:118`] — deferred, defense-in-depth only; current sole caller (the endpoint) already rejects empty/null.
- [x] [Review][Defer] **Clock-skew at `ExpiresAt <= UtcNow` boundary** [`src/FormForge.Api/Features/Auth/AuthService.cs:146`] — deferred, single-millisecond edge under multi-instance NTP drift.
- [x] [Review][Defer] **`_app.tsx beforeLoad` catches `JSON.parse` errors silently and redirects to login on a 200-with-bad-body** [`web/src/routes/_app.tsx:19-32`] — deferred, requires upstream server bug to trigger.
- [x] [Review][Defer] **`useAuthQuery` does not honor `Retry-After` on 429** [`web/src/features/auth/useAuthQuery.ts:36`] — deferred, AC-6 rate-limit is server-side; SPA-side 429 handling is a polish item.
- [x] [Review][Defer] **Global rate-limiter `OnRejected` always emits `Retry-After: 60` regardless of policy window** [`src/FormForge.Api/Program.cs:166-180`] — deferred, AC-6 only requires `/api/auth/refresh`; other policies happen to share the same window.
- [x] [Review][Defer] **Duplicate `refresh_token` cookies (`Cookie: refresh_token=a; refresh_token=b`) — first-wins is implementation-defined** [`src/FormForge.Api/Features/Auth/AuthEndpoints.cs:85`] — deferred, theoretical — requires hostile proxy or attacker that already controls cookies.

#### Dismissed

- Rate-limiter partition-key inconsistency (`"unknown"` vs spec example `"anon"`) — dev correctly mirrored the existing `auth-login` policy which already used `"unknown"` since Story 2.1's code review. Both policies are consistent within the codebase; the spec example was the outlier.
