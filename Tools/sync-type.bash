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
  rm -rf "$CLIENT_PATH"/*
else
  mkdir -p "$CLIENT_PATH"
fi

npx openapi2aspida -i=$repository_root/docs/schema/swagger.yaml -o=$CLIENT_PATH
