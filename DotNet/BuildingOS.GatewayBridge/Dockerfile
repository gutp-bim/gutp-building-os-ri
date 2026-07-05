# Build context is the repository root (proto/ is referenced by the csproj as ../../proto/).
# ----- application build stage -----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS builder
WORKDIR /app

# copy proto files (referenced by csproj as ../../proto/)
COPY ./proto/ /proto/

# copy sources and restore
COPY ./DotNet/ ./
RUN dotnet restore ./BuildingOS.GatewayBridge/BuildingOS.GatewayBridge.csproj
WORKDIR /app/BuildingOS.GatewayBridge
RUN dotnet publish -c Release -o out

# ----- application packaging stage -----
# Chiseled: Ubuntu 24.04 (Noble)-based, no shell/package manager, non-root by default (A-5).
# GatewayBridge is gRPC (h2c) only — no HTTP /health here — but the Helm Deployment already probes
# the gRPC port via kubelet-native tcpSocket (no exec needed), so the missing shell is not a problem.
# The chiseled base image already sets ENTRYPOINT ["/usr/bin/dotnet"], so CMD is just the DLL name.
FROM mcr.microsoft.com/dotnet/aspnet:8.0-noble-chiseled AS main
WORKDIR /out
EXPOSE 8080
COPY --from=builder --chown=app:app /app/BuildingOS.GatewayBridge/out /out
CMD ["BuildingOS.GatewayBridge.dll"]
