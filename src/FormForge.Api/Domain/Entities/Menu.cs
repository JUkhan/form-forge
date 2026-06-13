namespace FormForge.Api.Domain.Entities;

internal sealed class Menu
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Order { get; set; }
    public string? Icon { get; set; }           // stored as jsonb; full type validation in Story 4.3
    public bool IsActive { get; set; } = true;
    public Guid? ParentId { get; set; }         // null = top-level; Story 4.2 enables sub-menus
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    // Story 5.2 — Designer binding. All four columns are nullable: a menu without a
    // binding is a section header (a tree node with no backing table). Provisioning
    // is async: BoundVersion is set synchronously inside the bind call, then the
    // BackgroundService flips ProvisioningStatus Pending → Success | Error.
    public string? DesignerId { get; set; }
    public int? BoundVersion { get; set; }
    public string? ProvisioningStatus { get; set; }  // "Pending" | "Success" | "Error" | null
    public string? ProvisioningError { get; set; }   // null unless ProvisioningStatus == "Error"

    // Alternative to a Designer binding: a literal route the menu item links to
    // (internal SPA path like "/audit" or external URL like "https://…"). Mutually
    // exclusive with DesignerId — setting one clears the other in MenuService. At
    // most one of (DesignerId, RoutePath) is non-null on any row.
    public string? RoutePath { get; set; }

    public Menu? Parent { get; set; }
    public ICollection<Menu> Children { get; set; } = [];
    public ICollection<MenuRoleAssignment> RoleAssignments { get; set; } = [];  // Story 4.4 populates
    // FK is string→string: ComponentSchema.DesignerId is the PK. Optional, OnDelete SetNull —
    // deleting a Designer clears the binding but keeps the menu (as a section header).
    public ComponentSchema? BoundDesigner { get; set; }
}
