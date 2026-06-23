FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Build context is the repo root (see docker-compose.oss.yaml) so the proto files referenced by the
# csproj as ../../proto/*.proto (e.g. gateway_ingress.proto) are available. Mirrors the ApiServer image.
COPY ./proto/ /proto/
COPY ./DotNet/ ./

# Restore against the full source tree (transitive packages such as AWSSDK.CognitoIdentity can be
# missed by a csproj-only restore layer), then publish.
RUN dotnet restore "BuildingOS.ConnectorWorker/BuildingOS.ConnectorWorker.csproj"
RUN dotnet publish "BuildingOS.ConnectorWorker/BuildingOS.ConnectorWorker.csproj" \
    -c Release -o /app/publish

# aspnet (not runtime) base: the worker is a Microsoft.NET.Sdk.Web app that always hosts Kestrel —
# an internal /health surface (#145) and the optional gRPC ingress (#181) — so it needs the
# ASP.NET Core shared framework. aspnet:8.0 is a superset of runtime:8.0; the BackgroundService
# connectors run identically.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "BuildingOS.ConnectorWorker.dll"]
