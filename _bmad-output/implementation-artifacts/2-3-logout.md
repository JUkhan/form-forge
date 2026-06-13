# Story 2.3: Logout

Status: done

<!-- Note: Validate with validate-create-story if needed before dev-story. -->

## Story

As an authenticated user,
I want to log out and revoke my refresh token,
so that my session cannot be resumed from this browser or from a stolen refresh cookie.

## Acceptance Criteria

### AC-1 — `POST /api/auth/logout` revokes the submitted refresh token and clears the cookie

**Given** I am logged in (a valid `refresh_token` HttpOnly cookie is present in the request)
**When** the client POSTs `/api/auth/logout` (cookie travels automatically — no request body)
**Then** the server returns HTTP 204 No Content
**And** the corresponding `refresh_tokens` row is marked `RevokedAt = now()` server-side
**And** the response sets a `refresh_token` cookie with `Expires=Thu, 01 Jan 1970 00:00:00 GMT` (or equivalent past date) + `Max-Age=0`, same `Path=/api/auth`, `HttpOnly`, `SameSite=Strict`, and `Secure` flags as the original cookie, so the browser drops the cookie atomically with the server-side revoke
**And** the `formforge.refresh_token.revoked` OTel counter (from Story 2.2's `AuthMetrics`) is incremented by 1

### AC-2 — Subsequent refresh with the revoked token fails closed

**Given** the user has just logged out (the previously valid refresh token is now `RevokedAt != null`)
**When** the client POSTs `/api/auth/refresh` with that same revoked token (e.g., a stale cached cookie, a stolen token, or an attacker)
**Then** the server returns HTTP 401 with `ProblemDetails { code: "REFRESH_TOKEN_INVALID", messageKey: "auth.refreshTokenInvalid", correlationId: "..." }`
**And** the `formforge.refresh_token.replayed` counter is incremented (the existing `AuthService.RefreshAsync` Replayed branch handles this — see Dev Notes)

### AC-3 — Logout is idempotent / no-information-leak on missing/invalid token

**Given** no `refresh_token` cookie is present in the request (already logged out, never logged in, cookie cleared by another tab)
**When** the client POSTs `/api/auth/logout`
**Then** the server returns HTTP 204 No Content (NOT 401) — logout is idempotent
**And** the response still emits the cookie-clearing Set-Cookie header (defense in depth — if a stale cookie is in the browser's jar that the server didn't see, the next browser navigation will overwrite it)
**And** no `AuthMetrics.RecordRevoked()` call is made (nothing was actually revoked)

**Given** a `refresh_token` cookie is present but the token is unknown / already revoked / expired
**When** the client POSTs `/api/auth/logout`
**Then** the server returns HTTP 204 No Content (same envelope as the success path — does not leak whether the token was valid)
**And** if the row exists and is currently unrevoked, it is revoked (covering the "expired but never revoked" race); otherwise no state change
**And** `RecordRevoked()` fires only when a row actually transitions from unrevoked → revoked

### AC-4 — SPA discards in-memory access token and navigates to `/login`

**Given** the user clicks the logout control in the SPA (Dev Notes describe placement)
**When** the `useLogoutMutation` mutation succeeds (or fails — see below)
**Then** `tokenStore.clear()` is called synchronously
**And** the TanStack Query cache is reset (`queryClient.clear()` or invalidation of the `['auth']` query key — see Dev Notes for the choice) so no stale per-user data persists for the next user on this machine
**And** the SPA navigates to `/login` with `replace: true` (so back-navigation cannot return to a now-unauthenticated route)
**And** the in-flight `useAuthQuery` 13-minute refetch timer is cancelled by the `/login` navigation unmounting `_app.tsx` (no extra code needed)

**Given** `POST /api/auth/logout` returns a network error or non-204 status
**When** `useLogoutMutation`'s `onSettled` runs
**Then** `tokenStore.clear()` + `queryClient.clear()` + navigate to `/login` still execute — the client-side logout is unconditional, because the user clicked logout and expects to be signed out regardless of the server's response (the server-side revoke is best-effort from the SPA's perspective; the HttpOnly cookie itself is also cleared by the browser when the response arrives, and worst case the refresh token continues to be valid for ≤7 days but no client holds it)

### AC-5 — Rate limiting on `POST /api/auth/logout`

**Given** rate-limiting policy AR-15 is configured
**When** the same IP sends more than 30 logout requests within 1 minute
**Then** subsequent requests receive HTTP 429 with `Retry-After: 60` (reuse the existing "auth-refresh" policy — see Dev Notes for the rationale)

### AC-6 — Logout endpoint requires no JWT (cookie-only authentication)

**Given** the access token has expired (15-min TTL elapsed) but the refresh cookie is still valid
**When** the client POSTs `/api/auth/logout` (no `Authorization` header, or an expired bearer token)
**Then** the server still processes the logout — it does NOT short-circuit on missing/invalid JWT
**And** the refresh token is revoked based on the cookie alone

**Rationale (do not paste into code):** Authentication on the logout endpoint is by *possession of the refresh cookie*, not the bearer token. The endpoint is the auth exit point — it must work in exactly the cases where the access token is unusable. The `/api/auth` route group is unauthenticated by design (see `Program.cs:278-280`); do not add `RequireAuth()` to the group.

---

## Tasks / Subtasks

> Conventions inherited from Stories 2.1 and 2.2 — all still in force:
> `internal sealed` on new types, `CA1812 [SuppressMessage]` on DI-injected ones, `ArgumentNullException.ThrowIfNull` on handler parameters, `InvariantGlobalization=true` (use `StringComparison.Ordinal` and `.ToLowerInvariant()`), `TreatWarningsAsErrors=true`, CPM (`Directory.Packages.props` only), `[LoggerMessage]` source-gen for any `ILogger` calls, single feature commit per story.

### Task 1 — Extend `IAuthService` with `LogoutAsync` (AC: 1, 2, 3)

Add a new outcome enum + result record + interface method to `src/FormForge.Api/Features/Auth/AuthService.cs`. Place them next to the existing `AuthRefreshOutcome` / `AuthRefreshResult` (around lines 22–31).

```csharp
internal enum AuthLogoutOutcome
{
    Revoked,        // a row was found unrevoked and we transitioned it to revoked
    NoOp,           // cookie missing, unknown token, already revoked, expired — all idempotent
}

internal sealed record AuthLogoutResult(AuthLogoutOutcome Outcome);
```

