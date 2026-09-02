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

# The base image ships tzdata (TZ is honoured for event folders, clip names and
# the UI clock); fail the build rather than silently fall back to UTC should a
# future base image stop shipping it.
RUN test -d /usr/share/zoneinfo/America

# ffmpeg (static, single binary — NOT the apt package and its dependency tree):
# lets AI event descriptions decode pre-roll frames, the seconds BEFORE the
# trigger that the live snapshot burst can never reach, and encodes Opus for
# clients that ask for it (?audio=opus). Strictly optional at runtime — the app
# probes PATH and skips both features when absent — so this line is what makes
# them work for Docker/HA users. Costs ~135 MB on disk (~50 MB of download);
# nothing runs unless a described event or an Opus client asks for it.
# Digest-pinned (the 7.1 tag could be repushed; the digest can't change under
# us) — verified multi-arch for this workflow's amd64+arm64 targets.
COPY --from=mwader/static-ffmpeg:7.1@sha256:a8090df5f5608daef387e1b2e93b98aaacb4d92153ad904e7d715c725724fca4 \
    /ffmpeg /usr/local/bin/ffmpeg

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
