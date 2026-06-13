using System.Data;
using FormForge.Api.Common;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Features.Auth;
using FormForge.Api.Features.Users.Dtos;
using FormForge.Api.Infrastructure.EventBus;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FormForge.Api.Features.Users;

internal enum AssignRolesOutcome { Success, UserNotFound, RolesNotFound, LastAdminLockout, Conflict }

internal sealed record AssignRolesResult(
    AssignRolesOutcome Outcome,
    IReadOnlyList<Guid>? InvalidRoleIds = null);

internal enum CreateUserOutcome { Success, DuplicateEmail }

internal sealed record CreateUserResult(
    CreateUserOutcome Outcome,
    Guid? UserId = null,
    UserDetailResponse? User = null);

internal enum UpdateUserOutcome { Success, NotFound }

internal sealed record UpdateUserResult(UpdateUserOutcome Outcome);

internal enum DeactivateUserOutcome { Success, NotFound, SelfDeactivation }

internal sealed record DeactivateUserResult(DeactivateUserOutcome Outcome);

internal enum ReactivateUserOutcome { Success, NotFound }

internal sealed record ReactivateUserResult(ReactivateUserOutcome Outcome);

internal enum AdminMfaResetOutcome { Success, NotFound }

internal sealed record AdminMfaResetResult(AdminMfaResetOutcome Outcome);

