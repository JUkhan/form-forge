using System.Collections.Frozen;
using FormForge.Api.Common;
using FormForge.Api.Common.Endpoints;
using FormForge.Api.Common.Logging;
using FormForge.Api.Domain.ValueTypes;
using FormForge.Api.Features.Datasets.Dtos;
using FormForge.Api.Features.DynamicCrud;
using FormForge.Api.Features.DynamicCrud.Export;
using Microsoft.AspNetCore.Mvc;

namespace FormForge.Api.Features.Datasets;

// Story 8.2 (FR-56) — scaffolds the /api/datasets route group with permission
// wiring. Story 8.3 (FR-57) — the write endpoints validate dataset_name inline via
// DatasetName.TryCreate, returning a 422 INVALID_DATASET_NAME envelope before any
// handler logic. Story 8.4 (FR-58) — POST / is now fully implemented (create row +
// backing VIEW). Story 8.5 (FR-58 / FR-59) — PUT /{id} updates the row + VIEW
// atomically with optimistic concurrency. Story 8.6 (FR-58) — DELETE /{id} removes
// the row + backing VIEW atomically. Story 8.7 (FR-62) — GET / list + GET /{id}
// single (auth-only). The remaining handlers stay 501/stub until: preview (11.3).
internal static class DatasetEndpoints
{
    internal static RouteGroupBuilder MapDatasetEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        // Story 8.7 (FR-62 / AR-65) — paginated list; auth-only (no RequireDatasetManagement).
        group.MapGet("/", async (
            IDatasetService datasetService,
            CancellationToken ct,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 25,
            [FromQuery] string? search = null,
            [FromQuery] string? sort = null) =>
        {
            var result = await datasetService.ListAsync(page, pageSize, search, sort, ct).ConfigureAwait(false);
            return Results.Ok(result);
        })
             .WithSummary("List datasets (Story 8.7 — FR-62)");

        // Story 8.7 (FR-62 / AR-65) — get by id; auth-only (no RequireDatasetManagement).
        group.MapGet("/{id:guid}", async (
            Guid id,
            IDatasetService datasetService,
            CancellationToken ct) =>
        {
            var dto = await datasetService.GetByIdAsync(id, ct).ConfigureAwait(false);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        })
             .WithSummary("Get dataset (Story 8.7 — FR-62)");

