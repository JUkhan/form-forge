using FluentValidation;
using FormForge.Api.Features.Menus.Dtos;

namespace FormForge.Api.Features.Menus.Validators;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI as IValidator<SetMenuRoutePathRequest>.")]
internal sealed class SetMenuRoutePathRequestValidator : AbstractValidator<SetMenuRoutePathRequest>
{
    // null/empty is allowed — it clears the custom route. A non-empty value must be
    // EITHER an internal SPA path (single leading slash, not protocol-relative "//")
    // OR an http(s) URL. This is the security gate: the navbar renders external paths
    // into an <a href>, so we must reject "javascript:"/"data:" and other schemes here
    // to prevent stored-XSS via a menu route.
    private const string InternalOrExternalPattern = @"^(/(?!/)[^\s]*|https?://[^\s]+)$";

    public SetMenuRoutePathRequestValidator()
    {
        RuleFor(x => x.RoutePath)
            .MaximumLength(512)
            .Matches(InternalOrExternalPattern)
            .WithMessage("routePath must be an internal path starting with '/' or an http(s):// URL")
            // FluentValidation's Matches treats null as a failure; only enforce the
            // pattern when a value is actually present so null/empty can clear the route.
            .When(x => !string.IsNullOrWhiteSpace(x.RoutePath));
    }
}
