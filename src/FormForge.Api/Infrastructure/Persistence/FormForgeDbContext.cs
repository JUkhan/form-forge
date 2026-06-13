using FormForge.Api.Domain.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FormForge.Api.Infrastructure.Persistence;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Instantiated by EF Core DI registration.")]
internal sealed class FormForgeDbContext(DbContextOptions<FormForgeDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<MfaBackupCode> MfaBackupCodes => Set<MfaBackupCode>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<ComponentSchema> ComponentSchemas => Set<ComponentSchema>();
    public DbSet<ComponentSchemaVersion> ComponentSchemaVersions => Set<ComponentSchemaVersion>();
    public DbSet<Menu> Menus => Set<Menu>();
    public DbSet<MenuRoleAssignment> MenuRoleAssignments => Set<MenuRoleAssignment>();
    public DbSet<SchemaAuditLogEntry> SchemaAuditLog => Set<SchemaAuditLogEntry>();
    public DbSet<MutationAuditLogEntry> MutationAuditLog => Set<MutationAuditLogEntry>();
    public DbSet<CustomDataset> CustomDatasets => Set<CustomDataset>();
    public DbSet<DatasetAuditLogEntry> DatasetAuditLog => Set<DatasetAuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(u => u.Email).HasColumnName("email").IsRequired().HasMaxLength(320);
            e.Property(u => u.DisplayName).HasColumnName("display_name").IsRequired().HasMaxLength(200);
            e.Property(u => u.PasswordHash).HasColumnName("password_hash").IsRequired();
            e.Property(u => u.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            e.Property(u => u.ThemePreference).HasColumnName("theme_preference").HasMaxLength(20);
            e.Property(u => u.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.Property(u => u.UpdatedAt).HasColumnName("updated_at");
            // Story 2.13 — TOTP MFA enrolment columns.
            e.Property(u => u.MfaEnabled).HasColumnName("mfa_enabled").HasDefaultValue(false);
            e.Property(u => u.MfaSecretProtected).HasColumnName("mfa_secret_protected");
            e.ToTable(t => t.HasCheckConstraint(
                "ck_users_theme_preference",
                "theme_preference IS NULL OR theme_preference IN ('default-light', 'slate-dark', 'solarized')"));
            e.HasIndex(u => u.Email).IsUnique().HasDatabaseName("uq_users_email");
        });

        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(r => r.UserId).HasColumnName("user_id").IsRequired();
            e.Property(r => r.TokenHash).HasColumnName("token_hash").IsRequired();
            e.Property(r => r.ExpiresAt).HasColumnName("expires_at").IsRequired();
            e.Property(r => r.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.Property(r => r.RevokedAt).HasColumnName("revoked_at");
            e.HasIndex(r => r.UserId).HasDatabaseName("idx_refresh_tokens_user_id");
            e.HasIndex(r => r.TokenHash).IsUnique().HasDatabaseName("uq_refresh_tokens_token_hash");
            e.HasOne(r => r.User)
             .WithMany(u => u.RefreshTokens)
             .HasForeignKey(r => r.UserId)
             .HasConstraintName("fk_refresh_tokens_users")
             .OnDelete(DeleteBehavior.Cascade);
        });

        // Story 2.11 (AR-54) — single-use password-reset tokens. Mirrors the
        // RefreshToken mapping: explicit snake_case columns, unique index on the
        // hash, a user_id lookup index, and cascade delete so removing a user
        // cleans up outstanding reset tokens. No nav collection on User (the FK is
        // configured WithMany() with no inverse).
        modelBuilder.Entity<PasswordResetToken>(e =>
        {
            e.ToTable("password_reset_tokens");
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(t => t.UserId).HasColumnName("user_id").IsRequired();
            e.Property(t => t.TokenHash).HasColumnName("token_hash").IsRequired().HasMaxLength(64);
            e.Property(t => t.ExpiresAt).HasColumnName("expires_at").IsRequired();
            e.Property(t => t.UsedAt).HasColumnName("used_at").IsRequired(false);
            e.Property(t => t.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.HasIndex(t => t.TokenHash).IsUnique().HasDatabaseName("uq_password_reset_tokens_token_hash");
            e.HasIndex(t => t.UserId).HasDatabaseName("idx_password_reset_tokens_user_id");
            e.HasOne(t => t.User)
             .WithMany()
             .HasForeignKey(t => t.UserId)
             .HasConstraintName("fk_password_reset_tokens_users")
             .OnDelete(DeleteBehavior.Cascade);
        });

        // Story 2.13 — TOTP MFA backup codes. One row per single-use code; cascade
        // delete with the owning user. CodeHash stores a bcrypt hash (treated like a
        // password), never the raw code. used_at is stamped when a code is consumed
        // at login (Story 2.14); enrolment only inserts unused rows.
        modelBuilder.Entity<MfaBackupCode>(e =>
        {
            e.ToTable("mfa_backup_codes");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(c => c.UserId).HasColumnName("user_id").IsRequired();
            e.Property(c => c.CodeHash).HasColumnName("code_hash").IsRequired();
            e.Property(c => c.UsedAt).HasColumnName("used_at");
            e.Property(c => c.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.HasIndex(c => c.UserId).HasDatabaseName("idx_mfa_backup_codes_user_id");
            e.HasOne(c => c.User)
             .WithMany(u => u.BackupCodes)
             .HasForeignKey(c => c.UserId)
             .HasConstraintName("fk_mfa_backup_codes_users")
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Role>(e =>
        {
            e.ToTable("roles");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(r => r.Name).HasColumnName("name").IsRequired().HasMaxLength(100);
            e.Property(r => r.Description).HasColumnName("description").HasMaxLength(500);
            e.Property(r => r.IsSystem).HasColumnName("is_system").HasDefaultValue(false);
            // Story 8.1 (AR-58) — Dataset Manager management permission. Column default
            // false; the platform-admin seed row is flipped to true in the migration.
            e.Property(r => r.CanManageDatasets).HasColumnName("can_manage_datasets").HasDefaultValue(false);
            e.Property(r => r.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.Property(r => r.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(r => r.Name).IsUnique().HasDatabaseName("uq_roles_name");
        });

        modelBuilder.Entity<RolePermission>(e =>
        {
            e.ToTable("role_permissions");
            e.HasKey(rp => rp.Id);
            e.Property(rp => rp.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(rp => rp.RoleId).HasColumnName("role_id").IsRequired();
            e.Property(rp => rp.ResourceId).HasColumnName("resource_id").IsRequired().HasMaxLength(63);
            e.Property(rp => rp.CanCreate).HasColumnName("can_create").HasDefaultValue(false);
            e.Property(rp => rp.CanRead).HasColumnName("can_read").HasDefaultValue(false);
            e.Property(rp => rp.CanUpdate).HasColumnName("can_update").HasDefaultValue(false);
            e.Property(rp => rp.CanDelete).HasColumnName("can_delete").HasDefaultValue(false);
            e.Property(rp => rp.CanExport).HasColumnName("can_export").HasDefaultValue(false);
            e.HasIndex(rp => new { rp.RoleId, rp.ResourceId })
             .IsUnique()
             .HasDatabaseName("uq_role_permissions_role_resource");
            e.HasOne(rp => rp.Role)
             .WithMany(r => r.Permissions)
             .HasForeignKey(rp => rp.RoleId)
             .HasConstraintName("fk_role_permissions_roles")
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserRole>(e =>
        {
            e.ToTable("user_roles");
            e.HasKey(ur => new { ur.UserId, ur.RoleId });
            e.Property(ur => ur.UserId).HasColumnName("user_id");
            e.Property(ur => ur.RoleId).HasColumnName("role_id");
            e.Property(ur => ur.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.HasOne(ur => ur.User)
             .WithMany(u => u.UserRoles)
             .HasForeignKey(ur => ur.UserId)
             .HasConstraintName("fk_user_roles_users")
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(ur => ur.Role)
             .WithMany(r => r.UserRoles)
             .HasForeignKey(ur => ur.RoleId)
             .HasConstraintName("fk_user_roles_roles")
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(ur => ur.RoleId).HasDatabaseName("idx_user_roles_role_id");
        });

        modelBuilder.Entity<ComponentSchema>(e =>
        {
            e.ToTable("component_schemas");
            e.HasKey(s => s.DesignerId);
            e.Property(s => s.DesignerId).HasColumnName("designer_id").IsRequired().HasMaxLength(63);
            e.Property(s => s.DisplayName).HasColumnName("display_name").IsRequired().HasMaxLength(200);
            e.Property(s => s.Mode).HasColumnName("mode").IsRequired().HasMaxLength(5).HasDefaultValue("CRUD");
            e.Property(s => s.CreatedBy).HasColumnName("created_by");
            e.Property(s => s.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.Property(s => s.UpdatedAt).HasColumnName("updated_at");
            e.HasMany(s => s.Versions)
             .WithOne(v => v.Schema)
             .HasForeignKey(v => v.DesignerId)
             .HasConstraintName("fk_component_schema_versions_schema")
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.Creator)
             .WithMany()
             .HasForeignKey(s => s.CreatedBy)
             .HasConstraintName("fk_component_schemas_users")
             .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ComponentSchemaVersion>(e =>
        {
            e.ToTable("component_schema_versions");
            e.HasKey(v => v.Id);
            e.Property(v => v.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(v => v.DesignerId).HasColumnName("designer_id").IsRequired().HasMaxLength(63);
            e.Property(v => v.Version).HasColumnName("version").IsRequired();
            e.Property(v => v.Status).HasColumnName("status").IsRequired().HasDefaultValue("Draft").HasMaxLength(20);
            e.Property(v => v.RootElement).HasColumnName("root_element").HasColumnType("jsonb");
            e.Property(v => v.AuthFilterFieldKey).HasColumnName("auth_filter_field_key").HasMaxLength(63);
            e.Property(v => v.CreatedBy).HasColumnName("created_by");
            e.Property(v => v.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.Property(v => v.PublishedAt).HasColumnName("published_at");
            e.HasIndex(v => new { v.DesignerId, v.Version })
             .IsUnique()
             .HasDatabaseName("uq_component_schema_versions_designer_version");
            // General non-unique lookup index on designer_id. (The former
            // at-most-one-Published filtered unique index was dropped — a designer
            // may now have multiple Published versions at once.)
            e.HasIndex(v => v.DesignerId, "idx_component_schema_versions_designer_id");
            e.HasOne(v => v.Creator)
             .WithMany()
             .HasForeignKey(v => v.CreatedBy)
             .HasConstraintName("fk_component_schema_versions_users")
             .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Menu>(e =>
        {
            e.ToTable("menus");
            e.HasKey(m => m.Id);
            e.Property(m => m.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(m => m.Name).HasColumnName("name").IsRequired().HasMaxLength(200);
            // Use "sort_order" to avoid PostgreSQL reserved keyword "order"
            e.Property(m => m.Order).HasColumnName("sort_order").HasDefaultValue(0);
            e.Property(m => m.Icon).HasColumnName("icon").HasColumnType("jsonb");
            e.Property(m => m.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            e.Property(m => m.ParentId).HasColumnName("parent_id");
            e.Property(m => m.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.Property(m => m.UpdatedAt).HasColumnName("updated_at");
            // Story 5.2 — Designer binding columns. designer_id is also the FK to
            // component_schemas.designer_id; the other three are unconstrained scalars.
            e.Property(m => m.DesignerId).HasColumnName("designer_id").HasMaxLength(63);
            e.Property(m => m.BoundVersion).HasColumnName("bound_version");
            e.Property(m => m.ProvisioningStatus).HasColumnName("provisioning_status").HasMaxLength(20);
            e.Property(m => m.ProvisioningError).HasColumnName("provisioning_error");
            // Custom route path — alternative target, mutually exclusive with the binding.
            e.Property(m => m.RoutePath).HasColumnName("route_path").HasMaxLength(512);
            e.HasOne(m => m.Parent)
             .WithMany(m => m.Children)
             .HasForeignKey(m => m.ParentId)
             .HasConstraintName("fk_menus_parent")
             .OnDelete(DeleteBehavior.Restrict);   // admin must remove children first (AC-2)
            // Story 5.2 — optional string→string FK to ComponentSchema.DesignerId (the PK).
            // SetNull on delete: removing a Designer clears the binding but keeps the menu
            // row (it becomes an unbound section header). The provisioned PostgreSQL table
            // is not dropped — schema lifecycle is decoupled from the binding metadata.
            e.HasOne(m => m.BoundDesigner)
             .WithMany()
             .HasForeignKey(m => m.DesignerId)
             .HasConstraintName("fk_menus_bound_designer")
             .OnDelete(DeleteBehavior.SetNull)
             .IsRequired(false);
            e.HasIndex(m => m.ParentId).HasDatabaseName("idx_menus_parent_id");
            e.HasIndex(m => m.Order).HasDatabaseName("idx_menus_sort_order");
            // Story 5.2 — non-unique lookup index for binding-based queries.
            e.HasIndex(m => m.DesignerId).HasDatabaseName("idx_menus_designer_id");
            // Story 5.2 — filtered index for Story 5.8's ProvisioningRecoveryService startup
            // scan ("WHERE provisioning_status = 'Pending'"). Created now so 5.8 can use it.
            e.HasIndex(m => m.ProvisioningStatus)
             .HasDatabaseName("idx_menus_provisioning_status_pending")
             .HasFilter("(provisioning_status = 'Pending')");
        });

        modelBuilder.Entity<MenuRoleAssignment>(e =>
        {
            e.ToTable("menu_role_assignments");
            e.HasKey(mra => new { mra.MenuId, mra.RoleId });
            e.Property(mra => mra.MenuId).HasColumnName("menu_id");
            e.Property(mra => mra.RoleId).HasColumnName("role_id");
            e.Property(mra => mra.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.HasOne(mra => mra.Menu)
             .WithMany(m => m.RoleAssignments)
             .HasForeignKey(mra => mra.MenuId)
             .HasConstraintName("fk_menu_role_assignments_menus")
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(mra => mra.Role)
             .WithMany()
             .HasForeignKey(mra => mra.RoleId)
             .HasConstraintName("fk_menu_role_assignments_roles")
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(mra => mra.RoleId).HasDatabaseName("idx_menu_role_assignments_role_id");
        });

        // Story 5.3 — append-only audit table for DDL operations on dynamic tables.
        // EF Core + Npgsql handles string[] ↔ text[] transparently; HasColumnType
        // is the only required mapping. Two indexes per Decision 1.5: a composite
        // (designer_id, created_at) for per-designer history queries and a single-
        // column correlation_id index for cross-row lookups by ULID.
        modelBuilder.Entity<SchemaAuditLogEntry>(e =>
        {
            e.ToTable("schema_audit_log");
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(a => a.DesignerId).HasColumnName("designer_id").IsRequired().HasMaxLength(63);
            e.Property(a => a.FromVersion).HasColumnName("from_version");
            e.Property(a => a.ToVersion).HasColumnName("to_version").IsRequired();
            e.Property(a => a.DdlOperation).HasColumnName("ddl_operation").IsRequired().HasMaxLength(10);
            e.Property(a => a.ColumnsAdded).HasColumnName("columns_added")
             .HasColumnType("text[]");
            // Story 5.6 — column names removed by a DROP. Same text[] mapping as ColumnsAdded.
            e.Property(a => a.ColumnsDropped).HasColumnName("columns_dropped")
             .HasColumnType("text[]");
            // Story 5.4 — JSON snapshot of the ALTER diff (existingUserColumns, addedColumns,
            // orphanedColumns). EF maps string? to TEXT NULL on PostgreSQL by default, which
            // is what we want — no HasColumnType needed.
            e.Property(a => a.ColumnsDiff).HasColumnName("column_diff");
            e.Property(a => a.CorrelationId).HasColumnName("correlation_id").HasMaxLength(26);
            e.Property(a => a.ActorId).HasColumnName("actor_id");
            e.Property(a => a.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            // Story 5.7 — free-text annotation; reserved for future manual entries.
            e.Property(a => a.Notes).HasColumnName("notes");
            // Story 5.7 — composite index uses DESCENDING created_at so the standard
            // newest-first query (ORDER BY created_at DESC) walks the index in forward
            // order. Replaces the ASC/ASC variant from Story 5.3 (AC-3).
            e.HasIndex(a => new { a.DesignerId, a.CreatedAt })
             .HasDatabaseName("idx_schema_audit_log_designer_id_created_at_desc")
             .IsDescending(false, true);
            e.HasIndex(a => a.CorrelationId)
             .HasDatabaseName("idx_schema_audit_log_correlation_id");
        });

        // Story 6.3 — append-only mutation audit table for dynamic-CRUD operations
        // on provisioned tables. Three indexes per Decision 1.5: composite
        // (designer_id, timestamp DESC) for per-designer history, composite
        // (record_id, timestamp DESC) for per-record history, and a single-column
        // correlation_id index for cross-row lookups by ULID.
        modelBuilder.Entity<MutationAuditLogEntry>(e =>
        {
            e.ToTable("mutation_audit_log");
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(a => a.DesignerId).HasColumnName("designer_id").IsRequired().HasMaxLength(63);
            e.Property(a => a.RecordId).HasColumnName("record_id").IsRequired();
            e.Property(a => a.Operation).HasColumnName("operation").IsRequired().HasMaxLength(20);
            e.Property(a => a.ActorId).HasColumnName("actor_id");
            e.Property(a => a.Timestamp).HasColumnName("timestamp").IsRequired();
            e.Property(a => a.NewValues).HasColumnName("new_values").HasColumnType("jsonb");
            e.Property(a => a.PreviousValues).HasColumnName("previous_values").HasColumnType("jsonb");
            e.Property(a => a.CorrelationId).HasColumnName("correlation_id").HasMaxLength(26);
            e.HasIndex(a => new { a.DesignerId, a.Timestamp })
             .HasDatabaseName("idx_mutation_audit_log_designer_id_timestamp_desc")
             .IsDescending(false, true);
            e.HasIndex(a => new { a.RecordId, a.Timestamp })
             .HasDatabaseName("idx_mutation_audit_log_record_id_timestamp_desc")
             .IsDescending(false, true);
            e.HasIndex(a => a.CorrelationId)
             .HasDatabaseName("idx_mutation_audit_log_correlation_id");
        });

        // Story 8.1 (FR-55 / AR-57) — Dataset definition store. dataset_name is TEXT
        // with a UNIQUE index (second line of defence behind name validation). query
        // and builder_state are mutually-populated by custom vs builder modes. The
        // created_by/updated_by FKs to users SetNull on delete (mirrors ComponentSchema).
        modelBuilder.Entity<CustomDataset>(e =>
        {
            e.ToTable("custom_dataset");
            e.HasKey(d => d.Id);
            e.Property(d => d.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(d => d.DatasetName).HasColumnName("dataset_name").HasColumnType("text").IsRequired();
            e.Property(d => d.IsCustomQuery).HasColumnName("is_custom_query").HasDefaultValue(true);
            e.Property(d => d.Query).HasColumnName("query").HasColumnType("text");
            e.Property(d => d.BuilderState).HasColumnName("builder_state").HasColumnType("jsonb");
            e.Property(d => d.Version).HasColumnName("version").HasDefaultValue(1);
            e.Property(d => d.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.Property(d => d.CreatedBy).HasColumnName("created_by");
            e.Property(d => d.UpdatedAt).HasColumnName("updated_at");
            e.Property(d => d.UpdatedBy).HasColumnName("updated_by");
            e.HasIndex(d => d.DatasetName)
             .IsUnique()
             .HasDatabaseName("idx_custom_dataset_dataset_name");
            e.HasOne(d => d.CreatedByUser)
             .WithMany()
             .HasForeignKey(d => d.CreatedBy)
             .HasConstraintName("fk_custom_dataset_users_created_by")
             .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(d => d.UpdatedByUser)
             .WithMany()
             .HasForeignKey(d => d.UpdatedBy)
             .HasConstraintName("fk_custom_dataset_users_updated_by")
             .OnDelete(DeleteBehavior.SetNull);
        });

        // Story 8.1 (FR-55 / AR-57) — append-only Dataset audit log. operation is
        // guarded by a DB CHECK. actor_id has a DB FK to users(id) but no navigation
        // property (append-only; nullable for system operations). Indexes per AR-57:
        // a composite (dataset_name, timestamp DESC) for per-dataset history and a
        // single-column operation index (mirrors the mutation_audit_log shape).
        modelBuilder.Entity<DatasetAuditLogEntry>(e =>
        {
            e.ToTable("dataset_audit_log", t => t.HasCheckConstraint(
                "ck_dataset_audit_log_operation",
                "operation IN ('CREATE', 'UPDATE', 'DELETE')"));
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(a => a.Timestamp).HasColumnName("timestamp").HasDefaultValueSql("now()");
            e.Property(a => a.ActorId).HasColumnName("actor_id");
            e.Property(a => a.ActorName).HasColumnName("actor_name").HasColumnType("text");
            e.Property(a => a.DatasetName).HasColumnName("dataset_name").HasColumnType("text").IsRequired();
            e.Property(a => a.Operation).HasColumnName("operation").HasColumnType("text").IsRequired();
            e.Property(a => a.PreviousValues).HasColumnName("previous_values").HasColumnType("jsonb");
            e.Property(a => a.NewValues).HasColumnName("new_values").HasColumnType("jsonb");
            e.Property(a => a.Ddl).HasColumnName("ddl").HasColumnType("text");
            e.Property(a => a.Succeeded).HasColumnName("succeeded").HasDefaultValue(true);
            e.Property(a => a.CorrelationId).HasColumnName("correlation_id").HasColumnType("text");
            e.HasIndex(a => new { a.DatasetName, a.Timestamp })
             .HasDatabaseName("idx_dataset_audit_log_dataset_name_timestamp")
             .IsDescending(false, true);
            e.HasIndex(a => a.Operation)
             .HasDatabaseName("idx_dataset_audit_log_operation");
            // DB-level FK to users(id) with no navigation property (append-only log).
            e.HasOne<User>()
             .WithMany()
             .HasForeignKey(a => a.ActorId)
             .HasConstraintName("fk_dataset_audit_log_users_actor_id")
             .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
