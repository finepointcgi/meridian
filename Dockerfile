# syntax=docker/dockerfile:1.7
# Meridian server image (multi-arch: linux/amd64, linux/arm64).
#   Dev:        docker compose --profile app up -d --build            → http://localhost:8180
#   Production: docker compose -f docker-compose.prod.yml up -d        (see docs/DEPLOYMENT.md)
#
# Runtime base: Ubuntu 24.04 "noble" ASP.NET image rather than chiseled/Alpine. PDF and Word export render through
# QuestPDF/SkiaSharp, which needs fontconfig + real fonts (chiseled images have no package manager to add them) and
# ships no linux-musl-arm64 native library (Alpine arm64 would lose PDF export). The image still runs as a non-root
# user, needs no shell at runtime, and works with a read-only root filesystem (mount a tmpfs on /tmp).

ARG DOTNET_TAG=10.0-noble

# ------------------------------------------------------------------ build (runs natively on the build host, output is portable IL)
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_TAG} AS build
WORKDIR /src
COPY Meridian.slnx ./
COPY src/Meridian.Core/Meridian.Core.csproj src/Meridian.Core/
COPY src/Meridian.Serialization/Meridian.Serialization.csproj src/Meridian.Serialization/
COPY src/Meridian.Data/Meridian.Data.csproj src/Meridian.Data/
COPY src/Meridian.Scripting/Meridian.Scripting.csproj src/Meridian.Scripting/
COPY src/Meridian.Web/Meridian.Web.csproj src/Meridian.Web/
RUN dotnet restore src/Meridian.Web/Meridian.Web.csproj
COPY src/ src/
# Declared after restore: build args become environment variables, and MSBuild reads $VERSION as the Version property.
ARG VERSION=0.0.0-dev
RUN export VERSION="${VERSION#v}" \
 && dotnet publish src/Meridian.Web/Meridian.Web.csproj -c Release -o /app --no-restore \
      -p:Version="$VERSION" -p:InformationalVersion="$VERSION" -p:UseAppHost=false \
 && find /app/runtimes -mindepth 1 -maxdepth 1 -type d \
      ! -name linux-x64 ! -name linux-arm64 ! -name linux ! -name unix -exec rm -rf {} + \
 && mkdir -p /data/keys /data/backups /data/plugins

# ------------------------------------------------------------------ runtime
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_TAG} AS runtime
ARG DOTNET_TAG
ARG VERSION=0.0.0-dev
ARG REVISION=unknown
ARG CREATED=unknown
LABEL org.opencontainers.image.title="Meridian" \
      org.opencontainers.image.description="Meridian MBSE modeling server (SysML, BPMN, ArchiMate) with live collaboration" \
      org.opencontainers.image.source="https://github.com/finepointcgi/meridian" \
      org.opencontainers.image.url="https://github.com/finepointcgi/meridian" \
      org.opencontainers.image.documentation="https://github.com/finepointcgi/meridian/blob/main/docs/DEPLOYMENT.md" \
      org.opencontainers.image.vendor="finepointcgi" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.created="${CREATED}" \
      org.opencontainers.image.base.name="mcr.microsoft.com/dotnet/aspnet:${DOTNET_TAG}"

# Fonts + fontconfig for PDF/Word diagram rendering (QuestPDF / Skia); GSSAPI for Npgsql (Kerberos auth, and it probes for it).
RUN apt-get update \
 && apt-get install -y --no-install-recommends libfontconfig1 fonts-dejavu-core libgssapi-krb5-2 \
 && apt-get upgrade -y --no-install-recommends \
 && rm -rf /var/lib/apt/lists/* /var/cache/apt/*

WORKDIR /app
COPY --from=build --chown=root:root /app ./
# Volume mount points owned by the app user, so fresh named volumes inherit the right owner.
COPY --from=build --chown=$APP_UID:$APP_UID /data /data

# APP_UID (1654, user "app") comes from the base image.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_EnableDiagnostics=0 \
    TMPDIR=/tmp \
    XDG_CACHE_HOME=/tmp/.cache \
    Meridian__DataPath=/data \
    Meridian__KeysPath=/data/keys \
    MERIDIAN_VERSION=${VERSION}
VOLUME ["/data/keys", "/data/backups", "/data/plugins"]
EXPOSE 8080
USER $APP_UID

# Liveness via the app itself (no curl in the image). Readiness (/readyz, checks the database) is for orchestrators.
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
  CMD ["dotnet", "/app/Meridian.Web.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "Meridian.Web.dll"]
