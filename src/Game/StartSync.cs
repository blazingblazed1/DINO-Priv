// Start / join / resync by save transfer.
//  1. The host freezes everyone and saves its game (the game's own SaveRequest).
//  2. The save (+ its .dat header) is gzipped and streamed to the friends.
//  3. Friends write it into their profile folder and load it (the game's own LoadRequest);
//     on the very first start the host reloads the same file too, so all PCs begin from
//     byte-identical state.
//  4. When everyone reports "loaded", the host sends Go: all PCs re-seed the random
//     generators with the same seed and un-freeze on the same message.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using NetKit;
using UnityEngine;

namespace DinoNet
{
    public static class StartSync
    {
        public const string SaveName = "DinoNet_coop";
        public enum Reason { Initial = 0, Join = 1, Resync = 2 }
        enum HostStep { Idle, WaitIdle, Saving, Sending, HostReloading, WaitLoaded, Matching }
        enum ClientStep { Idle, Receiving, Loading, WaitGo }

        // ------------------------------------------------------------- shared
        public static int SyncId;
        public static bool Busy => hostStep != HostStep.Idle || clientStep != ClientStep.Idle;
        public static string OverlayText = "";
        public static float Progress = -1f;

        static string ProfileDir => UI.Profiles.CurrentProfilePath;
        static string SavePath => ProfileDir + SaveName;

        static uint Crc(byte[] d)
        {
            uint h = 2166136261;
            for (int i = 0; i < d.Length; i++) { h ^= d[i]; h *= 16777619; }
            return h;
        }

