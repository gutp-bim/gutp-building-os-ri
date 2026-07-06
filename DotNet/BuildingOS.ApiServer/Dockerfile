# ----- application build stage -----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS builder
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
# Chiseled: Ubuntu 24.04 (Noble)-based, no shell/package manager, non-root by default (A-5).
# Health probes are plain HTTP so the missing shell is not a problem.
# The chiseled base image already sets ENTRYPOINT ["/usr/bin/dotnet"], so CMD is just the DLL name.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS main

COPY --from=builder --chown=app:app /app/out /out

WORKDIR /out

# ----- Run -----
CMD ["BuildingOS.ApiServer.dll"]