internal interface IUserService
{
    Task<AssignRolesResult> AssignRolesAsync(Guid userId, IReadOnlyList<Guid> roleIds, CancellationToken ct);
    Task<PagedResult<UserListItem>> GetUsersAsync(
        int page, int pageSize, string? sort, string? search, string? status, CancellationToken ct);
    Task<UserDetailResponse?> GetUserAsync(Guid id, CancellationToken ct);
    Task<CreateUserResult> CreateUserAsync(CreateUserRequest request, CancellationToken ct);
    Task<UpdateUserResult> UpdateUserAsync(Guid id, UpdateUserRequest request, CancellationToken ct);
    Task<DeactivateUserResult> DeactivateUserAsync(Guid id, Guid currentUserId, CancellationToken ct);
    Task<ReactivateUserResult> ReactivateUserAsync(Guid id, CancellationToken ct);
    Task<AdminMfaResetResult> ResetUserMfaAsync(Guid userId, CancellationToken ct);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class UserService(
    FormForgeDbContext db,
    IDomainEventBus bus,
    IPasswordHasher passwordHasher) : IUserService
{
    // Well-known platform-admin role ID seeded by the Story 2.4 migration
    // (20260523021147_CreateRolesRolePermissionsAndUserRoles).
    private static readonly Guid PlatformAdminRoleId = new("00000000-0000-0000-0000-000000000001");

    public async Task<AssignRolesResult> AssignRolesAsync(
        Guid userId,
        IReadOnlyList<Guid> roleIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roleIds);

        var userExists = await db.Users
            .AnyAsync(u => u.Id == userId, ct)
            .ConfigureAwait(false);

        if (!userExists)
        {
            return new AssignRolesResult(AssignRolesOutcome.UserNotFound);
        }

        // Second-layer Distinct: validator already rejects duplicate UUIDs in HTTP
        // requests, but a non-HTTP caller (e.g. tests seeding data) could pass dups
        // and trip the (UserId, RoleId) PK on insert.
        var distinctRoleIds = roleIds.Distinct().ToList();

        if (distinctRoleIds.Count > 0)
        {
            var foundRoleIds = await db.Roles
                .Where(r => distinctRoleIds.Contains(r.Id))
                .Select(r => r.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (foundRoleIds.Count != distinctRoleIds.Count)
            {
                var invalidIds = distinctRoleIds.Except(foundRoleIds).ToList();
                return new AssignRolesResult(AssignRolesOutcome.RolesNotFound, invalidIds);
            }
        }

        // Last-admin lockout guard + atomic replacement wrapped in a SERIALIZABLE
        // transaction. Without SERIALIZABLE the check is TOCTOU: two concurrent
        // demotions of distinct admins each read otherAdminExists=true against a
        // READ COMMITTED snapshot, both commit, and the system is left with zero
        // platform-admins. Postgres aborts one of the conflicting transactions
        // with SQLSTATE 40001, which we translate into Conflict so the client
        // can retry. Explicit try/finally rather than `await using` so the inner
        // try/catch can branch on DbUpdateException without dropping CA2007 on
        // the implicit DisposeAsync. (Story 2.6 review patch #1.)
        var txn = await db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, ct)
            .ConfigureAwait(false);

        try
        {
            var userHasAdmin = await db.UserRoles
                .AnyAsync(ur => ur.UserId == userId && ur.RoleId == PlatformAdminRoleId, ct)
                .ConfigureAwait(false);

            if (userHasAdmin && !distinctRoleIds.Contains(PlatformAdminRoleId))
            {
                var otherAdminExists = await db.UserRoles
                    .AnyAsync(ur => ur.RoleId == PlatformAdminRoleId && ur.UserId != userId, ct)
                    .ConfigureAwait(false);

                if (!otherAdminExists)
                {
                    return new AssignRolesResult(AssignRolesOutcome.LastAdminLockout);
                }
            }

            // Atomic replacement: load tracked rows, RemoveRange, then Add — committed
            // by a single SaveChangesAsync inside the SERIALIZABLE transaction above.
            // ExecuteDeleteAsync would issue an immediate DELETE separate from the later
            // SaveChangesAsync INSERT (still safe under the txn, but harder to reason about).
            var existing = await db.UserRoles
                .Where(ur => ur.UserId == userId)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            db.UserRoles.RemoveRange(existing);

            // Single timestamp shared by all rows in this transaction so audit ordering
            // reflects "one operation" rather than microsecond-level drift between rows.
            var now = DateTimeOffset.UtcNow;
            foreach (var roleId in distinctRoleIds)
            {
                db.UserRoles.Add(new UserRole
                {
                    UserId = userId,
                    RoleId = roleId,
                    CreatedAt = now,
                });
            }

            // Translate race-window FK / PK / serialization violations into a clean 409
            // Conflict. 23503 = FK violation (user or role deleted between our check and
            // insert); 23505 = (UserId, RoleId) PK collision from a concurrent PUT;
            // 40001 = serialization_failure from the SERIALIZABLE txn detecting that
            // a concurrent transaction would have invalidated our last-admin invariant.
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await txn.CommitAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (
                ex.InnerException is PostgresException { SqlState: "23503" or "23505" or "40001" })
            {
                return new AssignRolesResult(AssignRolesOutcome.Conflict);
            }
            catch (PostgresException pg) when (pg.SqlState is "23503" or "23505" or "40001")
            {
                // Npgsql sometimes surfaces commit-time constraint failures bare
                // rather than wrapping them in DbUpdateException (deferred-check
                // path on CommitAsync). Mirror the same SqlStates as the wrapping
                // catch above so we never bubble a 500. (Story 2.6 bmad review.)
                return new AssignRolesResult(AssignRolesOutcome.Conflict);
            }
        }
        finally
        {
            await txn.DisposeAsync().ConfigureAwait(false);
        }

        // Publish only after the DB transaction committed (we reach here only on
        // SaveChangesAsync success, not on the Conflict catch above) so the cache
        // bust never races a rolled-back assignment.
        bus.Publish(new UserRoleAssignmentChanged(userId));
        return new AssignRolesResult(AssignRolesOutcome.Success);
    }

    public async Task<PagedResult<UserListItem>> GetUsersAsync(
        int page,
        int pageSize,
        string? sort,
        string? search,
        string? status,
        CancellationToken ct)
    {
        IQueryable<User> query = db.Users;

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(u =>
                EF.Functions.ILike(u.Email, term)
                || EF.Functions.ILike(u.DisplayName, term));
        }

