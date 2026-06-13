var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    // pgAdmin sidecar — Aspire auto-injects a servers.json so the formforge
    // database is pre-registered (no manual "Add Server" step). Pinned host
    // port so the URL is stable across runs; pick something unlikely to clash.
    .WithPgAdmin(c => c.WithHostPort(5050));

var formforgeDb = postgres.AddDatabase("formforge");

var minio = builder.AddContainer("minio", "minio/minio", "RELEASE.2025-09-07T16-13-09Z")
    .WithArgs("server", "/data", "--console-address", ":9001")
    .WithEnvironment("MINIO_ROOT_USER", "minioadmin")
    .WithEnvironment("MINIO_ROOT_PASSWORD", "minioadmin")
    .WithVolume("minio-data", "/data")
    .WithHttpEndpoint(targetPort: 9000, port: 9000, name: "s3")
    .WithHttpEndpoint(targetPort: 9001, port: 9001, name: "console");

// Story 2.10 — Mailpit dev SMTP sink. SMTP on 1025; web UI at http://localhost:8025.
// No real mail server is needed for local development; the API points Smtp__Host
// at localhost:1025 (wired below). Ports pinned so the URLs are stable across runs.
var mailpit = builder.AddContainer("mailpit", "axllent/mailpit")
    .WithEndpoint(targetPort: 1025, port: 1025, name: "smtp")      // TCP — SMTP is not HTTP
    .WithHttpEndpoint(targetPort: 8025, port: 8025, name: "ui");

var api = builder.AddProject<Projects.FormForge_Api>("api")
    .WithReference(formforgeDb)
    // Story 11.3 dataset Preview reads a dedicated `formforge_preview` connection string
    // (a least-privileged read-only role) that only Compose configures. For local Aspire
    // dev — as the integration tests already do — point the preview pool at the same
    // (superuser) database connection so Preview works without provisioning the separate
    // role/password. Production (Compose) still uses the real least-privileged role.
    .WithEnvironment("ConnectionStrings__formforge_preview", formforgeDb.Resource.ConnectionStringExpression)
    .WithReference(minio.GetEndpoint("s3"))
    .WithEnvironment("MinIO__RootUser", "minioadmin")
    .WithEnvironment("MinIO__RootPassword", "minioadmin")
    .WithEnvironment("Smtp__Host", "localhost")
    .WithEnvironment("Smtp__Port", "1025")
    .WithEnvironment("Smtp__From", "noreply@formforge.local")
    // Outbound email links (welcome login link, password-reset link) must point at
    // the SPA on 5173 — NOT the API's own host:port. Without this the link base is
    // derived from the inbound request Host header, which in dev resolves to the API
    // port and produces an unreachable reset URL. Pinned to the same origin as the
    // CORS allow-list / Vite endpoint above.
    .WithEnvironment("Smtp__BaseUrl", "http://localhost:5173")
    .WaitFor(formforgeDb)
    .WaitFor(minio)
    .WaitFor(mailpit)
    .WithHttpHealthCheck("/health/live");

builder.AddViteApp(name: "web", workingDirectory: "../../web")
    .WithReference(api)
    .WaitFor(api)
    .WithNpmPackageInstallation()
    // Pin Vite to 5173 (both external and inside the Node process) so the URL
    // is stable across runs and matches the CORS allow-list in
    // appsettings.Development.json. isProxied:false keeps Aspire out of the
    // request path so Vite's HMR websocket connects directly without proxy
    // mangling.
    .WithHttpEndpoint(port: 5173, targetPort: 5173, env: "PORT", isProxied: false)
    .WithEnvironment("VITE_API_BASE_URL", api.GetEndpoint("http"));

builder.Build().Run();
