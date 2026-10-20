// Flight recorder: keeps the last 15 seconds of everything DinoNet sees in memory
// (all mod log lines including debug detail, Unity/game log lines and exceptions,
// every network message in and out, and a state snapshot every second).
// Nothing is written while things are fine except a small rolling file; when something
// goes wrong the buffer is saved to UserData\DinoNet\Recordings\.
//
// Saved automatically on: an exception (game or mod), a hang (watchdog), a lost
// connection / sync failure, and after a crash (the rolling file from the previous run).
// F10 saves one by hand. The 10 newest recordings are kept.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace DinoNet
{
    public static class Flight
    {
        const float Keep = 15f;
        const int MaxEntries = 40000;
        const int KeepFiles = 10;

        struct E { public double T; public int F; public string S; }
        static readonly Queue<E> buf = new Queue<E>(4096);
        static readonly object gate = new object();
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static volatile int frame;
        static string dir, liveFile, flagFile;
        static double lastDump = -100, lastSnap, lastLive;
        static bool started;

        static double Now => clock.Elapsed.TotalSeconds;

        // ---- performance (main thread)
        static double frameSum, frameMax, modSum, modMax; static int frames;
        static long lastCpuTicks; static double lastCpuWall; static int lastGc0;
        static long lastNetSent, lastNetRecv;
        public static void ModTime(double ms) { modSum += ms; if (ms > modMax) modMax = ms; }

        public static string SystemInfoText()
        {
            var sb = new StringBuilder();
            try
            {
                sb.Append("PC: ").Append(SystemInfo.processorType).Append(" (").Append(SystemInfo.processorCount).Append(" threads, ")
                  .Append(SystemInfo.processorFrequency).Append(" MHz), RAM ").Append(SystemInfo.systemMemorySize).Append(" MB\r\n");
                sb.Append("GPU: ").Append(SystemInfo.graphicsDeviceName).Append(", VRAM ").Append(SystemInfo.graphicsMemorySize).Append(" MB, ")
                  .Append(SystemInfo.graphicsDeviceType).Append(", driver ").Append(SystemInfo.graphicsDeviceVersion).Append("\r\n");
                sb.Append("OS: ").Append(SystemInfo.operatingSystem).Append(", screen ").Append(Screen.width).Append('x').Append(Screen.height)
                  .Append(Screen.fullScreen ? " fullscreen" : " windowed").Append(", refresh ").Append(Screen.currentResolution.refreshRate).Append(" Hz\r\n");
                sb.Append("Quality: level ").Append(QualitySettings.GetQualityLevel()).Append(" (").Append(QualitySettings.names[QualitySettings.GetQualityLevel()])
                  .Append("), render scale ").Append(Gfx.CurrentScale.ToString("0.00")).Append(", vsync ").Append(QualitySettings.vSyncCount)
                  .Append(", target fps ").Append(Application.targetFrameRate).Append("\r\n");
                sb.Append("Game ").Append(Application.version).Append(", Unity ").Append(Application.unityVersion).Append(", DinoNet ").Append(Mod.Version)
                  .Append(", Steam transport ").Append(Prefs.SteamTransport).Append("\r\n");
            }
            catch (Exception e) { sb.Append("system info failed: ").Append(e.Message).Append("\r\n"); }
            return sb.ToString();
        }
        static string sysInfo = "";

        public static void Start()
        {
            if (started) return;
            started = true;
            try
            {
                dir = Path.Combine(Mod.Dir, "Recordings");
                Directory.CreateDirectory(dir);
                string sfx = Prefs.Instance > 1 ? "_" + Prefs.Instance : "";   // one set per running copy
                liveFile = Path.Combine(Mod.Dir, "flight_live" + sfx + ".txt");
                flagFile = Path.Combine(Mod.Dir, "running" + sfx + ".flag");
                // the previous run didn't quit normally -> keep its last 15 seconds
                if (File.Exists(flagFile) && File.Exists(liveFile))
                {
                    string dst = Path.Combine(dir, "flight_" + File.GetLastWriteTime(liveFile).ToString("yyyy-MM-dd_HH-mm-ss") + "_previous-run-did-not-quit-normally.txt");
                    try
                    {
                        File.Copy(liveFile, dst, true);
                        Log.Warn("flight recorder: the last run didn't quit normally - saved " + Path.GetFileName(dst));
                        ToDesktop(Path.GetFileName(dst), File.ReadAllText(liveFile), true);
                    }
                    catch { }
                    Prune();
                }
                File.WriteAllText(flagFile, DateTime.Now.ToString("o"));
                sysInfo = SystemInfoText();
                Log.Msg("system:\r\n" + sysInfo.TrimEnd());
            }
            catch (Exception e) { Log.Warn("flight recorder: " + e.Message); }
            Application.logMessageReceivedThreaded += OnUnityLog;
        }

        public static void OnQuit()
        {
            try { WriteLive(); } catch { }
            CopyLatestLog();
            try { if (flagFile != null) File.Delete(flagFile); } catch { }
        }

        public static void Add(string s)
        {
            var e = new E { T = Now, F = frame, S = s };
            lock (gate)
            {
                version++;
                buf.Enqueue(e);
                double cut = e.T - Keep;
                while (buf.Count > 0 && (buf.Peek().T < cut || buf.Count > MaxEntries)) buf.Dequeue();
            }
        }

        static void OnUnityLog(string msg, string stack, LogType type)
        {
            if (msg != null && msg.StartsWith("[DinoNet]")) return;
            if (type == LogType.Exception || type == LogType.Error || type == LogType.Assert)
            {
                Add("[unity " + type + "] " + msg + (string.IsNullOrEmpty(stack) ? "" : "\n    " + stack.Replace("\n", "\n    ")));
                if (type == LogType.Exception) Dump("exception");
            }
            else Add("[unity " + type + "] " + msg);
        }

        public static void Packet(bool outgoing, int peer, uint kind, int bytes)
        {
            if (kind == Msg.Cursor) return;   // 5/s per player, no diagnostic value
            Add((outgoing ? "[net out] to " : "[net in] from ") + peer + " kind " + KindName(kind) + " " + bytes + " B");
        }

        static string KindName(uint k)
        {
            switch (k)
            {
                case Msg.Toast: return "Toast"; case Msg.ToMenu: return "ToMenu"; case Msg.Choosing: return "Choosing";
                case Msg.SyncBegin: return "SyncBegin"; case Msg.SyncChunk: return "SyncChunk"; case Msg.SyncEnd: return "SyncEnd";
                case Msg.SyncLoaded: return "SyncLoaded"; case Msg.SyncGo: return "SyncGo"; case Msg.SyncHold: return "SyncHold";
                case Msg.SyncFailed: return "SyncFailed"; case Msg.ResyncRequest: return "ResyncRequest"; case Msg.SyncCancel: return "SyncCancel";
                case Msg.Cmd: return "Cmd"; case Msg.Time: return "Time"; case Msg.Res: return "Res"; case Msg.Kill: return "Kill";
                default: return k.ToString();
            }
        }

        /// Main thread, every frame.
        static double lastLogCopy;

        public static void Update()
        {
            frame = Mod.Frame;
            if (Net.InSession && Now - lastLogCopy >= 60.0)
            {
                lastLogCopy = Now;
                System.Threading.ThreadPool.QueueUserWorkItem(_ => CopyLatestLog());
            }
            double ft = Time.unscaledDeltaTime * 1000.0;
            frameSum += ft; if (ft > frameMax) frameMax = ft; frames++;
            double t = Now;
            if (t - lastSnap >= 1.0)
            {
                lastSnap = t;
                try { Add(Snapshot()); } catch (Exception e) { Add("[snap] failed: " + e.Message); }
                try { Add(Perf()); } catch (Exception e) { Add("[perf] failed: " + e.Message); }
                frameSum = frameMax = modSum = modMax = 0; frames = 0;
            }
            if (Input.GetKeyDown(KeyCode.F10)) { Dump("manual-F10", true); Ui.Toast(Prefs.ReportsOnDesktop ? "Flight recording saved to Desktop\\DinoNet Reports\\" + Prefs.DisplayName : "Flight recording saved to UserData\\DinoNet\\Recordings", 5f); }
        }

        /// Background (watchdog) thread, every second: refresh the rolling file every 2 s.
        public static void Tick()
        {
            double t = Now;
            if (t - lastLive >= 2.0) { lastLive = t; try { WriteLive(); } catch { } }
        }

        static string Perf()
        {
            var sb = new StringBuilder(256);
            int n = Math.Max(1, frames);
            sb.Append("[perf] fps ").Append((frames / Math.Max(0.001, frameSum / 1000.0)).ToString("0"))
              .Append(" frame avg ").Append((frameSum / n).ToString("0.0")).Append("ms worst ").Append(frameMax.ToString("0"))
              .Append("ms | DinoNet avg ").Append((modSum / n).ToString("0.00")).Append("ms worst ").Append(modMax.ToString("0.0")).Append("ms");
            try
            {
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                long cpu = proc.TotalProcessorTime.Ticks; double wall = Now;
                if (lastCpuWall > 0)
                {
                    double used = (cpu - lastCpuTicks) / 10000000.0 / Math.Max(0.001, wall - lastCpuWall);
                    sb.Append(" | game CPU ").Append((used * 100.0 / Math.Max(1, SystemInfo.processorCount)).ToString("0")).Append("% of PC (")
                      .Append(used.ToString("0.0")).Append(" cores)");
                }
                lastCpuTicks = cpu; lastCpuWall = wall;
                sb.Append(" | RAM ").Append(proc.WorkingSet64 / (1024 * 1024)).Append("MB");
            }
            catch { }
            try
            {
                sb.Append(" managed ").Append(GC.GetTotalMemory(false) / (1024 * 1024)).Append("MB");
                int g = GC.CollectionCount(0); sb.Append(" GCs ").Append(g - lastGc0); lastGc0 = g;
            }
            catch { }
            try
            {
                if (Net.S != null)
                {
                    var st = Net.S.Stats;
                    sb.Append(" | net up ").Append((((long)st.SentBytes - lastNetSent) / 1024.0).ToString("0.0")).Append("KB/s down ")
                      .Append((((long)st.RecvBytes - lastNetRecv) / 1024.0).ToString("0.0")).Append("KB/s");
                    lastNetSent = (long)st.SentBytes; lastNetRecv = (long)st.RecvBytes;
                }
                if (Net.Steam != null) sb.Append(" | steam").Append(Net.Steam.LinkStats());
            }
            catch { }
            return sb.ToString();
        }

        static string Snapshot()
        {
            var sb = new StringBuilder(256);
            sb.Append("[snap] ").Append(Net.InSession ? (Net.IsHost ? "host" : "friend") : "no session");
            if (Net.InSession) sb.Append(Net.IsSteam ? " steam" : " iroh").Append(" me=").Append(Net.MyId);
            sb.Append(" phase=").Append(Game.Phase).Append(" held=").Append(Game.Held);
            sb.Append(" running=").Append(Ecs.GameRunning).Append(" timeScale=").Append(Time.timeScale.ToString("0.##"));
            sb.Append(" fps=").Append((1f / Mathf.Max(0.0001f, Time.unscaledDeltaTime)).ToString("0"));
            try { if (Ecs.GameRunning) sb.Append(" gameTime=").Append(Ecs.ElapsedGameTime.ToString("0.0")).Append(" speed=").Append(Ecs.CurrentSpeed); } catch { }
            sb.Append(" sync=").Append(StartSync.Busy ? "busy" : "idle").Append(" world=").Append(WorldSync.Running);
            if (WorldSync.Running)
                sb.Append(" bound=").Append(WorldSync.Bound).Append(" unmatched=").Append(WorldSync.Unmatched)
                  .Append(" fix=").Append(WorldSync.Corrections).Append(" snap=").Append(WorldSync.Snaps).Append(" hp=").Append(WorldSync.HpFixes)
                  .Append(" phantom=").Append(WorldSync.Phantoms).Append(" made=").Append(WorldSync.Spawned).Append(" res=").Append(Res.Fixes)
                  .Append(" clock=x").Append(WorldSync.TimeNudge.ToString("0.00"));
            sb.Append(" cmds s/r/a/d=").Append(Commands.SentCount).Append('/').Append(Commands.RecvCount).Append('/').Append(Commands.AppliedCount).Append('/').Append(Commands.DroppedCount);
            foreach (var p in Net.Players)
                sb.Append(" | p").Append(p.Id).Append(' ').Append(p.Name).Append(p.Ready ? " ready" : "").Append(p.Away ? " away" : "").Append(p.Id == Net.MyId ? "" : " " + p.PingMs + "ms");
            return sb.ToString();
        }

        static long version, liveVersion = -1;

        static string Render(string reason, int maxItems = int.MaxValue)
        {
            E[] items;
            lock (gate)
            {
                items = buf.ToArray();
                if (items.Length > maxItems) { var tail = new E[maxItems]; Array.Copy(items, items.Length - maxItems, tail, 0, maxItems); items = tail; }
            }
            var sb = new StringBuilder(items.Length * 80 + 512);
            sb.Append("DinoNet ").Append(Mod.Version).Append(" flight recording - reason: ").Append(reason).Append("\r\n");
            sb.Append("saved ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(", last ").Append(Keep).Append(" seconds, ").Append(items.Length).Append(" entries, last step: ").Append(Watch.Step).Append("\r\n");
            sb.Append("player: ").Append(Prefs.DisplayName).Append(" (game folder ").Append(Path.GetDirectoryName(Path.GetDirectoryName(Mod.Dir))).Append(")\r\n");
            sb.Append(sysInfo);
            sb.Append("time is seconds before the save; f = frame\r\n\r\n");
            double end = Now;
            foreach (var e in items)
                sb.Append('-').Append((end - e.T).ToString("00.000")).Append(" f").Append(e.F).Append("  ").Append(e.S).Append("\r\n");
            return sb.ToString();
        }

        static void WriteLive()
        {
            if (liveFile == null) return;
            long v; lock (gate) v = version;
            if (v == liveVersion) return;          // nothing new since the last write
            liveVersion = v;
            string tmp = liveFile + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
            File.WriteAllText(tmp, Render("rolling file (newest 15 s)", 6000));
            try { File.Delete(liveFile); } catch { }
            File.Move(tmp, liveFile);
        }

        /// Save the buffer now. Safe from any thread. At most one automatic save per 30 s.
        public static void Dump(string reason, bool force = false)
        {
            try
            {
                if (dir == null) return;
                double t = Now;
                if (!force && t - lastDump < 30.0) return;
                lastDump = t;
                string name = "flight_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + Safe(Prefs.DisplayName) + "_" + Safe(reason) + ".txt";
                string text = Render(reason);
                File.WriteAllText(Path.Combine(dir, name), text);
                Prune();
                ToDesktop(name, text);
                Log.Msg("flight recorder: saved Recordings\\" + name);
            }
            catch { }
        }

        /// A copy for the player to find easily: Desktop\DinoNet Reports\ (recording + Unity's Player.log).
        /// Desktop\DinoNet Reports\<player name>\ - one folder per player, so several copies of the game
        /// on one PC (or logs collected from friends) never mix.
        public static string PlayerFolder()
        {
            string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desk)) return null;
            var sb = new StringBuilder();
            foreach (char c in Prefs.DisplayName) sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || c < ' ' ? '_' : c);
            string n = sb.ToString().Trim().TrimEnd('.');
            if (n.Length == 0) n = "Player";
            if (n.Length > 40) n = n.Substring(0, 40);
            return Path.Combine(Path.Combine(desk, "DinoNet Reports"), n);
        }

        /// Unity's own log for this run (%USERPROFILE%\AppData\LocalLow\<company>\<game>\Player.log).
        static string UnityLog { get { try { return Application.consoleLogPath; } catch { return null; } } }

        static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var rd = new StreamReader(fs)) return rd.ReadToEnd();
        }

        /// Keep Desktop\DinoNet Reports\<player>\Latest.log current (session end, quit, every minute in co-op).
        public static void CopyLatestLog()
        {
            if (!Prefs.ReportsOnDesktop) return;
            try
            {
                string d = PlayerFolder(); if (d == null) return;
                Directory.CreateDirectory(d);
                if (Log.OwnPath != null && File.Exists(Log.OwnPath)) File.WriteAllText(Path.Combine(d, "DinoNet.log"), ReadShared(Log.OwnPath));
                string log = UnityLog;
                if (string.IsNullOrEmpty(log) || !File.Exists(log)) return;
                File.WriteAllText(Path.Combine(d, "Player.log"), ReadShared(log));
            }
            catch { }
        }

        /// Unity's log of the run before this one (Unity keeps it as Player-prev.log next to Player.log).
        static string PreviousRunLog()
        {
            try
            {
                string cur = UnityLog; if (string.IsNullOrEmpty(cur)) return null;
                string prev = Path.Combine(Path.GetDirectoryName(cur), "Player-prev.log");
                return File.Exists(prev) ? prev : null;
            }
            catch { return null; }
        }

        static void ToDesktop(string name, string text, bool previousRun = false)
        {
            if (!Prefs.ReportsOnDesktop) return;
            try
            {
                string d = PlayerFolder(); if (d == null) return;
                Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, name), text);
                string log = previousRun ? PreviousRunLog() : UnityLog;
                if (!string.IsNullOrEmpty(log) && File.Exists(log))
                    File.WriteAllText(Path.Combine(d, name.Replace(".txt", "_Unity-log.txt")), ReadShared(log));
                if (!previousRun && Log.OwnPath != null && File.Exists(Log.OwnPath))
                    File.WriteAllText(Path.Combine(d, name.Replace(".txt", "_DinoNet-log.txt")), ReadShared(Log.OwnPath));
                var files = new List<FileInfo>(new DirectoryInfo(d).GetFiles("flight_*.txt"));
                files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = KeepFiles * 3; i < files.Count; i++) { try { files[i].Delete(); } catch { } }
            }
            catch { }
        }

        static string Safe(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '-');
            return sb.Length > 40 ? sb.ToString(0, 40) : sb.ToString();
        }

        static void Prune()
        {
            try
            {
                var files = new List<FileInfo>(new DirectoryInfo(dir).GetFiles("flight_*.txt"));
                files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = KeepFiles; i < files.Count; i++) { try { files[i].Delete(); } catch { } }
            }
            catch { }
        }
    }
}
