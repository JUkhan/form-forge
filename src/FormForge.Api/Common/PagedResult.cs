namespace FormForge.Api.Common;

// Shared paged-response record per Decision 3.4. TotalPages is computed so the
// caller never has to derive it from Total/PageSize.
internal sealed record PagedResult<T>(
    IReadOnlyList<T> Data,
    long Total,
    int Page,
    int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling((double)Total / PageSize);
}
