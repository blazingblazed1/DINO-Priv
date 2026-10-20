// Command relay. DINO turns every player action into a short-lived "command entity"
// (CommandDataTag + a tag like UnitMoveCommand + parameter components) created by
// UIInputHandler and consumed by the game systems. We:
//  * capture the local player's gameplay command entities (one batch per frame) right
//    before UIInputHandler destroys them, with the sender's selection as net ids;
//  * send the batch to everyone (the host relays);
//  * on other PCs re-create the same command entities in a quiet frame, with the
//    sender's units temporarily marked Selected (and the sender's own construction
//    context swapped into ConstructionSystem for build commands).
// The sender's own command already ran locally (no input lag).
using System;
using System.Collections.Generic;
using System.Reflection;
using Components.Commands;
using Components.RawComponents;
using NetKit;
using Systems.GameStateSystems;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace DinoNet
{
    public static class Commands
    {
        public enum Cat : byte { Local = 0, Selection = 1, Global = 2, Construction = 3, Market = 4, Spell = 5 }

        // ------------------------------------------------------------------ type table
        static List<Type> types;                              // index -> type (sorted by full name)
        static Dictionary<Type, int> typeIdx;
        static Dictionary<Type, Cat> cats;
        static HashSet<Type> selectionFamily;                 // local selection commands (frame not quiet)
        static MethodInfo emGetGeneric, emSetGeneric;
        static readonly Dictionary<Type, MethodInfo> getters = new Dictionary<Type, MethodInfo>();
        static readonly Dictionary<Type, MethodInfo> setters = new Dictionary<Type, MethodInfo>();
        static readonly Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>();

        static readonly string[] SelectionNames =
        {
            "UnitTrainCommand", "UnitTrainCancelCommand", "UnitTrainerFlagActionCommand", "UnitTrainerFlagMoveCommand",
            "UnitTrainerFlagSetCommand", "CancelFlagCommand", "WorkLinkFlagActionCommand", "WorkLinkSetCommand",
            "WorkersDistributionLimitChangeCommand", "WorkersRadiusChangeCommand", "BuildingUpgradeCommand",
            "BuildingCancelUpgradeCommand", "BuildingRepairCommand", "BuildingAutoRepairCommand", "BuildingDestroyCommand",
            "BuildingQueueUpCommand", "BuildingConstructionPauseCommand", "GateToggleCommand", "TowerActionSwitchCommand",
            "UpdateFormationDensityCommand", "UpdateFormationFactorCommand", "ApplyFormationOrderCommand",
            "ApplyFormationPresetCommand", "SelfProducerPauseCommand", "ToggleBurialAutoConvertCommand", "TowerSwitchPlaceCommand",
        };
        static readonly string[] GlobalNames =
        {
            "TimeControlCommand", "RunResearchCommand", "AutoRepairGlobalCommand", "ResourceRestrictionCommand",
            "GateAllOpenCloseCommand", "ToggleMarketDealRepeatStateCommand", "SpellUndeadResurrection", "SandboxSpawnCommand",
            "SandboxSpawnBrushSwitchStateCommand", "CancelSandboxSpawnCommand", "TogglePlayerResurrectAutoConvertCommand",
        };
        static readonly string[] ConstructionNames =
        {
            "ConstructionSelectCommand", "FreeConstructionSelectCommand", "ConstructionMoveCommand", "ConstructionRotateCommand",
            "ConstructionDrawCommand", "ConstructionBuildCommand", "ConstructionCancelCommand", "ConstructionCancelTotalCommand",
        };

        static void EnsureTypes()
        {
            if (types != null) return;
            var asm = typeof(CommandDataTag).Assembly;
            types = new List<Type>();
            foreach (var t in asm.GetTypes())
            {
                if (!t.IsValueType || t.IsEnum || t.IsGenericType) continue;
                if (!typeof(IComponentData).IsAssignableFrom(t)) continue;
                string ns = t.Namespace ?? "";
                // every plain component of the game can ride along on a command entity (e.g. the
                // Orientation UIBridge adds for "copy building"), except UI bridge commands which
                // would replay the sender's UI clicks on the receiver
                if (ns.StartsWith("Components.RawComponents.UIComponents")) continue;
                types.Add(t);
            }
            types.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
            typeIdx = new Dictionary<Type, int>();
            for (int i = 0; i < types.Count; i++) typeIdx[types[i]] = i;
            cats = new Dictionary<Type, Cat>();
            selectionFamily = new HashSet<Type>();
            var byName = new Dictionary<string, Type>();
            foreach (var t in types) byName[t.Name] = t;
            foreach (var t in types)
            {
                string ns = t.Namespace ?? "";
                if (ns == "Components.Commands.UnitCommands") cats[t] = Cat.Selection;
                // pre-select is only the hover/drag highlight: it must not count as "the player is selecting"
                if ((ns == "Components.Commands.SelectionCommands" || ns == "Components.Commands.SquadCommands") && t.Name != "BorderPreSelectCommand") selectionFamily.Add(t);
            }
            foreach (var n in SelectionNames) { Type t; if (byName.TryGetValue(n, out t)) cats[t] = Cat.Selection; }
            foreach (var n in GlobalNames) { Type t; if (byName.TryGetValue(n, out t)) cats[t] = Cat.Global; }
            foreach (var n in ConstructionNames) { Type t; if (byName.TryGetValue(n, out t)) cats[t] = Cat.Construction; }
            { Type t; if (byName.TryGetValue("MarketDealCommand", out t)) cats[t] = Cat.Market; }
            { Type t; if (byName.TryGetValue("SpellCastCommand", out t)) cats[t] = Cat.Spell; }
            byName.TryGetValue("SpellTypeChangeCommand", out tSpellType);
            byName.TryGetValue("SpellCastCommand", out tSpellCast);
            byName.TryGetValue("SpellCancelCommand", out tSpellCancel);
            foreach (var m in typeof(EntityManager).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!m.IsGenericMethodDefinition) continue;
                var ps = m.GetParameters();
                if (m.Name == "GetComponentData" && ps.Length == 1 && ps[0].ParameterType == typeof(Entity)) emGetGeneric = m;
                if (m.Name == "SetComponentData" && ps.Length == 2 && ps[0].ParameterType == typeof(Entity)) emSetGeneric = m;
            }
            Log.Msg("commands: " + types.Count + " command types, " + cats.Count + " relayed" + (emGetGeneric == null || emSetGeneric == null ? " (NO EntityManager accessors!)" : ""));
        }

        static Type tSpellType, tSpellCast, tSpellCancel;

        static Cat CatOf(Type t) { Cat c; return cats.TryGetValue(t, out c) ? c : Cat.Local; }

        static FieldInfo[] Fields(Type t)
        {
            FieldInfo[] f;
            if (fieldCache.TryGetValue(t, out f)) return f;
            f = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Array.Sort(f, (a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
            fieldCache[t] = f;
            return f;
        }

        static bool IsTag(Type t) => Fields(t).Length == 0;

        static object GetData(EntityManager em, Entity e, Type t)
        {
            MethodInfo m;
            if (!getters.TryGetValue(t, out m)) { m = emGetGeneric.MakeGenericMethod(t); getters[t] = m; }
            return m.Invoke(em, new object[] { e });
        }

        static void SetData(EntityManager em, Entity e, Type t, object v)
        {
            MethodInfo m;
            if (!setters.TryGetValue(t, out m)) { m = emSetGeneric.MakeGenericMethod(t); setters[t] = m; }
            m.Invoke(em, new object[] { e, v });
        }

        // ------------------------------------------------------------------ value codec
        /// Set by Read when a command names an object this PC can't find (the command is skipped).
        public static bool Unresolved;

        public static void Write(Packer p, Type t, object v)
        {
            if (t == typeof(Entity))
            {
                var e = (Entity)v;
                if (e == Entity.Null) { p.UV(0); return; }
                uint id = WorldSync.NetIdOf(e);
                p.UV(id != 0 ? id : uint.MaxValue);   // MaxValue: something we can't name on other PCs
                return;
            }
            if (t.IsEnum) { p.SV(Convert.ToInt64(v)); return; }
            if (t.IsPointer || t == typeof(IntPtr) || t == typeof(UIntPtr)) return;
            if (t == typeof(float)) { p.F32((float)v); return; }
            if (t == typeof(double)) { p.F64((double)v); return; }
            if (t == typeof(bool)) { p.Bool((bool)v); return; }
            if (t == typeof(int)) { p.SV((int)v); return; }
            if (t == typeof(uint)) { p.UV((uint)v); return; }
            if (t == typeof(long)) { p.SV((long)v); return; }
            if (t == typeof(ulong)) { p.UV((ulong)v); return; }
            if (t == typeof(short)) { p.SV((short)v); return; }
            if (t == typeof(ushort)) { p.UV((ushort)v); return; }
            if (t == typeof(byte)) { p.U8((byte)v); return; }
            if (t == typeof(sbyte)) { p.SV((sbyte)v); return; }
            if (t == typeof(char)) { p.UV((char)v); return; }
            if (t.IsValueType)
            {
                foreach (var f in Fields(t))
                {
                    var ft = f.FieldType;
                    if (ft.IsPointer || ft == typeof(IntPtr)) continue;
                    Write(p, ft, f.GetValue(v));
                }
                return;
            }
            // reference types (managed) are never part of a command: nothing written
        }

        public static object Read(Unpacker u, Type t)
        {
            if (t == typeof(Entity))
            {
                uint id = (uint)u.UV();
                if (id == 0) return Entity.Null;
                var e = id == uint.MaxValue ? Entity.Null : WorldSync.EntityOf(id);
                if (e == Entity.Null) Unresolved = true;
                return e;
            }
            if (t.IsEnum) return Enum.ToObject(t, u.SV());
            if (t.IsPointer || t == typeof(IntPtr) || t == typeof(UIntPtr)) return null;
            if (t == typeof(float)) return u.F32();
            if (t == typeof(double)) return u.F64();
            if (t == typeof(bool)) return u.Bool();
            if (t == typeof(int)) return (int)u.SV();
            if (t == typeof(uint)) return (uint)u.UV();
            if (t == typeof(long)) return u.SV();
            if (t == typeof(ulong)) return u.UV();
            if (t == typeof(short)) return (short)u.SV();
            if (t == typeof(ushort)) return (ushort)u.UV();
            if (t == typeof(byte)) return (byte)u.U8();
            if (t == typeof(sbyte)) return (sbyte)u.SV();
            if (t == typeof(char)) return (char)u.UV();
            if (t.IsValueType)
            {
                object box = Activator.CreateInstance(t);
                foreach (var f in Fields(t))
                {
                    var ft = f.FieldType;
                    if (ft.IsPointer || ft == typeof(IntPtr)) continue;
                    f.SetValue(box, Read(u, ft));
                }
                return box;
            }
            return null;
        }

        // ------------------------------------------------------------------ capture (sender)
        static readonly HashSet<Entity> injected = new HashSet<Entity>();
        // Echo guard: payloads we injected in the last few frames are never captured and sent again
        // (0.0.3.4 test: a 289-command construction batch bounced host <-> friend about once a second).
        static readonly Dictionary<ulong, int> recentInjected = new Dictionary<ulong, int>();
        public static int EchoesBlocked;
        static ulong Hash(byte[] b)
        {
            ulong h = 1469598103934665603UL;
            for (int i = 0; i < b.Length; i++) { h ^= b[i]; h *= 1099511628211UL; }
            return h;
        }
        /// Injected command entities stay in the set until the game has destroyed them. Clearing it every
        /// input pass let a second pass in the same frame re-capture them (the "echoes" seen in every log).
        static readonly HashSet<Entity> sentLocal = new HashSet<Entity>();
        public static int DupSkipped;

        static void PruneInjectedEntities()
        {
            var em = Ecs.EM;
            if (sentLocal.Count > 0) { sentLocal.RemoveWhere(e => !em.Exists(e)); if (sentLocal.Count > 4000) sentLocal.Clear(); }
            if (injected.Count == 0) return;
            injected.RemoveWhere(e => !em.Exists(e));
            if (injected.Count > 2000) injected.Clear();
        }

        static bool RecentlyInjected(byte[] payload)
        {
            int f;
            if (!recentInjected.TryGetValue(Hash(payload), out f)) return false;
            return Mod.Frame - f <= 4;
        }
        static void PruneInjected()
        {
            if (recentInjected.Count < 512) return;
            var old = new List<ulong>();
            foreach (var kv in recentInjected) if (Mod.Frame - kv.Value > 4) old.Add(kv.Key);
            foreach (var k in old) recentInjected.Remove(k);
        }
        static EntityQuery qCmd, qSelected, qSpell;
        static World qWorld;
        static uint seq;
        static bool pendingMove;
        static float lastModeWarn = -100f;            // construction move not yet sent
        static byte[] pendingMoveBytes;
        public static int SentCount, RecvCount, AppliedCount, DroppedCount;
        // unit-order diagnostics, summarised in the log every 10 s
        public static int UnitSent, UnitSentUnknown, UnitSentUnshareable, UnitApplied, UnitRecvUnknown, UnitWaitedForMatch, WaitFrames;
        static int lastSummaryHash; static float summaryT;
        static void Summary()
        {
            float now = Time.realtimeSinceStartup;
            if (now < summaryT) return;
            summaryT = now + 10f;
            int h = UnitSent * 31 + UnitApplied * 17 + UnitSentUnknown * 7 + UnitRecvUnknown * 3 + queue.Count + RecvCount;
            if (h == lastSummaryHash) return;
            lastSummaryHash = h;
            Log.Msg("orders: sent " + SentCount + " (unit orders " + UnitSent + ", selected units with no shared id " + UnitSentUnknown + ", workers/previews not shared " + UnitSentUnshareable + ")"
                    + " | received " + RecvCount + " applied " + AppliedCount + " (unit orders " + UnitApplied + ", named units not found here " + UnitRecvUnknown + ", waited for a match " + UnitWaitedForMatch + ")"
                    + " | waiting " + queue.Count + ", waited " + WaitFrames + " frames, echoes blocked " + EchoesBlocked + ", dropped " + DroppedCount
                    + (Net.IsHost ? " | own unit orders replayed " + HostReplayed + " (run natively " + HostNative + ")" : "")
                    + " | builds sent " + BuildsSent + (BuildsSentBare > 0 ? " (" + BuildsSentBare + " without building)" : "") + ", friends' builds placed " + ConstructionCtx.RemoteBuildsOk + " failed " + ConstructionCtx.RemoteBuildsFailed
                    + " | shared objects " + WorldSync.Bound + (Net.IsHost ? "" : " of " + WorldSync.HostObjects));
        }

        static void EnsureQueries()
        {
            if (qWorld == Ecs.W) return;
            qWorld = Ecs.W;
            qCmd = Ecs.EM.CreateEntityQuery(ComponentType.ReadOnly<CommandDataTag>());
            qSelected = Ecs.EM.CreateEntityQuery(ComponentType.ReadOnly<Selected>());
            qSpell = Ecs.EM.CreateEntityQuery(ComponentType.ReadOnly<Components.SingletonComponents.SpellCastSingleton>());
        }

        static bool Live => Net.Active && WorldSync.Running && !Game.Held && Ecs.GameRunning;

        // ------------------------------------------------------------------ routing
        // Every order goes through the host: a friend sends to the host only, the host applies it
        // and passes it on to everyone else. Orders that spend shared resources or change shared
        // state (research, market, training, upgrades, speed...) are NOT run on the friend's PC
        // right away: the host runs them first and sends them back to the friend too, so a refused
        // order is refused on every PC instead of only on some.
        static readonly string[] AuthNames =
        {
            "RunResearchCommand", "MarketDealCommand", "ToggleMarketDealRepeatStateCommand", "ResourceRestrictionCommand",
            "AutoRepairGlobalCommand", "GateAllOpenCloseCommand", "TimeControlCommand", "TogglePlayerResurrectAutoConvertCommand",
            "UnitTrainCommand", "UnitTrainCancelCommand", "BuildingUpgradeCommand", "BuildingCancelUpgradeCommand", "BuildingRepairCommand",
        };
        static HashSet<Type> authTypes;

        static bool IsAuth(Type t)
        {
            if (authTypes == null)
            {
                authTypes = new HashSet<Type>();
                foreach (var ty in types)
                {
                    if (Array.IndexOf(AuthNames, ty.Name) >= 0) authTypes.Add(ty);
                    // unit orders (move, attack, patrol, hold, abilities...) too: a friend's order runs on the
                    // host first and comes back, exactly like the host's own orders (0.0.3.9: a friend's units
                    // walked ahead locally and were pulled back by the corrections - stutter on both screens).
                    // "Switch" commands only change the cursor mode on the clicking PC: those stay local.
                    else if (ty.Namespace == "Components.Commands.UnitCommands" && ty.Name.IndexOf("Switch", StringComparison.Ordinal) < 0) authTypes.Add(ty);
                }
            }
            return authTypes.Contains(t);
        }

        static byte[] WithOrigin(int origin, byte[] body, int bodyOffset)
        {
            byte[] head = new Packer().UV((ulong)origin).ToArray();
            var all = new byte[head.Length + body.Length - bodyOffset];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            Buffer.BlockCopy(body, bodyOffset, all, head.Length, body.Length - bodyOffset);
            return all;
        }

        static int VarLen(byte[] d) { int n = 0; while (n < d.Length && (d[n] & 0x80) != 0) n++; return Math.Min(d.Length, n + 1); }

        static bool IsTimeControl(List<Type> ts) { foreach (var t in ts) if (t.Name == "TimeControlCommand") return true; return false; }

        static void SendCmd(byte[] body)
        {
            var all = WithOrigin(Net.MyId, body, 0);
            if (Net.IsHost) Net.ToAll(Msg.Cmd, all);
            else Net.ToHost(Msg.Cmd, all);
        }

        /// Host: pass a friend's order on (and back to the friend when the host decides it).
        static void Relay(int origin, byte[] all, bool echo)
        {
            foreach (var pl in Net.Players)
            {
                if (pl.Id == Net.MyId) continue;
                if (pl.Id == origin && !echo) continue;
                Net.ToPlayer(pl.Id, Msg.Cmd, all);
            }
        }

        /// Friend, postfix of UIInputHandler: orders the host must decide are sent and removed here,
        /// before any game system runs them on this PC.
        /// Host (0.0.4.7): the host's own unit orders take the same route as a friend's - captured here and
        /// replayed as an injected command on every PC, the host included. A natively-run order and a
        /// replayed one could end in different formation spots, so friends' copies of the host's units
        /// were pulled back all the time (choppy on friends only; friends' own orders were smooth).
        static void CaptureAuthoritative()
        {
            bool host = Net.IsHost;
            var em = Ecs.EM;
            var ents = qCmd.ToEntityArray(Allocator.Temp);
            var kill = new List<Entity>();
            try
            {
                for (int i = 0; i < ents.Length; i++)
                {
                    var e = ents[i];
                    if (injected.Contains(e)) continue;
                    var cts = em.GetComponentTypes(e, Allocator.Temp);
                    try
                    {
                        Type tag = null;
                        foreach (var ct in cts) { var t = ct.GetManagedType(); if (t != null && IsAuth(t)) { tag = t; break; } }
                        if (tag == null) continue;
                        if (host && tag.Namespace != "Components.Commands.UnitCommands") continue;   // host: only unit orders take the replay route
                        if (em.HasComponent<CommandViewUpdateTag>(e)) continue;   // formation preview while dragging: local only
                        if (tag.Name == "TimeControlCommand" && em.HasComponent<CommandParameterData>(e) && em.GetComponentData<CommandParameterData>(e).parameter == 1) continue;
                        var cat = CatOf(tag);
                        var p = new Packer();
                        int n = 0;
                        foreach (var ct in cts) { var t = ct.GetManagedType(); if (t != null && typeIdx.ContainsKey(t)) n++; }
                        p.UV((ulong)n);
                        foreach (var ct in cts)
                        {
                            var t = ct.GetManagedType();
                            if (t == null || !typeIdx.ContainsKey(t)) continue;
                            p.UV((ulong)typeIdx[t]);
                            if (IsTag(t)) continue;
                            Write(p, t, GetData(em, e, t));
                        }
                        var batch = new Packer();
                        batch.UV(++seq).U8((int)cat).UV((ulong)types.Count);
                        var ids = new List<uint>();
                        int selCount = 0;
                        if (cat == Cat.Selection)
                        {
                            var sel = qSelected.ToEntityArray(Allocator.Temp);
                            selCount = sel.Length;
                            for (int k = 0; k < sel.Length; k++) { uint id = WorldSync.NetIdOf(sel[k]); if (id != 0) ids.Add(id); }
                            sel.Dispose();
                        }
                        // host: a unit not shared yet (just trained) would be left out of the replay - run it natively then
                        if (host && ids.Count < selCount) { HostNative++; continue; }
                        batch.UV((ulong)ids.Count);
                        foreach (var id in ids) batch.UV(id);
                        if (cat == Cat.Market) { batch.Bool(true); Write(batch, GameState.UIState.marketPanelDeal.GetType(), GameState.UIState.marketPanelDeal); }
                        else batch.Bool(false);
                        batch.UV(1); batch.Bytes(p.ToArray());
                        if (host)
                        {
                            // as if it came from a friend: applied below in this same postfix, then passed to everyone
                            OnMessage(Net.MyId, WithOrigin(Net.MyId, batch.ToArray(), 0));
                            SentCount++; UnitSent++; HostReplayed++;
                        }
                        else { SendCmd(batch.ToArray()); SentCount++; AuthSent++; }
                        kill.Add(e);
                        Log.Dbg("order sent to the host for approval: " + tag.Name + " sel " + ids.Count);
                    }
                    finally { cts.Dispose(); }
                }
            }
            finally { ents.Dispose(); }
            foreach (var e in kill) if (em.Exists(e)) em.DestroyEntity(e);
        }
        public static int AuthSent, HostReplayed, HostNative;

        /// Friend while frozen/syncing: orders that only the host may decide are not run here alone.
        static void DropAuthoritative()
        {
            var em = Ecs.EM;
            var ents = qCmd.ToEntityArray(Allocator.Temp);
            var kill = new List<Entity>();
            try
            {
                for (int i = 0; i < ents.Length; i++)
                {
                    var cts = em.GetComponentTypes(ents[i], Allocator.Temp);
                    try
                    {
                        foreach (var ct in cts)
                        {
                            var t = ct.GetManagedType();
                            if (t == null || !IsAuth(t)) continue;
                            if (t.Name == "TimeControlCommand" && em.HasComponent<CommandParameterData>(ents[i]) && em.GetComponentData<CommandParameterData>(ents[i]).parameter == 1) break;
                            kill.Add(ents[i]); break;
                        }
                    }
                    finally { cts.Dispose(); }
                }
            }
            finally { ents.Dispose(); }
            foreach (var e in kill) if (em.Exists(e)) em.DestroyEntity(e);
        }

        /// Prefix of UIInputHandler.OnUpdateSimulation: the previous frame's command entities still exist.
        public static void BeforeInput()
        {
            RestoreAfterInjection();
            if (!Live) { injected.Clear(); return; }
            EnsureTypes();
            EnsureQueries();
            var em = Ecs.EM;
            var ents = qCmd.ToEntityArray(Allocator.Temp);
            try
            {
                Packer batch = null; int count = 0; Cat batchCat = Cat.Local;
                bool hasDraw = false, hasMoveOnly = false, hasBuild = false, hasSelect = false, moveIncluded = false;
                byte[] moveBytes = null;
                var payloads = new List<byte[]>();
                for (int i = 0; i < ents.Length; i++)
                {
                    var e = ents[i];
                    if (injected.Contains(e)) continue;
                    // the input pass can run twice in one frame while a command entity lives on: send it once
                    // (0.0.4.9 logs: every friend batch arrived twice, e.g. #9 and #10 in the same frame)
                    if (!sentLocal.Add(e)) { DupSkipped++; continue; }
                    if (em.HasComponent<CommandViewUpdateTag>(e)) continue;   // formation preview while dragging: never sent
                    var cts = em.GetComponentTypes(e, Allocator.Temp);
                    try
                    {
                        Type tag = null; Cat cat = Cat.Local;
                        foreach (var ct in cts)
                        {
                            var t = ct.GetManagedType();
                            if (t == null) continue;
                            var c = CatOf(t);
                            if (c != Cat.Local) { tag = t; cat = c; break; }
                        }
                        if (tag == null) continue;
                        // friend: host-approved orders were already sent (and removed) in the postfix;
                        // any left here were made by game logic, which the host runs too
                        if (!Net.IsHost && IsAuth(tag)) continue;
                        if (tag.Name == "TimeControlCommand" && Net.IsHost) continue;   // friends follow the host's speed via the clock sync
                        if (cat == Cat.Spell)
                        {
                            // spells cast where the caster's mouse is: send type + ground position as a macro
                            Unity.Mathematics.float2 pos = Systems.InputSystems.PlayerInputSystem.PlayerInputState.groundPosition;
                            if (em.HasComponent<CommandGroundPositionData>(e)) pos = em.GetComponentData<CommandGroundPositionData>(e).value;
                            int st = 0;
                            try { st = (int)qSpell.GetSingleton<Components.SingletonComponents.SpellCastSingleton>().spellType; } catch { }
                            if (st == 0 && Time.realtimeSinceStartup - lastSpellTypeT < 3f) st = lastSpellType;
                            if (st != 0)
                            {
                                var sp = new Packer().UV(++seq).U8((int)Cat.Spell).UV((ulong)types.Count).UV(0).Bool(false).UV(0);
                                sp.U8(st).F32(pos.x).F32(pos.y);
                                SendCmd(sp.ToArray());
                                SentCount++;
                                Log.Dbg("spell sent type " + st + " at " + pos.x + "," + pos.y);
                            }
                            continue;
                        }
                        if (tag.Name == "TimeControlCommand")
                        {
                            // opening a menu/panel pauses the game locally - never relay that
                            if (em.HasComponent<CommandParameterData>(e) && em.GetComponentData<CommandParameterData>(e).parameter == 1) continue;
                        }
                        var p = new Packer();
                        int n = 0;
                        foreach (var ct in cts) { var t = ct.GetManagedType(); if (t != null && typeIdx.ContainsKey(t)) n++; }
                        p.UV((ulong)n);
                        foreach (var ct in cts)
                        {
                            var t = ct.GetManagedType();
                            if (t == null || !typeIdx.ContainsKey(t)) continue;
                            p.UV((ulong)typeIdx[t]);
                            if (IsTag(t)) continue;
                            Write(p, t, GetData(em, e, t));
                        }
                        byte[] bytes = p.ToArray();
                        if (RecentlyInjected(bytes)) { EchoesBlocked++; continue; }
                        if (cat == Cat.Construction)
                        {
                            if (tag.Name == "ConstructionDrawCommand") hasDraw = true;
                            if (tag.Name == "ConstructionBuildCommand") hasBuild = true;
                            if (tag.Name == "ConstructionSelectCommand" || tag.Name == "FreeConstructionSelectCommand") { hasSelect = true; lastMoveBytes = null; }
                            if (tag.Name == "ConstructionMoveCommand") { moveBytes = bytes; lastMoveBytes = bytes; hasMoveOnly = true; continue; }
                        }
                        payloads.Add(bytes);
                        if (cat > batchCat) batchCat = cat;
                        count++;
                    }
                    finally { cts.Dispose(); }
                }
                // construction moves: only relay the position that matters (before a build/rotate/draw)
                if (hasMoveOnly)
                {
                    if (count > 0 || hasDraw) { payloads.Insert(0, moveBytes); count++; pendingMove = false; moveIncluded = true; if (batchCat < Cat.Construction) batchCat = Cat.Construction; }
                    else { pendingMove = true; pendingMoveBytes = moveBytes; }
                }
                else if (pendingMove && count > 0 && batchCat == Cat.Construction)
                {
                    payloads.Insert(0, pendingMoveBytes); count++; pendingMove = false; moveIncluded = true;
                }
                // a build always carries where it goes (the spot may have been sent with an earlier batch)
                if (hasBuild && !moveIncluded && lastMoveBytes != null) { payloads.Insert(0, lastMoveBytes); count++; }
                if (count == 0) return;
                if (payloads.Count > 64)
                {
                    // no player issues this many orders in one frame: something is re-capturing game state
                    var hist = new Dictionary<string, int>();
                    for (int i = 0; i < ents.Length; i++)
                    {
                        if (!em.Exists(ents[i])) continue;
                        var tcs = em.GetComponentTypes(ents[i], Allocator.Temp);
                        try { foreach (var ct in tcs) { var t = ct.GetManagedType(); if (t == null || CatOf(t) == Cat.Local) continue; int c; hist.TryGetValue(t.Name, out c); hist[t.Name] = c + 1; } }
                        finally { tcs.Dispose(); }
                    }
                    var sbh = new System.Text.StringBuilder();
                    foreach (var kv in hist) sbh.Append(kv.Key).Append('=').Append(kv.Value).Append(' ');
                    Log.Throttled("bigbatch", "order batch of " + payloads.Count + " commands not sent (" + sbh + ") - please send this log");
                    Flight.Dump("big-order-batch");
                    return;
                }
                if (batchCat == Cat.Construction && !ConstructionCtx.LocalModeShareable())
                {
                    if (Time.realtimeSinceStartup - lastModeWarn > 30f) { lastModeWarn = Time.realtimeSinceStartup; Ui.Toast("Section walls and drawn trap lines aren't shared in co-op yet - friends won't see them", 5f); }
                    return;
                }
                batch = new Packer();
                batch.UV(++seq).U8((int)batchCat).UV((ulong)types.Count);
                // selection of the sender (net ids)
                var sel = qSelected.ToEntityArray(Allocator.Temp);
                var ids = new List<uint>();
                int unshareableBefore = UnitSentUnshareable;
                for (int i = 0; i < sel.Length; i++)
                {
                    uint id = WorldSync.NetIdOf(sel[i]);
                    if (id == 0 && Net.IsHost) id = WorldSync.HostRegisterNow(sel[i]);
                    if (id != 0) { ids.Add(id); continue; }
                    string why = WorldSync.WhyNotShared(em, sel[i]);
                    Log.Dbg("selected " + sel[i].Index + " has no shared id (" + (why ?? "not registered yet") + ")");
                    if (why != null && batchCat == Cat.Selection) UnitSentUnshareable++;
                }
                if (batchCat == Cat.Selection) { UnitSent++; if (sel.Length > 0 && ids.Count < sel.Length) UnitSentUnknown += sel.Length - ids.Count - (UnitSentUnshareable - unshareableBefore); }
                sel.Dispose();
                batch.UV((ulong)ids.Count);
                foreach (var id in ids) batch.UV(id);
                // market context (the deal shown in the sender's market panel)
                if (batchCat == Cat.Market)
                {
                    batch.Bool(true);
                    Write(batch, GameState.UIState.marketPanelDeal.GetType(), GameState.UIState.marketPanelDeal);
                }
                else batch.Bool(false);
                batch.UV((ulong)payloads.Count);
                foreach (var b in payloads) batch.Bytes(b);
                // 0.0.4.1: a build names its building so other PCs place it in one go from a clean state
                if (batchCat == Cat.Construction && hasBuild && !hasSelect)
                {
                    var lb = ConstructionCtx.LastLocal;
                    if (lb.Obj >= 0 && Mod.Frame - lb.Frame <= 3)
                    {
                        batch.U8(0xB1).UV((ulong)lb.Obj).Bool(lb.Free).U8(lb.Orient < 0 ? 255 : lb.Orient);
                        BuildsSent++;
                        Flight.Add("[build] sent obj " + lb.Obj + (lb.Free ? " free" : "") + " orient " + lb.Orient);
                    }
                    else { BuildsSentBare++; Log.Dbg("build sent without its building (record " + lb.Obj + " @" + lb.Frame + " now " + Mod.Frame + ")"); }
                }
                SendCmd(batch.ToArray());
                SentCount++;
                Log.Dbg("cmd sent #" + seq + " cat " + batchCat + " cmds " + payloads.Count + " sel " + ids.Count);
            }
            catch (Exception ex) { Log.Throttled("capture", "command capture: " + ex); }
            finally { ents.Dispose(); PruneInjectedEntities(); }
        }

        // ------------------------------------------------------------------ receive / inject
        static byte[] lastMoveBytes;
        public static int BuildsSent, BuildsSentBare;

        class Batch
        {
            public int BuildObj = -1, BuildOrient = -1; public bool BuildFree, HasBuild;
            public int From; public uint Seq; public Cat Cat; public uint[] Sel; public object Deal; public byte[][] Cmds; public float At;
            public int SpellType; public Unity.Mathematics.float2 SpellPos;
            public List<Type> Types;
            public bool WaitedForMatch;
            public byte[] RelayBytes; public bool RelayEcho;   // host: pass on after applying (approved orders)
        }
        static readonly Queue<Batch> queue = new Queue<Batch>();
        static bool selSwapped;
        static readonly List<Entity> savedSel = new List<Entity>();
        static readonly List<Entity> remoteSel = new List<Entity>();
        static bool dealSwapped;
        static object savedDeal;

        public static void OnMessage(int from, byte[] d)
        {
            EnsureTypes();
            try
            {
                var u = new Unpacker(d);
                int origin = (int)u.UV();
                if (Net.IsHost && origin != from) origin = from;          // a friend can only speak for itself
                var b = new Batch { From = origin, At = Time.realtimeSinceStartup };
                b.Seq = (uint)u.UV(); b.Cat = (Cat)u.U8();
                int tc = (int)u.UV();
                if (tc != types.Count) { Log.Throttled("typecount", "command type table differs (" + tc + " vs " + types.Count + ") - different game version?"); }
                int ns = (int)u.UV(); b.Sel = new uint[ns];
                for (int i = 0; i < ns; i++) b.Sel[i] = (uint)u.UV();
                if (u.Bool()) b.Deal = Read(u, GameState.UIState.marketPanelDeal.GetType());
                int nc = (int)u.UV(); b.Cmds = new byte[nc][];
                for (int i = 0; i < nc; i++) b.Cmds[i] = u.Bytes();
                if (b.Cat == Cat.Spell && !u.End) { b.SpellType = u.U8(); b.SpellPos = new Unity.Mathematics.float2(u.F32(), u.F32()); }
                if (b.Cat == Cat.Construction && !u.End && u.U8() == 0xB1)
                {
                    b.BuildObj = (int)u.UV(); b.BuildFree = u.Bool(); int o = u.U8(); b.BuildOrient = o == 255 ? -1 : o;
                }
                b.Types = new List<Type>();
                foreach (var raw in b.Cmds)
                {
                    var pu = new Unpacker(raw);
                    int pn = (int)pu.UV();
                    for (int k = 0; k < pn; k++)
                    {
                        int ti = (int)pu.UV();
                        if (ti < 0 || ti >= types.Count) break;
                        b.Types.Add(types[ti]);
                        if (!IsTag(types[ti])) Read(pu, types[ti]);
                    }
                }
                Unresolved = false;
                RecvCount++;
                bool auth = false;
                foreach (var t in b.Types) if (IsAuth(t)) { auth = true; break; }
                if (Net.IsHost)
                {
                    // game speed is never passed on as an order (pause/faster/slower are relative):
                    // friends follow the host's speed through the clock sync instead
                    if (!IsTimeControl(b.Types))
                    {
                        var relay = WithOrigin(origin, d, VarLen(d));   // the host states the sender
                        if (auth) { b.RelayBytes = relay; b.RelayEcho = true; }   // after the host has applied it
                        else Relay(origin, relay, false);
                    }
                }
                else
                {
                    if (from != 0) return;                                 // orders only come from the host
                    if (origin == Net.MyId && !auth) return;               // never apply our own order twice
                }
                if (!WorldSync.Running) { DroppedCount++; return; }
                queue.Enqueue(b);
                if (queue.Count > 200) { queue.Dequeue(); DroppedCount++; }
            }
            catch (Exception e) { Log.Throttled("cmdrx", "command receive: " + e); }
        }

        /// Postfix of UIInputHandler.OnUpdateSimulation: this frame's local commands exist now.
        static int lastSpellType;
        static float lastSpellTypeT;

        public static void AfterInput()
        {
            if (SpellOverrideFrames > 0) SpellOverrideFrames--;
            if (Live && spellStep < 0 && SpellOverrideFrames == 0)
            {
                // remember the spell the local player has armed (the cast may clear it before we capture)
                try
                {
                    EnsureQueries();
                    if (qSpell.CalculateEntityCount() == 1)
                    {
                        int st = (int)qSpell.GetSingleton<Components.SingletonComponents.SpellCastSingleton>().spellType;
                        if (st != 0) { lastSpellType = st; lastSpellTypeT = Time.realtimeSinceStartup; }
                    }
                }
                catch { }
            }
            if (Live) BlockQuickLoad();
            if (!Live && !Net.IsHost && Net.Active && Ecs.GameRunning) { EnsureTypes(); EnsureQueries(); try { DropAuthoritative(); } catch { } }
            if (Live)
            {
                EnsureTypes(); EnsureQueries();
                try { CaptureAuthoritative(); } catch (Exception e) { Log.Throttled("authcap", "order capture: " + e); }
            }
            if (queue.Count == 0 && spellStep < 0 && buildRest == null) return;
            if (!Live) { if (!Net.Active || !Ecs.GameRunning) queue.Clear(); return; }
            EnsureTypes();
            EnsureQueries();
            var em = Ecs.EM;
            // what the local player is doing this frame
            var localTypes = new HashSet<Type>();
            bool busy = false, localSel = false, localCons = false, localSpell = false;
            var ents = qCmd.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < ents.Length; i++)
                {
                    if (injected.Contains(ents[i])) continue;
                    var cts = em.GetComponentTypes(ents[i], Allocator.Temp);
                    try
                    {
                        foreach (var ct in cts)
                        {
                            var t = ct.GetManagedType();
                            if (t == null) continue;
                            var c = CatOf(t);
                            if (c != Cat.Local || selectionFamily.Contains(t)) localTypes.Add(t);   // tags only: params never collide
                            if (c != Cat.Local || selectionFamily.Contains(t)) busy = true;
                            if (c == Cat.Selection || selectionFamily.Contains(t)) localSel = true;
                            if (c == Cat.Construction) localCons = true;
                            if (t == tSpellType || t == tSpellCast || t == tSpellCancel || t.Name == "SpellTargetMoveCommand") localSpell = true;
                        }
                    }
                    finally { cts.Dispose(); }
                }
            }
            finally { ents.Dispose(); }
            if (busy)
            {
                // a quiet frame is preferred; after 2 s of waiting go ahead only if nothing collides
                // (the game reads each command as a singleton: two of a kind = both ignored)
                if (spellStep >= 0) { if (localSpell) return; goto inject; }
                if (buildRest != null) { if (localCons || !ConstructionCtx.SelectConsumed) return; goto inject; }
                var nb = queue.Peek();
                if (Time.realtimeSinceStartup - nb.At < 0.5f) { WaitFrames++; return; }
                if (nb.Cat == Cat.Spell) { if (localSpell) return; goto inject; }
                if (nb.Cat == Cat.Selection && localSel) { WaitFrames++; return; }
                if (nb.Cat == Cat.Construction && localCons) return;
                if (nb.Types != null) foreach (var t in nb.Types) if (t != typeof(CommandDataTag) && localTypes.Contains(t)) return;
            }
            inject:
            if (spellStep >= 0) { SpellStep(); return; }
            if (buildRest != null)
            {
                // step 2 of a friend's build: only after ConstructionSystem has run with the select
                // (the input pass can run twice before ConstructionSystem runs once)
                if (!ConstructionCtx.SelectConsumed && Time.realtimeSinceStartup - buildRestAt < 1f) return;
                if (busy && localCons) return;
                var br = buildRest; buildRest = null;
                try { Inject(br, true); } catch (Exception e) { Log.Throttled("inject", "build inject: " + e); }
                return;
            }
            {
                var nb = queue.Peek();
                if (nb.Cat == Cat.Selection && !Net.IsHost && nb.Sel != null && Time.realtimeSinceStartup - nb.At < 1.5f)
                {
                    bool missing = false;
                    foreach (var id in nb.Sel) if (!WorldSync.IsBound(id) && WorldSync.MatchExpected(id)) { missing = true; break; }
                    if (missing)
                    {
                        if (!nb.WaitedForMatch) { nb.WaitedForMatch = true; UnitWaitedForMatch++; }
                        WorldSync.BindSoon(); WaitFrames++;
                        return;
                    }
                }
            }
            var b = queue.Dequeue();
            if (b.Cat == Cat.Spell)
            {
                if (b.SpellType == 0 || tSpellType == null || tSpellCast == null) return;
                spell = b; spellStep = 0; SpellStep(); AppliedCount++;
                return;
            }
            try
            {
                Inject(b); AppliedCount++;
                if (b.RelayBytes != null && Net.IsHost) Relay(b.From, b.RelayBytes, b.RelayEcho);
            }
            catch (Exception e) { Log.Throttled("inject", "command inject: " + e); RestoreAfterInjection(); }
        }

        // ---- spell macro: select the friend's spell, cast it at the friend's point, cancel
        static Batch spell;
        static int spellStep = -1;
        public static int SpellOverrideFrames;
        public static Unity.Mathematics.float2 SpellOverridePos;

        static void SpellStep()
        {
            var em = Ecs.EM;
            SpellOverridePos = spell.SpellPos;
            SpellOverrideFrames = 4;
            Entity e;
            switch (spellStep)
            {
                case 0:
                    e = em.CreateEntity(ComponentType.ReadWrite<CommandDataTag>(), ComponentType.ReadWrite(tSpellType), ComponentType.ReadWrite<CommandParameterData>());
                    em.SetComponentData(e, new CommandParameterData { parameter = spell.SpellType });
                    injected.Add(e);
                    spellStep = 1;
                    break;
                case 1:
                    e = em.CreateEntity(ComponentType.ReadWrite<CommandDataTag>(), ComponentType.ReadWrite(tSpellCast), ComponentType.ReadWrite<CommandGroundPositionData>());
                    em.SetComponentData(e, new CommandGroundPositionData { value = spell.SpellPos });
                    injected.Add(e);
                    spellStep = tSpellCancel != null ? 2 : -1;
                    break;
                default:
                    e = em.CreateEntity(ComponentType.ReadWrite<CommandDataTag>(), ComponentType.ReadWrite(tSpellCancel));
                    injected.Add(e);
                    spellStep = -1;
                    Log.Dbg("spell from " + spell.From + " (type " + spell.SpellType + ") cast");
                    break;
            }
        }

        static Batch buildRest; static float buildRestAt;   // a friend's move + build, injected the frame after its select

        static void Inject(Batch b, bool buildStep2 = false)
        {
            var em = Ecs.EM;
            if (b.Cat == Cat.Selection)
            {
                var sel = qSelected.ToEntityArray(Allocator.Temp);
                savedSel.Clear(); remoteSel.Clear();
                for (int i = 0; i < sel.Length; i++) savedSel.Add(sel[i]);
                sel.Dispose();
                foreach (var e in savedSel) if (em.Exists(e)) em.RemoveComponent<Selected>(e);
                foreach (var id in b.Sel)
                {
                    var e = WorldSync.EntityOf(id);
                    if (e == Entity.Null || !em.Exists(e)) continue;
                    if (!em.HasComponent<Selected>(e)) em.AddComponent<Selected>(e);
                    remoteSel.Add(e);
                }
                selSwapped = true;
                RefreshSelectionState();
                UnitApplied++;
                if (remoteSel.Count < b.Sel.Length) UnitRecvUnknown += b.Sel.Length - remoteSel.Count;
                if (remoteSel.Count == 0 && b.Sel.Length > 0) Log.Dbg("cmd from " + b.From + ": none of " + b.Sel.Length + " selected units are known here");
            }
            if (b.Cat == Cat.Market && b.Deal != null)
            {
                savedDeal = GameState.UIState.marketPanelDeal;
                var st = GameState.UIState;
                st.marketPanelDeal = (Components.Structs.MarketDeal)b.Deal;
                GameState.UIState = st;
                dealSwapped = true;
            }
            if (b.Cat == Cat.Construction)
            {
                ConstructionCtx.PendingPlayer = b.From;
                bool build = false;
                if (b.Types != null) foreach (var t in b.Types) if (t.Name == "ConstructionBuildCommand") build = true;
                ConstructionCtx.PendingHasBuild = build;
                ConstructionCtx.PendingObj = b.BuildObj;
                if (build && b.BuildObj >= 0 && !buildStep2)
                {
                    // two-frame build: ConstructionSystem ends its update right after a building is selected
                    // (SwapConstruction returns early), so a select + build in one frame never builds.
                    // Frame 1: clean context + select the sender's building. Frame 2: the batch's move + build.
                    ConstructionCtx.PendingHasBuild = false;
                    ConstructionCtx.PendingSelectStep = true;
                    ConstructionCtx.PendingFresh = true;
                    ConstructionCtx.PendingOrient = b.BuildOrient;
                    var selType = b.BuildFree ? typeof(Components.Commands.ConstructionCommands.FreeConstructionSelectCommand) : typeof(Components.Commands.ConstructionCommands.ConstructionSelectCommand);
                    var se = em.CreateEntity(ComponentType.ReadWrite<CommandDataTag>(), ComponentType.ReadWrite(selType), ComponentType.ReadWrite<CommandParameterData>());
                    em.SetComponentData(se, new CommandParameterData { parameter = b.BuildObj });
                    injected.Add(se);
                    buildRest = b; buildRestAt = Time.realtimeSinceStartup;
                    ConstructionCtx.SelectConsumed = false;
                    Log.Dbg("build from " + b.From + ": selecting building " + b.BuildObj);
                    return;
                }
            }
            foreach (var raw in b.Cmds)
            {
                Unresolved = false;
                var u = new Unpacker(raw);
                int n = (int)u.UV();
                var ctypes = new List<ComponentType>();
                var datas = new List<KeyValuePair<Type, object>>();
                ctypes.Add(ComponentType.ReadWrite<CommandDataTag>());
                for (int i = 0; i < n; i++)
                {
                    int ti = (int)u.UV();
                    if (ti < 0 || ti >= types.Count) throw new Exception("bad command type " + ti);
                    var t = types[ti];
                    if (t != typeof(CommandDataTag)) ctypes.Add(ComponentType.ReadWrite(t));
                    if (!IsTag(t)) datas.Add(new KeyValuePair<Type, object>(t, Read(u, t)));
                }
                if (Unresolved)
                {
                    DroppedCount++;
                    Log.Dbg("cmd from " + b.From + " skipped: it targets something that doesn't exist here");
                    continue;
                }
                var e = em.CreateEntity(ctypes.ToArray());
                foreach (var kv in datas) SetData(em, e, kv.Key, kv.Value);
                injected.Add(e);
                recentInjected[Hash(raw)] = Mod.Frame;
            }
            PruneInjected();
            Log.Dbg("cmd applied from " + b.From + " #" + b.Seq + " cat " + b.Cat + " cmds " + b.Cmds.Length + " sel " + remoteSel.Count + "/" + b.Sel.Length);
        }

        /// Several consumers read the game's cached selection summary (upgradable buildings, trainer
        /// type, auto-repair state) instead of the Selected tag: rebuild it right away.
        static void RefreshSelectionState()
        {
            try
            {
                var em = Ecs.EM;
                GameStateUpdateTasksUtility.UpdateGameState(em, GameStateUpdateTaskType.Selection);
                var sys = Ecs.W.GetExistingSystem<GameStateUpdateHandler>();
                if (sys != null) sys.Update();
            }
            catch (Exception e) { Log.Throttled("selstate", "selection state refresh: " + e.Message); }
        }

        /// Quick-load (F-key) would replace one PC's world: not in co-op.
        static EntityQuery qQuick; static World qQuickW;
        static void BlockQuickLoad()
        {
            try
            {
                if (qQuickW != Ecs.W) { qQuickW = Ecs.W; qQuick = Ecs.EM.CreateEntityQuery(ComponentType.ReadOnly<FastSaveLoadCommand>()); }
                if (qQuick.IsEmptyIgnoreFilter) return;
                Ecs.EM.DestroyEntity(qQuick);
                Ui.Toast("Quick save / load is off in co-op (the host can load a save from the menu)");
            }
            catch (Exception e) { Log.Throttled("quick", "quickload block: " + e.Message); }
        }

        static void RestoreAfterInjection()
        {
            if (selSwapped)
            {
                selSwapped = false;
                try
                {
                    var em = Ecs.EM;
                    foreach (var e in remoteSel) if (em.Exists(e) && em.HasComponent<Selected>(e)) em.RemoveComponent<Selected>(e);
                    foreach (var e in savedSel) if (em.Exists(e) && !em.HasComponent<Selected>(e)) em.AddComponent<Selected>(e);
                }
                catch (Exception e) { Log.Throttled("selrestore", "selection restore: " + e.Message); }
                savedSel.Clear(); remoteSel.Clear();
                RefreshSelectionState();
            }
            if (dealSwapped)
            {
                dealSwapped = false;
                var st = GameState.UIState;
                st.marketPanelDeal = (Components.Structs.MarketDeal)savedDeal;
                GameState.UIState = st;
            }
        }

        public static void Update()
        {
            if (Net.Active && WorldSync.Running) { try { Summary(); } catch { } }
        }

        public static void OnPlayerLeft(int id) { ConstructionCtx.Drop(id); }

        public static void Reset()
        {
            spellStep = -1; SpellOverrideFrames = 0; buildRest = null;
            queue.Clear();
            injected.Clear();
            pendingMove = false;
            try { if (Ecs.Ok) RestoreAfterInjection(); } catch { }
            ConstructionCtx.DropAll();
        }
    }
}