        // active/inactive maps onto IsActive; unrecognized values are ignored so a
        // stale URL never breaks the page.
        if (string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(u => u.IsActive);
        }
        else if (string.Equals(status, "inactive", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(u => !u.IsActive);
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);

        // Mirror RoleService skip clamp so a very large page * pageSize doesn't
        // overflow into a negative int. (Story 2.4 review pattern.)
        var skip = (int)Math.Min(int.MaxValue, ((long)page - 1L) * pageSize);

        var items = await ApplySort(query, sort)
            .Skip(skip)
            .Take(pageSize)
            .Select(u => new UserListItem(
                u.Id,
                u.Email,
                u.DisplayName,
                u.IsActive,
                u.UserRoles.Count,
                u.CreatedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PagedResult<UserListItem>(items, total, page, pageSize);
    }

    // Whitelisted single-column sort ("field:dir"); Email is the stable tiebreaker.
    // Unknown/empty/malformed sort falls back to email asc (the legacy default).
    private static IQueryable<User> ApplySort(IQueryable<User> q, string? sort)
    {
        var field = "email";
        var desc = false;
        if (!string.IsNullOrWhiteSpace(sort))
        {
            var parts = sort.Split(':');
            if (parts.Length == 2)
            {
                field = parts[0];
                desc = string.Equals(parts[1], "desc", StringComparison.OrdinalIgnoreCase);
            }
        }

        return field switch
        {
            "displayName" => Tie(q, u => u.DisplayName, desc),
            "status" => Tie(q, u => u.IsActive, desc),
            "roleCount" => Tie(q, u => u.UserRoles.Count, desc),
            "createdAt" => Tie(q, u => u.CreatedAt, desc),
            "email" => desc ? q.OrderByDescending(u => u.Email) : q.OrderBy(u => u.Email),
            _ => q.OrderBy(u => u.Email),
        };

        static IQueryable<User> Tie<TKey>(
            IQueryable<User> src,
            System.Linq.Expressions.Expression<Func<User, TKey>> key,
            bool descending) =>
            (descending ? src.OrderByDescending(key) : src.OrderBy(key)).ThenBy(u => u.Email);
    }

    public async Task<UserDetailResponse?> GetUserAsync(Guid id, CancellationToken ct)
    {
        // Single projection populates the role list via the navigation property
        // so the API never round-trips a separate roles query.
        return await db.Users
            .Where(u => u.Id == id)
            .Select(u => new UserDetailResponse(
                u.Id,
                u.Email,
                u.DisplayName,
                u.IsActive,
                u.CreatedAt,
                u.UpdatedAt,
                u.UserRoles
                    .OrderBy(ur => ur.Role.Name)
                    .Select(ur => new UserRoleItem(ur.RoleId, ur.Role.Name))
                    .ToList(),
                u.MfaEnabled)) // Story 2.15
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<CreateUserResult> CreateUserAsync(CreateUserRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Validator already enforces non-null/non-empty on these three fields, so a
        // null-suppression here is safe at runtime.
        var email = request.Email!.Trim().ToLowerInvariant();
        var displayName = request.DisplayName!.Trim();
        var password = request.TemporaryPassword!;

        var exists = await db.Users
            .AnyAsync(u => u.Email == email, ct)
            .ConfigureAwait(false);

        if (exists)
        {
            return new CreateUserResult(CreateUserOutcome.DuplicateEmail);
        }

        var user = new User
        {
            Email = email,
            DisplayName = displayName,
            PasswordHash = passwordHasher.Hash(password),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);

        // Wrap SaveChanges to translate the race-window unique-violation on
        // uq_users_email back into the documented 409 outcome — two concurrent
        // POSTs with the same email both pass the AnyAsync pre-check and the
        // second insert would otherwise surface as a 500. (Mirrors CreateRoleAsync.)
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { SqlState: "23505" } pg
            && string.Equals(pg.ConstraintName, "uq_users_email", StringComparison.Ordinal))
        {
            return new CreateUserResult(CreateUserOutcome.DuplicateEmail);
        }
        catch (PostgresException pg) when (
            pg.SqlState is "23505"
            && string.Equals(pg.ConstraintName, "uq_users_email", StringComparison.Ordinal))
        {
            // Npgsql sometimes surfaces commit-time constraint failures bare
            // rather than wrapping them in DbUpdateException — mirror the
            // defensive catch from AssignRolesAsync. (Story 2.8 code review P7.)
            return new CreateUserResult(CreateUserOutcome.DuplicateEmail);
        }

        var detail = new UserDetailResponse(
            user.Id,
            user.Email,
            user.DisplayName,
            user.IsActive,
            user.CreatedAt,
            user.UpdatedAt,
            [],
            false); // MfaEnabled — new user never has MFA at creation time

        return new CreateUserResult(CreateUserOutcome.Success, user.Id, detail);
    }

    public async Task<UpdateUserResult> UpdateUserAsync(Guid id, UpdateUserRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Mutating a deactivated user is intentional — supports the "reset password,
        // then reactivate" admin flow. (Story 2.8 code review decision D2.)
        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Id == id, ct)
            .ConfigureAwait(false);

        if (user is null)
        {
            return new UpdateUserResult(UpdateUserOutcome.NotFound);
        }

        // Track whether any field actually changed so a same-value resubmit
        // does not bump UpdatedAt or revoke refresh tokens needlessly.
        // (Story 2.8 code review patch P10.)
        var changed = false;

        if (request.DisplayName is not null)
        {
            var trimmed = request.DisplayName.Trim();
            if (!string.Equals(trimmed, user.DisplayName, StringComparison.Ordinal))
            {
                user.DisplayName = trimmed;
                changed = true;
            }
        }

        var passwordChanged = false;
        if (request.NewPassword is not null)
        {
            user.PasswordHash = passwordHasher.Hash(request.NewPassword);
            passwordChanged = true;
            changed = true;
        }

        if (!changed)
        {
            return new UpdateUserResult(UpdateUserOutcome.Success);
        }

        var updatedAt = DateTimeOffset.UtcNow;
        user.UpdatedAt = updatedAt;

        if (passwordChanged)
        {
            // Password rotation must invalidate every existing refresh token —
            // a leaked refresh credential from before the reset would otherwise
            // keep minting access tokens. Same discipline as DeactivateUserAsync.
            // Wrap the row update + token sweep in one explicit transaction so
            // a partial failure cannot leave the password hash advanced while
            // stale refresh tokens remain live. (Story 2.8 code review patch P6.)
            var txn = await db.Database
                .BeginTransactionAsync(ct)
                .ConfigureAwait(false);
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await RevokeActiveRefreshTokensAsync(id, updatedAt, ct).ConfigureAwait(false);
                await txn.CommitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                await txn.DisposeAsync().ConfigureAwait(false);
            }
        }
        else
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return new UpdateUserResult(UpdateUserOutcome.Success);
    }

