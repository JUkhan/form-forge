namespace FormForge.Api.Infrastructure.EventBus;

internal interface IDomainEventBus
{
    void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class;
    void Publish<TEvent>(TEvent @event) where TEvent : class;
}

// Closed v1 domain event set. Co-located with IDomainEventBus so the full surface
// is visible at a glance. UserDeactivated has no publisher in 2.6 (Story 2.8
// adds DeactivateUserAsync); MenuBindingCreated has no subscriber (Story 4.1
// adds the handler). Declared now per AR-47 — no null-bus stubs.
internal sealed record UserRoleAssignmentChanged(Guid UserId);
internal sealed record RolePermissionsChanged(Guid RoleId);
internal sealed record UserDeactivated(Guid UserId);
internal sealed record UserReactivated(Guid UserId);
internal sealed record MenuBindingCreated(string DesignerId);
internal sealed record SchemaPublished(string DesignerId, int Version);
