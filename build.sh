#!/bin/bash
# Build DinoNet.dll against the game's own Managed assemblies + MelonLoader (net35).
set -e
cd "$(dirname "$0")"
M=/home/claude/dino/managed
ML=/home/claude/dino/ml/pkg/MelonLoader/net35
CSC=/usr/lib/dotnet/sdk/8.0.131/Roslyn/bincore/csc.dll
REFS=""
for f in $M/*.dll; do REFS="$REFS -r:$f"; done
REFS="$REFS -r:$ML/0Harmony.dll"
dotnet $CSC -nologo -noconfig -nostdlib+ -target:library -unsafe+ -optimize+ -langversion:9 -deterministic \
  -nowarn:CS0618,CS0649,CS0169,CS0414,CS1701,CS1702 \
  -out:out/DinoNet.dll $REFS $(find src -name '*.cs' | sort) "$@"
echo "built out/DinoNet.dll ($(stat -c%s out/DinoNet.dll) bytes)"
