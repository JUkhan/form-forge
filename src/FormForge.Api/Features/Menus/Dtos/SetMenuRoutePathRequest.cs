namespace FormForge.Api.Features.Menus.Dtos;

// PUT /api/admin/menus/{id}/route-path payload. RoutePath is nullable: a null or
// empty value clears the custom route (the menu reverts to an unbound section
// header), while a non-null value sets the route and clears any Designer binding.
internal sealed record SetMenuRoutePathRequest(string? RoutePath);
