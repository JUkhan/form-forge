---
title: 'Dataset Manager preview resolves tables in the tenant schema'
type: 'bugfix'
created: '2026-09-21'
status: 'done'
route: 'oneshot'
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Dataset Manager Preview fails with `42P01 relation "public.message" does not exist` (Query Builder) and `relation "message" does not exist` (custom query). The builder SQL hardcodes `"public"."<table>"`, and the preview connection's search_path is only `{schema}_datasets, public`, so tenant tables in the tenant's own schema are never found. The `formforge_preview` role also has no USAGE on tenant schemas, which would hide them even if on the search_path.

**Approach:** Emit unqualified quoted table names from the SQL generator, put the tenant schema on the preview search_path, and grant `formforge_preview` USAGE on the tenant and tenant-datasets schemas at onboarding.

</frozen-after-approval>

## Implementation Notes

- `DatasetSqlGenerator`: dropped the `"public".` qualifier from FROM/JOIN; resolution now follows the connection search_path (`{schema}, public` for DbConnectionFactory, used by CREATE VIEW).
- `PreviewConnectionFactory`: search_path is `{tenantSchema}, {tenantSchema}_datasets, public` when a tenant is resolved.
- `TenantOnboardingService.BuildPreviewGrantSql`: adds `GRANT USAGE ON SCHEMA` for tenant + `_datasets` schemas.
- Existing tenants need the same USAGE grant applied once (applied to the local DB).
