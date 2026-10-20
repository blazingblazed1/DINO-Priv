// DinoNet - online co-op (shared faction) for Diplomacy is Not an Option.
// Loaded by Unity itself (no mod loader): see Core/Boot.cs. Networking: NetKit (netkit.dll in
// <game>_Data\Plugins\x86_64). Settings and logs: <game folder>\UserData\DinoNet.
using System;
using System.IO;
using UnityEngine;

namespace DinoNet
{
    public class Mod
    {
        public const string Version = "0.2.5.4";
        public const string GameKey = "dino-coop";
        public static Mod I;
        public static string Dir;          // UserData\DinoNet
        public static bool NetKitOk;
        public static string NetKitError = "";
        public static int Frame;

        public static string GameFolder => Path.GetDirectoryName(Application.dataPath);

        public void Init()
        {
            I = this;
            Dir = Path.Combine(Path.Combine(GameFolder, "UserData"), "DinoNet");
            try { Directory.CreateDirectory(Dir); } catch { }
            Prefs.Load();
            try
            {
                string nk = Path.Combine(Path.Combine(Path.Combine(Application.dataPath, "Plugins"), "x86_64"), "netkit.dll");
                if (!File.Exists(nk)) nk = Path.Combine(Dir, "netkit.dll");   // older layout
                NetKit.NetKitLib.Load(nk);
                NetKitOk = true;
                Log.Msg("NetKit " + NetKit.NetKitLib.Version + " loaded");
            }
            catch (Exception e)
            {
                NetKitError = e.Message;
                Log.Err("NetKit could not be loaded: " + e.Message);
            }
            try { new HarmonyLib.Harmony("dinonet").PatchAll(typeof(Mod).Assembly); Log.Msg("patches applied"); }
            catch (Exception e) { Log.Err("patching failed: " + e); }
            Log.Msg("DinoNet " + Version + " ready");
            Watch.Start();
            Flight.Start();
        }

        static readonly System.Diagnostics.Stopwatch updWatch = new System.Diagnostics.Stopwatch();

        public void Update()
        {
            Frame++;
            updWatch.Reset(); updWatch.Start();
            Watch.Beat();
            try { Flight.Update(); } catch { }
            Watch.Step = "Net.Update";
            try { Net.Update(); } catch (Exception e) { Log.Throttled("net", "Net.Update: " + e); }
            Watch.Step = "Game.Update";
            try { Game.Update(); } catch (Exception e) { Log.Throttled("game", "Game.Update: " + e); }
            Watch.Step = "Ui.Update";
            try { Ui.Update(); } catch (Exception e) { Log.Throttled("ui", "Ui.Update: " + e); }
            Watch.Step = "Gfx.Update";
            try { Gfx.Update(); } catch (Exception e) { Log.Throttled("gfx", "Gfx.Update: " + e); }
            Flight.ModTime(updWatch.Elapsed.TotalMilliseconds);
            Watch.Step = "game code (after DinoNet's update)";
        }

        public void LateUpdate()
        {
            try { Game.LateUpdate(); } catch (Exception e) { Log.Throttled("late", "Game.LateUpdate: " + e); }
            Gfx.FreeCursor();
        }

        public void OnSceneLoaded(int buildIndex, string sceneName)
        {
            Log.Msg("scene loaded: " + buildIndex + " " + sceneName);
            if (sceneName == "MainMenu") Watch.Armed = true;
            Watch.Beat();
            try { Ui.OnSceneLoaded(buildIndex, sceneName); } catch (Exception e) { Log.Err("Ui.OnSceneLoaded: " + e); }
            try { Game.OnSceneLoaded(buildIndex, sceneName); } catch (Exception e) { Log.Err("Game.OnSceneLoaded: " + e); }
        }

        public void OnQuit()
        {
            try { Net.Leave("quit"); } catch { }
            Flight.OnQuit();
        }
    }