        static byte[] Gzip(byte[] d)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionMode.Compress)) gz.Write(d, 0, d.Length);
                return ms.ToArray();
            }
        }

        static byte[] Gunzip(byte[] d)
        {
            using (var ms = new MemoryStream(d))
            using (var gz = new GZipStream(ms, CompressionMode.Decompress))
            using (var o = new MemoryStream())
            {
                var buf = new byte[65536]; int n;
                while ((n = gz.Read(buf, 0, buf.Length)) > 0) o.Write(buf, 0, n);
                return o.ToArray();
            }
        }

        // ------------------------------------------------------------- host
        static HostStep hostStep;
        static Reason reason;
        static List<int> targets = new List<int>();
        static HashSet<int> loaded = new HashSet<int>();
        static float stepT, saveRequestedT;
        static DateTime saveRequestedUtc;
        static bool sawSaveReq;
        static uint seed;
        static readonly Queue<int> pendingJoins = new Queue<int>();
        static bool pendingResync;

        public static void HostBegin(Reason r, List<int> who)
        {
            if (!Net.IsHost) return;
            if (hostStep != HostStep.Idle)
            {
                if (r == Reason.Join) foreach (var p in who) pendingJoins.Enqueue(p);
                else if (r == Reason.Resync) pendingResync = true;
                return;
            }
            if (!Ecs.GameRunning) { Log.Warn("sync: game not running"); return; }
            if (string.IsNullOrEmpty(UI.Profiles.ProfileName)) { Ui.Toast("No game profile - create one first"); return; }
            reason = r;
            targets = new List<int>(who);
            loaded.Clear();
            SyncId++;
            seed = (uint)UnityEngine.Random.Range(1, int.MaxValue);
            Game.Hold("sync", true);
            Net.ToAll(Msg.SyncHold, new Packer().UV((ulong)SyncId).Bool(true).ToArray());
            hostStep = HostStep.WaitIdle; stepT = Time.realtimeSinceStartup;
            OverlayText = r == Reason.Initial ? "Preparing the co-op game..." : r == Reason.Join ? "A friend is joining - sending the game..." : "Re-syncing everyone...";
            Log.Msg("sync " + SyncId + " (" + r + ") to [" + string.Join(",", targets.ConvertAll(x => x.ToString()).ToArray()) + "]");
        }

        public static void HostRequestResync()
        {
            var all = new List<int>();
            foreach (var p in Net.Players) if (p.Id != Net.MyId && !p.Away) all.Add(p.Id);
            if (all.Count == 0) { Ui.Toast("Nobody to re-sync"); return; }
            HostBegin(Reason.Resync, all);
        }

        static void HostUpdate()
        {
            if (!Net.IsHost)
            {
                if (hostStep != HostStep.Idle) { hostStep = HostStep.Idle; Game.Hold("sync", false); }
                return;
            }
            float now = Time.realtimeSinceStartup;
            switch (hostStep)
            {
                case HostStep.Idle:
                    if (pendingJoins.Count > 0 && Game.Phase == Game.PhaseInGame)
                    {
                        var list = new List<int>();
                        while (pendingJoins.Count > 0) { int p = pendingJoins.Dequeue(); if (!list.Contains(p)) list.Add(p); }
                        HostBegin(Reason.Join, list);
                    }
                    else if (pendingResync && Game.Phase == Game.PhaseInGame) { pendingResync = false; HostRequestResync(); }
                    break;
                case HostStep.WaitIdle:
                    // never two SaveRequests at once (the game only handles a single one) and never
                    // a save while a load is reading (they share one path builder)
                    if (!Ecs.SaveBusy && !Ecs.LoadBusy)
                    {
                        try { if (File.Exists(SavePath)) File.Delete(SavePath); if (File.Exists(SavePath + ".dat")) File.Delete(SavePath + ".dat"); } catch { }
                        Ecs.RequestSave(SaveName);
                        hostStep = HostStep.Saving; stepT = now; saveRequestedT = now; sawSaveReq = false;
                        saveRequestedUtc = DateTime.UtcNow.AddSeconds(-2);
                    }
                    else if (now - stepT > 60f) Abort("The game was busy saving/loading for too long");
                    break;
                case HostStep.Saving:
                    if (Ecs.SaveBusy) { sawSaveReq = true; }
                    bool done = !Ecs.SaveBusy && (sawSaveReq || now - saveRequestedT > 1f);
                    if (done && !sawSaveReq && !File.Exists(SavePath) && now - saveRequestedT > 3f) { Abort("The game refused to save (game over screen?)"); return; }
                    if (done && File.Exists(SavePath) && File.Exists(SavePath + ".dat") && File.GetLastWriteTimeUtc(SavePath) >= saveRequestedUtc)
                    {
                        try { SendSave(); }
                        catch (Exception e) { Log.Err("sync: send failed: " + e); Abort("Could not send the save: " + e.Message); return; }
                    }
                    else if (now - stepT > 60f) Abort("Saving the game timed out");
                    break;
                case HostStep.HostReloading:
                    if (Game.LoadFailed) { Game.LoadFailed = false; Abort("Reloading the co-op save failed"); return; }
                    if (Game.WorldReadyAfterLoad())
                    {
                        Log.Msg("sync: host reload done");
                        Net.S?.NotifyLoaded();
                        Game.Hold("sync", true);
                        hostStep = HostStep.WaitLoaded; stepT = now;
                        Game.NotifyWorldLoaded();
                    }
                    else if (now - stepT > 240f) Abort("Reloading the game took too long");
                    break;
                case HostStep.WaitLoaded:
                    {
                        // drop targets that left
                        targets.RemoveAll(p => !Net.Players.Exists(x => x.Id == p && !x.Away));
                        bool all = true;
                        foreach (var p in targets) if (!loaded.Contains(p)) all = false;
                        Progress = targets.Count == 0 ? 1f : (float)loaded.Count / targets.Count;
                        OverlayText = "Waiting for friends to load (" + loaded.Count + "/" + targets.Count + ")";
                        if (all || now - stepT > 240f)
                        {
                            if (!all) Ui.Toast("Some friends took too long to load - starting anyway", 6f);
                            BeginMatching();
                        }
                    }
                    break;
                case HostStep.Matching:
                    {
                        targets.RemoveAll(p => !Net.Players.Exists(x => x.Id == p && !x.Away));
                        int total = Math.Max(1, WorldSync.Bound);
                        float worst = 1f;
                        foreach (var p in targets)
                        {
                            if (!loaded.Contains(p)) continue;   // still loading: it gets GO later and matches then
                            int m; float r = matchReports.TryGetValue(p, out m) ? Math.Min(1f, (float)m / total) : 0f;
                            if (r < worst) worst = r;
                        }
                        if (targets.Count == 0) worst = 1f;
                        Progress = worst;
                        OverlayText = "Matching units with friends " + (int)(worst * 100) + "%";
                        // 95 %: a few objects can legitimately differ (a unit mid-death, a wave enemy)
                        if ((worst >= 0.95f && now - stepT > 1f) || now - stepT > 20f)
                        {
                            Log.Msg("sync " + SyncId + ": matching " + (int)(worst * 100) + "% of " + total + " objects after " + (now - stepT).ToString("0.0") + " s");
                            if (worst < 0.95f) Ui.Toast("Not every unit could be matched with a friend - starting anyway (F9 re-syncs)", 6f);
                            Go();
                        }
                    }
                    break;
            }
        }

        static void SendSave()
        {
            byte[] main = File.ReadAllBytes(SavePath);
            byte[] head = File.ReadAllBytes(SavePath + ".dat");
            byte[] packed = Gzip(new Packer().Bytes(main).Bytes(head).ToArray());
            uint crc = Crc(packed);
            Log.Msg("sync " + SyncId + ": save " + main.Length + " B + header " + head.Length + " B -> " + packed.Length + " B gzipped");
            foreach (int p in targets)
            {
                Net.ToPlayer(p, Msg.SyncBegin, new Packer().UV((ulong)SyncId).UV((ulong)packed.Length).U8((int)reason).ToArray());
                const int chunk = 16000;
                for (int off = 0; off < packed.Length; off += chunk)
                {
                    int n = Math.Min(chunk, packed.Length - off);
                    var part = new byte[n];
                    Buffer.BlockCopy(packed, off, part, 0, n);
                    Net.ToPlayer(p, Msg.SyncChunk, new Packer().UV((ulong)SyncId).UV((ulong)off).Bytes(part).ToArray());
                }
                Net.ToPlayer(p, Msg.SyncEnd, new Packer().UV((ulong)SyncId).UV(crc).ToArray());
            }
            if (reason == Reason.Initial)
            {
                // reload our own save so the host starts from the same bytes as everyone else
                Game.ExpectLoad();
                Ecs.RequestLoad(SaveName, false);
                Net.S?.NotifyLoading(300000);
                hostStep = HostStep.HostReloading;
                OverlayText = "Loading the co-op game...";
            }
            else
            {
                hostStep = HostStep.WaitLoaded;
            }
            stepT = Time.realtimeSinceStartup;
            Net.S?.SetPhase(Game.PhaseStarting, new Packer().UV((ulong)SyncId).ToArray());
        }

        static readonly Dictionary<int, int> matchReports = new Dictionary<int, int>();
        static bool matchStarted;

        /// Everyone is loaded and still frozen: share the host's objects now and let friends match
        /// them before anyone moves (so the first orders already name units every PC knows).
        static void BeginMatching()
        {
            foreach (int p in targets) { try { Net.S.MarkSynced(p); } catch { } }
            matchReports.Clear();
            WorldSync.HostStart(reason == Reason.Initial);
            matchStarted = true;
            foreach (int p in targets) Net.ToPlayer(p, Msg.SyncMatch, new Packer().UV((ulong)SyncId).ToArray());
            hostStep = HostStep.Matching; stepT = Time.realtimeSinceStartup;
            Log.Msg("sync " + SyncId + ": matching objects with " + targets.Count + " friend(s)");
        }

        static void Go()
        {
            Log.Msg("sync " + SyncId + ": GO (seed " + seed + ")");
            var speed = Ecs.CurrentSpeed;
            foreach (int p in targets) { try { Net.S.MarkSynced(p); } catch { } }
            Net.ToAll(Msg.SyncGo, new Packer().UV((ulong)SyncId).UV(seed).U8((int)speed).ToArray());
            hostStep = HostStep.Idle;
            OverlayText = ""; Progress = -1f;
            Ecs.Reseed(seed);
            bool first = reason == Reason.Initial;
            Game.Hold("sync", false);
            Game.Phase = Game.PhaseInGame;
            Net.S?.SetPhase(Game.PhaseInGame, new Packer().UV((ulong)SyncId).ToArray());
            if (!matchStarted) WorldSync.HostStart(first);
            matchStarted = false;
        }

        static void Abort(string why)
        {
            Flight.Add("[sync] sync-abort: " + why); Flight.Dump("sync-abort");
            Log.Warn("sync aborted: " + why);
            Ui.Toast(why, 6f);
            bool wasReloading = hostStep == HostStep.HostReloading;
            if (reason == Reason.Initial)
            {
                // back to "host is choosing": the host can start (or load) again and everyone follows
                Game.Phase = Game.PhaseChoosing;
                Net.S?.SetPhase(Game.PhaseChoosing);
                Game.WaitForNewWorld();   // retry only after a new start (or ~20 s)
                Net.S?.NotifyLoaded();
            }
            hostStep = HostStep.Idle; OverlayText = ""; Progress = -1f; matchStarted = false;
            Game.Hold("sync", false);
            Game.ClearExpectLoad();
            Net.ToAll(Msg.SyncHold, new Packer().UV((ulong)SyncId).Bool(false).ToArray());
            foreach (int p in targets) { try { Net.S.MarkSynced(p); } catch { } Net.ToPlayer(p, Msg.SyncCancel, new Packer().UV((ulong)SyncId).ToArray()); }
        }

        // ------------------------------------------------------------- client
        static ClientStep clientStep;
        static byte[] rxBuf;
        static int rxGot, rxSyncId;
        static float clientT;

        static void ClientUpdate()
        {
            if (clientStep == ClientStep.Idle) return;
            if (!Net.InSession) { clientStep = ClientStep.Idle; OverlayText = ""; Progress = -1f; matchingId = -1; Game.Hold("sync", false); return; }
            float now = Time.realtimeSinceStartup;
            switch (clientStep)
            {
                case ClientStep.Receiving:
                    Progress = rxBuf == null ? 0f : (float)rxGot / Math.Max(1, rxBuf.Length);
                    OverlayText = "Receiving the host's game " + (int)(Progress * 100) + "%";
                    break;
                case ClientStep.Loading:
                    OverlayText = "Loading the co-op game...";
                    Progress = -1f;
                    if (Game.LoadFailed)
                    {
                        Game.LoadFailed = false;
                        ClientFail("load failed");
                        return;
                    }
                    if (Game.WorldReadyAfterLoad())
                    {
                        Log.Msg("sync " + rxSyncId + ": loaded");
                        clientStep = ClientStep.WaitGo; clientT = now;
                        Game.Hold("sync", true);
                        Game.NotifyWorldLoaded();
                        Net.S?.NotifyLoaded();
                        Net.ToHost(Msg.SyncLoaded, new Packer().UV((ulong)rxSyncId).ToArray());
                        if (pendingGoId == rxSyncId) { ClientGo(rxSyncId, pendingGoSeed); pendingGoId = -1; }
                    }
                    else if (now - clientT > 300f)
                    {
                        Game.ClearExpectLoad();
                        Ui.Toast("Loading the host's game failed", 6f);
                        ClientFail("load timeout");
                    }
                    break;
                case ClientStep.WaitGo:
                    OverlayText = matchingId == rxSyncId ? "Matching units with the host... " + WorldSync.Bound + " / " + WorldSync.HostObjects : "Waiting for the other players...";
                    if (matchingId == rxSyncId && now >= reportT)
                    {
                        reportT = now + 0.5f;
                        Net.ToHost(Msg.MatchReport, new Packer().UV((ulong)rxSyncId).UV((ulong)WorldSync.Bound).UV((ulong)WorldSync.HostObjects).ToArray());
                    }
                    if (now - clientT > 300f)
                    {
                        Log.Warn("sync " + rxSyncId + ": no Go from the host - asking for a re-sync");
                        clientStep = ClientStep.Idle; OverlayText = ""; Game.Hold("sync", false);
                        Net.ToHost(Msg.ResyncRequest, new byte[0]);
                    }
                    break;
            }
        }

        public static void OnMessage(int from, uint kind, byte[] d)
        {
            var u = new Unpacker(d);
            switch (kind)
            {
                case Msg.SyncHold:
                    {
                        if (Net.IsHost) return;
                        u.UV(); bool on = u.Bool();
                        if (Ecs.GameRunning) Game.Hold("sync", on);
                        if (on && clientStep == ClientStep.Idle) OverlayText = "The host is syncing a friend - please wait...";
                        if (!on && clientStep == ClientStep.Idle) OverlayText = "";
                    }
                    break;
                case Msg.SyncBegin:
                    {
                        rxSyncId = (int)u.UV(); int size = (int)u.UV(); var r = (Reason)u.U8();
                        matchingId = -1;
                        rxBuf = new byte[size]; rxGot = 0;
                        clientStep = ClientStep.Receiving; clientT = Time.realtimeSinceStartup;
                        pendingGoId = -1;
                        Game.Hold("sync", true);   // applied as soon as a world runs
                        Log.Msg("sync " + rxSyncId + " (" + r + "): receiving " + size + " B");
                    }
                    break;
                case Msg.SyncChunk:
                    {
                        int id = (int)u.UV(); int off = (int)u.UV(); var part = u.Bytes();
                        if (id != rxSyncId || rxBuf == null || off + part.Length > rxBuf.Length) return;
                        Buffer.BlockCopy(part, 0, rxBuf, off, part.Length);
                        rxGot += part.Length;
                    }
                    break;
                case Msg.SyncEnd:
                    {
                        int id = (int)u.UV(); uint crc = (uint)u.UV();
                        if (id != rxSyncId || rxBuf == null) return;
                        if (Crc(rxBuf) != crc || rxGot != rxBuf.Length)
                        {
                            Log.Err("sync " + id + ": bad data (" + rxGot + "/" + rxBuf.Length + ")");
                            Net.ToHost(Msg.SyncFailed, new Packer().UV((ulong)id).Str("bad data").ToArray());
                            ClientFail(null);
                            return;
                        }
                        try
                        {
                            if (string.IsNullOrEmpty(UI.Profiles.ProfileName))
                            {
                                Ui.Toast("You need a game profile first: click New Game once in the vanilla menu", 8f);
                                Net.ToHost(Msg.SyncFailed, new Packer().UV((ulong)id).Str("no profile").ToArray());
                                ClientFail(null);
                                return;
                            }
                            var inner = new Unpacker(Gunzip(rxBuf));
                            byte[] main = inner.Bytes(), head = inner.Bytes();
                            Directory.CreateDirectory(ProfileDir);
                            File.WriteAllBytes(SavePath, main);
                            File.WriteAllBytes(SavePath + ".dat", head);
                            Log.Msg("sync " + id + ": wrote " + main.Length + " B save, loading");
                        }
                        catch (Exception e)
                        {
                            Log.Err("sync: write failed: " + e);
                            Net.ToHost(Msg.SyncFailed, new Packer().UV((ulong)id).Str("write failed").ToArray());
                            ClientFail(null);
                            return;
                        }
                        rxBuf = null;
                        WorldSync.ClientReset();
                        bool inGame = Ecs.GameRunning;
                        Game.ExpectLoad();
                        Game.AllowNextLoad = true;
                        Ecs.RequestLoad(SaveName, !inGame);
                        Net.S?.NotifyLoading(300000);
                        clientStep = ClientStep.Loading; clientT = Time.realtimeSinceStartup;
                    }
                    break;
                case Msg.SyncLoaded:
                    if (Net.IsHost && (int)u.UV() == SyncId) { loaded.Add(from); Log.Msg("sync " + SyncId + ": player " + from + " loaded"); }
                    break;
                case Msg.SyncFailed:
                    if (Net.IsHost)
                    {
                        u.UV(); string why = u.Str();
                        Ui.Toast(Net.NameOf(from) + " could not load the game (" + why + ")", 6f);
                        targets.Remove(from);
                        try { Net.S.MarkSynced(from); } catch { }
                    }
                    break;
                case Msg.SyncMatch:
                    {
                        if (Net.IsHost) return;
                        int id = (int)u.UV();
                        if (clientStep == ClientStep.WaitGo && id == rxSyncId)
                        {
                            matchingId = id;
                            WorldSync.ClientStart();
                            Log.Msg("sync " + id + ": matching objects with the host");
                        }
                    }
                    break;
                case Msg.MatchReport:
                    if (Net.IsHost && (int)u.UV() == SyncId) matchReports[from] = (int)u.UV();
                    break;
                case Msg.SyncGo:
                    {
                        if (Net.IsHost) return;
                        int id = (int)u.UV(); uint sd = (uint)u.UV();
                        if (clientStep == ClientStep.Receiving || clientStep == ClientStep.Loading)
                        {
                            if (id == rxSyncId) { pendingGoId = id; pendingGoSeed = sd; Log.Msg("sync " + id + ": GO arrived early - applied after loading"); }
                            return;
                        }
                        ClientGo(id, sd);
                    }
                    break;
                case Msg.SyncCancel:
                    if (Net.IsHost) return;
                    Log.Warn("sync " + (int)u.UV() + ": cancelled by the host");
                    clientStep = ClientStep.Idle; OverlayText = ""; Progress = -1f; rxBuf = null; pendingGoId = -1; matchingId = -1;
                    Game.ClearExpectLoad();
                    Game.Hold("sync", false);
                    break;
                case Msg.ResyncRequest:
                    if (Net.IsHost) { Ui.Toast(Net.NameOf(from) + " asked for a re-sync"); HostBegin(Reason.Resync, new List<int> { from }); }
                    break;
            }
        }

        /// Give up on this sync on our side: unfreeze, tell the host (it marks us synced again).
        static void ClientFail(string why)
        {
            Flight.Add("[sync] sync-failed-friend: " + why); Flight.Dump("sync-failed-friend");
            if (why != null) Net.ToHost(Msg.SyncFailed, new Packer().UV((ulong)rxSyncId).Str(why).ToArray());
            clientStep = ClientStep.Idle; OverlayText = ""; Progress = -1f; rxBuf = null; pendingGoId = -1; matchingId = -1;
            Game.ClearExpectLoad();
            Game.Hold("sync", false);
            Net.S?.NotifyLoaded();
        }

        static int pendingGoId = -1, matchingId = -1;
        static float reportT;
        static uint pendingGoSeed;

        static void ClientGo(int id, uint sd)
        {
            bool mine = clientStep == ClientStep.WaitGo && id == rxSyncId;
            Log.Msg("sync " + id + ": GO" + (mine ? "" : " (others)"));
            clientStep = ClientStep.Idle;
            OverlayText = ""; Progress = -1f;
            Ecs.Reseed(sd);
            Game.Hold("sync", false);
            if (mine && !(matchingId == id && WorldSync.Running)) { WorldSync.ClientStart(); DesyncCheck.Reset(); }
            else if (mine) { WorldSync.OnUnfreeze(); DesyncCheck.Reset(); }
            if (mine && matchingId == id) Log.Msg("sync " + id + ": matched " + WorldSync.Bound + " of " + WorldSync.HostObjects + " host objects before starting");
            matchingId = -1;
        }

        public static void Update()
        {
            HostUpdate();
            ClientUpdate();
        }

        public static void Reset()
        {
            hostStep = HostStep.Idle; clientStep = ClientStep.Idle;
            OverlayText = ""; Progress = -1f; rxBuf = null;
            pendingJoins.Clear(); pendingResync = false; pendingGoId = -1; matchingId = -1; matchStarted = false;
            Game.Hold("sync", false);
            Game.ClearExpectLoad();
        }
    }
}
