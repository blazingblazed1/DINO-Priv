// Session management: one NetKit session (Iroh join codes or Steam lobby) and
// message routing. Host-authoritative hub: friends talk only to the host; a
// friend's message to "others" is relayed by the host (NetKit does that).
using System;
using System.Collections.Generic;
using NetKit;
using UnityEngine;
using EventType = NetKit.EventType;

namespace DinoNet
{
    public static class Msg
    {
        // lobby / flow
        public const uint Toast = 1;          // any -> all: text
        public const uint ToMenu = 2;         // host -> all: host returned to the main menu
        public const uint Choosing = 3;       // host -> all: host is picking a game
        // start / resync (save transfer)
        public const uint SyncBegin = 10;     // host -> target(s): syncId, size, reason
        public const uint SyncChunk = 11;     // host -> target(s): syncId, offset, bytes
        public const uint SyncEnd = 12;       // host -> target(s): syncId, crc, seed
        public const uint SyncLoaded = 13;    // client -> host: syncId
        public const uint SyncGo = 14;        // host -> all: syncId, speed
        public const uint SyncHold = 15;      // host -> all: freeze while someone loads
        public const uint SyncFailed = 16;    // client -> host: syncId, reason
        public const uint ResyncRequest = 17; // client -> host: please resync me
        public const uint SyncCancel = 18;    // host -> target: sync aborted
        // gameplay
        public const uint Cmd = 20;           // any -> others: a player's command
        public const uint Time = 21;          // host -> all: host clock + speed
        public const uint Res = 22;           // host -> all: resources
        public const uint Cursor = 23;        // any -> others: where I am pointing
        public const uint Kill = 24;          // host -> all: netId died/destroyed
        public const uint SyncMatch = 19;     // host -> target(s): syncId - start matching objects while still frozen
        public const uint MatchReport = 26;   // client -> host: syncId, matched, known
        public const uint MapPing = 27;       // any -> all: x, z (middle-click ping)
        public const uint Check = 28;         // host -> all: desync check summary
        public const uint Probe = 25;         // client -> host: unbound host ids I could not match (debug)
    }

    public static class Net
    {
        public static NetSession S;
        public static SteamLink Steam;
        public static bool IsSteam;
        public static string Status = "";
        public static string JoinCode = "";
        public static bool Connecting => S != null && S.State == 0;
        public static bool Active => S != null && S.Active;
        public static bool InSession => S != null && S.State != 2;
        public static bool IsHost => S != null && S.IsHost;
        public static bool IsClient => S != null && S.State != 2 && !S.IsHost;
        public static int MyId => S == null ? -1 : S.MyId;
        public static List<PlayerInfo> Players = new List<PlayerInfo>();
        public static int Version;          // bumps when the roster changes (UI refresh)
        static float rosterT;
        static float connectStart;

        public static readonly Color[] Colors =
        {
            new Color(0.96f, 0.90f, 0.75f), // P1 cream (host)
            new Color(0.45f, 0.85f, 0.40f), // P2 green
            new Color(1.00f, 0.60f, 0.25f), // P3 orange
            new Color(1.00f, 0.50f, 0.75f), // P4 pink
            new Color(1.00f, 0.90f, 0.30f), // P5 yellow
            new Color(0.40f, 0.70f, 1.00f), // P6 blue
            new Color(0.75f, 0.55f, 1.00f), // P7 purple
            new Color(0.55f, 0.95f, 0.95f), // P8 cyan
        };
        public static Color ColorOf(int id) => Colors[((id % Colors.Length) + Colors.Length) % Colors.Length];
        public static string NameOf(int id)
        {
            foreach (var p in Players) if (p.Id == id) return string.IsNullOrEmpty(p.Name) ? "Player " + (id + 1) : p.Name;
            return "Player " + (id + 1);
        }

        static NetOptions Options()
        {
            var o = new NetOptions
            {
                GameId = Mod.GameKey,
                AppVersion = Mod.Version,
                Name = Prefs.DisplayName,
                Pid = Prefs.SessionPid,
                MaxPlayers = Prefs.MaxPlayers,
                Internet = !Prefs.LanOnly,
                StrictVersion = true,
                JoinInProgress = true,
                RejoinAttempts = 5,
                HostUploadBps = (Prefs.HostUploadKbps > 0 ? Prefs.HostUploadKbps : 2400) * 125,   // default 300 KB/s shared by all friends
            };
            o.Ints["keep_slot_ms"] = 120000;
            return o;
        }

