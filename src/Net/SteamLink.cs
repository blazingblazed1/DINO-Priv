// Steam lobbies + Steam P2P for NetKit through the game's own Steamworks.NET.
// DINO ships Steamworks.NET (manual dispatch) and calls SteamAPI.RunCallbacks
// itself, so lobby results MUST come through Steamworks.NET CallResults (the flat
// IsAPICallCompleted polling loses them - the WanderNet 0.7.4/0.7.5 failure).
// Membership is polled every 0.5 s (callbacks can be missed); the NetKit session
// resends Hello, so a first packet Steam dropped is not fatal.
using System;
using System.Collections.Generic;
using NetKit;
using Steamworks;

namespace DinoNet
{
    public class LobbyInfo
    {
        public ulong Id;
        public string Name = "", Host = "", Version = "", State = "", Vis = "";
        public int Members, Max;
        public bool Friend;
    }

    public sealed class SteamLink
    {
        static bool availCache;
        static float availT = -10f;
        /// Steam initialised by the game (checked at most every 2 s until it is).
        public static bool Available
        {
            get
            {
                if (availCache) return true;
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (now - availT < 2f) return false;
                availT = now;
                try { availCache = SteamAPI.IsSteamRunning() && SteamUser.GetSteamID().m_SteamID != 0; }
                catch { availCache = false; }
                return availCache;
            }
        }

        public static string PersonaName()
        {
            try { return Available ? SteamFriends.GetPersonaName() : ""; } catch { return ""; }
        }

        public static ulong Me { get { try { return SteamUser.GetSteamID().m_SteamID; } catch { return 0; } } }

        // ------------------------------------------------------------------ global callbacks
        static Callback<GameLobbyJoinRequested_t> cbJoinReq;
        static Callback<GameRichPresenceJoinRequested_t> cbRpJoin;
        static Callback<P2PSessionRequest_t> cbP2P;
        static Callback<SteamNetworkingMessagesSessionRequest_t> cbSnsReq;
        static Callback<SteamNetworkingMessagesSessionFailed_t> cbSnsFail;
        /// "messages" = ISteamNetworkingMessages (Valve's current P2P: congestion control, proper
        /// reliable/unreliable lanes, live stats); "p2p" = the old ISteamNetworking API (0.0.3.4 and earlier).
        public static bool PreferMessages => Prefs.SteamTransport != "p2p";
        /// This session's transport: the host's choice (published as lobby data "tr"), so both ends always match.
        public bool UseMessages = PreferMessages;
        public static ulong PendingJoinLobby;   // friend clicked "Join Game" (handled by Ui/Net)