    /// Hang detector: a background thread that reports where the main thread stopped
    /// if the game stops advancing for 8 seconds. Writes to the log and to
    /// UserData\DinoNet\hang.txt.
    public static class Watch
    {
        public static volatile string Step = "startup";
        /// Off until the main menu first appears: the game's own boot can take 10+ s on slow PCs.
        public static volatile bool Armed;
        static long beat = DateTime.UtcNow.Ticks;
        static System.Threading.Thread th;
        public static void Beat() { System.Threading.Interlocked.Exchange(ref beat, DateTime.UtcNow.Ticks); }
        public static void Mark(string step) { Step = step; Log.Msg("[step] " + step); }
        public static void Start()
        {
            if (th != null) return;
            th = new System.Threading.Thread(Loop);
            th.IsBackground = true;
            th.Name = "DinoNet watchdog";
            th.Start();
        }
        static void Write(string s)
        {
            try { Log.Warn(s); } catch { }
            try { File.AppendAllText(Path.Combine(Mod.Dir ?? ".", "hang.txt"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s + "\r\n"); } catch { }
        }
        static void Loop()
        {
            bool reported = false; string at = "";
            while (true)
            {
                try
                {
                    System.Threading.Thread.Sleep(1000);
                    Flight.Tick();
                    double stuck = (DateTime.UtcNow.Ticks - System.Threading.Interlocked.Read(ref beat)) / 10000000.0;
                    if (stuck > 8.0 && !reported && Armed)
                    {
                        reported = true; at = Step;
                        Write("HANG: the game has not advanced for " + (int)stuck + " s - last step: " + at);
                        Flight.Add("[watchdog] HANG at step: " + at);
                        Flight.Dump("hang", true);
                    }
                    else if (stuck < 2.0 && reported)
                    {
                        reported = false;
                        Write("HANG over: the game is running again (was stuck at: " + at + ")");
                    }
                }
                catch { }
            }
        }
    }

    public static class Log
    {
        const string Tag = "[DinoNet] ";
        static readonly System.Collections.Generic.Dictionary<string, float> last = new System.Collections.Generic.Dictionary<string, float>();
        public static bool Verbose;
        // Unity's own log (Player.log) gets everything too; DinoNet's own log is the main one.
        public static void Msg(string s) { Flight.Add(s); Debug.Log(Tag + s); Own(s); }
        public static void Dbg(string s) { Flight.Add("[dbg] " + s); if (Verbose) { Debug.Log(Tag + "[dbg] " + s); Own("[dbg] " + s); } }
        public static void Warn(string s) { Flight.Add("[warn] " + s); Debug.LogWarning(Tag + s); Own("[WARNING] " + s); }
        public static void Err(string s) { Flight.Add("[ERROR] " + s); Debug.LogError(Tag + s); Own("[ERROR] " + s); }

