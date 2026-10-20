#!/bin/bash
# Build a full drag-and-drop package: MelonLoader + Mods\DinoNet.dll + UserData\DinoNet\netkit.dll
set -e
V=$(grep -o 'Version = "[0-9.]*"' /home/claude/dino/mod/src/Core/Mod.cs | grep -o '[0-9.]*')
/home/claude/dino/mod/build.sh > /dev/null
S=/home/claude/dino/pkg/stage
rm -rf $S && mkdir -p $S/Mods $S/UserData/DinoNet
cp -r /home/claude/dino/ml/pkg/MelonLoader /home/claude/dino/ml/pkg/version.dll $S/
cp /home/claude/dino/mod/out/DinoNet.dll $S/Mods/
cp /home/claude/netkit31/NetKit-3.1/bin/windows-x64/netkit.dll $S/UserData/DinoNet/
cp /home/claude/dino/README-DinoNet.txt $S/
mkdir -p /home/claude/dino/out
rm -f /home/claude/dino/out/DinoNet-$V.zip
(cd $S && zip -qr -9 /home/claude/dino/out/DinoNet-$V.zip .)
ls -la /home/claude/dino/out/DinoNet-$V.zip
unzip -l /home/claude/dino/out/DinoNet-$V.zip | grep -E "DinoNet|netkit|version.dll|README" 
