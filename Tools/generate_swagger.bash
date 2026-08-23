#!/bin/bash
# set -e: without it a failed `dotnet build` falls through to the DLL lookup below, which happily
# finds a *stale* DLL from an earlier build and generates swagger from it — yielding "no diff" and a
# green check for a tree that does not compile. That matters now that pr-check.yml gates on this
# script (#354).
set -euo pipefail

repository_root=`git rev-parse --show-toplevel`

export ASPNETCORE_ENVIRONMENT=Development
export TZ=Asia/Tokyo
export PORT=8081
export POSTGRES_CONNECTION_STRING="Host=localhost;Port=5433;Database=buildingos;Username=buildingos;Password=buildingos"

cd $repository_root/DotNet
dotnet build BuildingOS.ApiServer/BuildingOS.ApiServer.csproj -c Release

# GitHub ActionsとローカルでDLLの出力先が異なるため、両方をチェック
if [ -f "$repository_root/DotNet/BuildingOS.ApiServer/bin/Release/net8.0/BuildingOS.ApiServer.dll" ]; then
    DLL_PATH="$repository_root/DotNet/BuildingOS.ApiServer/bin/Release/net8.0/BuildingOS.ApiServer.dll"
elif [ -f "$repository_root/out/BuildingOS.ApiServer/bin/Release/net8.0/BuildingOS.ApiServer.dll" ]; then
    DLL_PATH="$repository_root/out/BuildingOS.ApiServer/bin/Release/net8.0/BuildingOS.ApiServer.dll"
else
    echo "Error: BuildingOS.ApiServer.dll not found"
    exit 1
fi

dotnet swagger tofile --yaml --output $repository_root/docs/schema/swagger.yaml $DLL_PATH building-os
