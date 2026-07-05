# Build context is the repo root (see docker-compose.oss.yaml) so the proto files referenced by the
# csproj as ../../proto/*.proto (e.g. gateway_ingress.proto) are available. Mirrors the ApiServer image.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# proto files referenced by csproj as ../../proto/; relative dest lands at /src/proto/
COPY ./proto/ proto/

# copy *.csproj files only — restore is cached until dependencies change
COPY ./DotNet/BuildingOS.ConnectorWorker/BuildingOS.ConnectorWorker.csproj DotNet/BuildingOS.ConnectorWorker/
COPY ./DotNet/BuildingOS.Shared/BuildingOS.Shared.csproj DotNet/BuildingOS.Shared/

RUN dotnet restore DotNet/BuildingOS.ConnectorWorker/BuildingOS.ConnectorWorker.csproj

# copy full source and publish
COPY ./DotNet/ DotNet/
RUN dotnet publish DotNet/BuildingOS.ConnectorWorker/BuildingOS.ConnectorWorker.csproj \
    -c Release -o /app/publish --no-restore

# aspnet (not runtime) base: the worker is a Microsoft.NET.Sdk.Web app that always hosts Kestrel —
# an internal /health surface (#145) and the optional gRPC ingress (#181) — so it needs the
# ASP.NET Core shared framework. aspnet:8.0 is a superset of runtime:8.0; the BackgroundService
# connectors run identically.
# Chiseled: Ubuntu 24.04 (Noble)-based, no shell/package manager, non-root by default (A-5).
# Health probes are plain HTTP so the missing shell is not a problem.
# The chiseled base image already sets ENTRYPOINT ["/usr/bin/dotnet"], so CMD is just the DLL name.
FROM mcr.microsoft.com/dotnet/aspnet:8.0-noble-chiseled AS runtime
WORKDIR /app

COPY --from=build --chown=app:app /app/publish .
CMD ["BuildingOS.ConnectorWorker.dll"]