        public static void EnsureCallbacks()
        {
            if (cbJoinReq != null || !Available) return;
            try
            {
                cbJoinReq = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
                cbRpJoin = Callback<GameRichPresenceJoinRequested_t>.Create(OnRichJoin);
                cbP2P = Callback<P2PSessionRequest_t>.Create(OnP2PRequest);
                SteamNetworking.AllowP2PPacketRelay(true);
                try
                {
                    cbSnsReq = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSnsRequest);
                    cbSnsFail = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnSnsFailed);
                    SteamNetworkingUtils.InitRelayNetworkAccess();
                    SetGlobalInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, 2 * 1024 * 1024);
                }
                catch (Exception e) { Log.Warn("Steam networking messages unavailable: " + e.Message); }
                ulong cl = CommandLineLobby();
                if (cl != 0) { PendingJoinLobby = cl; Log.Msg("Steam: +connect_lobby " + cl + " on the command line"); }
                Log.Msg("Steam: callbacks registered (" + PersonaName() + ")");
                Log.Msg("player name: " + Prefs.DisplayName);   // the log header is written before Steam is up
            }
            catch (Exception e) { Log.Warn("Steam: callbacks failed: " + e.Message); }
        }

        static void OnJoinRequested(GameLobbyJoinRequested_t p)
        {
            Log.Msg("Steam: join requested for lobby " + p.m_steamIDLobby.m_SteamID + " (friend " + p.m_steamIDFriend.m_SteamID + ")");
            PendingJoinLobby = p.m_steamIDLobby.m_SteamID;
        }

        static void OnRichJoin(GameRichPresenceJoinRequested_t p)
        {
            string c = p.m_rgchConnect ?? "";
            Log.Msg("Steam: rich presence join: " + c);
            int i = c.IndexOf("+connect_lobby", StringComparison.Ordinal);
            if (i >= 0)
            {
                ulong id;
                if (ulong.TryParse(c.Substring(i + 14).Trim().Split(' ')[0], out id)) PendingJoinLobby = id;
            }
        }

        static void OnP2PRequest(P2PSessionRequest_t p)
        {
            // Accept from anyone in our lobby; the session itself validates (version/password).
            var cur = Net.Steam;
            ulong id = p.m_steamIDRemote.m_SteamID;
            if (cur != null && cur.Lobby != 0 && cur.IsMember(id))
            {
                SteamNetworking.AcceptP2PSessionWithUser(p.m_steamIDRemote);
                Log.Dbg("Steam: accepted P2P from " + id);
            }
            else Log.Dbg("Steam: P2P request from non-member " + id + " ignored");
        }

        static void SetGlobalInt(ESteamNetworkingConfigValue k, int v)
        {
            IntPtr mem = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
            try
            {
                System.Runtime.InteropServices.Marshal.WriteInt32(mem, v);
                SteamNetworkingUtils.SetConfigValue(k, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, mem);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(mem); }
        }

        static void OnSnsRequest(SteamNetworkingMessagesSessionRequest_t p)
        {
            var who = p.m_identityRemote;
            ulong id = who.GetSteamID64();
            var cur = Net.Steam;
            if (cur != null && cur.Lobby != 0 && cur.IsMember(id))
            {
                SteamNetworkingMessages.AcceptSessionWithUser(ref who);
                if (!cur.UseMessages) Log.Warn("Steam: " + id + " uses the new Steam networking but this session uses the old one");
                Log.Msg("Steam: accepted connection from " + id);
            }
            else Log.Msg("Steam: connection request from non-member " + id + " ignored");
        }

        static void OnSnsFailed(SteamNetworkingMessagesSessionFailed_t p)
        {
            try { Log.Warn("Steam: connection to " + p.m_info.m_identityRemote.GetSteamID64() + " failed (" + p.m_info.m_eEndReason + " " + p.m_info.m_szEndDebug + ")"); }
            catch { Log.Warn("Steam: a connection failed"); }
        }

        public static ulong CommandLineLobby()
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++)
                if (a[i] == "+connect_lobby") { ulong id; if (ulong.TryParse(a[i + 1], out id)) return id; }
            return 0;
        }

        // ------------------------------------------------------------------ instance
        readonly NetSession net;
        public ulong Lobby { get; private set; }
        public bool IsHost { get; private set; }
        public string Status = "";
        bool isPublic;
        string lobbyName = "";
        int maxPlayers = 8;
        readonly Dictionary<ulong, uint> idToPeer = new Dictionary<ulong, uint>();
        readonly Dictionary<uint, ulong> peerToId = new Dictionary<uint, ulong>();
        uint nextPeer = 1;
        float memberCheckT, callStart;
        bool callPending;
        readonly byte[] rx = new byte[1 << 20];
        CallResult<LobbyCreated_t> crCreate;
        CallResult<LobbyEnter_t> crEnter;
        static CallResult<LobbyMatchList_t> crList;

        public SteamLink(NetSession net)
        {
            this.net = net;
            net.ExtMaxUnreliable(1100);
            crCreate = CallResult<LobbyCreated_t>.Create(OnCreated);
            crEnter = CallResult<LobbyEnter_t>.Create(OnEntered);
        }

        static float Now => UnityEngine.Time.realtimeSinceStartup;

        static SteamNetworkingIdentity Ident(ulong id) { var i = new SteamNetworkingIdentity(); i.SetSteamID64(id); return i; }

        void Accept(ulong id)
        {
            SteamNetworking.AcceptP2PSessionWithUser(new CSteamID(id));
            if (UseMessages) { try { var i = Ident(id); SteamNetworkingMessages.AcceptSessionWithUser(ref i); } catch { } }
        }

        void Close(ulong id)
        {
            try { SteamNetworking.CloseP2PSessionWithUser(new CSteamID(id)); } catch { }
            if (UseMessages) { try { var i = Ident(id); SteamNetworkingMessages.CloseSessionWithUser(ref i); } catch { } }
        }

        public long SentBytes, RecvBytes, DroppedUnreliable;

        unsafe void Send(ulong id, byte[] buf, int len, bool rel)
        {
            SentBytes += len;
            if (UseMessages)
            {
                var i = Ident(id);
                // unreliable: NoDelay = dropped by Steam instead of queued when the link is busy
                int flags = (rel ? Constants.k_nSteamNetworkingSend_Reliable : Constants.k_nSteamNetworkingSend_UnreliableNoDelay)
                            | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;
                fixed (byte* p = buf) SteamNetworkingMessages.SendMessageToUser(ref i, (IntPtr)p, (uint)len, flags, 0);
                return;
            }
            if (!rel && Queued(id) > 32 * 1024) { DroppedUnreliable++; return; }   // don't pile state behind a full queue
            SteamNetworking.SendP2PPacket(new CSteamID(id), buf, (uint)len, rel ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliableNoDelay);
        }

        readonly Dictionary<ulong, int> queuedCache = new Dictionary<ulong, int>();
        float queuedT;
        int Queued(ulong id)
        {
            if (Now - queuedT > 0.2f) { queuedT = Now; queuedCache.Clear(); }
            int q;
            if (queuedCache.TryGetValue(id, out q)) return q;
            P2PSessionState_t st;
            q = SteamNetworking.GetP2PSessionState(new CSteamID(id), out st) ? st.m_nBytesQueuedForSend : 0;
            queuedCache[id] = q;
            return q;
        }

        readonly IntPtr[] msgs = new IntPtr[64];

        void Receive()
        {
            // both APIs are always drained, whichever this session sends on
            {
                int guard = 0;
                while (guard++ < 40)
                {
                    int n = SteamNetworkingMessages.ReceiveMessagesOnChannel(0, msgs, msgs.Length);
                    if (n <= 0) break;
                    for (int k = 0; k < n; k++)
                    {
                        try
                        {
                            var m = SteamNetworkingMessage_t.FromIntPtr(msgs[k]);
                            int len = Math.Min(m.m_cbSize, rx.Length);
                            System.Runtime.InteropServices.Marshal.Copy(m.m_pData, rx, 0, len);
                            Deliver(m.m_identityPeer.GetSteamID64(), len);
                        }
                        finally { SteamNetworkingMessage_t.Release(msgs[k]); }
                    }
                    if (n < msgs.Length) break;
                }
            }
            // the old API is always drained too (a friend on an older build, or the fallback)
            uint size; int g2 = 0;
            while (g2++ < 500 && SteamNetworking.IsP2PPacketAvailable(out size))
            {
                uint n; CSteamID from;
                if (!SteamNetworking.ReadP2PPacket(rx, (uint)rx.Length, out n, out from)) break;
                Deliver(from.m_SteamID, (int)n);
            }
        }

        void Deliver(ulong from, int n)
        {
            RecvBytes += n;
            uint peer;
            if (!idToPeer.TryGetValue(from, out peer))
            {
                if (IsHost) { CheckMembers(); if (!idToPeer.TryGetValue(from, out peer)) return; }
                else return;
            }
            net.ExtData(peer, rx, n);
        }

        /// One line per peer for F8 / the flight recorder.
        public string LinkStats()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in peerToId)
            {
                sb.Append(" | peer").Append(kv.Key).Append(':');
                try
                {
                    if (UseMessages)
                    {
                        var i = Ident(kv.Value);
                        SteamNetConnectionInfo_t info; SteamNetConnectionRealTimeStatus_t st;
                        var state = SteamNetworkingMessages.GetSessionConnectionInfo(ref i, out info, out st);
                        string sname = state.ToString().Replace("k_ESteamNetworkingConnectionState_", "");
                        sb.Append(sname).Append(" ping ").Append(st.m_nPing).Append("ms q ").Append((int)(st.m_flConnectionQualityLocal * 100)).Append('%')
                          .Append(" out ").Append((int)(st.m_flOutBytesPerSec / 1024)).Append("KB/s in ").Append((int)(st.m_flInBytesPerSec / 1024)).Append("KB/s")
                          .Append(" rate ").Append(st.m_nSendRateBytesPerSecond / 1024).Append("KB/s pendRel ").Append(st.m_cbPendingReliable)
                          .Append(" pendUnrel ").Append(st.m_cbPendingUnreliable).Append(" unacked ").Append(st.m_cbSentUnackedReliable)
                          .Append(" queue ").Append(st.m_usecQueueTime.m_SteamNetworkingMicroseconds / 1000).Append("ms")
                          .Append((info.m_nFlags & 16) != 0 ? " relay" : "");
                    }
                    else
                    {
                        P2PSessionState_t ps;
                        if (SteamNetworking.GetP2PSessionState(new CSteamID(kv.Value), out ps))
                            sb.Append(ps.m_bConnectionActive != 0 ? "active" : "inactive").Append(ps.m_bUsingRelay != 0 ? " relay" : " direct")
                              .Append(" queued ").Append(ps.m_nBytesQueuedForSend).Append("B/").Append(ps.m_nPacketsQueuedForSend).Append("pk err ").Append(ps.m_eP2PSessionError);
                    }
                }
                catch (Exception e) { sb.Append("? ").Append(e.Message); }
            }
            sb.Append(" | sent ").Append(SentBytes / 1024).Append("KB recv ").Append(RecvBytes / 1024).Append("KB dropped ").Append(DroppedUnreliable);
            return sb.ToString();
        }

        public bool IsMember(ulong id)
        {
            if (Lobby == 0) return false;
            try
            {
                int n = SteamMatchmaking.GetNumLobbyMembers(new CSteamID(Lobby));
                for (int i = 0; i < n; i++) if (SteamMatchmaking.GetLobbyMemberByIndex(new CSteamID(Lobby), i).m_SteamID == id) return true;
            }
            catch { }
            return false;
        }

        public bool Host(bool pub, int max, string name)
        {
            if (!Available) { Status = "Steam isn't running"; return false; }
            IsHost = true; isPublic = pub; maxPlayers = Math.Max(2, Math.Min(250, max)); lobbyName = name;
            var call = SteamMatchmaking.CreateLobby(pub ? ELobbyType.k_ELobbyTypePublic : ELobbyType.k_ELobbyTypeFriendsOnly, maxPlayers);
            crCreate.Set(call);
            callPending = true; callStart = Now;
            Status = "Creating Steam lobby...";
            Log.Msg("Steam: CreateLobby(" + (pub ? "public" : "friends") + ", " + maxPlayers + ")");
            return true;
        }

        public bool Join(ulong lobby)
        {
            if (!Available) { Status = "Steam isn't running"; return false; }
            IsHost = false;
            var call = SteamMatchmaking.JoinLobby(new CSteamID(lobby));
            crEnter.Set(call);
            callPending = true; callStart = Now;
            Status = "Joining Steam lobby...";
            Log.Msg("Steam: JoinLobby(" + lobby + ")");
            return true;
        }

        void OnCreated(LobbyCreated_t r, bool ioFail)
        {
            callPending = false;
            if (ioFail || r.m_eResult != EResult.k_EResultOK || r.m_ulSteamIDLobby == 0)
            {
                Status = "Steam could not create the lobby (" + r.m_eResult + ")";
                Log.Warn("Steam: " + Status);
                net.ExtDisconnected(0, Status);
                return;
            }
            Lobby = r.m_ulSteamIDLobby;
            var l = new CSteamID(Lobby);
            SteamMatchmaking.SetLobbyData(l, "nk", Mod.GameKey);
            SteamMatchmaking.SetLobbyData(l, "ver", Mod.Version);
            SteamMatchmaking.SetLobbyData(l, "name", lobbyName);
            SteamMatchmaking.SetLobbyData(l, "host", PersonaName());
            SteamMatchmaking.SetLobbyData(l, "state", "lobby");
            SteamMatchmaking.SetLobbyData(l, "vis", isPublic ? "public" : "friends");
            SteamMatchmaking.SetLobbyData(l, "tr", UseMessages ? "messages" : "p2p");
            SteamMatchmaking.SetLobbyJoinable(l, true);
            try { SteamFriends.SetRichPresence("status", "Hosting DinoNet co-op"); } catch { }
            net.ExtReady(Lobby.ToString());
            Status = "Steam lobby ready";
            Log.Msg("Steam: lobby created " + Lobby);
        }

        void OnEntered(LobbyEnter_t r, bool ioFail)
        {
            callPending = false;
            if (ioFail || r.m_EChatRoomEnterResponse != 1)
            {
                uint resp = r.m_EChatRoomEnterResponse;
                string why = ioFail ? "network error" : resp == 2 ? "the lobby no longer exists" : resp == 4 ? "the lobby is full" : resp == 7 ? "limited Steam accounts cannot join" : "Steam refused (" + resp + ")";
                Status = "Could not join: " + why;
                Log.Warn("Steam: " + Status);
                net.ExtDisconnected(0, Status);
                return;
            }
            Lobby = r.m_ulSteamIDLobby;
            ulong owner = SteamMatchmaking.GetLobbyOwner(new CSteamID(Lobby)).m_SteamID;
            string ver = SteamMatchmaking.GetLobbyData(new CSteamID(Lobby), "ver");
            string tr = SteamMatchmaking.GetLobbyData(new CSteamID(Lobby), "tr");
            UseMessages = tr == "messages";   // empty = a host on 0.0.3.4 or older (old API)
            Log.Msg("Steam: entered lobby " + Lobby + " owner " + owner + " ver " + ver + " (transport: " + (UseMessages ? "messages" : "p2p") + ")");
            if (owner == 0) { net.ExtDisconnected(0, "lobby has no owner"); return; }
            idToPeer[owner] = 0; peerToId[0] = owner;
            Accept(owner);
            net.ExtConnected(0, owner.ToString());
            Status = "In Steam lobby";
        }

        public void SetState(string state)
        {
            if (Lobby == 0 || !IsHost) return;
            try { SteamMatchmaking.SetLobbyData(new CSteamID(Lobby), "state", state); } catch { }
        }

        public void InviteFriends()
        {
            try { if (Lobby != 0) SteamFriends.ActivateGameOverlayInviteDialog(new CSteamID(Lobby)); } catch (Exception e) { Log.Warn("invite: " + e.Message); }
        }

        public void Leave()
        {
            try
            {
                foreach (var id in peerToId.Values) Close(id);
                if (Lobby != 0) SteamMatchmaking.LeaveLobby(new CSteamID(Lobby));
                SteamFriends.ClearRichPresence();
            }
            catch { }
            Lobby = 0; idToPeer.Clear(); peerToId.Clear();
        }

        /// Call every frame before and after net.Update().
        public void Pump()
        {
            if (callPending && Now - callStart > 20f)
            {
                callPending = false;
                Status = "Steam did not answer (timed out)";
                net.ExtDisconnected(0, Status);
            }
            if (Lobby != 0 && Now >= memberCheckT) { memberCheckT = Now + 0.5f; CheckMembers(); }
            Receive();
            uint p; byte[] buf; int len; bool rel;
            while (net.ExtTakeOutgoing(out p, out buf, out len, out rel))
            {
                if (p == uint.MaxValue) { foreach (var kv in peerToId) Send(kv.Value, buf, len, rel); }
                else { ulong id; if (peerToId.TryGetValue(p, out id)) Send(id, buf, len, rel); }
            }
            long k;
            while ((k = net.ExtTakeKick()) >= 0)
            {
                ulong id;
                if (peerToId.TryGetValue((uint)k, out id)) Close(id);
            }
        }

        void CheckMembers()
        {
            if (Lobby == 0) return;
            var lobby = new CSteamID(Lobby);
            ulong me = Me;
            if (!IsHost)
            {
                ulong owner = SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID;
                ulong cur;
                if (peerToId.TryGetValue(0, out cur) && owner != 0 && owner != cur) net.ExtDisconnected(0, "the host left the Steam lobby");
                return;
            }
            var present = new HashSet<ulong>();
            int n = SteamMatchmaking.GetNumLobbyMembers(lobby);
            for (int i = 0; i < n; i++)
            {
                ulong m = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i).m_SteamID;
                if (m == 0 || m == me) continue;
                present.Add(m);
                if (idToPeer.ContainsKey(m)) continue;
                if (!isPublic)
                {
                    bool friend = false;
                    try { friend = SteamFriends.HasFriend(new CSteamID(m), EFriendFlags.k_EFriendFlagImmediate); } catch { }
                    if (!friend) continue;
                }
                uint peer = nextPeer++;
                idToPeer[m] = peer; peerToId[peer] = m;
                Accept(m);
                Log.Msg("Steam: member " + m + " -> peer " + peer);
                net.ExtConnected(peer, m.ToString());
            }
            var gone = new List<ulong>();
            foreach (var kv in idToPeer) if (!present.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var id in gone)
            {
                uint peer = idToPeer[id];
                idToPeer.Remove(id); peerToId.Remove(peer);
                Close(id);
                Log.Msg("Steam: member " + id + " left");
                net.ExtDisconnected(peer, "left the Steam lobby");
            }
        }

        // ------------------------------------------------------------------ lobby lists
        public static readonly List<LobbyInfo> ListResults = new List<LobbyInfo>();
        public static bool ListBusy;
        public static float ListTime;

        public static void RefreshList()
        {
            if (!Available || ListBusy) return;
            try
            {
                if (crList == null) crList = CallResult<LobbyMatchList_t>.Create(OnList);
                SteamMatchmaking.AddRequestLobbyListStringFilter("nk", Mod.GameKey, ELobbyComparison.k_ELobbyComparisonEqual);
                SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
                SteamMatchmaking.AddRequestLobbyListResultCountFilter(50);
                crList.Set(SteamMatchmaking.RequestLobbyList());
                ListBusy = true; ListTime = Now;
            }
            catch (Exception e) { Log.Warn("Steam list: " + e.Message); ListBusy = false; }
        }

        static void OnList(LobbyMatchList_t r, bool ioFail)
        {
            ListBusy = false;
            ListResults.Clear();
            var seen = new HashSet<ulong>();
            // friends first (friends-only lobbies never show up in the search)
            try
            {
                uint app = SteamUtils.GetAppID().m_AppId;
                int n = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
                for (int i = 0; i < n && i < 500; i++)
                {
                    var f = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                    FriendGameInfo_t gi;
                    if (!SteamFriends.GetFriendGamePlayed(f, out gi)) continue;
                    if (gi.m_gameID.AppID().m_AppId != app || gi.m_steamIDLobby.m_SteamID == 0) continue;
                    ulong lid = gi.m_steamIDLobby.m_SteamID;
                    if (!seen.Add(lid)) continue;
                    SteamMatchmaking.RequestLobbyData(gi.m_steamIDLobby);
                    var info = Describe(lid);
                    info.Friend = true;
                    if (string.IsNullOrEmpty(info.Host)) info.Host = SteamFriends.GetFriendPersonaName(f);
                    ListResults.Add(info);
                }
            }
            catch (Exception e) { Log.Warn("Steam friends lobbies: " + e.Message); }
            if (!ioFail)
            {
                for (int i = 0; i < (int)r.m_nLobbiesMatching; i++)
                {
                    ulong lid = SteamMatchmaking.GetLobbyByIndex(i).m_SteamID;
                    if (lid == 0 || !seen.Add(lid)) continue;
                    ListResults.Add(Describe(lid));
                }
            }
            Log.Msg("Steam: lobby list " + ListResults.Count);
        }

        public static LobbyInfo Describe(ulong lid)
        {
            var l = new CSteamID(lid);
            var info = new LobbyInfo { Id = lid };
            try
            {
                info.Name = SteamMatchmaking.GetLobbyData(l, "name");
                info.Host = SteamMatchmaking.GetLobbyData(l, "host");
                info.Version = SteamMatchmaking.GetLobbyData(l, "ver");
                info.State = SteamMatchmaking.GetLobbyData(l, "state");
                info.Vis = SteamMatchmaking.GetLobbyData(l, "vis");
                info.Members = SteamMatchmaking.GetNumLobbyMembers(l);
                info.Max = SteamMatchmaking.GetLobbyMemberLimit(l);
            }
            catch { }
            return info;
        }
    }
}
