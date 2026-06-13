namespace FormForge.Api.Features.Designer.Dtos;

// Body for PUT /api/designers/{designerId}/versions/{version}/auth-filter.
// A null / blank AuthFilterFieldKey clears the filter for the version; a non-blank
// value must be a user fieldKey declared on that version's schema.
internal sealed record SetAuthFilterFieldKeyRequest(string? AuthFilterFieldKey);