Extend the interface (currently lines 33–37 of `AuthService.cs`):

```csharp
internal interface IAuthService
{
    Task<AuthServiceResult> LoginAsync(string email, string password, CancellationToken ct);
    Task<AuthRefreshResult> RefreshAsync(string rawToken, CancellationToken ct);
    Task<AuthLogoutResult> LogoutAsync(string? rawToken, CancellationToken ct);  // NEW
}
```

> **Why a nullable `rawToken` parameter (unlike `RefreshAsync` which is non-null):** the logout endpoint accepts the no-cookie case as a valid input (AC-3) and the service is the right place to express the idempotent semantics. The endpoint should not need to short-circuit on `rawToken is null` before delegating.

### Task 2 — Implement `LogoutAsync` in `AuthService` (AC: 1, 3)

Add the method body to the existing `AuthService` partial class. Place it after `RefreshAsync` and reuse the existing `HashToken` helper (lines 228–230).

```csharp
public async Task<AuthLogoutResult> LogoutAsync(string? rawToken, CancellationToken ct)
{
    // AC-3: no cookie → idempotent NoOp. Do not 401 here.
    if (string.IsNullOrEmpty(rawToken))
    {
        return new AuthLogoutResult(AuthLogoutOutcome.NoOp);
    }

    var hash = HashToken(rawToken);

    // Lookup by hash (single round-trip). No Include(r => r.User) — the
    // logout flow has no need for user data; we only need the token row
    // and the RevokedAt column for the ConcurrencyCheck.
    var token = await db.RefreshTokens
        .FirstOrDefaultAsync(r => r.TokenHash == hash, ct)
        .ConfigureAwait(false);

    // AC-3: unknown token → idempotent NoOp. Do not 401.
    // (An attacker presenting a guessed cookie value learns nothing from
    // the 204 response — they would have received 204 with a valid cookie too.)
    if (token is null)
    {
        return new AuthLogoutResult(AuthLogoutOutcome.NoOp);
    }

    // AC-3: already revoked → idempotent NoOp. The state transition we
    // promised has already happened; no second metric increment.
    if (token.RevokedAt is not null)
    {
        return new AuthLogoutResult(AuthLogoutOutcome.NoOp);
    }

    // Optional: expired-and-unrevoked tokens are revoked anyway so the row
    // can never re-enter circulation if the expiry semantics ever change.
    // No early return — fall through to the revoke path.

    token.RevokedAt = DateTimeOffset.UtcNow;

    try
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
    catch (DbUpdateConcurrencyException)
    {
        // Another caller (refresh, parallel logout from another tab) just
        // revoked this row. Spec AC-3: idempotent NoOp from this caller's
        // perspective. Log Warning via the existing concurrency-conflict
        // LoggerMessage from RefreshAsync (re-used; same semantics).
        RefreshTokenConcurrencyConflict(logger, token.UserId);
        return new AuthLogoutResult(AuthLogoutOutcome.NoOp);
    }

    metrics.RecordRevoked();
    return new AuthLogoutResult(AuthLogoutOutcome.Revoked);
}
```

> **Why no `formforge.refresh_token.logged_out` counter is added in this story:** architecture (Decision 5.3, lines 645–651) lists exactly `formforge.refresh_token.{issued, revoked, replayed}`. Logout produces a `revoked` (when a row transitions); no new counter is in the spec for v1. If post-launch you want to disambiguate "revoked-by-logout" vs "revoked-by-rotation" vs "revoked-by-deactivation", add a `cause` tag to `RecordRevoked` in a later story — out of scope here.

### Task 3 — Add the `/logout` endpoint to `AuthEndpoints` (AC: 1, 3, 5, 6)

Add the route registration inside `MapAuthEndpoints` in `src/FormForge.Api/Features/Auth/AuthEndpoints.cs` (after the existing `/refresh` registration at line 22–28):

```csharp
group.MapPost("/logout", LogoutHandler)
     .RequireRateLimiting("auth-refresh")   // Reuse — see AC-5 rationale below
     .WithSummary("Revoke the refresh token and clear the refresh cookie")
     .WithDescription(
         "Reads the refresh token from the HttpOnly cookie. " +
         "On success returns 204 No Content with a Set-Cookie that expires the refresh_token cookie immediately. " +
         "Idempotent: returns 204 even when no cookie is present or the token is already revoked.");
```

> **AC-5 rate-limit policy reuse:** the existing "auth-refresh" policy (`Program.cs:155-164`) is 30/min per IP, which matches the spec's expected logout volume (logout is rarer than refresh; any IP attempting more than 30 logout calls per minute is anomalous). Do NOT introduce a third policy ("auth-logout") — extra policies bloat the rate-limiter config without observed benefit. If a future story finds a real reason to differentiate, that story owns the split.

Add the handler method (place it after `RefreshHandler`, before `RefreshTokenInvalidResponse`):

```csharp
private static async Task<IResult> LogoutHandler(
    IAuthService authService,
    HttpContext httpContext,
    IHostEnvironment env,
    CancellationToken ct)
{
    ArgumentNullException.ThrowIfNull(authService);
    ArgumentNullException.ThrowIfNull(httpContext);
    ArgumentNullException.ThrowIfNull(env);

    var rawToken = httpContext.Request.Cookies["refresh_token"];

    // Delegate idempotency to the service — pass nullable through.
    _ = await authService.LogoutAsync(rawToken, ct).ConfigureAwait(false);

    // AC-1/AC-3: always clear the cookie, even on NoOp. If a stale cookie
    // lives in the browser's jar that the server didn't actually invalidate,
    // this Set-Cookie will overwrite it. Same cookie attributes as
    // SetRefreshCookieAndReturn (Path, HttpOnly, SameSite, Secure) so the
    // browser matches and replaces the existing cookie instead of creating
    // a parallel one.
    var secure = !env.IsDevelopment() || httpContext.Request.IsHttps;
    httpContext.Response.Cookies.Append("refresh_token", string.Empty, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = secure,
        Path = "/api/auth",
        Expires = DateTimeOffset.UnixEpoch,  // Jan 1 1970 → browser drops cookie
        MaxAge = TimeSpan.Zero,
    });

    return Results.NoContent();
}
```