        // DinoNet's own log, one per running copy: several copies run from one game folder share (and
        // overwrite) Unity's Player.log, so this one is what tells them apart.
        static StreamWriter own;
        static readonly object ownGate = new object();
        public static string OwnPath;
        public static void OpenOwn()
        {
            try
            {
                // first free file wins: a copy still running keeps its file open for writing, so the next copy
                // can't open it and moves on to dinonet_2.log, _3... (works even if the copy check failed)
                for (int k = Math.Max(1, Prefs.Instance); k <= 12 && own == null; k++)
                {
                    string sfx = k > 1 ? "_" + k : "";
                    string path = Path.Combine(Mod.Dir, "dinonet" + sfx + ".log");
                    try
                    {
                        var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read | FileShare.Delete);
                        try { File.Copy(path, Path.Combine(Mod.Dir, "dinonet" + sfx + "_previous.log"), true); } catch { }
                        fs.SetLength(0);
                        lock (ownGate) { own = new StreamWriter(fs); own.AutoFlush = true; }
                        OwnPath = path;
                    }
                    catch (IOException) { }
                }
                Own("DinoNet " + Mod.Version + " - player " + Prefs.DisplayName + (Prefs.Instance > 1 ? " (copy #" + Prefs.Instance + " on this PC)" : ""));
            }
            catch (Exception e) { Debug.LogWarning(Tag + "own log: " + e.Message); }
        }
        static void Own(string s)
        {
            if (own == null) return;
            try { lock (ownGate) own.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + s); } catch { }
        }
        /// Same key logs at most once per 5 s (errors inside per-frame code).
        public static void Throttled(string key, string s)
        {
            float t = Time.realtimeSinceStartup, p;
            if (last.TryGetValue(key, out p) && t - p < 5f) return;
            last[key] = t; Err(s);
        }
    }

    /// Small key=value settings file in UserData\DinoNet\settings.txt.
    public static class Prefs
    {
        public static string Name = "";
        public static string Pid = "";          // this running copy's player id (unique per running game)
        static string savedPid = "", fileName = "";           // the one in settings.txt
        public static int Instance = 1;        // 2, 3... when several games run from the same settings on one PC
        static FileStream instanceLock;
        static IntPtr winMutex;
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr CreateMutexW(IntPtr attr, bool owner, string name);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);
        public static bool LanOnly;
        public static bool SteamPublic;
        public static int MaxPlayers = 4;
        public static int HostUploadKbps = 0;   // 0 = automatic
        public static bool VerboseLog;
        public static string SteamTransport = "messages";   // messages | p2p
        public static bool ReportsOnDesktop = true;
        public static bool AutoResync;

        static string FilePath => Path.Combine(Mod.Dir, "settings.txt");

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath))
                    {
                        int i = line.IndexOf('=');
                        if (i <= 0) continue;
                        string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                        switch (k)
                        {
                            case "name": Name = v; break;
                            case "pid": Pid = v; break;
                            case "lan_only": LanOnly = v == "1"; break;
                            case "steam_public": SteamPublic = v == "1"; break;
                            case "max_players": int.TryParse(v, out MaxPlayers); break;
                            case "host_upload_kbps": int.TryParse(v, out HostUploadKbps); break;
                            case "verbose_log": VerboseLog = v == "1"; break;
                            case "steam_transport": SteamTransport = v == "p2p" ? "p2p" : "messages"; break;
                            case "reports_on_desktop": ReportsOnDesktop = v != "0"; break;
                            case "auto_resync": AutoResync = v == "1"; break;
                        }
                    }
            }
            catch (Exception e) { Log.Warn("settings: " + e.Message); }
            if (string.IsNullOrEmpty(Pid))
            {
                var r = new System.Random();
                const string a = "abcdefghijklmnopqrstuvwxyz0123456789";
                var c = new char[16];
                for (int i = 0; i < c.Length; i++) c[i] = a[r.Next(a.Length)];
                Pid = new string(c);
            }
            savedPid = Pid; fileName = Name;
            ClaimInstance();
            if (MaxPlayers < 2) MaxPlayers = 2;
            if (MaxPlayers > 8) MaxPlayers = 8;
            Log.Verbose = VerboseLog;
            Save();
            Log.OpenOwn();
        }

        public static void Save()
        {
            try
            {
                File.WriteAllLines(FilePath, new[]
                {
                    "name=" + ((Instance > 1 ? fileName : Name) ?? "").Replace("\n", " "),   // extra copies keep their name to themselves
                    "pid=" + (savedPid.Length > 0 ? savedPid : Pid),
                    "lan_only=" + (LanOnly ? 1 : 0),
                    "steam_public=" + (SteamPublic ? 1 : 0),
                    "max_players=" + MaxPlayers,
                    "host_upload_kbps=" + HostUploadKbps,
                    "verbose_log=" + (VerboseLog ? 1 : 0),
                    "steam_transport=" + SteamTransport,
                    "reports_on_desktop=" + (ReportsOnDesktop ? 1 : 0),
                    "auto_resync=" + (AutoResync ? 1 : 0),
                });
            }
            catch (Exception e) { Log.Warn("settings save: " + e.Message); }
        }

        /// Several copies of the game on one PC (test multiboxing) used to share one player id, so each
        /// new copy that joined replaced the previous one in the host's game. The first running copy keeps
        /// the saved id; the 2nd, 3rd... get their own (saved id + number) and " #2", " #3" on their name.
        static void ClaimInstance()
        {
            // a lock file held open for the whole run (Mono's named mutexes don't reach other processes)
            for (int k = 1; k <= 8; k++)
            {
                string id = k == 1 ? savedPid : savedPid.Substring(0, Math.Max(0, savedPid.Length - 2)) + "i" + k;
                try
                {
                    // Windows: a real system-wide mutex (kept open for the whole run)
                    if (Application.platform == RuntimePlatform.WindowsPlayer)
                    {
                        IntPtr h = CreateMutexW(IntPtr.Zero, false, "Local\\DinoNet_" + savedPid + "_" + k);
                        int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                        if (h == IntPtr.Zero) throw new Exception("CreateMutex failed " + err);
                        if (err == 183) { CloseHandle(h); continue; }   // ERROR_ALREADY_EXISTS: copy #k is running
                        winMutex = h;
                    }
                    else instanceLock = new FileStream(Path.Combine(Mod.Dir, "instance_" + k + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    Pid = id; Instance = k;
                    if (k > 1) Log.Msg("another copy of the game is running from this folder: this one is player copy #" + k);
                    return;
                }
                catch (IOException) { continue; }   // that copy number is taken by a running game
                catch (Exception e) { Log.Warn("instance check: " + e.Message); return; }
            }
        }

        /// The id sent to the host: this copy's id mixed with the player name, so copies that share one
        /// settings file but use different names are always different players (even if the copy check
        /// above can't see the other copies). A rejoin under the same name keeps the same id.
        public static string SessionPid
        {
            get
            {
                uint h = 2166136261;
                foreach (char c in DisplayName.Trim().ToLowerInvariant()) { h ^= c; h *= 16777619; }
                string b = Pid.Length >= 8 ? Pid.Substring(0, 8) : Pid.PadRight(8, 'x');
                return b + h.ToString("x8");
            }
        }

        public static string DisplayName
        {
            get
            {
                string n = Name;
                if (string.IsNullOrEmpty(n)) { n = SteamLink.PersonaName(); if (string.IsNullOrEmpty(n)) n = "Player"; }
                return Instance > 1 && Name == fileName ? n + " #" + Instance : n;
            }
        }
    }
}
