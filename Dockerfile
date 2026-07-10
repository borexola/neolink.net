# Neolink.NET — RTSP bridge + web UI for Reolink cameras (Baichuan protocol).
#
# Build:  docker build -t neolink-net .
# Run:    docker run -d --name neolink \
#             -p 8654:8654 -p 8655:8655 \
#             -v /path/to/config.json:/config/config.json:ro \
#             neolink-net
#
# The config file defines the cameras, the RTSP/web ports, and whether the
# web UI is served ("webui": true|false).

# ---------- build ----------
# --platform=$BUILDPLATFORM: compile natively even for cross-arch images;
# the framework-dependent output is portable IL, so only the runtime stage is per-arch.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# NOTE: restore must run with the FULL source tree present, not just the csproj
# files. The SDK decides at restore time whether this is a Blazor app (by seeing
# .razor files) and only then pulls the framework's static web assets — a staged
# csproj-only restore silently drops /_framework/blazor.web.js from the output,
# which bricks the web UI (page loads, but the interactive circuit never starts).
COPY Neolink.sln nuget.config* ./
COPY src/ src/
# VERSION: release builds pass the git tag (docker.yml) so the app reports it;
# unset (local builds) falls back to the <Version> in the csproj.
ARG VERSION=
RUN dotnet publish src/Neolink.Server/Neolink.Server.csproj -c Release -o /app \
    ${VERSION:+-p:Version=$VERSION}

# Fail the image build outright if the UI's interactivity script is missing.
RUN test -f /app/wwwroot/_framework/blazor.web.js

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# Time zone support: the base image ships no zoneinfo database, so the TZ env
# var would otherwise be ignored and all timestamps (event folders, clip names,
# the UI clock) would be UTC. Install tzdata so `TZ` is honoured.
RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata \
    && rm -rf /var/lib/apt/lists/*

# 8654 = RTSP, 8655 = web UI + HTTP/WebSocket API
EXPOSE 8654 8655
VOLUME /config

# Don't advertise the default ASP.NET port; the app binds from its config file.
ENV ASPNETCORE_URLS=""

# Set the container's time zone here or, preferably, at runtime:
#   docker run -e TZ=Europe/London ...   (or "environment: [TZ=...]" in compose)
# Defaults to UTC when unset.
ENV TZ=UTC

ENTRYPOINT ["dotnet", "neolink.net.dll", "--config", "/config/config.json"]
