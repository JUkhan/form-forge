# syntax=docker/dockerfile:1.7

# Stage 1 — .NET restore + publish (FormForge.Api only; AppHost requires the Aspire workload and is not needed in the image)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /src

# Copy solution manifests and csproj files first for layer-cache-efficient restore
COPY ["FormForge.sln", "global.json", ".editorconfig", "Directory.Build.props", "Directory.Packages.props", "./"]
COPY ["src/FormForge.Api/FormForge.Api.csproj", "src/FormForge.Api/"]
COPY ["src/FormForge.ServiceDefaults/FormForge.ServiceDefaults.csproj", "src/FormForge.ServiceDefaults/"]
# Restore only the API project graph (excludes AppHost which requires the Aspire workload)
RUN dotnet restore "src/FormForge.Api/FormForge.Api.csproj"

# Copy source and publish
COPY src/ ./src/
RUN dotnet publish "src/FormForge.Api/FormForge.Api.csproj" \
    --no-restore -c Release -o /app/publish

# Stage 2 — Vite build
FROM node:22-alpine AS web-build
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci --prefer-offline
COPY web/ .
RUN npm run build

# Stage 3 — runtime (Debian/glibc; non-root 'app' user ships built-in since .NET 8).
# NOT alpine: the Dataset SQL parser (pgsqlparser → libpg_query, used by
# SqlSelectEnforcer/PreviewService) ships a glibc-only native lib. On a musl/alpine
# image it fails with DllNotFoundException (no linux-musl-x64 build exists), which
#500s every dataset save/validate/preview. glibc is required.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# wget backs the container healthcheck; the slim Debian image does not bundle it.
RUN apt-get update \
    && apt-get install -y --no-install-recommends wget \
    && rm -rf /var/lib/apt/lists/*
COPY --from=dotnet-build /app/publish .
COPY --from=web-build /web/dist ./wwwroot/
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "FormForge.Api.dll"]