> **Why discard the `AuthLogoutResult` (the `_ =`)** — the wire response is the same (204 + cookie-clear) for both `Revoked` and `NoOp`, per AC-3. The outcome is only consumed by the metric inside `LogoutAsync`. Keeping the variable name `_` documents the intent.

> **Why `DateTimeOffset.UnixEpoch` + `MaxAge = TimeSpan.Zero` together:** `Expires` is the legacy mechanism (older browsers); `Max-Age` is the modern one (RFC 6265). Setting both maximizes compatibility. The browser will drop the cookie on receipt.

> **No `Cache-Control: no-store` is added here:** that is a deferred item from Story 2.2 (see `deferred-work.md`). A single sweep across all `/api/auth/*` endpoints will land that header in a future hardening story — not this one (in-scope creep prevention).

### Task 4 — Backend integration tests for `/logout` (AC: 1, 2, 3, 5, 6)

Append the following tests to `src/FormForge.Api.Tests/Features/Auth/AuthIntegrationTests.cs`, after the existing `Refresh_*` tests (after line 280). All tests share the existing `LoginAndGetTokensAsync` helper and the `_client` configured with `HandleCookies = false` (critical for predictable cookie behavior — see Story 2.2 Debug Log).

```csharp
[Fact]
public async Task Logout_ValidCookie_Returns204AndRevokesToken()
{
    var (_, rawToken) = await LoginAndGetTokensAsync();

    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
    request.Headers.Add("Cookie", $"refresh_token={rawToken}");
    using var response = await _client!.SendAsync(request);

    Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

    // DB assertion: the token row is now revoked
    using var scope = _factory!.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
    var tokenHash = HashTokenForTest(rawToken);
    var row = await db.RefreshTokens.AsNoTracking().FirstAsync(r => r.TokenHash == tokenHash);
    Assert.NotNull(row.RevokedAt);
}

[Fact]
public async Task Logout_ValidCookie_ClearsRefreshCookie()
{
    var (_, rawToken) = await LoginAndGetTokensAsync();

    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
    request.Headers.Add("Cookie", $"refresh_token={rawToken}");
    using var response = await _client!.SendAsync(request);

    Assert.True(response.Headers.Contains("Set-Cookie"));
    var setCookie = response.Headers.GetValues("Set-Cookie").First();
    Assert.Contains("refresh_token=", setCookie, StringComparison.Ordinal);
    Assert.Contains("path=/api/auth", setCookie, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
    // expires in the past OR max-age=0 — accept either
    Assert.True(
        setCookie.Contains("max-age=0", StringComparison.OrdinalIgnoreCase)
        || setCookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase),
        $"Cookie did not signal immediate expiry: {setCookie}");
}

[Fact]
public async Task Logout_NoCookie_Returns204_NotChallenge()
{
    using var response = await _client!.PostAsync(new Uri("/api/auth/logout", UriKind.Relative), content: null);

    Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    // AC-3: idempotent — no error body
    var body = await response.Content.ReadAsStringAsync();
    Assert.Equal(string.Empty, body);
}

[Fact]
public async Task Logout_UnknownToken_Returns204()
{
    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
    // A syntactically plausible token that does not exist in the DB
    request.Headers.Add("Cookie", "refresh_token=this-token-does-not-exist-in-the-database");
    using var response = await _client!.SendAsync(request);

    Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
}

[Fact]
public async Task Logout_AlreadyRevokedToken_Returns204_NoSecondRevoke()
{
    var (_, rawToken) = await LoginAndGetTokensAsync();

    // First logout — revokes the token
    using var first = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
    first.Headers.Add("Cookie", $"refresh_token={rawToken}");
    using var firstResponse = await _client!.SendAsync(first);
    Assert.Equal(HttpStatusCode.NoContent, firstResponse.StatusCode);

    // Capture the RevokedAt timestamp from the first logout
    DateTimeOffset? firstRevokedAt;
    using (var scope = _factory!.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var hash = HashTokenForTest(rawToken);
        firstRevokedAt = (await db.RefreshTokens.AsNoTracking().FirstAsync(r => r.TokenHash == hash)).RevokedAt;
    }
    Assert.NotNull(firstRevokedAt);

    // Second logout with the same (now-revoked) token — must still return 204
    using var second = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
    second.Headers.Add("Cookie", $"refresh_token={rawToken}");
    using var secondResponse = await _client!.SendAsync(second);
    Assert.Equal(HttpStatusCode.NoContent, secondResponse.StatusCode);

    // RevokedAt is unchanged — we did not overwrite the original revocation timestamp
    using (var scope = _factory!.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<FormForgeDbContext>();
        var hash = HashTokenForTest(rawToken);
        var row = await db.RefreshTokens.AsNoTracking().FirstAsync(r => r.TokenHash == hash);
        Assert.Equal(firstRevokedAt, row.RevokedAt);
    }
}

[Fact]
public async Task Logout_ThenRefresh_WithSameToken_Returns401Invalid()
{
    var (_, rawToken) = await LoginAndGetTokensAsync();

    // Logout
    using var logoutReq = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
    logoutReq.Headers.Add("Cookie", $"refresh_token={rawToken}");
    using var logoutResp = await _client!.SendAsync(logoutReq);
    Assert.Equal(HttpStatusCode.NoContent, logoutResp.StatusCode);

    // Attempt refresh with the now-revoked token → 401 REFRESH_TOKEN_INVALID
    using var refreshReq = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
    refreshReq.Headers.Add("Cookie", $"refresh_token={rawToken}");
    using var refreshResp = await _client!.SendAsync(refreshReq);

    Assert.Equal(HttpStatusCode.Unauthorized, refreshResp.StatusCode);
    var body = await refreshResp.Content.ReadAsStringAsync();
    Assert.Contains("REFRESH_TOKEN_INVALID", body, StringComparison.Ordinal);
}

[Fact]
public async Task Logout_DoesNotRequireBearerToken()
{
    // Cookie only, no Authorization header — AC-6
    var (_, rawToken) = await LoginAndGetTokensAsync();

    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
    request.Headers.Add("Cookie", $"refresh_token={rawToken}");
    // Deliberately no request.Headers.Authorization
    using var response = await _client!.SendAsync(request);

    Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
}
```

Add the private hash helper near the bottom of the test class (next to `LoginAndGetTokensAsync` at line 282, before `SeedTestUserAsync`). Same algorithm as `AuthService.HashToken` — DO NOT diverge:

```csharp
private static string HashTokenForTest(string raw) =>
    Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(raw)))
        .ToLowerInvariant();
```

