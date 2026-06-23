repository_root=`git rev-parse --show-toplevel`

cd $repository_root/DotNet/BuildingOS.Shared

dotnet ef migrations add $1 --context RelationalDbContext
