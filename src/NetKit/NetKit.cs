// NetKit.cs - friendly C# API over the native core. Drop into any C# mod
// (Unity Mono, Unity IL2CPP via MelonLoader/BepInEx, plain .NET).
//
//   NetKitLib.Load(Path.Combine(modDir, "netkit.dll"));
//   var opts = new NetOptions { GameId = "mygame", AppVersion = "1.0.0", Name = "Mike", Pid = savedPid };
//   var net = NetSession.HostIroh(opts);           // or JoinIroh(code, opts), HostExternal("steam", opts)
//   ...every frame (main thread):
//   net.Update();
//   while (net.Poll(out var ev)) { switch (ev.Type) { ... } }
//
// Everything is polled; nothing is called back on another thread, so it is
// safe to touch Unity objects while handling events.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace NetKit
{
    public static class NetKitLib
    {
        public static void Load(string path) => Native.Load(path);
        public static bool IsJoinCode(string s) => Native.Loaded && Native.nk_is_ticket(Native.Z(s?.Trim())) != 0;
        public static string Version
        {
            get { uint v = Native.nk_version(); return (v / 10000) + "." + (v / 100 % 100) + "." + (v % 100); }
        }
        public static unsafe string LastError
        {
            get
            {
                var b = new byte[512];
                fixed (byte* p = b) { int n = Native.nk_last_error(p, b.Length); return n > 0 ? Encoding.UTF8.GetString(b, 0, n) : ""; }
            }
        }
    }

    public enum EventType
    {
        Hosting = 1, Joined = 2, PlayerJoined = 3, PlayerLeft = 4, PlayerAway = 5, Roster = 6, Message = 7, Phase = 8,
        LobbyMeta = 9, HostSilent = 10, HostBack = 11, Ended = 12, Log = 13,
        Spawned = 20, Despawned = 21, Action = 22,
        LockstepStarted = 30, LockstepWaiting = 31, LockstepDesync = 32,
        Rejoining = 40,
    }

    public enum EndKind { Left = 0, ConnectFailed = 1, Rejected = 2, Kicked = 3, HostClosed = 4, Lost = 5 }

    public static class DespawnReason { public const int Removed = 0, Died = 1, Culled = 2, OwnerLeft = 3; }

    /// One event. Field meaning depends on Type (see netkit.h).
    public struct NetEvent
    {
        public EventType Type;
        public int A, B, C;
        public uint Id, Kind;
        public byte[] Data;
        public string Text => Data == null ? "" : Encoding.UTF8.GetString(Data);
        // convenience views
        public int Player => A;
        public EndKind EndKind => (EndKind)A;
        public override string ToString() => Type + " a=" + A + " b=" + B + " c=" + C + " id=" + Id + " kind=" + Kind + (Data != null ? " data=" + Data.Length + "B" : "");
    }

    public enum Field : byte { Float = 0, AngleDeg = 1, AngleRad = 2, Int = 3, Byte = 4, Bool = 5, FloatStep = 6 }

    /// Describes one networked object type. Same definitions on every PC.
    public class EntityClassDef
    {
        public uint Id;
        public Field[] Fields;
        public double[] Steps;          // precision per float field (null = 0.01)
        public int[] Position = new int[0]; // indices of x,y(,z)
        public double SnapDistance;     // teleports bigger than this snap
        public bool AlwaysRelevant;     // ignore interest distance
        public float Priority = 1f;
    }

    public class NetOptions
    {
        public string GameId = "game", AppVersion = "1.0.0", Name = "Player", Pid = "", Password = "", Alpn = "", IdentityFile = "", ExtraIps = "";
        public int Protocol = 1, MaxPlayers = 8;
        public bool Internet = true, StrictVersion = true, JoinInProgress = true;
        public int RejoinAttempts = 5;
        public int HostUploadBps = 0;   // e.g. 110000 for a 0.9 Mbit/s upload
        public Dictionary<string, long> Ints = new Dictionary<string, long>();      // any other nk_config_set_int key
        public Dictionary<string, string> Strings = new Dictionary<string, string>();

        static void S(IntPtr c, string k, string v) { if (!string.IsNullOrEmpty(v)) Native.nk_config_set_str(c, Native.Z(k), Native.Z(v)); }
        static void I(IntPtr c, string k, long v) { Native.nk_config_set_int(c, Native.Z(k), v); }

        internal IntPtr Build()
        {
            IntPtr c = Native.nk_config_new();
            S(c, "game_id", GameId); S(c, "app_version", AppVersion); S(c, "name", Name); S(c, "pid", Pid); S(c, "password", Password);
            S(c, "alpn", string.IsNullOrEmpty(Alpn) ? GameId + "-netkit/1" : Alpn);
            S(c, "identity_file", IdentityFile); S(c, "extra_ips", ExtraIps);
            I(c, "protocol", Protocol); I(c, "max_players", MaxPlayers); I(c, "internet", Internet ? 1 : 0);
            I(c, "strict_version", StrictVersion ? 1 : 0); I(c, "join_in_progress", JoinInProgress ? 1 : 0);
            I(c, "rejoin_attempts", RejoinAttempts); I(c, "host_upload_bps", HostUploadBps);
            foreach (var kv in Ints) I(c, kv.Key, kv.Value);
            foreach (var kv in Strings) S(c, kv.Key, kv.Value);
            return c;
        }
    }

    public struct PlayerInfo
    {
        public int Id;
        public string Name;
        public bool Ready, Loading, Away, Syncing, IsHost;
        public int PingMs;
        public int Path; // 0 unknown 1 direct 2 relay
        public byte[] Data;
    }

    public unsafe sealed class NetSession : IDisposable
    {
        IntPtr h;
        NetSession(IntPtr handle)
        {
            if (handle == IntPtr.Zero) throw new InvalidOperationException("NetKit: could not start session: " + NetKitLib.LastError);
            h = handle;
        }

        static NetSession Make(NetOptions o, Func<IntPtr, IntPtr> f)
        {
            IntPtr c = o.Build();
            try { return new NetSession(f(c)); } finally { Native.nk_config_free(c); }
        }

        public static NetSession HostIroh(NetOptions o) => Make(o, c => Native.nk_host_iroh(c));
        public static NetSession JoinIroh(string code, NetOptions o) => Make(o, c => Native.nk_join_iroh(c, Native.Z(code.Trim())));
        /// Packets are moved by you (Steam etc.): see ExternalPump / SteamFlatTransport.
        public static NetSession HostExternal(string name, NetOptions o) => Make(o, c => Native.nk_host_external(c, Native.Z(name)));
        public static NetSession JoinExternal(string name, NetOptions o) => Make(o, c => Native.nk_join_external(c, Native.Z(name)));

        public IntPtr Handle => h;
        public bool Alive => h != IntPtr.Zero;

        /// Leave politely and free native memory.
        public void Dispose()
        {
            if (h != IntPtr.Zero) { Native.nk_free(h); h = IntPtr.Zero; }
        }

        // ---------------------------------------------------------------- loop
        public void Update() { if (h != IntPtr.Zero) Native.nk_update(h); }

        public bool Poll(out NetEvent ev)
        {
            ev = default(NetEvent);
            if (h == IntPtr.Zero) return false;
            var e = new NkEvent();
            if (Native.nk_poll_event(h, ref e) == 0) return false;
            ev.Type = (EventType)e.Type; ev.A = e.A; ev.B = e.B; ev.C = e.C; ev.Id = e.Id; ev.Kind = e.Kind;
            if (e.Len > 0 && e.Data != IntPtr.Zero) { ev.Data = new byte[e.Len]; Marshal.Copy(e.Data, ev.Data, 0, e.Len); }
            return true;
        }

        // ---------------------------------------------------------------- session
        public bool IsHost => h != IntPtr.Zero && Native.nk_is_host(h) != 0;
        public int MyId => h == IntPtr.Zero ? -1 : Native.nk_my_id(h);
        /// 0 connecting, 1 active, 2 ended
        public int State => h == IntPtr.Zero ? 2 : Native.nk_state(h);
        public bool Active => State == 1;
        public uint NowMs => Native.nk_now_ms(h);
        public string JoinCode => ReadString((p, cap) => Native.nk_ticket(h, p, cap));
        public int Phase => Native.nk_phase(h);
        public byte[] PhaseData => ReadBytes((p, cap) => Native.nk_phase_data(h, p, cap));
        public byte[] LobbyMeta => ReadBytes((p, cap) => Native.nk_lobby_meta(h, p, cap));
        public bool AllReady => Native.nk_all_ready(h) != 0;
        public bool AllLoaded => Native.nk_all_loaded(h) != 0;

        /// to: -1 others, -2 all, -3 host, >=0 one player. kind: your number (0..0x7FFF), add HostOnly for host-validated requests.
        public const uint HostOnly = 0x8000;
        public void Send(int to, uint kind, byte[] data, bool reliable = true)
        {
            int tt = to == -1 ? 0 : to == -2 ? 1 : to == -3 ? 2 : 3;
            fixed (byte* p = data) Native.nk_send(h, tt, Math.Max(0, to), kind, p, data?.Length ?? 0, reliable ? 1 : 0);
        }
        public void SendToOthers(uint kind, byte[] d, bool reliable = true) => Send(-1, kind, d, reliable);
        public void SendToAll(uint kind, byte[] d, bool reliable = true) => Send(-2, kind, d, reliable);
        public void SendToHost(uint kind, byte[] d, bool reliable = true) => Send(-3, kind, d, reliable);

        public void SetReady(bool r) => Native.nk_set_ready(h, r ? 1 : 0);
        public void SetMyData(byte[] d) { fixed (byte* p = d) Native.nk_set_my_data(h, p, d?.Length ?? 0); }
        public void SetPhase(int phase, byte[] d = null) { fixed (byte* p = d) Native.nk_set_phase(h, phase, p, d?.Length ?? 0); }
        public void SetLobbyPhase(int phase) => Native.nk_set_lobby_phase(h, phase);
        public void SetLobbyMeta(byte[] d) { fixed (byte* p = d) Native.nk_set_lobby_meta(h, p, d?.Length ?? 0); }
        public void ResetReady() => Native.nk_reset_ready(h);
        public void NotifyLoading(int graceMs = 120000) => Native.nk_notify_loading(h, graceMs);
        public void NotifyLoaded() => Native.nk_notify_loaded(h);
        public void MarkSynced(int player) => Native.nk_mark_synced(h, player);
        public void Kick(int player, string reason) => Native.nk_kick(h, player, Native.Z(reason));
        public void Leave(string reason = "left") => Native.nk_leave(h, Native.Z(reason));
        public int RttMs(int player) => Native.nk_rtt_ms(h, player);

        public List<PlayerInfo> Players()
        {
            var ids = ReadBytes((p, cap) => Native.nk_player_ids(h, p, cap)) ?? new byte[0];
            var list = new List<PlayerInfo>(ids.Length);
            foreach (var id in ids)
            {
                var info = new NkPlayerInfo();
                if (Native.nk_player_info(h, id, ref info) == 0) continue;
                list.Add(new PlayerInfo
                {
                    Id = id,
                    Name = ReadString((p, cap) => Native.nk_player_name(h, id, p, cap)),
                    Data = ReadBytes((p, cap) => Native.nk_player_data(h, id, p, cap)) ?? new byte[0],
                    Ready = (info.Flags & 1) != 0, Loading = (info.Flags & 2) != 0, Away = (info.Flags & 4) != 0,
                    Syncing = (info.Flags & 8) != 0, IsHost = (info.Flags & 16) != 0, PingMs = info.PingMs, Path = info.Path,
                });
            }
            return list;
        }

        public NkStats Stats { get { var s = new NkStats(); Native.nk_stats(h, ref s); return s; } }

        // ---------------------------------------------------------------- replication
        public void DefineClass(EntityClassDef c)
        {
            int n = c.Fields.Length;
            var kinds = new byte[n];
            for (int i = 0; i < n; i++) kinds[i] = (byte)c.Fields[i];
            var steps = c.Steps ?? new double[n];
            if (steps.Length < n) Array.Resize(ref steps, n);
            var pos = c.Position ?? new int[0];
            int r;
            fixed (byte* k = kinds) fixed (double* s = steps) fixed (int* p = pos)
                r = Native.nk_define_class(h, c.Id, n, k, s, pos.Length > 0 ? p : null, pos.Length, c.SnapDistance, c.AlwaysRelevant ? 1 : 0, c.Priority);
            if (r != 0) throw new ArgumentException("NetKit DefineClass: " + NetKitLib.LastError);
        }

        public uint NewHostId() => Native.nk_new_host_id(h);
        public uint NewOwnedId() => Native.nk_new_owned_id(h);
        public bool SetEntity(uint id, uint cls, double[] values) { fixed (double* v = values) return Native.nk_set_entity(h, id, cls, v, values.Length) != 0; }
        public void RemoveEntity(uint id, int reason = DespawnReason.Removed) => Native.nk_remove_entity(h, id, reason);

        readonly double[] getBuf = new double[64];
        /// Smoothed values to draw now. Returns field count or -1. Reuses an internal buffer (no allocation).
        public int GetEntity(uint id, out double[] values)
        {
            int n;
            fixed (double* v = getBuf) n = Native.nk_get_entity(h, id, v, getBuf.Length);
            values = getBuf;
            return n;
        }
        public double[] GetEntityCopy(uint id)
        {
            double[] v;
            int n = GetEntity(id, out v);
            if (n < 0) return null;
            var o = new double[n]; Array.Copy(v, o, n); return o;
        }
        public double[] LatestEntity(uint id)
        {
            var b = new double[64]; int n;
            fixed (double* v = b) n = Native.nk_latest_entity(h, id, v, b.Length);
            if (n < 0) return null;
            Array.Resize(ref b, n); return b;
        }
        public int EntityClass(uint id) => Native.nk_entity_class(h, id);
        public int EntityOwner(uint id) => Native.nk_entity_owner(h, id);
        public int EntityCount => Native.nk_entity_count(h);
        public uint[] EntityIds()
        {
            var b = new uint[Math.Max(16, EntityCount + 16)];
            int n;
            fixed (uint* p = b) n = Native.nk_entity_ids(h, p, b.Length);
            if (n < -1) { b = new uint[-(n + 2)]; fixed (uint* p = b) n = Native.nk_entity_ids(h, p, b.Length); }
            if (n < 0) return new uint[0];
            Array.Resize(ref b, n); return b;
        }
        public void SetViewer(int player, double x, double y, double z, double radius) => Native.nk_set_viewer(h, player, x, y, z, radius);
        public void SetViewerEntity(int player, uint id, double radius) => Native.nk_set_viewer_entity(h, player, id, radius);
        public void ReportView(double x, double y, double z, double radius) => Native.nk_report_view(h, x, y, z, radius);
        public void SetAlwaysRelevant(uint id, bool on) => Native.nk_set_always_relevant(h, id, on ? 1 : 0);
        public uint SendAction(uint kind, byte[] d) { fixed (byte* p = d) return Native.nk_send_action(h, kind, p, d?.Length ?? 0); }
        public uint AckedAction => Native.nk_acked_action(h);
        public void AckAction(int player, uint seq) => Native.nk_ack_action(h, player, seq);

        // ---------------------------------------------------------------- lockstep
        public void LockstepStart(byte[] players, uint frame) { fixed (byte* p = players) Native.nk_ls_start(h, p, players.Length, frame); }
        public void LockstepAddPlayer(int p) => Native.nk_ls_add_player(h, p);
        public bool LockstepCanSubmit => Native.nk_ls_can_submit(h) != 0;
        public uint LockstepSubmit(byte[] input) { fixed (byte* p = input) return Native.nk_ls_submit(h, p, input?.Length ?? 0); }
        public void LockstepHash(uint frame, ulong hash) => Native.nk_ls_report_hash(h, frame, hash);
        byte[] lsBuf = new byte[4096];
        /// Next frame with everybody's inputs, or false if it has not arrived.
        public bool LockstepNext(out uint frame, out List<KeyValuePair<int, byte[]>> inputs)
        {
            frame = 0; inputs = null;
            uint f; int n;
            fixed (byte* p = lsBuf) n = Native.nk_ls_next_frame(h, &f, p, lsBuf.Length);
            if (n < -1) { lsBuf = new byte[-(n + 2)]; fixed (byte* p = lsBuf) n = Native.nk_ls_next_frame(h, &f, p, lsBuf.Length); }
            if (n < 0) return false;
            frame = f;
            inputs = new List<KeyValuePair<int, byte[]>>();
            int i = 0;
            while (i + 3 <= n)
            {
                int pl = lsBuf[i]; int len = lsBuf[i + 1] | (lsBuf[i + 2] << 8); i += 3;
                var d = new byte[len]; Buffer.BlockCopy(lsBuf, i, d, 0, len); i += len;
                inputs.Add(new KeyValuePair<int, byte[]>(pl, d));
            }
            return true;
        }

        // ---------------------------------------------------------------- external transport
        public void ExtReady(string ticket) => Native.nk_ext_push_ready(h, Native.Z(ticket));
        public void ExtConnected(uint peer, string info) => Native.nk_ext_push_connected(h, peer, Native.Z(info));
        public void ExtDisconnected(uint peer, string reason) => Native.nk_ext_push_disconnected(h, peer, Native.Z(reason));
        public void ExtData(uint peer, byte[] data, int len) { fixed (byte* p = data) Native.nk_ext_push_data(h, peer, p, len); }
        public void ExtRtt(uint peer, uint ms) => Native.nk_ext_set_rtt(h, peer, ms);
        public void ExtMaxUnreliable(int bytes) => Native.nk_ext_set_max_unreliable(h, bytes);
        byte[] outBuf = new byte[64 * 1024];
        /// Next packet the core wants on the wire. Returns false when there is none.
        public bool ExtTakeOutgoing(out uint peer, out byte[] buf, out int len, out bool reliable)
        {
            uint pe; int rel; int n;
            fixed (byte* p = outBuf) n = Native.nk_ext_take_outgoing(h, &pe, p, outBuf.Length, &rel);
            if (n < -1) { outBuf = new byte[-(n + 2) + 1024]; fixed (byte* p = outBuf) n = Native.nk_ext_take_outgoing(h, &pe, p, outBuf.Length, &rel); }
            peer = pe; buf = outBuf; len = Math.Max(0, n); reliable = rel != 0;
            return n >= 0;
        }
        public long ExtTakeKick() => Native.nk_ext_take_kick(h);

        // ---------------------------------------------------------------- helpers
        delegate int Filler(byte* p, int cap);
        static byte[] ReadBytes(Filler f)
        {
            var b = new byte[256];
            int n;
            fixed (byte* p = b) n = f(p, b.Length);
            if (n < -1) { b = new byte[-(n + 2)]; fixed (byte* p = b) n = f(p, b.Length); }
            if (n < 0) return null;
            Array.Resize(ref b, n); return b;
        }
        static string ReadString(Filler f) { var b = ReadBytes(f); return b == null ? "" : Encoding.UTF8.GetString(b); }
    }

    /// Little binary writer/reader for your own messages (same varint format as the core).
    public class Packer
    {
        readonly List<byte> b = new List<byte>(64);
        public Packer U8(int v) { b.Add((byte)v); return this; }
        public Packer Bool(bool v) { b.Add((byte)(v ? 1 : 0)); return this; }
        public Packer UV(ulong v) { while (v >= 0x80) { b.Add((byte)(v | 0x80)); v >>= 7; } b.Add((byte)v); return this; }
        public Packer SV(long v) => UV((ulong)((v << 1) ^ (v >> 63)));
        public Packer F32(float v) { b.AddRange(BitConverter.GetBytes(v)); return this; }
        public Packer F64(double v) { b.AddRange(BitConverter.GetBytes(v)); return this; }
        public Packer Q(double v, double step) => SV((long)Math.Round(v / step));
        public Packer Str(string s) { var d = Encoding.UTF8.GetBytes(s ?? ""); UV((ulong)d.Length); b.AddRange(d); return this; }
        public Packer Bytes(byte[] d) { UV((ulong)(d?.Length ?? 0)); if (d != null) b.AddRange(d); return this; }
        public byte[] ToArray() => b.ToArray();
    }

    public class Unpacker
    {
        readonly byte[] d; int i;
        public Unpacker(byte[] data) { d = data ?? new byte[0]; }
        public bool End => i >= d.Length;
        public int U8() => d[i++];
        public bool Bool() => d[i++] != 0;
        public ulong UV() { ulong v = 0; int s = 0; while (true) { byte x = d[i++]; v |= (ulong)(x & 0x7f) << s; if ((x & 0x80) == 0) return v; s += 7; } }
        public long SV() { ulong z = UV(); return (long)(z >> 1) ^ -(long)(z & 1); }
        public float F32() { float v = BitConverter.ToSingle(d, i); i += 4; return v; }
        public double F64() { double v = BitConverter.ToDouble(d, i); i += 8; return v; }
        public double Q(double step) => SV() * step;
        public string Str() { int n = (int)UV(); var s = Encoding.UTF8.GetString(d, i, n); i += n; return s; }
        public byte[] Bytes() { int n = (int)UV(); var o = new byte[n]; Buffer.BlockCopy(d, i, o, 0, n); i += n; return o; }
    }
}