> **Do not call `AuthService.HashToken` from tests.** It is `private static` and `internal sealed` containment is intentional. The test-side duplication is the lesser evil (caught instantly if either side diverges — a single failing refresh test).

### Task 5 — Frontend: add `useLogoutMutation` (AC: 4)

Create `web/src/features/auth/authMutations.ts` does already exist (Story 2.1/2.2). Extend it — do NOT create a new file. Append after `useLoginMutation`:

```ts
import { useMutation, useQueryClient } from '@tanstack/react-query'
// ... existing imports remain unchanged

export function useLogoutMutation() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: async (): Promise<void> => {
      // Direct fetch (not httpClient) for the same reason useAuthQuery uses fetch:
      // httpClient's 401-retry would call refreshSession() on a 401 from /logout,
      // which would race with the logout itself. /logout always returns 204 on
      // happy path so the 401-retry would only fire on infrastructure errors —
      // not auth — but we still avoid the coupling here.
      await fetch('/api/auth/logout', {
        method: 'POST',
        credentials: 'include',
        headers: { 'X-Correlation-ID': generateCorrelationId() },
      })
      // Intentionally ignore the response status — AC-4 mandates that client-side
      // logout runs even on server failure (best-effort server revoke).
    },
    onSettled: () => {
      // AC-4: unconditional client-side logout, regardless of server outcome.
      tokenStore.clear()
      // Reset all query caches so the next user (or post-logout state) does not
      // see the previous session's data. .clear() removes every query; the
      // alternative `invalidateQueries()` keeps cached data which is wrong here.
      queryClient.clear()
      // replace: true — back button should not return to a now-unauthenticated route
      void navigate({ to: '/login', replace: true })
    },
  })
}
```

Add the missing import at the top of the file:

```ts
import { generateCorrelationId } from '../../lib/correlationId'
```

> **Why `onSettled` and not `onSuccess`/`onError`:** `onSettled` fires for both — exactly what AC-4 requires (unconditional client-side logout).

> **Do not call `httpClient.post('/api/auth/logout')`** even though `httpClient`'s `/api/auth/refresh` 401-loop guard would *probably* prevent the recursion. The direct `fetch` is intentional defensive coupling: the logout flow must not depend on the refresh-and-retry machinery being correct.

### Task 6 — Frontend: add a logout control to the authenticated layout (AC: 4)

Update `web/src/routes/_app.tsx` to expose a logout button in `AppLayout`. The full nav/sidebar is deferred to Story 4.7 / Epic 7, but the logout entry point must exist now so users can sign out.

Add the import:

```tsx
import { useLogoutMutation } from '../features/auth/authMutations'
import { useTranslation } from 'react-i18next'
```

Replace the `AppLayout` component body:

```tsx
function AppLayout() {
  const { t } = useTranslation()
  const logoutMutation = useLogoutMutation()

  // Keeps the in-memory access token fresh every 13 minutes without
  // requiring a page reload. Complements the one-shot beforeLoad above.
  useAuthQuery()

  return (
    <main style={{ padding: '2rem' }}>
      <header style={{ display: 'flex', justifyContent: 'flex-end', marginBottom: '1rem' }}>
        <button
          type="button"
          onClick={() => logoutMutation.mutate()}
          disabled={logoutMutation.isPending}
        >
          {logoutMutation.isPending ? t('auth.logout.submitting') : t('auth.logout.button')}
        </button>
      </header>
      {/* Full layout (nav, sidebar) added in Story 4.7 / Epic 7 */}
      <Outlet />
    </main>
  )
}
```

> **Placement note:** the header is intentionally minimal (just the logout button). Story 4.7 will replace this whole `<header>` block with the real navbar — the logout button will be repositioned inside the navbar/user-menu at that time. This story ships only enough UI to satisfy AC-4. Do not style heavily; do not add shadcn/ui components here — the Epic 7 polish story owns final visual treatment.

### Task 7 — i18n keys (AC: 4)

Update `web/src/lib/i18n/locales/en.json`. Add to the existing `auth` block:

```json
{
  "auth": {
    "login": { /* unchanged */ },
    "logout": {
      "button": "Sign out",
      "submitting": "Signing out…"
    },
    "invalidCredentials": "Invalid email or password.",
    "accountInactive": "Your account has been deactivated. Contact an administrator.",
    "refreshTokenInvalid": "Your session has expired. Please sign in again.",
    "sessionExpired": "Your session has expired. Please sign in again.",
    "genericError": "Something went wrong. Please try again."
  }
}
```

> **No new error keys** — logout failures are silent from the user's perspective per AC-4 (they always reach `/login`). The button transitions through `Signing out…` then disappears with the page; no toast, no inline error.

### Task 8 — OpenAPI metadata (no AC, polish — keeps `/openapi/v1.json` honest)

The `WithSummary` and `WithDescription` in Task 3 already feed OpenAPI. Verify after `dotnet run`:

- `GET /openapi/v1.json` includes a `/api/auth/logout` path with:
  - `summary: "Revoke the refresh token and clear the refresh cookie"`
  - `responses.204` (no body)
  - rate-limit policy is not surfaced in OpenAPI by default — no action needed.

No `Bearer` security requirement should appear on this endpoint (cookie-only, AC-6) — the global default in `Program.cs` AddOpenApi is to add Bearer as a *scheme*, not a per-endpoint *requirement*. The `/api/auth` group bypasses `RequireAuth()`, so the endpoint is correctly documented as cookie-only without further action.

### Review Findings (2026-05-23 — three-reviewer pass)

Two reviewers ran in parallel against commit `6e4afa8`: Blind Hunter (diff only) and Edge Case Hunter (diff + repo). The Acceptance Auditor (diff + spec) found zero violations — the diff implements the prescribed recipe verbatim. Most Blind Hunter findings were dismissed as intentional spec decisions (AC-6 cookie-only auth, AC-4 unconditional client logout, shared `auth-refresh` rate-limit policy justified in Task 3, JWT-revocation explicitly out of scope, `_ = ` discard documented in Task 3, `HashTokenForTest` duplication justified in Task 4).

