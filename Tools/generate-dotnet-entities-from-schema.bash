#!/bin/bash

repository_root=`git rev-parse --show-toplevel`
cd $repository_root/DotNet

dotnet tool restore

# The pinned Corvus generator (corvus.json.jsonschema.typegeneratortool 2.0.20) targets the .NET 8
# runtime without major roll-forward. The solution is on .NET 10 (#634) and a fresh machine may
# have only the .NET 10 runtime, so let the tool run on the newest installed major instead of
# failing with "You must install or update .NET". Verified: the generator emits byte-identical
# output on the .NET 8 and .NET 10 runtimes.
export DOTNET_ROLL_FORWARD=Major

while read -r f; do

    dotnet tool run generatejsonschematypes --rootNamespace BuildingOS.Shared.Entities $f --outputPath ./BuildingOS.Shared/Defines/Entities/

done < <(find ./BuildingOS.Shared/Defines/Schemas -mindepth 1 -maxdepth 1)
