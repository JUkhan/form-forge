using FluentValidation;

namespace FormForge.Api.Common.Endpoints.EndpointFilters;

// Layer 1 of two-layer validation (Decision 3.3): FluentValidation rules executed
// per request, before the handler. On failure, responds with 422 ValidationProblem.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via AddEndpointFilter<T>() on individual routes.")]
internal sealed class ValidationFilter<T>(IValidator<T> validator) : IEndpointFilter
    where T : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var argument = context.Arguments.OfType<T>().FirstOrDefault();
        if (argument is null)
        {
            // The request body failed to bind (empty body, wrong content-type, malformed
            // JSON). Without this guard the handler would run with a null argument and
            // throw ArgumentNullException, surfacing as a 500. Return a clean 400.
            return Results.Problem(
                detail: "The request body is missing, empty, or malformed.",
                title: "Invalid request body",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "errors.invalidRequestBody",
                });
        }

        var result = await validator.ValidateAsync(argument, context.HttpContext.RequestAborted).ConfigureAwait(false);
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

        return await next(context).ConfigureAwait(false);
    }
}
