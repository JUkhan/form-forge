namespace FormForge.Api.Common.Endpoints;

// Named authorization policies (registered in Program.cs). Tab-level Settings access is
// role-name based: platform-admin (tenant admin) vs the hidden platform-dev developer role.
internal static class AuthPolicies
{
    public const string PlatformAdmin = "platform-admin";
    public const string PlatformDev = "platform-dev";
    public const string PlatformAdminOrDev = "platform-admin-or-dev";
}
