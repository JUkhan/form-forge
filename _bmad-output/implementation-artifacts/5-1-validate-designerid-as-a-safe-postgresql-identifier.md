# Story 5.1: Validate designerId as a Safe PostgreSQL Identifier

Status: done

## Story

As the system,
I validate any `designerId` before using it as a table name,
so that DDL statements cannot be constructed with unsafe input.

## Acceptance Criteria

1. **Given** a `designerId` value entering the validation pipeline,
   **When** it is checked against the regex `^[a-z_][a-z0-9_]{0,62}$` (lowercase letters, digits, underscores; starts with letter or underscore; 1–63 characters) (per AR-4),
   **Then** invalid values are rejected with HTTP 422, `code: "IDENTIFIER_INVALID"`.

2. **Given** a `designerId` that matches the regex but appears in the hardcoded PostgreSQL 17 reserved-keyword list (per AR-4),
   **When** it is validated,
   **Then** the response is HTTP 422 with `code: "IDENTIFIER_RESERVED_KEYWORD"`.

3. **Given** any code path that composes SQL with a dynamic identifier,
   **When** it builds the SQL,
   **Then** the identifier must pass through the `SafeIdentifier` value type (per AR-4); raw strings are never interpolated;
   **And** a code-review pattern check (Roslyn analyzer deferred) flags any direct string interpolation of a `designerId` or `fieldKey` into SQL.

4. **Given** validation runs at Designer creation (Story 3.2 — already implemented) and at every DDL emit and dynamic-CRUD identifier substitution (Stories 5.3+),
   **When** any layer encounters an invalid identifier,
   **Then** the request is rejected at that layer (defense in depth — FR-23 AC-4). Layers 2–3 (DDL and DynamicCrud) are deferred to Stories 5.3+ when those code paths are built.

## Tasks / Subtasks

- [x] **Task 1 — Enhance `SafeIdentifier` to expose failure reason** (AC: 1, 2)
  - [x] Add `internal enum SafeIdentifierError { InvalidPattern, ReservedKeyword }` inside `SafeIdentifier.cs` (same file, same namespace `FormForge.Api.Features.Designer`)
  - [x] Add an overload (keep the existing 3-param signature unchanged for `FieldKeyValidator` compatibility):
    ```csharp
    public static bool TryCreate(
        string? raw,
        out SafeIdentifier? result,
        out SafeIdentifierError? errorCode,
        out string? error)
    ```
    The implementation calls the existing logic; `errorCode` is set to `SafeIdentifierError.InvalidPattern` on regex/empty/length failure, `SafeIdentifierError.ReservedKeyword` when `PgReservedKeywords.IsReserved` is true.
  - [x] The existing 3-param overload `TryCreate(raw, out result, out error)` MUST be preserved unchanged so `FieldKeyValidator.cs` compiles without modification.

- [x] **Task 2 — Update `DesignerService` to propagate the distinction** (AC: 1, 2)
  - [x] In `DesignerService.cs`, add `IdentifierReservedKeyword` to `CreateDesignerOutcome`:
    ```csharp
    internal enum CreateDesignerOutcome { Success, DesignerExists, IdentifierInvalid, IdentifierReservedKeyword }
    ```
  - [x] In `CreateAsync`, call the new 4-param overload:
    ```csharp
    if (!SafeIdentifier.TryCreate(request.DesignerId, out var safeId, out var failureCode, out var idError))
    {
        var outcome = failureCode == SafeIdentifierError.ReservedKeyword
            ? CreateDesignerOutcome.IdentifierReservedKeyword
            : CreateDesignerOutcome.IdentifierInvalid;
        return new CreateDesignerResult(outcome, idError);
    }
    ```