- [x] [Review][Patch] Add `Set-Cookie` header assertion to `Logout_NoCookie_Returns204_NotChallenge` — AC-3 requires the cookie-clearing header on the no-cookie path; current test only asserts 204 + empty body, so a regression that conditioned the `Cookies.Append(...)` call on token presence would not be caught. [src/FormForge.Api.Tests/Features/Auth/AuthIntegrationTests.cs around the `Logout_NoCookie_Returns204_NotChallenge` body] — applied 2026-05-23
- [x] [Review][Patch] Add `Set-Cookie` header assertion to `Logout_UnknownToken_Returns204` — same AC-3 coverage gap as above on the unknown-token NoOp branch. [src/FormForge.Api.Tests/Features/Auth/AuthIntegrationTests.cs around the `Logout_UnknownToken_Returns404` test] — applied 2026-05-23
- [x] [Review][Defer] Concurrent logout-vs-refresh race leaves rotated-chain token valid [src/FormForge.Api/Features/Auth/AuthService.cs `LogoutAsync`] — deferred, real concurrency gap not addressed by spec
- [x] [Review][Defer] No test exercises the `DbUpdateConcurrencyException` NoOp branch [src/FormForge.Api/Features/Auth/AuthService.cs catch block] — deferred, hard to reproduce in integration tests
- [x] [Review][Defer] Logout button missing `aria-busy` [web/src/routes/_app.tsx button] — deferred, Story 4.7 / Epic 7 owns navbar polish
- [x] [Review][Defer] `fetch('/api/auth/logout')` has no `AbortSignal` / timeout [web/src/features/auth/authMutations.ts] — deferred, hung-network edge case
- [x] [Review][Defer] No length / format guard on `rawToken` before SHA-256 hashing [src/FormForge.Api/Features/Auth/AuthService.cs `LogoutAsync`] — deferred, DoS amplifier shared with refresh path
- [x] [Review][Defer] Rate-limited 429 response on `/logout` does NOT emit cookie-clearing Set-Cookie [src/FormForge.Api/Program.cs `OnRejected` handler] — deferred, conflicts with AC-1/AC-3 "always emits" but is cross-cutting middleware change
- [x] [Review][Defer] `Logout_AlreadyRevokedToken_Returns204_NoSecondRevoke` does not assert `RecordRevoked()` was NOT called second time [src/FormForge.Api.Tests/Features/Auth/AuthIntegrationTests.cs] — deferred, needs metric-test infrastructure
- [x] [Review][Defer] Misconfigured proxy can strip HTTPS, leaving `Secure=true` and dropping the cookie-clearing Set-Cookie over plain HTTP [src/FormForge.Api/Features/Auth/AuthEndpoints.cs `secure` computation] — deferred, deployment hardening affecting login path too

### Task 9 — Build gates and manual verification (AC: all)

- [x] `dotnet build` — zero warnings, zero errors (CPM + TreatWarningsAsErrors enforced).
- [x] `dotnet format --verify-no-changes` — clean.
- [x] `dotnet test` — all 82 existing tests still pass; 7 new logout tests pass. Zero regressions (89/89).
- [x] `cd web && npm run build` — clean TypeScript + Vite build.
- [x] `cd web && npx tsc --noEmit` — type check passes (run via `tsc -b --noEmit` step inside `npm run build`).
- [ ] Manual verification (interactive) — deferred to reviewer; no headed browser / full docker-compose stack available in this dev session. Automated coverage (integration tests + OpenAPI doc check) covers all server-side behavior; SPA logout button compiles and the `_app.tsx` rendering is exercised by the existing route tree.
  - `dotnet run --project src/FormForge.AppHost` — Aspire Dashboard shows all services healthy.
  - Log in at `http://localhost:5173/login` with `test@example.com / Password1!`. Confirm landing on `/`.
  - Click "Sign out" in the top-right of `/`. Confirm:
    - Browser navigates to `/login`.
    - Browser DevTools → Application → Cookies shows the `refresh_token` cookie is gone for the API origin.
    - DevTools → Network shows `POST /api/auth/logout` returned 204.
  - Use the browser back button. Confirm it does NOT return to `/` — instead either stays on `/login` (correct: `replace: true`) or attempts `/` and lands back on `/login` via `_app.tsx` beforeLoad (also correct).
  - Manually re-POST the (now-revoked) refresh token from before logout: `curl -X POST http://localhost:5190/api/auth/refresh -H "Cookie: refresh_token=<the-old-token>"`. Confirm 401 + `REFRESH_TOKEN_INVALID` body.
  - Re-log in, click logout. Manually POST to `/api/auth/logout` again with no cookie (`curl -X POST http://localhost:5190/api/auth/logout`). Confirm 204.

---

## Dev Notes

### What this story does — and what it does NOT

**In scope:**
1. `POST /api/auth/logout` — server-side revoke + cookie clear, idempotent on missing/invalid token.
2. `IAuthService.LogoutAsync` + `AuthLogoutOutcome` / `AuthLogoutResult` types.
3. `useLogoutMutation` (frontend) — unconditional `tokenStore.clear() + queryClient.clear() + navigate('/login')` via `onSettled`.
4. Minimal logout button in `_app.tsx` `AppLayout` header (placeholder until Story 4.7 ships the real navbar).
5. i18n keys `auth.logout.button` and `auth.logout.submitting`.
6. Rate-limit reuse of "auth-refresh" policy (30/min per IP).
7. Integration tests covering AC-1, AC-2, AC-3, AC-6.

**Out of scope (deferred to named stories):**
- "Logout from all devices" (revoke ALL refresh tokens for a user) — not in PRD; revisit if asked.
- Server-side `UserLoggedOut` domain event — no consumer in v1; permission cache is keyed on user, not on token, so logout does not need to evict (the access token still validates by signature for ≤15 min, by spec — Decision 2.1 line 357 explicitly accepts this lag).
- Bearer token blacklist / JWT revocation list — the spec accepts the 15-min grace window; the access token is not server-revocable in v1. Adding a blacklist would contradict NFR-5 (stateless JWT verification).
- Full styled logout button with shadcn/ui — Story 4.7 / Epic 7 owns final navbar UX.
- `Cache-Control: no-store` on `/api/auth/*` responses — deferred from Story 2.2 (single sweep planned).
- `formforge.refresh_token.logged_out` counter — not in architecture (Decision 5.3 lines 645–651); revisit if v2 needs cause disambiguation.
- Logout-related security audit log entry — `schema_audit_log` and `mutation_audit_log` cover DDL and CRUD only (FR-28, FR-36); no auth-audit table exists in v1.

