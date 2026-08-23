#!/bin/bash
# set -e: this script is now upstream of a CI gate (#357), and without it a failed `dotnet build`
# falls through to generate swagger from a stale DLL — the same false-green that bit
# generate_swagger.bash (#354).
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

cd $repository_root/web-client

CLIENT_PATH=$repository_root/web-client/src/lib/infra/aspida-client/generated

if [ -d "$CLIENT_PATH" ]; then
  # ${VAR:?} so an empty CLIENT_PATH aborts instead of expanding to `rm -rf /*`
  rm -rf "${CLIENT_PATH:?}"/*
else
  mkdir -p "$CLIENT_PATH"
fi

# Pinned: the #357 drift check regenerates this tree and fails on any difference, so an unpinned
# generator would make an upstream release break unrelated PRs. Keep in step with
# OPENAPI2ASPIDA_VERSION in .github/workflows/pr-check.yml.
npx --yes openapi2aspida@0.24.0 -i=$repository_root/docs/schema/swagger.yaml -o=$CLIENT_PATH