        // Dataset-backed Dropdown source — the backing VIEW's columns, for the
        // designer inspector's Label/Value field comboboxes. Auth-only (a form
        // author needs it; it leaks only column NAMES, never row data). The literal
        // "/columns" segment is more specific than the bare "/{id:guid}" above.
        group.MapGet("/{id:guid}/columns", async (
            Guid id,
            IDatasetDropdownService dropdownService,
            CancellationToken ct) =>
        {
            var dto = await dropdownService.GetColumnsAsync(id, ct).ConfigureAwait(false);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        })
             .WithSummary("Get a dataset's VIEW columns (Dataset-backed Dropdown source)")
             .Produces<DatasetColumnsDto>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status404NotFound);

        // Dataset-backed Dropdown options — paginated {value,label} pairs read from
        // the dataset's VIEW. Auth-only (mirrors the Designer-backed options endpoint:
        // a form filler may need a lookup whose dataset they cannot otherwise manage).
        group.MapGet("/{id:guid}/options", async (
            Guid id,
            HttpContext httpContext,
            IDatasetDropdownService dropdownService,
            CancellationToken ct,
            [FromQuery] string? labelField = null,
            [FromQuery] string? valueField = null,
            [FromQuery] string? search = null,
            [FromQuery] string? value = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50) =>
        {
            if (string.IsNullOrWhiteSpace(labelField) || string.IsNullOrWhiteSpace(valueField))
            {
                return Results.Problem(
                    detail: "'labelField' and 'valueField' query parameters are required.",
                    title: "Validation failed",
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["code"] = "VALIDATION_FAILED",
                        ["messageKey"] = "errors.validationFailed",
                    });
            }

            var filters = ParseCascadingFilters(httpContext.Request.Query);
            var result = await dropdownService
                .GetOptionsAsync(id, labelField, valueField, search, value, filters, page, pageSize, ct)
                .ConfigureAwait(false);

            return result.Outcome switch
            {
                DatasetOptionsOutcome.Success => Results.Ok(result.Data),
                DatasetOptionsOutcome.NotFound => Results.NotFound(),
                DatasetOptionsOutcome.InvalidField => Results.Problem(
                    detail: result.ErrorDetail,
                    title: "Validation failed",
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["code"] = "VALIDATION_FAILED",
                        ["messageKey"] = "errors.validationFailed",
                    }),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
            };
        })
             .WithSummary("List {value,label} options for a Dataset-backed dropdown (paginated, searchable).")
             .Produces<PagedResult<DropdownOption>>(StatusCodes.Status200OK)
             .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
             .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity);

        // Name-keyed variants of the two Dropdown lookups above. A Dataset-backed
        // Dropdown stores the dataset's VIEW NAME (optionsDatasetId) — not its GUID —
        // exactly like a Designer-backed Dropdown stores a table name. These let the
        // inspector + runtime resolve columns/options straight from that name, so the
        // record-list label column needs no id→name lookup. The "/by-name/" prefix
        // keeps these unambiguous against the "/{id:guid}/" routes. Auth-only, matching
        // the GUID variants (a form filler may need a lookup they cannot otherwise manage).
        group.MapGet("/by-name/{name}/columns", async (
            string name,
            IDatasetDropdownService dropdownService,
            CancellationToken ct) =>
        {
            if (!DatasetName.TryCreate(name, out var datasetName, out _))
                return InvalidDatasetName($"'{name}' is not a valid dataset name.");

            var dto = await dropdownService.GetColumnsByNameAsync(datasetName!, ct).ConfigureAwait(false);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        })
             .WithSummary("Get a dataset's VIEW columns by name (Dataset-backed Dropdown source)")
             .Produces<DatasetColumnsDto>(StatusCodes.Status200OK)
             .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
             .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/by-name/{name}/options", async (
            string name,
            HttpContext httpContext,
            IDatasetDropdownService dropdownService,
            CancellationToken ct,
            [FromQuery] string? labelField = null,
            [FromQuery] string? valueField = null,
            [FromQuery] string? search = null,
            [FromQuery] string? value = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50) =>
        {
            if (!DatasetName.TryCreate(name, out var datasetName, out _))
                return InvalidDatasetName($"'{name}' is not a valid dataset name.");

            if (string.IsNullOrWhiteSpace(labelField) || string.IsNullOrWhiteSpace(valueField))
            {
                return Results.Problem(
                    detail: "'labelField' and 'valueField' query parameters are required.",
                    title: "Validation failed",
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["code"] = "VALIDATION_FAILED",
                        ["messageKey"] = "errors.validationFailed",
                    });
            }

            var filters = ParseCascadingFilters(httpContext.Request.Query);
            var result = await dropdownService
                .GetOptionsByNameAsync(datasetName!, labelField, valueField, search, value, filters, page, pageSize, ct)
                .ConfigureAwait(false);

            return result.Outcome switch
            {
                DatasetOptionsOutcome.Success => Results.Ok(result.Data),
                DatasetOptionsOutcome.NotFound => Results.NotFound(),
                DatasetOptionsOutcome.InvalidField => Results.Problem(
                    detail: result.ErrorDetail,
                    title: "Validation failed",
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["code"] = "VALIDATION_FAILED",
                        ["messageKey"] = "errors.validationFailed",
                    }),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
            };
        })
             .WithSummary("List {value,label} options for a Dataset-backed dropdown by name (paginated, searchable).")
             .Produces<PagedResult<DropdownOption>>(StatusCodes.Status200OK)
             .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
             .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity);

        // DatasetComponent runtime data-view — paginated rows from the dataset's VIEW with an
        // optional filter tree + sort. POST (not GET) because the filter tree is a structured
        // body (mirrors /preview). Auth-only.
        group.MapPost("/{id:guid}/rows", async (
            Guid id,
            [FromBody] DatasetRowsRequest request,
            IDatasetRowQueryService rowService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var result = await rowService
                .GetRowsAsync(id, request, ct, ResolveAuthUserId(httpContext))
                .ConfigureAwait(false);
            return result.Outcome switch
            {
                DatasetRowsOutcome.Success => Results.Ok(result.Page),
                DatasetRowsOutcome.NotFound => Results.NotFound(),
                DatasetRowsOutcome.InvalidRequest => DatasetValidationFailed(result.ErrorDetail),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
            };
        })
             .WithSummary("List dataset VIEW rows (paginated, filterable, sortable) for a DatasetComponent.")
             .Produces<DatasetRowsPage>(StatusCodes.Status200OK)
             .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
             .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity);

        // Same filtered/sorted result set as a CSV/XLSX/PDF download (reuses the record
        // export writers). Auth-only.
        group.MapPost("/{id:guid}/rows/export", async (
            Guid id,
            [FromBody] DatasetExportRequest request,
            IDatasetRowQueryService rowService,
            HttpContext httpContext,
            CancellationToken ct,
            [FromQuery] string? format = null) =>
        {
            if (format is null || !ExportWriterFactories.TryGetValue(format, out var writerFactory))
                return DatasetValidationFailed("A valid 'format' query parameter is required (csv, xlsx, pdf).");

            var result = await rowService
                .GetExportAsync(id, request, ct, ResolveAuthUserId(httpContext))
                .ConfigureAwait(false);
            switch (result.Outcome)
            {
                case DatasetExportOutcome.Success:
                {
                    var data = result.Data!;
                    var writer = writerFactory();
                    var fileBase = SanitizeFileNameSegment(data.DatasetName);
                    using var ms = new MemoryStream();
                    await writer.WriteAsync(ms, data.DatasetName, data.Headers, data.Rows, ct)
                        .ConfigureAwait(false);
                    return Results.File(
                        fileContents: ms.ToArray(),
                        contentType: writer.ContentType,
                        fileDownloadName: $"{fileBase}.{writer.FileExtension}");
                }
                case DatasetExportOutcome.NotFound:
                    return Results.NotFound();
                case DatasetExportOutcome.TooManyRows:
                case DatasetExportOutcome.InvalidRequest:
                    return DatasetValidationFailed(result.ErrorDetail);
                default:
                    return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }
        })
             .WithSummary("Export dataset VIEW rows (CSV/XLSX/PDF) honoring the current filter and sort.")
             .Produces(StatusCodes.Status200OK)
             .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
             .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity);

        // DatasetComponent chart data — GROUP BY category, aggregate(value) over the dataset's
        // VIEW (honoring the same filter tree). Auth-only.
        group.MapPost("/{id:guid}/chart", async (
            Guid id,
            [FromBody] DatasetChartRequest request,
            IDatasetRowQueryService rowService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var result = await rowService
                .GetChartAsync(id, request, ct, ResolveAuthUserId(httpContext))
                .ConfigureAwait(false);
            return result.Outcome switch
            {
                DatasetChartOutcome.Success => Results.Ok(result.Data),
                DatasetChartOutcome.NotFound => Results.NotFound(),
                DatasetChartOutcome.InvalidRequest => DatasetValidationFailed(result.ErrorDetail),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
            };
        })
             .WithSummary("Aggregated chart data (GROUP BY category) for a DatasetComponent chart view.")
             .Produces<DatasetChartData>(StatusCodes.Status200OK)
             .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
             .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity);

        // Story 9.1 (FR-63 / AR-62) — allowlisted tables + columns; requires dataset-management.
        // Registered BEFORE /preview and MapPost("/") to avoid any static-segment-vs-guid
        // ambiguity (the /{id:guid} routes above only match valid GUIDs, so this is
        // defense-in-depth — see story §3).
        group.MapGet("/catalog", async (
            IDatasetAllowlist allowlist,
            CancellationToken ct) =>
        {
            var catalog = await allowlist.GetCatalogAsync(ct).ConfigureAwait(false);
            return Results.Ok(catalog);
        })
             .WithSummary("List allowlisted tables — names + column counts (Story 9.1 — FR-63 / AR-62)")
             .RequireDatasetManagement();

        // One allowlisted table's columns, fetched lazily when a table is dragged onto the
        // canvas (keeps the catalog list small for large schemas). GetTableColumnsAsync
        // checks the allowlist first, so a non-allowed/system/unknown table → 404 (never
        // leaks internal-table columns). Registered after /catalog and before /{id:guid}.
        group.MapGet("/catalog/{table}", async (
            string table,
            IDatasetAllowlist allowlist,
            CancellationToken ct) =>
        {
            var columns = await allowlist.GetTableColumnsAsync(table, ct).ConfigureAwait(false);
            return columns is null ? Results.NotFound() : Results.Ok(columns);
        })
             .WithSummary("Get one allowlisted table's columns (lazy load — FR-63 / AR-62)")
             .RequireDatasetManagement();

        // Write endpoints — require dataset-management (AC-2). Register /preview
        // BEFORE / to avoid any route-shadowing risk (Story 8.2 review deferral).
        group.MapPost("/preview", async (
            [FromBody] PreviewRequest request,
            IPreviewService previewService,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var result = await previewService.ExecuteAsync(request, ct).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case PreviewOutcome.Success:
                    return Results.Ok(result.Data);
                case PreviewOutcome.Timeout:
                    return Results.Problem(
                        detail: result.ErrorMessage,
                        title: "Preview timeout",
                        statusCode: StatusCodes.Status408RequestTimeout,
                        extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["code"] = "PREVIEW_TIMEOUT",
                            ["messageKey"] = "datasets.previewTimeout",
                        });
                case PreviewOutcome.BuilderStateInvalid:
                    return Results.Problem(
                        detail: result.ErrorMessage,
                        title: "Builder state invalid",
                        statusCode: StatusCodes.Status422UnprocessableEntity,
                        extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["code"] = "BUILDER_STATE_INVALID",
                            ["messageKey"] = "datasets.builderStateInvalid",
                        });
                case PreviewOutcome.MissingParameters:
                    return Results.Problem(
                        detail: result.ErrorMessage,
                        title: "Parameters required",
                        statusCode: StatusCodes.Status422UnprocessableEntity,
                        extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["code"] = "DATASET_PARAMETERS_REQUIRED",
                            ["messageKey"] = "datasets.parametersRequired",
                            ["missingParameters"] = result.MissingParameters,
                        });
                case PreviewOutcome.SqlError:
                    return Results.Problem(
                        detail: result.ErrorMessage,
                        title: "SQL error",
                        statusCode: StatusCodes.Status422UnprocessableEntity,
                        extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["code"] = "DATASET_QUERY_INVALID",
                            ["messageKey"] = "datasets.invalidQuery",
                        });
                default:
                {
                    var previewLogger = loggerFactory.CreateLogger(nameof(DatasetEndpoints));
                    DatasetEndpointsLog.UnhandledPreviewOutcome(previewLogger, result.Outcome);
                    return Results.StatusCode(StatusCodes.Status500InternalServerError);
                }
            }
        })
             .WithSummary("Preview dataset query — LIMIT 10 (Story 11.3 — FR-72 / AR-63)")
             .RequireDatasetManagement();

        // Story 8.4 (FR-58): create the custom_dataset row and the backing VIEW
        // atomically. dataset_name is validated inline (NOT via the FluentValidation
        // filter — see §7) so the 422 body carries a root `code: "INVALID_DATASET_NAME"`,
        // the same Results.Problem pattern as DesignerEndpoints' IDENTIFIER_INVALID.
        group.MapPost("/", async (
            [FromBody] CreateDatasetRequest request,
            IDatasetService datasetService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (!DatasetName.TryCreate(request.DatasetName, out var name, out _, out var nameError))
            {
                return InvalidDatasetName(nameError);
            }

            if (!Guid.TryParse(httpContext.User.FindFirst("userId")?.Value, out var actorId)
                || actorId == Guid.Empty)
            {
                return Results.Unauthorized();
            }

            var correlationId = httpContext.GetCorrelationId();
            var result = await datasetService
                .CreateAsync(request, name!, actorId, correlationId, ct)
                .ConfigureAwait(false);

            return result.Outcome switch
            {
                CreateDatasetOutcome.Success => Results.Created(
                    $"/api/datasets/{result.Dataset!.Id}", result.Dataset),
                CreateDatasetOutcome.NameConflict => Results.Problem(
                    detail: $"A dataset named '{request.DatasetName}' already exists.",
                    title: "Dataset name conflict",
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["code"] = "DATASET_NAME_CONFLICT",
                        ["messageKey"] = "datasets.nameConflict",
                    }),
                CreateDatasetOutcome.InvalidQuery => Results.Problem(
                    detail: result.ErrorDetail,
                    title: "Invalid query",
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["code"] = "INVALID_QUERY",
                        ["messageKey"] = "datasets.invalidQuery",
                    }),
                // Story 11.2 (FR-71 / AR-66) — server-side SQL generation from a builder_state
                // blob supplied at create time failed pre-flight/allowlist/identifier/expression
                // validation. 422 before any DDL (mirrors the PUT BUILDER_STATE_INVALID case).
                CreateDatasetOutcome.BuilderStateInvalid => Results.Problem(
                    detail: result.ErrorDetail,
                    title: "Builder state invalid",
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["code"] = "BUILDER_STATE_INVALID",
                        ["messageKey"] = "datasets.builderStateInvalid",
                    }),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
            };
        })
             .WithSummary("Create dataset (Story 8.4 — FR-58)")
             .RequireDatasetManagement();

        // Story 8.5 (FR-58 / FR-59): update the row + backing VIEW atomically with
        // optimistic concurrency. dataset_name is validated inline only when present
        // (null/omitted means "keep existing"); same Results.Problem envelope pattern
        // as POST. NOT wired to the FluentValidation filter (see §4 / Story 8.4 §7).
        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateDatasetRequest request,
            IDatasetService datasetService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            // Validate dataset_name only if it was provided in the request body.
            DatasetName? newName = null;
            if (request.DatasetName is not null
                && !DatasetName.TryCreate(request.DatasetName, out newName, out _, out var nameError))
            {
                return InvalidDatasetName(nameError);
            }

            if (!Guid.TryParse(httpContext.User.FindFirst("userId")?.Value, out var actorId)
                || actorId == Guid.Empty)
            {
                return Results.Unauthorized();
            }

            var correlationId = httpContext.GetCorrelationId();
            var result = await datasetService
                .UpdateAsync(id, request, newName, actorId, correlationId, ct)
                .ConfigureAwait(false);

            return result.Outcome switch
            {
                UpdateDatasetOutcome.Success => Results.Ok(result.Dataset),
                UpdateDatasetOutcome.NotFound => Results.NotFound(),
                UpdateDatasetOutcome.ConcurrencyConflict => Results.Problem(
                    detail: $"Version mismatch. Current version is {result.CurrentVersion}.",
                    title: "Dataset concurrency conflict",
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["code"] = "DATASET_CONCURRENCY_CONFLICT",
                        ["messageKey"] = "datasets.concurrencyConflict",
                        ["currentVersion"] = result.CurrentVersion,
                    }),
                UpdateDatasetOutcome.NameConflict => Results.Problem(
                    detail: $"A dataset named '{request.DatasetName}' already exists.",
                    title: "Dataset name conflict",
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["code"] = "DATASET_NAME_CONFLICT",
                        ["messageKey"] = "datasets.nameConflict",
                    }),
                UpdateDatasetOutcome.InvalidQuery => Results.Problem(
                    detail: result.ErrorDetail,
                    title: "Invalid query",
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["code"] = "INVALID_QUERY",
                        ["messageKey"] = "datasets.invalidQuery",
                    }),
                // Story 11.1 (FR-70 / AR-65) — server-side SQL generation from builder_state
                // failed pre-flight/allowlist/identifier/expression validation. 422 before DDL.
                UpdateDatasetOutcome.BuilderStateInvalid => Results.Problem(
                    detail: result.ErrorDetail,
                    title: "Builder state invalid",
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["code"] = "BUILDER_STATE_INVALID",
                        ["messageKey"] = "datasets.builderStateInvalid",
                    }),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
            };
        })
             .WithSummary("Update dataset (Story 8.5 — FR-58 / FR-59)")
             .RequireDatasetManagement();

        // Story 8.6 (FR-58 / AR-59): delete the custom_dataset row and DROP the backing
        // VIEW atomically. No request body, so no FluentValidation/DatasetName validation
        // (§9). Success → 204, missing id → 404, catastrophic DROP failure → 500.
        group.MapDelete("/{id:guid}", async (
            Guid id,
            IDatasetService datasetService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(httpContext.User.FindFirst("userId")?.Value, out var actorId)
                || actorId == Guid.Empty)
            {
                return Results.Unauthorized();
            }

            var correlationId = httpContext.GetCorrelationId();
            var result = await datasetService
                .DeleteAsync(id, actorId, correlationId, ct)
                .ConfigureAwait(false);

            return result.Outcome switch
            {
                DeleteDatasetOutcome.Success => Results.NoContent(),
                DeleteDatasetOutcome.NotFound => Results.NotFound(),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
            };
        })
             .WithSummary("Delete dataset (Story 8.6 — FR-58)")
             .RequireDatasetManagement();

        return group;
    }

    // The authenticated user's id from the JWT, or null when absent/unparseable. Used as
    // the VALUE for a DatasetComponent auth filter so a client can never spoof whose rows
    // it sees (the column comes from the request, the identity always from the token).
    private static Guid? ResolveAuthUserId(HttpContext httpContext) =>
        Guid.TryParse(httpContext.User.FindFirst("userId")?.Value, out var id) ? id : null;

    // Parses cascading "Depends on" filters (filter[col]=value) from the raw query
    // string into target-column → value pairs. Column names are NOT validated here —
    // the dropdown service checks each against the dataset VIEW's discovered columns
    // (returning a 422 on mismatch) before any identifier is interpolated. An empty
    // value is dropped (a not-yet-set parent never narrows the list to nothing).
    private static Dictionary<string, string> ParseCascadingFilters(IQueryCollection query)
    {
        var filters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, values) in query)
        {
            if (!key.StartsWith("filter[", StringComparison.Ordinal) ||
                !key.EndsWith(']'))
                continue;
            var column = key["filter[".Length..^1];
            if (column.Length == 0) continue;
            var value = values.Count > 0 ? values[^1] : null;
            if (string.IsNullOrEmpty(value)) continue;
            filters[column] = value;
        }
        return filters;
    }

    private static IResult InvalidDatasetName(string? detail) =>
        Results.Problem(
            detail: detail,
            title: "Invalid dataset name",
            statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "INVALID_DATASET_NAME",
                ["messageKey"] = "datasets.invalidDatasetName",
            });

    // 422 envelope for DatasetComponent rows/export validation failures (unknown column,
    // bad operator/sort, missing/over-limit export) — mirrors the DynamicCrud VALIDATION_FAILED shape.
    private static IResult DatasetValidationFailed(string? detail) =>
        Results.Problem(
            detail: detail ?? "Validation failed.",
            title: "Validation failed",
            statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "VALIDATION_FAILED",
                ["messageKey"] = "errors.validationFailed",
            });

    // The same three writers the record-list export uses (Features/DynamicCrud/Export).
    private static readonly FrozenDictionary<string, Func<IRecordExportWriter>> ExportWriterFactories =
        new Dictionary<string, Func<IRecordExportWriter>>(StringComparer.OrdinalIgnoreCase)
        {
            ["csv"] = () => new CsvExportWriter(),
            ["xlsx"] = () => new XlsxExportWriter(),
            ["pdf"] = () => new PdfExportWriter(),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // Strip characters illegal in a Content-Disposition filename (mirrors the record-export helper).
    private static string SanitizeFileNameSegment(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "dataset";
        var buf = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|') continue;
            buf.Append(char.IsWhiteSpace(ch) ? '_' : ch);
        }
        var s = buf.ToString().Trim('_', '.');
        return s.Length == 0 ? "dataset" : s;
    }
}

internal static partial class DatasetEndpointsLog
{
    [LoggerMessage(Level = LogLevel.Error,
        Message = "Unhandled PreviewOutcome {Outcome} — no switch arm matched; returning 500")]
    public static partial void UnhandledPreviewOutcome(ILogger logger, PreviewOutcome outcome);
}
