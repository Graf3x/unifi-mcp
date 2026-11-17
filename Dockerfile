# syntax=docker/dockerfile:1
# The published image (ghcr.io/<owner>/unifi-mcp) and `docker compose build`. Build context: the repository root.
# deploy/Dockerfile is the owner's Fleet build (a checksummed ./build folder) and is separate from this one.
#
# Multi-arch without emulation: the SDK stage runs on the build machine and cross-publishes for TARGETARCH
# (amd64 or arm64); the runtime stage only copies files.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
ARG TARGETARCH
WORKDIR /src
COPY src/UnifiMcp/UnifiMcp.csproj src/UnifiMcp/
RUN dotnet restore src/UnifiMcp/UnifiMcp.csproj -a "$TARGETARCH"
COPY openapi/ openapi/
COPY src/UnifiMcp/ src/UnifiMcp/
RUN dotnet publish src/UnifiMcp/UnifiMcp.csproj -c Release -a "$TARGETARCH" --self-contained false --no-restore -o /app \
 && chmod -R a+rX /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.9
ENV DOTNET_EnableDiagnostics=0
WORKDIR /app
COPY --from=build /app .
# The image's non-root user; docker-compose.yml overrides it with PUID:PGID so the bind mounts are writable.
USER $APP_UID
ENTRYPOINT ["dotnet", "unifi-mcp.dll"]
