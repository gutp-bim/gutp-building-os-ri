#!/bin/bash

repository_root=`git rev-parse --show-toplevel`
cd $repository_root/DotNet

dotnet tool restore

while read -r f; do

    dotnet tool run generatejsonschematypes --rootNamespace BuildingOS.Shared.Entities $f --outputPath ./BuildingOS.Shared/Defines/Entities/

done < <(find ./BuildingOS.Shared/Defines/Schemas -mindepth 1 -maxdepth 1)
