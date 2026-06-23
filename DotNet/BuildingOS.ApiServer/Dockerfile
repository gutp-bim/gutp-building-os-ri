# ----- application build stage -----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS builder
WORKDIR /app

# copy proto files (referenced by csproj as ../../proto/)
COPY ./proto/ /proto/

# copy csproj and restore as distinct layers
COPY ./DotNet/ ./
RUN dotnet restore ./
WORKDIR /app/BuildingOS.ApiServer
RUN dotnet publish -c Release -o out

# ----- application packaging stage -----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS main

# copy files from buider
COPY --from=builder /app/BuildingOS.ApiServer/out /out

WORKDIR /out

# ----- Run -----
CMD ["dotnet", "BuildingOS.ApiServer.dll"]