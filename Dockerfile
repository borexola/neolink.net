# Neolink.NET — RTSP bridge + web UI for Reolink cameras (Baichuan protocol).
#
# Build:  docker build -t neolink-net .
# Run:    docker run -d --name neolink \
#             -p 8554:8554 -p 8555:8555 \
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

# Restore first for layer caching
COPY Neolink.sln nuget.config* ./
COPY src/Neolink.Server/Neolink.Server.csproj src/Neolink.Server/
COPY src/Neolink.WebClient/Neolink.WebClient.csproj src/Neolink.WebClient/
RUN dotnet restore src/Neolink.Server/Neolink.Server.csproj

COPY src/ src/
RUN dotnet publish src/Neolink.Server/Neolink.Server.csproj -c Release -o /app --no-restore

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# 8554 = RTSP, 8555 = web UI + HTTP/WebSocket API
EXPOSE 8554 8555
VOLUME /config

# Don't advertise the default ASP.NET port; the app binds from its config file.
ENV ASPNETCORE_URLS=""

ENTRYPOINT ["dotnet", "neolink.net.dll", "--config", "/config/config.json"]
