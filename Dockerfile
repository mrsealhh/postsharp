# syntax=docker/dockerfile:1

# PostSharp headless image: MCP + Mock only, no window and no tray icon.
# The MCP endpoint listens on loopback inside the container, so do not publish
# port 5199 to the host. Run CI steps with `docker exec` instead.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/PostSharp/PostSharp.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:Version=0.0.0-container \
    -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        fonts-dejavu-core \
        libdbus-1-3 \
        libegl1 \
        libfontconfig1 \
        libfreetype6 \
        libgl1 \
        libgtk-3-0 \
        libice6 \
        libsm6 \
        libx11-6 \
        libxi6 \
        libxrandr2 \
        xauth \
        xvfb \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

ENV POSTSHARP_DATA_DIR=/data
VOLUME ["/data"]
EXPOSE 5199

HEALTHCHECK --interval=10s --timeout=3s --start-period=30s --retries=6 \
    CMD curl -fsS http://127.0.0.1:5199/api/health || exit 1

ENTRYPOINT ["xvfb-run", "-a", "./PostSharp", "--headless"]
