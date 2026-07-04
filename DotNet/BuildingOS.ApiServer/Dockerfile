# ----- application build stage -----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS builder
WORKDIR /app

# copy proto files (referenced by csproj as ../../proto/; relative dest lands at /app/proto/)
COPY ./proto/ proto/

# copy *.csproj files only — restore is cached until dependencies change
COPY ./DotNet/BuildingOS.ApiServer/BuildingOS.ApiServer.csproj DotNet/BuildingOS.ApiServer/
COPY ./DotNet/BuildingOS.Shared/BuildingOS.Shared.csproj DotNet/BuildingOS.Shared/

RUN dotnet restore DotNet/BuildingOS.ApiServer/BuildingOS.ApiServer.csproj

# copy full source and publish
COPY ./DotNet/ DotNet/
RUN dotnet publish DotNet/BuildingOS.ApiServer/BuildingOS.ApiServer.csproj \
    -c Release -o /app/out --no-restore

# ----- application packaging stage -----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS main

COPY --from=builder /app/out /out

WORKDIR /out

# ----- Run -----
CMD ["dotnet", "BuildingOS.ApiServer.dll"]
