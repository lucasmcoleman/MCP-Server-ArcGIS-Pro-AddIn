# syntax=docker/dockerfile:1.7
#
# Container image for the ArcGIS Pro MCP server.
#
# The server uses STDIO for MCP transport, so the container MUST be run
# interactively (-i, no -t needed) and the MCP client will pipe JSON-RPC
# through stdin/stdout.
#
# NOTE ON IPC: the server communicates with the ArcGIS Pro Add-In over a
# named pipe. Named pipes don't cross container ↔ host boundaries natively,
# so this image is primarily useful in two scenarios:
#   1. Packaging/distribution of the MCP server binary.
#   2. Windows container hosts where the pipe is shared (see README).
# For typical local dev on the same Windows machine as ArcGIS Pro, running
# `dotnet run` from the solution is still the simplest path.
#
# Build (from repo root):
#   docker build -f McpServer/ArcGisMcpServer/Dockerfile -t arcgis-mcp-server .
#
# Run (interactive, MCP STDIO transport):
#   docker run --rm -i \
#     -e ARCGIS_MCP_PIPE_NAME=ArcGisProBridgePipe \
#     arcgis-mcp-server

ARG DOTNET_VERSION=8.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Restore first for better layer caching.
COPY McpServer/ArcGisMcpServer/ArcGisMcpServer.csproj McpServer/ArcGisMcpServer/
RUN dotnet restore McpServer/ArcGisMcpServer/ArcGisMcpServer.csproj

# Then copy source and publish a self-contained-ish framework-dependent build.
COPY McpServer/ArcGisMcpServer/ McpServer/ArcGisMcpServer/
RUN dotnet publish McpServer/ArcGisMcpServer/ArcGisMcpServer.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_VERSION} AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# Tunables (override with `docker run -e`)
ENV ARCGIS_MCP_PIPE_NAME=ArcGisProBridgePipe \
    ARCGIS_MCP_MAX_RETRIES=3 \
    ARCGIS_MCP_CONNECT_TIMEOUT_MS=5000 \
    ARCGIS_MCP_REQUEST_TIMEOUT_MS=30000 \
    ARCGIS_MCP_INITIAL_BACKOFF_MS=250 \
    ARCGIS_MCP_MAX_BACKOFF_MS=4000 \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

ENTRYPOINT ["dotnet", "ArcGisMcpServer.dll"]
