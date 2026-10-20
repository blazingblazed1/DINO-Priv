# Copy the game's two Unity start-up lists and add DinoNet to them (idempotent).
import json, os, sys
src, dst = sys.argv[1], sys.argv[2]
sa = json.load(open(os.path.join(src, "ScriptingAssemblies.json")))
if "DinoNet.dll" not in sa["names"]:
    sa["names"].append("DinoNet.dll"); sa["types"].append(16)
ri = json.load(open(os.path.join(src, "RuntimeInitializeOnLoads.json")))
entry = {"assemblyName": "DinoNet", "nameSpace": "DinoNet", "className": "Boot", "methodName": "Start", "loadTypes": 1, "isUnityClass": False}
if not any(e.get("assemblyName") == "DinoNet" for e in ri["root"]):
    ri["root"].append(entry)
json.dump(sa, open(os.path.join(dst, "ScriptingAssemblies.json"), "w"), separators=(",", ":"))
json.dump(ri, open(os.path.join(dst, "RuntimeInitializeOnLoads.json"), "w"), separators=(",", ":"))
print("lists: %d assemblies, %d start-up methods" % (len(sa["names"]), len(ri["root"])))
