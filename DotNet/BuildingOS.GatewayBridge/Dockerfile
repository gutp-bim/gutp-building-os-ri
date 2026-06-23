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
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS main
WORKDIR /out
EXPOSE 8080
COPY --from=builder /app/BuildingOS.GatewayBridge/out /out
ENTRYPOINT ["dotnet", "BuildingOS.GatewayBridge.dll"]