### Architecture compliance

- **NFR-5 (token storage):** Refresh token is revoked server-side AND the HttpOnly cookie is invalidated client-side (Max-Age=0). In-memory access token cleared via `tokenStore.clear()`.
- **AR-12, AR-13 (JWT, BCrypt):** No new crypto. `HashToken` and `JwtTokenService.CreateAccessToken` are unchanged.
- **AR-14 (CORS):** No new CORS concerns. Same origin / same allowlist as login and refresh.
- **AR-15 (Rate limiting):** "auth-refresh" policy reused — see Task 3.
- **AR-18 (Error envelope):** N/A — the spec mandates 204 on success and 204 on idempotent NoOp (AC-1, AC-3). No ProblemDetails responses from this endpoint. The 429 path is handled by the global `OnRejected` handler in `Program.cs:166-180`.
- **AR-22 (Route groups):** `/logout` lives in the `/api/auth` group (no `RequireAuth()` — AC-6 requires the endpoint to work with no bearer token). Filter chain order remains: correlation ID → rate limit → handler.
- **AR-24 (Correlation ID):** existing `CorrelationIdMiddleware` already covers this endpoint.
- **AR-32 (HTTP client):** the `useLogoutMutation` deliberately uses raw `fetch` (not `httpClient`) — same pattern as `useAuthQuery` for the same reason (avoid 401-retry coupling).
- **AR-48 (Query keys):** `queryClient.clear()` is the right call (not `invalidateQueries({queryKey: ['auth']})`), because logout must wipe ALL cached data, not just auth-scoped queries (e.g., menu cache, future user-list cache).
- **Decision 2.1 (Silent re-auth flow lines 352–357):** logout is the explicit opposite — discards the in-memory access token, lets the access token expire naturally within ≤15 min (the deactivation-handling lag is the same window).
- **CA rules / Story 2.2 carry-forward:** `internal sealed` types, `[SuppressMessage(CA1812, Justification=...)]` on DI types, no `[LoggerMessage]` calls in the logout path beyond the reused `RefreshTokenConcurrencyConflict` from `AuthService`.

### Current code state (files being modified)

`src/FormForge.Api/Features/Auth/AuthService.cs` — Add `AuthLogoutOutcome`, `AuthLogoutResult`, `IAuthService.LogoutAsync`, and the `LogoutAsync` method body. The existing `HashToken` (line 228) and `RefreshTokenConcurrencyConflict` LoggerMessage (line 242) are reused unchanged.

`src/FormForge.Api/Features/Auth/AuthEndpoints.cs` — Add `MapPost("/logout", LogoutHandler)` registration (after `/refresh` registration at line 22–28) and the `LogoutHandler` static method (after `RefreshHandler`). The `SetRefreshCookieAndReturn` helper is NOT reused — logout writes a different cookie (Max-Age=0). However, the cookie *attributes* (`Path`, `HttpOnly`, `SameSite`, `Secure`) must mirror `SetRefreshCookieAndReturn` exactly so the browser identifies and overwrites the existing cookie rather than creating a parallel one.

`src/FormForge.Api.Tests/Features/Auth/AuthIntegrationTests.cs` — Append 6–7 logout integration tests after the refresh tests (current end of class at line 280). Add the `HashTokenForTest` private helper next to `LoginAndGetTokensAsync` (line 282).

`web/src/features/auth/authMutations.ts` — Add `useLogoutMutation` after the existing `useLoginMutation`. Add `useQueryClient` to the imports from `@tanstack/react-query`. Add `generateCorrelationId` import.

`web/src/routes/_app.tsx` — Add `useLogoutMutation` + `useTranslation` imports. Replace `AppLayout` body to include a `<header>` with the logout button. Do NOT touch the `beforeLoad` async refresh logic — that is Story 2.2's contract.

`web/src/lib/i18n/locales/en.json` — Add `auth.logout.button` and `auth.logout.submitting` keys.

### Do NOT touch

- `src/FormForge.Api/Features/Auth/JwtTokenService.cs` — JWT issuance is unchanged.
- `src/FormForge.Api/Features/Auth/AuthMetrics.cs` — counters are unchanged. `RecordRevoked()` is reused as-is.
- `src/FormForge.Api/Features/Auth/Dtos/LoginResponse.cs` — logout has no response body.
- `src/FormForge.Api/Domain/Entities/RefreshToken.cs` — no schema change. `[ConcurrencyCheck]` from Story 2.2 is already in place and is what makes the logout race-safe.
- Any existing EF Core migration files.
- `src/FormForge.Api/Program.cs` — no changes. The "auth-refresh" policy is reused; no third policy added; no new service registrations needed (`AuthMetrics` and `IAuthService` are already registered).
- `web/src/features/auth/httpClient.ts` — the existing 401-retry logic correctly excludes `/api/auth/login` and `/api/auth/refresh`; `/api/auth/logout` is not 401-retried because it returns 204 on the happy path (no 401 to trigger retry). Do not add a third exclusion.
- `web/src/features/auth/useAuthQuery.ts` — the 13-min refetch is naturally cancelled when `_app.tsx` unmounts on navigation to `/login`. No code change needed.
- `web/src/routes/login.tsx` — no change.

### Anti-patterns to avoid

