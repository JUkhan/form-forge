namespace FormForge.Api.Features.Tenancy.Dtos;

internal sealed record TenantDto(
    Guid Id,
    string Name,
    string SchemaName,
    string Status,
    DateTimeOffset CreatedAt);