        static bool CheckLib()
        {
            if (Mod.NetKitOk) return true;
            Status = "netkit.dll missing - reinstall DinoNet (" + Mod.NetKitError + ")";
            Ui.Toast(Status);
            return false;
        }

        public static bool HostIroh()
        {
            if (!CheckLib()) return false;
            Leave("new session");
            try
            {
                S = NetSession.HostIroh(Options());
                IsSteam = false; Status = "Starting..."; JoinCode = ""; connectStart = UnityEngine.Time.realtimeSinceStartup;
                Log.Msg("hosting (Iroh, lan_only=" + Prefs.LanOnly + ")");
                return true;
            }
            catch (Exception e) { Fail("Could not host: " + e.Message); return false; }
        }

        public static bool JoinIroh(string code)
        {
            if (!CheckLib()) return false;
            code = (code ?? "").Trim();
            if (!NetKitLib.IsJoinCode(code)) { Ui.Toast("That is not a DinoNet join code"); return false; }
            Leave("new session");
            try
            {
                S = NetSession.JoinIroh(code, Options());
                IsSteam = false; Status = "Connecting..."; JoinCode = code; connectStart = UnityEngine.Time.realtimeSinceStartup;
                Log.Msg("joining (Iroh)");
                return true;
            }
            catch (Exception e) { Fail("Could not join: " + e.Message); return false; }
        }

        public static bool HostSteam(bool isPublic)
        {
            if (!CheckLib()) return false;
            if (!SteamLink.Available) { Ui.Toast("Steam isn't running"); return false; }
            Leave("new session");
            try
            {
                S = NetSession.HostExternal("steam", Options());
                Steam = new SteamLink(S);
                IsSteam = true; Status = "Creating Steam lobby..."; connectStart = UnityEngine.Time.realtimeSinceStartup;
                if (!Steam.Host(isPublic, Prefs.MaxPlayers, Prefs.DisplayName + "'s game")) { Fail(Steam.Status); return false; }
                return true;
            }
            catch (Exception e) { Fail("Could not host on Steam: " + e.Message); return false; }
        }

        public static bool JoinSteam(ulong lobby)
        {
            if (!CheckLib()) return false;
            if (!SteamLink.Available) { Ui.Toast("Steam isn't running"); return false; }
            Leave("new session");
            try
            {
                S = NetSession.JoinExternal("steam", Options());
                Steam = new SteamLink(S);
                IsSteam = true; Status = "Joining Steam lobby..."; connectStart = UnityEngine.Time.realtimeSinceStartup;
                if (!Steam.Join(lobby)) { Fail(Steam.Status); return false; }
                return true;
            }
            catch (Exception e) { Fail("Could not join on Steam: " + e.Message); return false; }
        }

        static void Fail(string why)
        {
            Status = why; Log.Warn(why); Ui.Toast(why); Flight.Dump("network-failure");
            Leave("failed");
        }

        public static void Leave(string reason)
        {
            if (S == null && Steam == null) return;
            Log.Msg("leaving session: " + reason);
            try { Game.OnSessionEnded(); } catch (Exception e) { Log.Err("OnSessionEnded: " + e); }
            System.Threading.ThreadPool.QueueUserWorkItem(_ => Flight.CopyLatestLog());
            try { Steam?.Leave(); } catch { }
            try { S?.Dispose(); } catch { }
            S = null; Steam = null; Players.Clear(); rosterSig = ""; Version++;
            JoinCode = "";
        }

