#!/bin/bash
repository_root=`git rev-parse --show-toplevel`

# TODO: 環境変数をセットしなくても良いようにする
export ASPNETCORE_ENVIRONMENT=Development
export TZ=Asia/Tokyo
export PORT=8081
export ADT_ENDPOINT=http://dummy
export COSMOS_DATABASE_NAME=building-os-db
export COSMOS_CONTAINER_NAME=TelemetryContainer
export COSMOS_CONTROL_CONTAINER_NAME=PointControlContainer
export COSMOS_CONNECTION_STRING=AccountEndpoint="https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw=="
export AZURE_TENANT_ID=env
export AZURE_CLIENT_ID=env
export AZURE_CLIENT_SECRET=env
export LAKE_HOUSE_SQL_CONNECTION_STRING=env
export APPLICATION_INSIGHTS_CONNECTION_STRING=env
export LOG_LEVEL=Information

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