- [x] **Task 3 — Update `DesignerEndpoints` to emit the correct error codes** (AC: 1, 2)
  - [x] In `DesignerEndpoints.cs` `CreateDesignerHandler`, add a new `IdentifierReservedKeyword` branch BEFORE the existing `IdentifierInvalid` branch:
    ```csharp
    CreateDesignerOutcome.IdentifierReservedKeyword => Results.Problem(
        title: "Identifier is a reserved PostgreSQL keyword",
        detail: result.ErrorDetail,
        statusCode: StatusCodes.Status422UnprocessableEntity,
        extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = "IDENTIFIER_RESERVED_KEYWORD",
            ["messageKey"] = "designers.identifierReservedKeyword",
        }),
    CreateDesignerOutcome.IdentifierInvalid => Results.Problem(  // keep existing
        ...
    ```
  - [x] No change needed to `VersionNotPublishedProblem()` or any other helper — they are unrelated.

- [x] **Task 4 — Update `SafeIdentifierTests.cs`** (AC: 1, 2)
  - [x] Add tests for the new 4-param overload — specifically proving that `errorCode` is set correctly:
    - `TryCreate_InvalidPattern_SetsErrorCodeInvalidPattern` — e.g., `"Has-Bad-Chars"` → `errorCode == SafeIdentifierError.InvalidPattern`
    - `TryCreate_ReservedKeyword_SetsErrorCodeReservedKeyword` — e.g., `"select"` → `errorCode == SafeIdentifierError.ReservedKeyword`
    - `TryCreate_ValidIdentifier_ErrorCodeIsNull` — e.g., `"incident_report"` → `errorCode == null`, returns `true`
  - [x] The existing tests (`TryCreate_ValidIdentifiers_Succeed`, `TryCreate_InvalidIdentifiers_Fail`, `TryCreate_ReservedKeywords_Fail`) use the 3-param overload and must be left unchanged.

- [x] **Task 5 — Update `DesignerIntegrationTests.cs`** (AC: 2)
  - [x] Rename existing `CreateDesigner_ReservedPgKeyword_Returns422_IdentifierInvalid` to `CreateDesigner_ReservedPgKeyword_Returns422_IdentifierReservedKeyword`.
  - [x] Update its assertion from `Assert.Contains("IDENTIFIER_INVALID", ...)` to `Assert.Contains("IDENTIFIER_RESERVED_KEYWORD", ...)`.
  - [x] Add a new `[Fact]` `CreateDesigner_InvalidPattern_Returns422_IdentifierInvalid` that explicitly asserts `IDENTIFIER_INVALID` for a format-invalid id (e.g., `"Has-Uppercase"`) — makes both codes test-locked at integration level.
  - [x] **DO NOT** change `CreateDesigner_InvalidDesignerId_Returns422_IdentifierInvalid` (the `[Theory]` for charset/length failures) — it correctly asserts `IDENTIFIER_INVALID`.

- [x] **Task 6 — Add `FieldKeyValidatorTests.cs`** (AC: 3)
  - [x] Create `src/FormForge.Api.Tests/Features/Designer/FieldKeyValidatorTests.cs` — unit tests that do NOT need a DB:
    - `Validate_NullRoot_ReturnsValid` — `FieldKeyValidator.Validate(null).IsValid == true`.
    - `Validate_ComponentMissingFieldKey_ReturnsFieldKeyMissing` — JSON node for a `"Text Input"` component with no `properties.fieldKey` → error code `FIELD_KEY_MISSING`.
    - `Validate_ComponentInvalidFieldKey_ReturnsFieldKeyInvalid` — fieldKey `"Has-Dash"` → error code `FIELD_KEY_INVALID`.
    - `Validate_ComponentReservedKeyword_ReturnsFieldKeyInvalid` — fieldKey `"select"` (reserved keyword) → error code `FIELD_KEY_INVALID` (NOT `FIELD_KEY_RESERVED_KEYWORD` — see Dev Notes for why this is intentional for v1).
    - `Validate_DuplicateFieldKeys_ReturnsFieldKeyCollision` — two `"Text Input"` siblings with same fieldKey → error code `FIELD_KEY_COLLISION`.
    - `Validate_ValidSchema_ReturnsValid` — a tree with two valid, distinct field keys → `IsValid == true`.
  - [x] Do NOT add a DB-fixture dependency — these are pure in-memory JSON node tests.