        public static void Update()
        {
            if (SteamLink.Available) SteamLink.EnsureCallbacks();
            if (SteamLink.PendingJoinLobby != 0 && Mod.NetKitOk)
            {
                ulong l = SteamLink.PendingJoinLobby; SteamLink.PendingJoinLobby = 0;
                if (!(Steam != null && Steam.Lobby == l)) { Ui.Toast("Joining your friend's game..."); JoinSteam(l); Ui.ShowLobby(); }
            }
            if (S == null) return;
            Application.runInBackground = true;
            Steam?.Pump();
            // object classes must exist before the first spawn arrives: NetKit drops spawns of unknown
            // classes and the host never resends them (0.0.4.0: 64 objects missing at start, all after a rejoin)
            try { WorldSync.EnsureClasses(); } catch (Exception e) { Log.Throttled("classes", "classes: " + e.Message); }
            S.Update();
            NetEvent ev;
            int guard = 0;
            while (S != null && guard++ < 5000 && S.Poll(out ev))
            {
                try { Handle(ev); } catch (Exception e) { Log.Throttled("ev" + (int)ev.Type, "event " + ev.Type + ": " + e); }
            }
            if (S == null) return;
            Steam?.Pump();
            if (UnityEngine.Time.realtimeSinceStartup >= rosterT)
            {
                rosterT = UnityEngine.Time.realtimeSinceStartup + 0.5f;
                RefreshRoster();
            }
            if (S != null && S.State == 0 && !IsSteam && !IsHost && UnityEngine.Time.realtimeSinceStartup - connectStart > 25f)
                Fail("Couldn't reach the host (check the code, or try Steam)");
        }

        static string rosterSig = "";
        public static void RefreshRoster()
        {
            if (S == null) return;
            try
            {
                var list = S.Players();
                var sb = new System.Text.StringBuilder();
                foreach (var p in list) sb.Append(p.Id).Append('|').Append(p.Name).Append('|').Append(p.Ready).Append('|').Append(p.IsHost).Append('|').Append(p.Away).Append('|').Append(p.Path).Append(';');
                string sig = sb.ToString();
                Players = list;
                if (sig != rosterSig) { rosterSig = sig; Version++; }
            }
            catch { }
        }

        static void Handle(NetEvent ev)
        {
            switch (ev.Type)
            {
                case EventType.Hosting:
                    JoinCode = ev.Text; Status = IsSteam ? "Steam lobby ready" : "Hosting";
                    Log.Msg("hosting; code " + (string.IsNullOrEmpty(JoinCode) ? "(steam)" : JoinCode.Substring(0, Math.Min(12, JoinCode.Length)) + "..."));
                    RefreshRoster(); Ui.Dirty();
                    break;
                case EventType.Joined:
                    Status = "Connected"; Log.Msg("joined as player " + ev.A + (ev.B != 0 ? " (mid-game)" : ""));
                    RefreshRoster(); Ui.Dirty(); Ui.Toast("Connected to the host");
                    Game.OnJoined(ev.B != 0);
                    break;
                case EventType.PlayerJoined:
                    RefreshRoster(); Ui.Dirty();
                    Ui.Toast(NameOf(ev.A) + (ev.B != 0 ? " is back" : " joined"));
                    Log.Msg("player " + ev.A + " joined (rejoin=" + ev.B + " midgame=" + ev.C + ")");
                    Game.OnPlayerJoined(ev.A, ev.B != 0);
                    break;
                case EventType.PlayerLeft:
                    Ui.Toast(NameOf(ev.A) + " left" + (ev.Data != null && ev.Data.Length > 0 ? " (" + ev.Text + ")" : ""));
                    Log.Msg("player " + ev.A + " left: " + ev.Text);
                    Game.OnPlayerLeft(ev.A);
                    RefreshRoster(); Ui.Dirty();
                    break;
                case EventType.PlayerAway:
                    Ui.Toast(NameOf(ev.A) + " lost connection - waiting for them to come back");
                    Game.OnPlayerLeft(ev.A);
                    RefreshRoster(); Ui.Dirty();
                    break;
                case EventType.Roster:
                    RefreshRoster(); Ui.Dirty();
                    break;
                case EventType.Phase:
                    Game.OnPhase(ev.A, ev.Data);
                    Ui.Dirty();
                    break;
                case EventType.HostSilent:
                    if (ev.A > 4000) Ui.Toast("The host is not answering...", 2f);
                    break;
                case EventType.HostBack:
                    Ui.Toast("The host is back");
                    break;
                case EventType.Rejoining:
                    Ui.Toast("Connection lost - reconnecting (" + ev.A + "/" + ev.B + ")");
                    break;
                case EventType.Ended:
                    {
                        string why = ev.Text;
                        string msg;
                        switch (ev.EndKind)
                        {
                            case EndKind.ConnectFailed: msg = "Couldn't reach the host" + (why.Length > 0 ? ": " + why : ""); break;
                            case EndKind.Rejected: msg = "The host refused: " + why; break;
                            case EndKind.Kicked: msg = "You were kicked" + (why.Length > 0 ? ": " + why : ""); break;
                            case EndKind.HostClosed: msg = "The host closed the game"; break;
                            case EndKind.Lost: msg = "Connection lost"; break;
                            default: msg = "Session ended" + (why.Length > 0 ? ": " + why : ""); break;
                        }
                        Log.Msg("session ended: " + ev.EndKind + " " + why);
                        if (ev.EndKind != EndKind.Kicked) Flight.Dump("session-ended-" + ev.EndKind);
                        Ui.Toast(msg, 6f);
                        Status = msg;
                        bool wasClient = !IsHost;
                        Leave("ended");
                        if (wasClient) Game.ClientLostSession();
                        Ui.Dirty();
                    }
                    break;
                case EventType.Log:
                    Log.Dbg("netkit: " + ev.Text);
                    break;
                case EventType.Message:
                    Route(ev.A, ev.Kind & 0x7FFF, ev.Data ?? new byte[0], ev.B != 0);
                    break;
                case EventType.Spawned:
                    WorldSync.OnSpawned(ev.Id, ev.Kind);
                    break;
                case EventType.Despawned:
                    WorldSync.OnDespawned(ev.Id, ev.A);
                    break;
            }
        }

