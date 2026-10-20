#!/bin/bash
# Build the loader-free drag-and-drop package (0.2.5.4+). Zip layout mirrors the game folder:
#   Diplomacy is Not an Option_Data\Managed\        DinoNet.dll + Harmony (and its MonoMod/Cecil helpers)
#   Diplomacy is Not an Option_Data\Plugins\x86_64\ netkit.dll
#   Diplomacy is Not an Option_Data\ScriptingAssemblies.json / RuntimeInitializeOnLoads.json (DinoNet added)
#   UserData\DinoNet\                               (empty: settings and logs go here)
set -e
V=$(grep -o 'Version = "[0-9.]*"' /home/claude/dino/mod/src/Core/Mod.cs | grep -o '[0-9.]*')
/home/claude/dino/mod/build.sh > /dev/null
GAME_DATA="/mnt/user-data/uploads/Diplomacy is Not an Option/Diplomacy is Not an Option_Data"   # original lists (dno_1.0.187_r)
ML=/home/claude/dino/ml/pkg/MelonLoader/net35
S=/home/claude/dino/pkg/stage-free
D="$S/Diplomacy is Not an Option_Data"
mkdir -p "$S"; find "$S" -mindepth 1 -delete
mkdir -p "$D/Managed" "$D/Plugins/x86_64" "$S/UserData/DinoNet"
cp /home/claude/dino/mod/out/DinoNet.dll "$D/Managed/"
cp $ML/0Harmony.dll $ML/MonoMod.RuntimeDetour.dll $ML/MonoMod.Utils.dll $ML/Mono.Cecil.dll "$D/Managed/"
cp /home/claude/netkit31/NetKit-3.1/bin/windows-x64/netkit.dll "$D/Plugins/x86_64/"
python3 -I /home/claude/dino/tools/addlists.py "$GAME_DATA" "$D"
cp /home/claude/dino/README-DinoNet.txt "$S/"
mkdir -p /home/claude/dino/out
rm -f /home/claude/dino/out/DinoNet-$V.zip
(cd "$S" && zip -qr -9 /home/claude/dino/out/DinoNet-$V.zip .)
ls -la /home/claude/dino/out/DinoNet-$V.zip
unzip -l /home/claude/dino/out/DinoNet-$V.zip