## Dev Notes

### CRITICAL: What Already Exists — Read Before Writing Any Code

The foundation for Story 5.1 was partially built during Epic 3 (Designer stories). You MUST read and understand these files before touching anything:

- **`src/FormForge.Api/Features/Designer/SafeIdentifier.cs`** — already implements the 3-param `TryCreate(raw, out result, out error)` with the correct regex and reserved-keyword check. Located in `Features/Designer/` NOT in `Domain/ValueTypes/` (architecture doc describes the intended final location; actual code landed in the feature folder during Story 3.2 implementation). DO NOT move it.
- **`src/FormForge.Api/Features/Designer/PgReservedKeywords.cs`** — complete PostgreSQL 17 reserved keyword list plus pg_* prefix block plus system-column collision list (`id`, `created_at`, etc.). DO NOT recreate.
- **`src/FormForge.Api/Features/Designer/FieldKeyValidator.cs`** — already uses `SafeIdentifier.TryCreate` for fieldKey validation. Uses the 3-param overload. DO NOT break its compilation.
- **`src/FormForge.Api/Features/Designer/DesignerService.cs:80-83`** — `CreateAsync` already calls `SafeIdentifier.TryCreate` and returns `CreateDesignerOutcome.IdentifierInvalid` for ALL failures (both regex and reserved keyword). This is the GAP to fix.
- **`src/FormForge.Api/Features/Designer/DesignerEndpoints.cs:87-95`** — maps `IdentifierInvalid` to `code: "IDENTIFIER_INVALID"`. Currently uses one code for BOTH failure types. Needs the new branch.
- **`src/FormForge.Api.Tests/Features/Designer/SafeIdentifierTests.cs`** — tests the 3-param overload with 27 [InlineData] cases. The new 4-param overload needs its own tests.
- **`src/FormForge.Api.Tests/Features/Designer/DesignerIntegrationTests.cs:159-177`** — `CreateDesigner_ReservedPgKeyword_Returns422_IdentifierInvalid` currently asserts `IDENTIFIER_INVALID` but must be updated to assert `IDENTIFIER_RESERVED_KEYWORD` (that's the AC-2 gap).

### The One Real Gap This Story Fixes

AC-2 requires `code: "IDENTIFIER_RESERVED_KEYWORD"` for reserved-keyword rejections. The existing code emits `code: "IDENTIFIER_INVALID"` for BOTH regex failures and reserved keyword failures. Everything else (the `SafeIdentifier` type, the `PgReservedKeywords` list, Designer creation validation, FieldKeyValidator) already exists and works correctly. This story's backend work is narrow and surgical.

### File Location Convention — Feature Folder, Not Domain Layer

The architecture doc describes `Domain/ValueTypes/SafeIdentifier.cs` as the intended path. The actual implementation landed in `Features/Designer/SafeIdentifier.cs` during Story 3.2. Follow the actual code, not the architecture doc. Any new Epic 5 feature that needs `SafeIdentifier` imports it from `FormForge.Api.Features.Designer`.

### FieldKeyValidator Must Not Be Broken

`FieldKeyValidator.cs` calls `SafeIdentifier.TryCreate(rawKey, out _, out var detail)` (the 3-param overload). You MUST preserve the 3-param signature. The new 4-param overload is an ADDITION, not a replacement. C# supports method overloads by parameter count — both `TryCreate(raw, out result, out error)` and `TryCreate(raw, out result, out errorCode, out error)` can coexist.

### Why `FIELD_KEY_INVALID` (Not `FIELD_KEY_RESERVED_KEYWORD`) for FieldKeyValidator

`FieldKeyValidator` uses a single `FIELD_KEY_INVALID` code for all SafeIdentifier failures including reserved keywords. This is intentional for v1: the designer UI's inline error list shows the human-readable `error` message (which includes the word "reserved" for reserved-keyword hits), so the SPA doesn't need a distinct code to localize. This story does NOT change fieldKey error codes — the AC is specifically about `designerId`. If a future story needs to distinguish fieldKey failure reasons, that's the time to update `FieldKeyValidator` to use the 4-param overload.

### Roslyn Analyzer — Code Review Pattern Check for v1

AC-3 says "a Roslyn analyzer (or code-review pattern check) flags any direct string interpolation of a `designerId` or `fieldKey` into SQL." Building a Roslyn analyzer is out of scope for v1. The code-review enforcement is: (1) `SafeIdentifier` is a sealed class with a private constructor — you cannot get a raw `string` into SQL without going through `TryCreate` first; (2) the code review checklist flags any `$"... {something} ..."` interpolation inside a Dapper SQL string. Document this in a comment in `SafeIdentifier.cs` if not already present. No new code needed for this AC.

### Defense in Depth — Layer Map

| Layer | Location | Status |
|---|---|---|
| Layer 1: Designer creation | `DesignerService.CreateAsync` + `SafeIdentifier.TryCreate` | ✅ Exists — this story fixes the error-code gap |
| Layer 2: DDL emit | `Features/Provisioning/DdlEmitter.cs` | ❌ File doesn't exist yet — deferred to Story 5.3 |
| Layer 3: Dynamic CRUD | `Features/DynamicCrud/DynamicQueryBuilder.cs` | ❌ File doesn't exist yet — deferred to Epic 6 |

`DynamicDataEndpoints.cs` currently exists as an empty stub with a comment. Do NOT add SafeIdentifier validation there yet — there are no handlers to validate.

### Error Code Envelope Pattern (Copy Exactly)

Every 422 in this codebase uses this exact shape (from `DesignerEndpoints.cs`):
```csharp
Results.Problem(
    title: "...",
    detail: result.ErrorDetail,  // can be null
    statusCode: StatusCodes.Status422UnprocessableEntity,
    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["code"] = "IDENTIFIER_RESERVED_KEYWORD",
        ["messageKey"] = "designers.identifierReservedKeyword",
    })
```
The `messageKey` follows dot-notation per AR-33 / architecture line 610. Add the i18n key to `web/src/lib/i18n/locales/en.json` in the `designers` block. Suggested value: `"identifierReservedKeyword": "This name is a reserved PostgreSQL keyword. Choose a different name."`.

### Frontend Scope

No frontend changes are required for this story beyond the new i18n key. The existing designer form already shows a 422 error toast when `POST /api/designers` fails; the toast displays the backend `detail` message which will now correctly say "reserved keyword" for those cases. No new component, no new API call.

### Test Patterns to Follow

**Unit tests (no DB):** `SafeIdentifierTests.cs` pattern — `[Theory]` + `[InlineData]`, pure in-memory assertions, no fixture.

**Integration tests:** `DesignerIntegrationTests.cs` pattern:
- `LoginAsync("admin@example.com", "Password1!")` for the platform-admin JWT
- Raw `HttpRequestMessage` with `JsonContent.Create(new { designerId = "...", displayName = "..." })`
- `response.Content.ReadAsStringAsync()` then `Assert.Contains("IDENTIFIER_RESERVED_KEYWORD", body, StringComparison.Ordinal)`
- `[IClassFixture<PostgresFixture>]` + `IAsyncLifetime` — the test class already has both wired

**FieldKeyValidatorTests.cs new file:** Use `JsonNode.Parse(...)` to build test inputs. No fixture, no DB. Follow the same namespace pattern `FormForge.Api.Tests.Features.Designer` (same folder as the other Designer tests).

### Test Count Baseline and Estimate

- Backend baseline (end of Story 4.7): **306 tests**
- Expected additions:
  - +3 `SafeIdentifierTests.cs` (new 4-param overload)
  - +1 `DesignerIntegrationTests.cs` (new `CreateDesigner_InvalidPattern_Returns422_IdentifierInvalid` [Fact])
  - +6 `FieldKeyValidatorTests.cs` (new file)
  - Note: updating the renamed test is a 1:1 swap, not a net new test
- Expected total: **306 + 10 = ~316** (all existing 306 must still pass)

### Project Structure Notes

**Backend modified files:**
- `src/FormForge.Api/Features/Designer/SafeIdentifier.cs` — add `SafeIdentifierError` enum and 4-param `TryCreate` overload
- `src/FormForge.Api/Features/Designer/DesignerService.cs` — add `IdentifierReservedKeyword` to `CreateDesignerOutcome` enum; update `CreateAsync` to use 4-param overload and set the new outcome
- `src/FormForge.Api/Features/Designer/DesignerEndpoints.cs` — add `IdentifierReservedKeyword` branch in `CreateDesignerHandler` switch

**Backend new files:**
- `src/FormForge.Api.Tests/Features/Designer/FieldKeyValidatorTests.cs`

**Backend modified tests:**
- `src/FormForge.Api.Tests/Features/Designer/SafeIdentifierTests.cs` — add 3 new [Fact] methods for the 4-param overload
- `src/FormForge.Api.Tests/Features/Designer/DesignerIntegrationTests.cs` — rename + update 1 test; add 1 new test

**Frontend modified files:**
- `web/src/lib/i18n/locales/en.json` — add `"identifierReservedKeyword"` key to the `designers` block

**No DB migration needed** — no new columns, tables, or constraints.
**No new DI registrations** — `SafeIdentifier` is a static class; no `IValidator<T>` or service registration needed.
**No frontend component changes** — existing error toast picks up the new error detail message automatically.

### References

- **Epic 5 spec:** `_bmad-output/planning-artifacts/epics.md` (Story 5.1 AC verbatim)
- **Architecture — identifier sanitization:** `architecture.md:263-267` (Decision 1.1 — regex, reserved keyword list, defense in depth, SafeIdentifier value type)
- **Architecture — backend folder structure:** `architecture.md:985-1107` (Feature folders, Domain/ValueTypes, test structure)
- **Architecture — error codes:** `architecture.md:503` (`designerId` path param OpenAPI pattern)
- **Existing `SafeIdentifier`:** `src/FormForge.Api/Features/Designer/SafeIdentifier.cs`
- **Existing `PgReservedKeywords`:** `src/FormForge.Api/Features/Designer/PgReservedKeywords.cs`
- **Existing `FieldKeyValidator`:** `src/FormForge.Api/Features/Designer/FieldKeyValidator.cs` (uses 3-param overload — must not break)
- **Existing service gap:** `src/FormForge.Api/Features/Designer/DesignerService.cs:80-83` (both failure types collapse to `IdentifierInvalid`)
- **Existing endpoint gap:** `src/FormForge.Api/Features/Designer/DesignerEndpoints.cs:87-95` (one code for both failures)
- **Existing reserved-keyword test to update:** `src/FormForge.Api.Tests/Features/Designer/DesignerIntegrationTests.cs:159-177`
- **i18n pattern:** `architecture.md:608-612` (dot-notation, `en.json` location)
- **FR-23 (designerId validation):** `_bmad-output/planning-artifacts/epics.md` Epic 5 overview
- **NFR-6 (SQL injection defense):** Epic 5 NFRs covered by SafeIdentifier

## Dev Agent Record

### Agent Model Used

claude-opus-4-7[1m] (Claude Code)

### Debug Log References

- `dotnet build src/FormForge.Api/FormForge.Api.csproj` — clean (0 warnings, 0 errors) after the SafeIdentifier + DesignerService + DesignerEndpoints edits.
- `dotnet test src/FormForge.Api.Tests/FormForge.Api.Tests.csproj --filter "FullyQualifiedName~Features.Designer"` — 92/92 passed (Designer feature subset).
- `dotnet test src/FormForge.Api.Tests/FormForge.Api.Tests.csproj` — 316/316 passed (full backend suite). Matches the spec estimate of 306 baseline + 10 new tests exactly.
- `npm run type-check` (web/) — clean.
- `npm run lint` (web/) — 32 errors total, all pre-existing (Story 3.6 `react-hooks/set-state-in-effect` at `designer.$designerId.tsx:175` + the route-file `react-refresh/only-export-components` cluster). Zero net new lint errors from this story.

### Completion Notes List

- **Design choice on the 3-param/4-param overload pair:** The spec says "preserve the 3-param overload unchanged" so `FieldKeyValidator` continues to compile. I preserved the public signature exactly (`TryCreate(raw, out result, out error)`) and made it a one-line delegation to the new 4-param overload (`=> TryCreate(raw, out result, out _, out error);`). This satisfies the binary-compat requirement (FieldKeyValidator.cs needed zero edits, confirmed by clean build) while consolidating the validation logic in one place — no risk of the two overloads drifting on the error-string wording or the order of the three checks. The story's "implementation calls the existing logic" guidance describes the new overload's structure; the consolidation direction (3-param delegates to 4-param instead of the reverse) is equivalent in behavior and avoids logic duplication.
- **AC-3 / Roslyn analyzer:** Per Dev Notes "Roslyn analyzer deferred", added a comment on the `SafeIdentifier` class explaining that the sealed-class + private-ctor design is the v1 enforcement mechanism — there is no way to obtain a `SafeIdentifier` instance without going through `TryCreate`, so downstream DDL/CRUD code that takes `SafeIdentifier` (not `string`) as a parameter is statically protected from raw-string interpolation. No new code beyond the comment is needed for AC-3.
- **AC-4 / defense in depth:** Layers 2 (DDL emit) and 3 (DynamicCrud) are explicitly out of scope per the story (deferred to Stories 5.3+ and Epic 6). Layer 1 (Designer creation) was the only existing layer; this story fixed its error-code reporting gap. `DynamicDataEndpoints.cs` remains an empty stub untouched.
- **i18n key placement:** Added `designers.identifierReservedKeyword` to `en.json` directly after `designers.identifierInvalid` (alphabetical-by-purpose grouping, matching the surrounding block's ordering). Suggested wording from Dev Notes used verbatim: "This name is a reserved PostgreSQL keyword. Choose a different name."
- **No frontend component changes required:** The existing designer-create error toast already renders the backend `detail` message verbatim; SafeIdentifier's "is a reserved PostgreSQL keyword..." text will now flow through automatically. No new component, no new API call, no TanStack mutation update.
- **Test count delta:** baseline 306 + 3 (SafeIdentifier 4-param overload tests) + 1 (DesignerIntegrationTests new InvalidPattern Fact) + 6 (FieldKeyValidatorTests new file) = 316. The renamed reserved-keyword integration test is a 1:1 swap, not a net new test, per the spec.

### File List

**Modified (backend production):**
- `src/FormForge.Api/Features/Designer/SafeIdentifier.cs` — added `SafeIdentifierError` enum + 4-param `TryCreate` overload; original 3-param overload preserved as a one-line delegation to the new method.
- `src/FormForge.Api/Features/Designer/DesignerService.cs` — added `IdentifierReservedKeyword` to `CreateDesignerOutcome`; updated `CreateAsync` to call the 4-param overload and route the outcome based on `failureCode`.
- `src/FormForge.Api/Features/Designer/DesignerEndpoints.cs` — added `CreateDesignerOutcome.IdentifierReservedKeyword` switch branch before the existing `IdentifierInvalid` branch in `CreateDesignerHandler`; emits `IDENTIFIER_RESERVED_KEYWORD` + `designers.identifierReservedKeyword` message key.

**Modified (backend tests):**
- `src/FormForge.Api.Tests/Features/Designer/SafeIdentifierTests.cs` — added 3 new `[Fact]` methods (`TryCreate_InvalidPattern_SetsErrorCodeInvalidPattern`, `TryCreate_ReservedKeyword_SetsErrorCodeReservedKeyword`, `TryCreate_ValidIdentifier_ErrorCodeIsNull`) for the 4-param overload; existing 3-param `[Theory]` cases unchanged.
- `src/FormForge.Api.Tests/Features/Designer/DesignerIntegrationTests.cs` — renamed `CreateDesigner_ReservedPgKeyword_Returns422_IdentifierInvalid` → `..._IdentifierReservedKeyword`, swapped assertion to `IDENTIFIER_RESERVED_KEYWORD`, added new `CreateDesigner_InvalidPattern_Returns422_IdentifierInvalid` Fact that also asserts `DoesNotContain("IDENTIFIER_RESERVED_KEYWORD", ...)` to lock the two-code distinction.

**New (backend tests):**
- `src/FormForge.Api.Tests/Features/Designer/FieldKeyValidatorTests.cs` — 6 pure in-memory `JsonNode.Parse(...)` tests covering null-root, missing fieldKey, invalid fieldKey, reserved-keyword fieldKey (asserts `FIELD_KEY_INVALID` not `FIELD_KEY_RESERVED_KEYWORD` per v1 design), collision, and valid schema cases.

**Modified (frontend):**
- `web/src/lib/i18n/locales/en.json` — added `"identifierReservedKeyword"` key under the `designers` block, immediately after `identifierInvalid`.

### Change Log

- 2026-05-25 — Implemented Story 5.1 (Validate designerId as a Safe PostgreSQL Identifier). Backend now distinguishes IDENTIFIER_INVALID (regex/length/empty failures) from IDENTIFIER_RESERVED_KEYWORD (PG reserved-keyword failures) per AC-2; FieldKeyValidator semantics intentionally unchanged. Backend test count 306 → 316 (+10 across SafeIdentifierTests, DesignerIntegrationTests, and the new FieldKeyValidatorTests file). Frontend gets one new i18n key; no component changes.

### Review Findings

- [x] [Review][Patch] No 4-param overload test for `pg_*`-prefix input producing `ReservedKeyword` errorCode [`src/FormForge.Api.Tests/Features/Designer/SafeIdentifierTests.cs`] — existing `TryCreate_ReservedKeywords_Fail` [Theory] covers `pg_*` through the 3-param overload, but no test asserts that a `pg_`-prefixed input (e.g. `"pg_toast"`) sets `errorCode == SafeIdentifierError.ReservedKeyword` through the new 4-param overload. Add one `[InlineData]` to `TryCreate_ReservedKeyword_SetsErrorCodeReservedKeyword` or a new `[Fact]`.
- [x] [Review][Patch] Stale comment in `[Theory]` test claims reserved-keyword failures produce `IDENTIFIER_INVALID` [`src/FormForge.Api.Tests/Features/Designer/DesignerIntegrationTests.cs`] — the unchanged `CreateDesigner_InvalidDesignerId_Returns422_IdentifierInvalid` [Theory] retains a comment "Structural failures (regex/length/empty) and semantic failures (reserved keyword) both flow through SafeIdentifier … IDENTIFIER_INVALID" which is now false after Story 5.1. The InlineData inputs are all regex failures so the assertion is correct, but the comment misleads future readers. Update the comment to reflect that reserved keywords now produce `IDENTIFIER_RESERVED_KEYWORD`.
- [x] [Review][Defer] `"Has-Bad-Chars"` test input exercises uppercase-start failure first; hyphen never evaluated [`src/FormForge.Api.Tests/Features/Designer/SafeIdentifierTests.cs:TryCreate_InvalidPattern_SetsErrorCodeInvalidPattern`] — deferred, pre-existing
