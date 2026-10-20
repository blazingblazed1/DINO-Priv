// NetKit.Native.cs - raw bindings to netkit.dll / libnetkit.so.
//
// Works on every C# runtime a game can have:
//   * Unity Mono (.NET Framework 4.x / netstandard2.0) - no NativeLibrary API
//   * Unity IL2CPP through MelonLoader / BepInEx (mod code runs on .NET 6+)
//   * plain .NET / Mono console apps (tests)
// The library is loaded from an explicit path (LoadLibraryW / dlopen), so it
// can live in UserData\YourMod\ instead of next to the game exe, and every
// export is bound to a delegate. A missing export throws with its name.
//
// Copy this file and NetKit.cs into your mod. Nothing here is game specific.

using System;
using System.Runtime.InteropServices;

namespace NetKit
{
    [StructLayout(LayoutKind.Sequential)]
    public struct NkEvent
    {
        public int Type, A, B, C;
        public uint Id, Kind;
        public IntPtr Data;
        public int Len;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NkPlayerInfo
    {
        public int Flags, PingMs, Path;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NkStats
    {
        public ulong SentBytes, RecvBytes, SentFrames, RecvFrames;
        public double RenderDelayMs;
        public uint Entities;
        public ulong StarvedTicks;
    }

    public static unsafe class Native
    {
        // ---------------------------------------------------------------- loader
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr h, string name);
        [DllImport("libdl.so.2")] static extern IntPtr dlopen(string path, int flags);
        [DllImport("libdl.so.2")] static extern IntPtr dlsym(IntPtr h, string name);
        [DllImport("libdl.so.2")] static extern IntPtr dlerror();

        static IntPtr lib;
        static bool IsWindows => Environment.OSVersion.Platform == PlatformID.Win32NT;
        public static bool Loaded => lib != IntPtr.Zero;

        /// Load the library once per process. Never unload it (unloading a
        /// native library that has threads running crashes on Windows).
        public static void Load(string path)
        {
            if (lib != IntPtr.Zero) return;
            lib = IsWindows ? LoadLibraryW(path) : dlopen(path, 2 /* RTLD_NOW */);
            if (lib == IntPtr.Zero)
            {
                string why = IsWindows ? "error " + Marshal.GetLastWin32Error() : Marshal.PtrToStringAnsi(dlerror());
                throw new DllNotFoundException("NetKit: could not load " + path + " (" + why + ")");
            }
            Bind();
        }

        static T F<T>(string name) where T : class
        {
            IntPtr p = IsWindows ? GetProcAddress(lib, name) : dlsym(lib, name);
            if (p == IntPtr.Zero) throw new EntryPointNotFoundException("NetKit: missing export " + name + " (old netkit library?)");
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        // ---------------------------------------------------------------- delegate types
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate uint U32_V();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr Ptr_V();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void V_P(IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int I_P(IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate uint U32_P(IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int I_PSS(IntPtr p, byte[] k, byte[] v);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int I_PSL(IntPtr p, byte[] k, long v);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr Ptr_P(IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr Ptr_PS(IntPtr p, byte[] s);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int I_PE(IntPtr p, ref NkEvent e);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int I_PBI(IntPtr p, byte* buf, int cap);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int I_S(byte[] s);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void Send_(IntPtr p, int tt, int tid, uint kind, byte* d, int len, int rel);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void V_PI(IntPtr p, int i);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void V_PBI(IntPtr p, byte* d, int len);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void V_PIBI(IntPtr p, int i, byte* d, int len);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void V_PIS(IntPtr p, int i, byte[] s);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void V_PS(IntPtr p, byte[] s);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int I_PIBI(IntPtr p, int id, byte* buf, int cap);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int I_PIInfo(IntPtr p, int id, ref NkPlayerInfo info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int I_PI(IntPtr p, int id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void V_PStats(IntPtr p, ref NkStats s);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int I_BI(byte* buf, int cap);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DefineClass_(IntPtr p, uint cls, int n, byte* kinds, double* steps, int* pos, int npos, double snap, int always, float prio);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int SetEnt_(IntPtr p, uint id, uint cls, double* v, int n);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void V_PUI(IntPtr p, uint id, int i);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int GetEnt_(IntPtr p, uint id, double* o, int cap);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int I_PU(IntPtr p, uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int Ids_(IntPtr p, uint* buf, int cap);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void Viewer_(IntPtr p, int player, double x, double y, double z, double r);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void ViewerEnt_(IntPtr p, int player, uint id, double r);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void View_(IntPtr p, double x, double y, double z, double r);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate uint Action_(IntPtr p, uint kind, byte* d, int len);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void V_PIU(IntPtr p, int player, uint seq);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void LsStart_(IntPtr p, byte* players, int n, uint frame);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate uint LsSubmit_(IntPtr p, byte* d, int len);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int LsNext_(IntPtr p, uint* frame, byte* buf, int cap);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void LsHash_(IntPtr p, uint frame, ulong hash);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void ExtStr_(IntPtr p, uint peer, byte[] s);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void ExtData_(IntPtr p, uint peer, byte* d, int len);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void ExtRtt_(IntPtr p, uint peer, uint ms);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int ExtTake_(IntPtr p, uint* peer, byte* buf, int cap, int* reliable);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate long ExtKick_(IntPtr p);

        // ---------------------------------------------------------------- functions
        public static U32_V nk_version;
        public static Ptr_V nk_config_new;
        public static V_P nk_config_free;
        public static I_PSS nk_config_set_str;
        public static I_PSL nk_config_set_int;
        public static Ptr_P nk_host_iroh;
        public static Ptr_PS nk_join_iroh, nk_host_external, nk_join_external;
        public static V_P nk_free, nk_update;
        public static I_PE nk_poll_event;
        public static I_P nk_is_host, nk_my_id, nk_state, nk_phase, nk_all_ready, nk_all_loaded, nk_entity_count, nk_ls_can_submit;
        public static U32_P nk_now_ms, nk_new_host_id, nk_new_owned_id, nk_acked_action;
        public static I_PBI nk_ticket, nk_phase_data, nk_lobby_meta, nk_player_ids;
        public static I_S nk_is_ticket;
        public static Send_ nk_send;
        public static V_PI nk_set_ready, nk_set_lobby_phase, nk_notify_loading, nk_mark_synced, nk_ls_add_player, nk_ext_set_max_unreliable;
        public static V_PBI nk_set_my_data, nk_set_lobby_meta;
        public static V_PIBI nk_set_phase;
        public static V_P nk_reset_ready, nk_notify_loaded;
        public static V_PIS nk_kick;
        public static V_PS nk_leave, nk_ext_push_ready;
        public static I_PIInfo nk_player_info;
        public static I_PIBI nk_player_name, nk_player_data;
        public static I_PI nk_rtt_ms;
        public static V_PStats nk_stats;
        public static I_BI nk_last_error;
        public static DefineClass_ nk_define_class;
        public static SetEnt_ nk_set_entity;
        public static V_PUI nk_remove_entity, nk_set_always_relevant;
        public static GetEnt_ nk_get_entity, nk_latest_entity;
        public static I_PU nk_entity_class, nk_entity_owner;
        public static Ids_ nk_entity_ids;
        public static Viewer_ nk_set_viewer;
        public static ViewerEnt_ nk_set_viewer_entity;
        public static View_ nk_report_view;
        public static Action_ nk_send_action;
        public static V_PIU nk_ack_action;
        public static LsStart_ nk_ls_start;
        public static LsSubmit_ nk_ls_submit;
        public static LsNext_ nk_ls_next_frame;
        public static LsHash_ nk_ls_report_hash;
        public static ExtStr_ nk_ext_push_connected, nk_ext_push_disconnected;
        public static ExtData_ nk_ext_push_data;
        public static ExtRtt_ nk_ext_set_rtt;
        public static ExtTake_ nk_ext_take_outgoing;
        public static ExtKick_ nk_ext_take_kick;

        static void Bind()
        {
            nk_version = F<U32_V>("nk_version");
            nk_config_new = F<Ptr_V>("nk_config_new");
            nk_config_free = F<V_P>("nk_config_free");
            nk_config_set_str = F<I_PSS>("nk_config_set_str");
            nk_config_set_int = F<I_PSL>("nk_config_set_int");
            nk_host_iroh = F<Ptr_P>("nk_host_iroh");
            nk_join_iroh = F<Ptr_PS>("nk_join_iroh");
            nk_host_external = F<Ptr_PS>("nk_host_external");
            nk_join_external = F<Ptr_PS>("nk_join_external");
            nk_free = F<V_P>("nk_free");
            nk_update = F<V_P>("nk_update");
            nk_poll_event = F<I_PE>("nk_poll_event");
            nk_is_host = F<I_P>("nk_is_host");
            nk_my_id = F<I_P>("nk_my_id");
            nk_state = F<I_P>("nk_state");
            nk_phase = F<I_P>("nk_phase");
            nk_all_ready = F<I_P>("nk_all_ready");
            nk_all_loaded = F<I_P>("nk_all_loaded");
            nk_entity_count = F<I_P>("nk_entity_count");
            nk_ls_can_submit = F<I_P>("nk_ls_can_submit");
            nk_now_ms = F<U32_P>("nk_now_ms");
            nk_new_host_id = F<U32_P>("nk_new_host_id");
            nk_new_owned_id = F<U32_P>("nk_new_owned_id");
            nk_acked_action = F<U32_P>("nk_acked_action");
            nk_ticket = F<I_PBI>("nk_ticket");
            nk_phase_data = F<I_PBI>("nk_phase_data");
            nk_lobby_meta = F<I_PBI>("nk_lobby_meta");
            nk_player_ids = F<I_PBI>("nk_player_ids");
            nk_is_ticket = F<I_S>("nk_is_ticket");
            nk_send = F<Send_>("nk_send");
            nk_set_ready = F<V_PI>("nk_set_ready");
            nk_set_lobby_phase = F<V_PI>("nk_set_lobby_phase");
            nk_notify_loading = F<V_PI>("nk_notify_loading");
            nk_mark_synced = F<V_PI>("nk_mark_synced");
            nk_ls_add_player = F<V_PI>("nk_ls_add_player");
            nk_ext_set_max_unreliable = F<V_PI>("nk_ext_set_max_unreliable");
            nk_set_my_data = F<V_PBI>("nk_set_my_data");
            nk_set_lobby_meta = F<V_PBI>("nk_set_lobby_meta");
            nk_set_phase = F<V_PIBI>("nk_set_phase");
            nk_reset_ready = F<V_P>("nk_reset_ready");
            nk_notify_loaded = F<V_P>("nk_notify_loaded");
            nk_kick = F<V_PIS>("nk_kick");
            nk_leave = F<V_PS>("nk_leave");
            nk_ext_push_ready = F<V_PS>("nk_ext_push_ready");
            nk_player_info = F<I_PIInfo>("nk_player_info");
            nk_player_name = F<I_PIBI>("nk_player_name");
            nk_player_data = F<I_PIBI>("nk_player_data");
            nk_rtt_ms = F<I_PI>("nk_rtt_ms");
            nk_stats = F<V_PStats>("nk_stats");
            nk_last_error = F<I_BI>("nk_last_error");
            nk_define_class = F<DefineClass_>("nk_define_class");
            nk_set_entity = F<SetEnt_>("nk_set_entity");
            nk_remove_entity = F<V_PUI>("nk_remove_entity");
            nk_set_always_relevant = F<V_PUI>("nk_set_always_relevant");
            nk_get_entity = F<GetEnt_>("nk_get_entity");
            nk_latest_entity = F<GetEnt_>("nk_latest_entity");
            nk_entity_class = F<I_PU>("nk_entity_class");
            nk_entity_owner = F<I_PU>("nk_entity_owner");
            nk_entity_ids = F<Ids_>("nk_entity_ids");
            nk_set_viewer = F<Viewer_>("nk_set_viewer");
            nk_set_viewer_entity = F<ViewerEnt_>("nk_set_viewer_entity");
            nk_report_view = F<View_>("nk_report_view");
            nk_send_action = F<Action_>("nk_send_action");
            nk_ack_action = F<V_PIU>("nk_ack_action");
            nk_ls_start = F<LsStart_>("nk_ls_start");
            nk_ls_submit = F<LsSubmit_>("nk_ls_submit");
            nk_ls_next_frame = F<LsNext_>("nk_ls_next_frame");
            nk_ls_report_hash = F<LsHash_>("nk_ls_report_hash");
            nk_ext_push_connected = F<ExtStr_>("nk_ext_push_connected");
            nk_ext_push_disconnected = F<ExtStr_>("nk_ext_push_disconnected");
            nk_ext_push_data = F<ExtData_>("nk_ext_push_data");
            nk_ext_set_rtt = F<ExtRtt_>("nk_ext_set_rtt");
            nk_ext_take_outgoing = F<ExtTake_>("nk_ext_take_outgoing");
            nk_ext_take_kick = F<ExtKick_>("nk_ext_take_kick");
        }

        /// UTF-8, NUL terminated (Mono has no PtrToStringUTF8 / StringToHGlobalUTF8).
        public static byte[] Z(string s)
        {
            var b = System.Text.Encoding.UTF8.GetBytes(s ?? "");
            var z = new byte[b.Length + 1];
            Buffer.BlockCopy(b, 0, z, 0, b.Length);
            return z;
        }
    }
}
