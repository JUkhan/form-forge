using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace FormForge.Api.Common.OpenApi;

// Applied to /api/data/{designerId}/* endpoints in Epic 6.
// - Marks 2xx request/response bodies as object+additionalProperties:true
// - Stamps the designerId path parameter with the SQL-safe identifier pattern
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "Instantiated by ASP.NET Core OpenAPI pipeline via AddOperationTransformer<T>().")]
internal sealed class DynamicEndpointDocumentTransformer : IOpenApiOperationTransformer
{
    private const string DesignerIdPattern = @"^[a-z_][a-z0-9_]{0,62}$";
    private const string RequestBodyDescription =
        "Runtime-determined shape; fieldKeys and types defined by the bound Designer version schema.";
    private const string ResponseBodyDescription =
        "Runtime-determined shape. System columns (id, createdAt, updatedAt, etc.) are camelCase; user fieldKeys are verbatim (Option C hybrid, per AR-46).";

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        cancellationToken.ThrowIfCancellationRequested();

        var relativePath = context.Description.RelativePath;
        if (relativePath is null ||
            !relativePath.TrimStart('/').StartsWith("api/data/", StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        ApplyDesignerIdPattern(operation);
        ApplyDynamicRequestBodySchema(operation);
        ApplyDynamicResponseBodySchema(operation);

        return Task.CompletedTask;
    }

    private static void ApplyDesignerIdPattern(OpenApiOperation operation)
    {
        if (operation.Parameters is null)
        {
            return;
        }

        foreach (var parameter in operation.Parameters)
        {
            // OpenApiParameterReference is not handled here — ASP.NET Core
            // ApiExplorer emits inline parameters for path tokens, not refs.
            // If Epic 6 introduces parameter-level $refs, extend this branch
            // to resolve via context.Document.Components.Parameters.
            if (parameter is not OpenApiParameter mutable ||
                !string.Equals(mutable.Name, "designerId", StringComparison.OrdinalIgnoreCase) ||
                mutable.In != ParameterLocation.Path)
            {
                continue;
            }

            // Replace the schema with a fresh concrete instance so the pattern
            // is applied regardless of whether the original schema was inline,
            // an OpenApiSchemaReference, or null. Preserve type/description if
            // the original was a concrete OpenApiSchema (best-effort).
            var existing = mutable.Schema as OpenApiSchema;
            mutable.Schema = new OpenApiSchema
            {
                Type = existing?.Type ?? JsonSchemaType.String,
                Pattern = DesignerIdPattern,
                Description = existing?.Description,
                Example = existing?.Example,
            };
        }
    }

    private static void ApplyDynamicRequestBodySchema(OpenApiOperation operation)
    {
        if (operation.RequestBody?.Content is not { } requestContent)
        {
            return;
        }

        foreach (var content in requestContent.Values)
        {
            content.Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                AdditionalPropertiesAllowed = true,
                Description = RequestBodyDescription,
            };
        }
    }

    private static void ApplyDynamicResponseBodySchema(OpenApiOperation operation)
    {
        if (operation.Responses is null)
        {
            return;
        }

        foreach (var (statusCode, response) in operation.Responses)
        {
            // Only rewrite 2xx success-response bodies. ProblemDetails
            // (4xx/5xx) and default error responses keep their declared
            // schemas — overwriting them would lie about the error envelope.
            if (!IsSuccessStatusCode(statusCode) || response.Content is null)
            {
                continue;
            }

            foreach (var content in response.Content.Values)
            {
                content.Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    AdditionalPropertiesAllowed = true,
                    Description = ResponseBodyDescription,
                };
            }
        }
    }

    private static bool IsSuccessStatusCode(string statusCode) =>
        !string.IsNullOrEmpty(statusCode) && statusCode[0] == '2';
}