        static void Route(int from, uint kind, byte[] d, bool reliable)
        {
            Flight.Packet(false, from, kind, d.Length);
            switch (kind)
            {
                case Msg.Toast: Ui.Toast(new Unpacker(d).Str(), 4f); break;
                case Msg.ToMenu: Game.OnHostToMenu(); break;
                case Msg.Choosing: Game.OnHostChoosing(); break;
                case Msg.SyncBegin:
                case Msg.SyncChunk:
                case Msg.SyncEnd:
                case Msg.SyncLoaded:
                case Msg.SyncGo:
                case Msg.SyncHold:
                case Msg.SyncFailed:
                case Msg.ResyncRequest:
                case Msg.SyncCancel:
                    StartSync.OnMessage(from, kind, d); break;
                case Msg.Cmd: Commands.OnMessage(from, d); break;
                case Msg.Time:
                case Msg.Res:
                case Msg.Kill:
                    WorldSync.OnMessage(from, kind, d); break;
                case Msg.Cursor: Hud.OnCursor(from, d); break;
                case Msg.MapPing: Pings.OnMessage(from, d); break;
                case Msg.Check: DesyncCheck.OnMessage(from, d); break;
                case Msg.SyncMatch:
                case Msg.MatchReport:
                    StartSync.OnMessage(from, kind, d); break;
            }
        }

        /// Every friend is ready (the host starting counts as ready).
        public static bool AllReadyOrAlone()
        {
            foreach (var p in Players) if (p.Id != MyId && !p.Ready && !p.Away) return false;
            return true;
        }

        // ------------------------------------------------------------------ send helpers
        public static void ToAll(uint kind, byte[] d, bool reliable = true) { if (Active) { Flight.Packet(true, -1, kind, d.Length); S.SendToOthers(kind, d, reliable); } }
        public static void ToHost(uint kind, byte[] d, bool reliable = true) { if (Active) { Flight.Packet(true, 0, kind, d.Length); S.SendToHost(kind | NetSession.HostOnly, d, reliable); } }
        public static void ToPlayer(int id, uint kind, byte[] d, bool reliable = true) { if (Active) { Flight.Packet(true, id, kind, d.Length); S.Send(id, kind, d, reliable); } }
        public static void Say(string text) { if (Active) { Flight.Packet(true, -1, Msg.Toast, 0); S.SendToOthers(Msg.Toast, new Packer().Str(text).ToArray()); } }
    }
}
