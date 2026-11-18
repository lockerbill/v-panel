# syntax=docker/dockerfile:1

# ---- build ----
# The SDK stage runs natively on the build machine and cross-publishes for the target architecture
# (linux/amd64 and linux/arm64), which avoids slow emulated builds.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
# Restore first so most packages are cached until a csproj changes. Publish restores again once the
# sources are present: Razor-dependent implicit packages (e.g. the one providing blazor.web.js) are
# only resolved when .razor files exist, so do NOT publish with --no-restore.
COPY VPanel.sln ./
COPY src/VPanel.Core/VPanel.Core.csproj src/VPanel.Core/
COPY src/VPanel.Infrastructure/VPanel.Infrastructure.csproj src/VPanel.Infrastructure/
COPY src/VPanel.Web/VPanel.Web.csproj src/VPanel.Web/
RUN dotnet restore src/VPanel.Web/VPanel.Web.csproj -a $TARGETARCH
COPY src/ src/
RUN dotnet publish src/VPanel.Web/VPanel.Web.csproj -c Release -a $TARGETARCH --self-contained false -o /app -p:DebugType=none

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
# v-panel drives the host's Docker through the mounted socket: it needs the docker CLI and the compose plugin
# (static binaries from the official CLI image).
COPY --from=docker:29-cli /usr/local/bin/docker /usr/local/bin/docker
COPY --from=docker:29-cli /usr/local/libexec/docker/cli-plugins/docker-compose /usr/local/libexec/docker/cli-plugins/docker-compose

WORKDIR /app
COPY --from=build /app ./
# `docker exec -it vpanel vpanel create-admin` (see `vpanel help`).
COPY --chmod=755 deploy/vpanel-cli.sh /usr/local/bin/vpanel

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ASPNETCORE_HTTP_PORTS= \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_NOLOGO=1 \
    VPanel__DataDir=/opt/vpanel/data \
    VPanel__Defaults__AppsRoot=/opt/vpanel/apps \
    VPanel__Defaults__CaddyDir=/opt/vpanel/caddy \
    VPanel__Defaults__PanelUpstream=vpanel:8080

# Runs as root: access to /var/run/docker.sock is root-equivalent anyway, and the socket's group id differs per host.
EXPOSE 8080
ENTRYPOINT ["dotnet", "VPanel.Web.dll"]