| Don't | Why |
|---|---|
| Return 401 from `/api/auth/logout` when cookie is missing | AC-3 mandates idempotent 204. A 401 here would leak whether the user was logged in to anyone who can hit the endpoint without a cookie. |
| Add `RequireAuth()` to the `/api/auth` route group | AC-6: logout must work with no bearer / expired bearer. The `/api/auth` group is unauthenticated by design. |
| Call `httpClient.post('/api/auth/logout', null)` | Couples logout to the 401-retry/refresh machinery. Use raw `fetch` — same pattern as `useAuthQuery`. |
| Use `onSuccess` / `onError` instead of `onSettled` | AC-4: client-side logout runs unconditionally. `onSettled` is the only callback that fires on both. |
| Call `queryClient.invalidateQueries()` instead of `queryClient.clear()` | Invalidation keeps cached data; logout must wipe ALL per-user data so the next user does not see stale results. |
| Use `navigate({ href: '/login' })` instead of `navigate({ to: '/login', replace: true })` | `href` accepts absolute URLs (potential open-redirect surface from Story 2.2 review P1). `to: '/login'` is the typed route form. `replace: true` ensures back-button cannot return to a now-unauthenticated route. |
| Reuse `SetRefreshCookieAndReturn` for logout | That helper returns `Results.Ok(LoginResponse)` with a real cookie value. Logout writes an empty value with past expiry. The helpers do different things; do not bend one to serve both. |
| Diverge cookie attributes (`Path`, `SameSite`, `HttpOnly`, `Secure`) between login/refresh and logout | The browser matches cookies by name + path + domain. Mismatched attributes create a *parallel* cookie instead of overwriting the existing one — leaving the original cookie in place and the user "logged in" forever. |
| Set `Cache-Control: no-store` only on `/logout` | Deferred from Story 2.2 — single sweep across all `/api/auth/*` planned. Don't ship an inconsistent half-fix. |
| Add a third rate-limit policy ("auth-logout") | "auth-refresh" (30/min per IP) is the right shape for logout volume too. Don't bloat `Program.cs`. |
| Increment `RecordRevoked()` in the NoOp branch | The counter measures actual state transitions. Incrementing on NoOp would distort the `formforge.refresh_token.revoked` dashboard. |
| Add a Bearer security requirement to the `/logout` OpenAPI spec | AC-6 — cookie-only. Per-endpoint `RequireAuthorization()` would force a Bearer requirement and break the spec. |
| Overwrite `RevokedAt` on a second logout call | Idempotency invariant: a token transitions `unrevoked → revoked` exactly once. Re-setting the timestamp would falsely report a fresh revocation and confuse audit-trail readers. The current code (Task 2) correctly returns NoOp on the already-revoked branch. |
| Add the logout button inline in the page body | Story 4.7 owns final navbar placement. Keep the placeholder in `<header>` of `AppLayout` — easy to lift out when Story 4.7 builds the real navbar. |

### Previous story intelligence (Story 2.2, last done story)

**Critical carry-forward patterns:**

- **`HandleCookies = false` in `AuthIntegrationTests.InitializeAsync`** (line 47–50) — already in place. New logout tests rely on this to inject Cookie headers per-request without the auto-jar overriding them.
- **`TRUNCATE TABLE refresh_tokens, users RESTART IDENTITY CASCADE`** in `InitializeAsync` (line 40–41) — already truncates between tests; new logout tests don't pollute each other.
- **`partial class AuthService`** — already partial from Story 2.2 (line 41) for `[LoggerMessage]` source-gen. New `LogoutAsync` goes in the same partial.
- **`RefreshTokenConcurrencyConflict` LoggerMessage** (line 242) — reused unchanged for the logout DbUpdateConcurrencyException catch.
- **`AuthMetrics.RecordRevoked()`** — reused for AC-1. No new counter added (Decision 5.3).
- **`navigate({ to, replace: true })`** pattern from Story 2.2's login redirect fix (Review P1) — same pattern used here for logout navigation.
- **`generateCorrelationId()`** — already exists at `web/src/lib/correlationId.ts` (used by Story 2.2). Reused as-is.
- **`tokenStore.clear()` + `window.location.replace('/login')`** is the *failure* pattern from `httpClient.refreshSession` (line 32–33). Logout uses the *success* pattern: `tokenStore.clear()` + `navigate({ to: '/login', replace: true })` because we are in a React context with the router available.

**Surprising / non-obvious decisions from Story 2.2 to inherit:**

- `useAuthQuery` deliberately bypasses `httpClient` (line 11–15 comment) to avoid the circular 401→refresh→retry loop. `useLogoutMutation` follows the same defensive pattern.
- `WebApplicationFactoryClientOptions { HandleCookies = false }` was a Story 2.2 Debug Log discovery — the default `HandleCookies = true` auto-jars Set-Cookie responses and overrides manually-set Cookie headers, breaking replay tests. Inherit this; don't undo it.
- `_pendingRefresh` deduplication in `httpClient.ts` (line 15) is a *module-level* variable (persists across React re-renders). Logout doesn't need an analogous flag because mutations are already debounced by `useMutation`'s `isPending` + the button's `disabled` attribute (Task 6).

### Git intelligence

Recent commits (most recent first):

- `8cac5fa` — Story 2.2 code review — apply 15 patches from three-reviewer pass
- `88b2f9e` — Story 2.2 — Token refresh: POST /api/auth/refresh + 401-retry in httpClient + useAuthQuery SPA boot + OTel metrics
- `aa9ec15` — Story 2.1 code review — apply 14 patches from three-reviewer pass
- `da1452c` — Story 2.1 — JWT login + refresh token rotation + protected web routes

**Pattern:** one feature commit per story, then a separate commit for code-review patches.

**Expected commit message for this story:**

`Story 2.3 — Logout: POST /api/auth/logout + useLogoutMutation + AppLayout logout button`

### Testing approach

**Integration tests (Testcontainers) — extend `AuthIntegrationTests`:**

- All 6–7 new tests reuse `LoginAndGetTokensAsync()`. No new fixtures.
- The DB-state assertions (RevokedAt is set, then RevokedAt is unchanged on second logout) catch the "second logout overwrites timestamp" anti-pattern.
- `Logout_ThenRefresh_WithSameToken_Returns401Invalid` is the AC-2 cross-test — verifies the contract between this story's endpoint and Story 2.2's refresh endpoint.
- The cookie-attributes test uses case-insensitive `Contains` on `Set-Cookie` because ASP.NET emits canonical casing (`HttpOnly`, `SameSite=Strict`) but `Path=/api/auth` uses lowercase `path=` in some framework versions. Match what `Refresh_*` tests already do at line 84–87.

**No new unit tests:** `LogoutAsync` is primarily DB interaction (best tested via integration). `HashToken` is already tested implicitly by login + refresh + logout sharing it.

**No new frontend tests:** there are no frontend tests in this codebase yet (Vitest is wired but no test files exist). The deferred item from Story 2.2 ("tokenStore singleton, no `.reset()` test helper") still applies. Do not add the first frontend test as part of this story; that belongs to a dedicated test-infrastructure story.

### File structure

**New files:** none.

**Modified files (backend):**

- `src/FormForge.Api/Features/Auth/AuthService.cs`
- `src/FormForge.Api/Features/Auth/AuthEndpoints.cs`
- `src/FormForge.Api.Tests/Features/Auth/AuthIntegrationTests.cs`

**Modified files (frontend):**

- `web/src/features/auth/authMutations.ts`
- `web/src/routes/_app.tsx`
- `web/src/lib/i18n/locales/en.json`