    public async Task<DeactivateUserResult> DeactivateUserAsync(Guid id, Guid currentUserId, CancellationToken ct)
    {
        // Self-deactivation is blocked server-side regardless of client checks
        // (FR-7 AC-5). Return before any DB work so the outcome is unambiguous.
        if (id == currentUserId)
        {
            return new DeactivateUserResult(DeactivateUserOutcome.SelfDeactivation);
        }

        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Id == id, ct)
            .ConfigureAwait(false);

        if (user is null)
        {
            return new DeactivateUserResult(DeactivateUserOutcome.NotFound);
        }

        // Idempotent on IsActive but always sweep refresh tokens AND publish
        // the bust event — a retry must clean up a stale cache entry or any
        // token minted in the window between two deactivation calls.
        // (Story 2.8 code review patches P2 + P3 + P5.)
        var now = DateTimeOffset.UtcNow;
        var txn = await db.Database
            .BeginTransactionAsync(ct)
            .ConfigureAwait(false);
        try
        {
            if (user.IsActive)
            {
                user.IsActive = false;
                user.UpdatedAt = now;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            // FR-1: revoke every active refresh token so the user cannot bounce
            // a stolen refresh credential to re-acquire a JWT after deactivation.
            await RevokeActiveRefreshTokensAsync(id, now, ct).ConfigureAwait(false);

            await txn.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await txn.DisposeAsync().ConfigureAwait(false);
        }

        // Publish AFTER the commit (same discipline as AssignRolesAsync) so
        // PermissionService's cache bust never races a rolled-back deactivation.
        // Published even on the idempotent path so a stale cache entry can be
        // evicted by retry. InProcessEventBus isolates subscriber exceptions,
        // so a handler failure does not bubble back to this caller. AR-11/AR-47.
        bus.Publish(new UserDeactivated(id));
        return new DeactivateUserResult(DeactivateUserOutcome.Success);
    }

