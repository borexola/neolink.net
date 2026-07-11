# Home Assistant add-on image: the stock Neolink.NET image plus a small
# launcher that wires the add-on options and the HA Mosquitto broker into
# config.json before starting the app. Built per-arch by the docker workflow
# (the Supervisor installs prebuilt images; it never builds this itself).
#
# BUILD_FROM is the released app image for the SAME version, e.g.
#   docker build --build-arg BUILD_FROM=ghcr.io/borexola/neolink.net:0.8.4 .
ARG BUILD_FROM=ghcr.io/borexola/neolink.net:latest
FROM ${BUILD_FROM}

# jq: JSON surgery in the launcher; curl: the Supervisor services API.
RUN apt-get update \
    && apt-get install -y --no-install-recommends jq curl \
    && rm -rf /var/lib/apt/lists/*

COPY run.sh /run.sh
# The sed guards against CRLF sneaking in from a Windows checkout — a stray
# \r after the shebang breaks the container with a cryptic "no such file".
RUN sed -i 's/\r$//' /run.sh && chmod +x /run.sh

ENTRYPOINT []
CMD ["/run.sh"]