**No modifications:** `Program.cs`, `RefreshToken.cs`, `AuthMetrics.cs`, `JwtTokenService.cs`, `httpClient.ts`, `tokenStore.ts`, `useAuthQuery.ts`, `login.tsx`, `__root.tsx`, `_app/index.tsx`.

### References

- `_bmad-output/planning-artifacts/epics.md` — §"Story 2.3: Logout" (lines 518–529 in epics.md)
- `_bmad-output/planning-artifacts/architecture.md`
  - Decision 2.1 — JWT Silent Re-Auth Flow (lines 352–357; logout is the explicit opposite of silent refresh)
  - Decision 2.6 — Rate Limiting (auth-refresh policy reused: 30/min per IP)
  - Decision 5.3 — Observability Stack (RecordRevoked counter; no new counter for logout)
  - AR-12, AR-14, AR-15, AR-18, AR-22, AR-24, AR-32, AR-48
  - NFR-5 (HttpOnly cookie + in-memory access token)
  - Section "Sprint sequencing reminder" — S1 exit criteria includes "Login/logout work"
  - Section "Frontend Architecture" 4.6 — folder layout for `features/auth/`
- `_bmad-output/implementation-artifacts/2-2-token-refresh.md`
  - Task 2 — `RefreshAsync` (and the `HashToken` + `[ConcurrencyCheck]` patterns this story reuses)
  - Task 4 — `httpClient.ts` (the 401-retry guard list this story explicitly does NOT touch)
  - Dev Notes §"Anti-patterns" (patterns this story extends)
  - Review Findings P1 — open-redirect via `redirect` param (the `navigate({to, replace: true})` pattern used here)
- `_bmad-output/implementation-artifacts/2-1-jwt-login.md`
  - Task 10 — `SetRefreshCookieAndReturn` (the helper this story deliberately does NOT reuse but mirrors cookie attributes from)
- `_bmad-output/implementation-artifacts/deferred-work.md` — Story 2.2 deferred: `Cache-Control: no-store` sweep (explicitly out of scope for this story)
- `src/FormForge.Api/Features/Auth/AuthService.cs:228-242` — `HashToken` + `RefreshTokenConcurrencyConflict` LoggerMessage (reused)
- `src/FormForge.Api/Features/Auth/AuthEndpoints.cs:113-130` — `SetRefreshCookieAndReturn` (cookie attribute reference)
- `src/FormForge.Api/Program.cs:155-164` — `"auth-refresh"` rate-limit policy (reused for `/logout`)
- `src/FormForge.Api/Program.cs:278-280` — `MapGroup("/api/auth").MapAuthEndpoints()` (group is unauthenticated by design)

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-7[1m]

### Debug Log References

- `launchSettings.json` overrides `ASPNETCORE_URLS` — first attempt at the OpenAPI verification step bound to `http://localhost:5429` instead of the requested `127.0.0.1:55190`. Worked around by reading the actual listening URL from the startup log; in a follow-up run without a valid launch profile name (`--launch-profile FormForge.Api` does not exist), the app bound to `localhost:5000` (Production env). Both URLs returned the OpenAPI doc; the value verified is independent of the bind.
- After Task 3, ASP.NET Core OpenAPI emitted `"200": "OK"` as the only response for `/api/auth/logout` (framework default). The handler returns 204. Added `.Produces(StatusCodes.Status204NoContent)` to the route registration so `/openapi/v1.json` honestly advertises the contract (Task 8 explicit verification check). Verified post-change: `responses.204: "No Content"` is the only documented response.

### Completion Notes List

- Backend: `IAuthService.LogoutAsync` (idempotent NoOp on missing cookie / unknown token / already-revoked / DbUpdateConcurrencyException; `metrics.RecordRevoked()` only on actual state transition); `MapPost("/logout", LogoutHandler)` with reused `auth-refresh` rate-limit policy and a cookie-clearing `Set-Cookie` whose attributes mirror `SetRefreshCookieAndReturn` exactly.
- Frontend: `useLogoutMutation` uses raw `fetch` (not `httpClient`) — same defensive decoupling as `useAuthQuery` to keep logout independent of the 401-retry/refresh machinery. `onSettled` runs `tokenStore.clear()` + `queryClient.clear()` + `navigate({ to: '/login', replace: true })` unconditionally (AC-4 — runs on both server success and server failure).
- Logout button placed in the `<header>` of `_app.tsx::AppLayout`. Placeholder treatment per story spec — Story 4.7 owns the final navbar UX.
- Test coverage added: 7 new logout integration tests appended to `AuthIntegrationTests` covering valid-cookie revoke + DB assertion, cookie-clearing Set-Cookie attributes, no-cookie idempotent 204, unknown-token 204, already-revoked idempotent 204 with timestamp preservation, post-logout refresh returns `REFRESH_TOKEN_INVALID`, and the no-Authorization-header path (AC-6). Private `HashTokenForTest` mirrors `AuthService.HashToken` (test-side duplication is intentional — see Task 4 note).
- No `Cache-Control: no-store` added — deferred to a single `/api/auth/*` sweep planned outside this story.
- No new metric counter — `RecordRevoked()` is reused per Decision 5.3.
- Final regression: 89/89 backend tests pass (was 82 before this story; +7 new).

### File List

Modified (backend):

- `src/FormForge.Api/Features/Auth/AuthService.cs`
- `src/FormForge.Api/Features/Auth/AuthEndpoints.cs`
- `src/FormForge.Api.Tests/Features/Auth/AuthIntegrationTests.cs`

Modified (frontend):

- `web/src/features/auth/authMutations.ts`
- `web/src/routes/_app.tsx`
- `web/src/lib/i18n/locales/en.json`

Modified (process):

- `_bmad-output/implementation-artifacts/sprint-status.yaml` — `2-3-logout` advanced `ready-for-dev` → `in-progress` → `review`.
- `_bmad-output/implementation-artifacts/2-3-logout.md` — Tasks/Subtasks checkboxes, Dev Agent Record, File List, Change Log, Status.

### Change Log

- 2026-05-23 — Implemented Story 2.3 (Logout): server endpoint `POST /api/auth/logout` (idempotent revoke + cookie clear), `useLogoutMutation` + `AppLayout` logout button, i18n keys, 7 backend integration tests. All ACs met. Status moved to `review`.