    public async Task<ReactivateUserResult> ReactivateUserAsync(Guid id, CancellationToken ct)
    {
        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Id == id, ct)
            .ConfigureAwait(false);

        if (user is null)
        {
            return new ReactivateUserResult(ReactivateUserOutcome.NotFound);
        }

        if (!user.IsActive)
        {
            user.IsActive = true;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // Publish on every call (including idempotent re-activation) so the
        // permission cache reflects the live IsActive flag promptly. Without
        // this, a reactivated user whose JWT survived sees IsActive=false in
        // the cached permission entry — usePermission denies every check —
        // until the 30 s TTL expires. (Story 2.8 code review patch P4.)
        bus.Publish(new UserReactivated(id));
        return new ReactivateUserResult(ReactivateUserOutcome.Success);
    }

    public async Task<AdminMfaResetResult> ResetUserMfaAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            .ConfigureAwait(false);

        if (user is null)
        {
            return new AdminMfaResetResult(AdminMfaResetOutcome.NotFound);
        }

        var now = DateTimeOffset.UtcNow;

        // Wrap all mutations in a transaction so a partial failure cannot leave
        // the user row cleared while backup codes or refresh tokens remain live.
        var txn = await db.Database
            .BeginTransactionAsync(ct)
            .ConfigureAwait(false);
        try
        {
            // Idempotent: always clear — already-false fields are no-ops in SaveChangesAsync.
            user.MfaEnabled = false;
            user.MfaSecretProtected = null;
            user.UpdatedAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            // Bulk delete all backup codes for this user (no audit trail needed — codes are
            // cryptographic material, not business records; the user row's UpdatedAt records
            // the reset timestamp). ExecuteDeleteAsync issues a single DELETE WHERE user_id=X.
            await db.MfaBackupCodes
                .Where(c => c.UserId == userId)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            // Revoke all active refresh tokens so the user must re-authenticate.
            await RevokeActiveRefreshTokensAsync(userId, now, ct).ConfigureAwait(false);

            await txn.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await txn.DisposeAsync().ConfigureAwait(false);
        }

        return new AdminMfaResetResult(AdminMfaResetOutcome.Success);
    }

    // Revoke every active refresh token for a user in a single UPDATE … WHERE … via
    // ExecuteUpdateAsync (bounded memory — tokens are never loaded into the change
    // tracker; audit history survives because RevokedAt is stamped rather than deleted).
    // Shared by password rotation, deactivation, and admin MFA reset so the sweep
    // semantics stay identical across all three. Caller owns any enclosing transaction.
    private Task<int> RevokeActiveRefreshTokensAsync(Guid userId, DateTimeOffset revokedAt, CancellationToken ct) =>
        db.RefreshTokens
            .Where(rt => rt.UserId == userId && rt.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(rt => rt.RevokedAt, revokedAt), ct);
}
