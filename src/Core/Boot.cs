// Start-up without a mod loader.
// Unity reads two lists in <game>_Data at launch:
//   ScriptingAssemblies.json      - which assemblies hold scripts (DinoNet.dll is added)
//   RuntimeInitializeOnLoads.json - which static methods to call at start (Boot.Start is added)
// Boot.Start then creates one hidden object that drives DinoNet every frame, exactly like the
// MelonLoader entry points did (Update, LateUpdate, scene loads, quit).
// If the MonoBehaviour can't be created, the frame calls are inserted into Unity's player loop
// instead (re-checked after every scene load, in case the game rebuilds the loop).
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.SceneManagement;

namespace DinoNet
{
    public static class Boot
    {
        static bool started;
        static Mod mod;
        static bool usingLoop;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Start()
        {
            if (started) return;
            started = true;
            // the old MelonLoader install would load a second copy: let that one run alone
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "MelonLoader")
                {
                    Debug.LogWarning("[DinoNet] MelonLoader is installed too - remove version.dll, the MelonLoader folder and Mods\\DinoNet.dll. This copy stays off.");
                    return;
                }
            try
            {
                mod = new Mod();
                mod.Init();
            }
            catch (Exception e) { Debug.LogError("[DinoNet] start failed: " + e); return; }

            SceneManager.sceneLoaded += (s, m) =>
            {
                if (usingLoop) EnsureLoop();
                try { mod.OnSceneLoaded(s.buildIndex, s.name); } catch (Exception e) { Log.Err("scene load: " + e); }
            };
            Application.quitting += () => { try { mod.OnQuit(); } catch { } };

            try
            {
                var go = new GameObject("DinoNet");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<Host>();
                Log.Msg("started without a mod loader");
            }
            catch (Exception e)
            {
                Log.Warn("frame object unavailable (" + e.Message + ") - using the player loop");
                usingLoop = true;
                EnsureLoop();
            }
        }

        internal static void Frame() { try { mod?.Update(); } catch (Exception e) { Log.Throttled("upd", "update: " + e); } }
        internal static void LateFrame() { try { mod?.LateUpdate(); } catch (Exception e) { Log.Throttled("late0", "late update: " + e); } }

        // ---- player loop fallback
        struct DinoUpdate { }
        struct DinoLateUpdate { }

        static void EnsureLoop()
        {
            try
            {
                var root = PlayerLoop.GetCurrentPlayerLoop();
                bool changed = Insert(ref root, "Update", typeof(DinoUpdate), Frame);
                changed |= Insert(ref root, "PreLateUpdate", typeof(DinoLateUpdate), LateFrame);
                if (changed) PlayerLoop.SetPlayerLoop(root);
            }
            catch (Exception e) { Log.Err("player loop: " + e.Message); }
        }

        static bool Insert(ref PlayerLoopSystem root, string phase, Type marker, PlayerLoopSystem.UpdateFunction fn)
        {
            var subs = root.subSystemList;
            if (subs == null) return false;
            for (int i = 0; i < subs.Length; i++)
            {
                if (subs[i].type == null || subs[i].type.Name != phase) continue;
                var list = new List<PlayerLoopSystem>(subs[i].subSystemList ?? new PlayerLoopSystem[0]);
                foreach (var s in list) if (s.type == marker) return false;
                list.Add(new PlayerLoopSystem { type = marker, updateDelegate = fn });
                subs[i].subSystemList = list.ToArray();
                root.subSystemList = subs;
                return true;
            }
            return false;
        }
    }

    /// The hidden per-frame driver.
    public sealed class Host : MonoBehaviour
    {
        void Update() => Boot.Frame();
        void LateUpdate() => Boot.LateFrame();
    }
}
