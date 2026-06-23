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
export POSTGRES_CONNECTION_STRING="Host=localhost;Port=5433;Database=buildingos;Username=buildingos;Password=buildingos"

cd $repository_root/DotNet
dotnet build -c Release 
dotnet swagger tofile --yaml --output $repository_root/docs/schema/swagger.yaml $repository_root/out/BuildingOS.ApiServer/bin/Release/net8.0/BuildingOS.ApiServer.dll building-os

cd $repository_root/web-client

CLIENT_PATH=$repository_root/web-client/src/lib/infra/aspida-client/generated

if [ -d "$CLIENT_PATH" ]; then
  rm -rf "$CLIENT_PATH"/*
else
  mkdir -p "$CLIENT_PATH"
fi

npx openapi2aspida -i=$repository_root/docs/schema/swagger.yaml -o=$CLIENT_PATH
