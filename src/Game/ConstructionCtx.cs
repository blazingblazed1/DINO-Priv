// ConstructionSystem keeps ONE player's build mode in private fields (the building being
// placed, its preview entity, position, rotation...). To replay a friend's build commands
// without breaking the local player's own placement we give every friend a private
// "construction context": right before ConstructionSystem runs on a frame that carries a
// friend's construction commands we swap the friend's fields in (and hide the local
// preview entities from the system's queries with Disabled), then swap back afterwards.
using System;
using System.Collections.Generic;
using System.Reflection;
using Components.RawComponents;
using Components.SingletonComponents;
using Systems.ConstructionSystems;
using Unity.Collections;
using Unity.Entities;

namespace DinoNet
{
    public static class ConstructionCtx
    {
        public static int PendingPlayer = -1;      // set by Commands when injecting a construction batch
        // 0.0.4.1 one-shot builds: a friend's build arrives with the building it is for, so it is placed from
        // a clean context in one frame (select + move + build) instead of relying on a preview kept from earlier
        public static bool PendingFresh;
        public static int PendingOrient = -1, PendingObj = -1;
        public static bool PendingHasBuild, PendingSelectStep, SelectConsumed;
        static bool selectStep;
        static int desiredOrient = -1, attemptObj = -1, attemptFrom = -1;
        static bool attemptBuild;
        public static int BuildResult = -1;        // ProcessBuildCommand's answer while a friend's context is active
        public static int RemoteBuildsOk, RemoteBuildsFailed;

        // what the local player last built (read before ConstructionSystem consumes the command)
        public struct LocalBuildInfo { public int Obj, Orient, Frame; public bool Free; public bool HasCell; public int CellX, CellY; }
        // 0.2.5.6: the sender's final grid cell. ConstructionSystem keeps a move "hysteresis" (_lastLocalPos with a
        // soft bound): the cell a cursor position lands on depends on the previous cells, so a friend's fresh context
        // could resolve the same ground position to the neighbouring tile. We seed _lastLocalPos with the sender's cell.
        public static bool PendingHasCell; public static int PendingCellX, PendingCellY;
        static bool wantCell; static int wantX, wantY;
        public static int CellMismatches;
        public static LocalBuildInfo LastLocal = new LocalBuildInfo { Obj = -1, Orient = -1, Frame = -100 };
        static EntityQuery qBuildCmd;

        class Ctx
        {
            public Dictionary<FieldInfo, object> Vals = new Dictionary<FieldInfo, object>();
            public bool Allowed, Enough;
            public int SingletonObjectId = -1;
            public List<Entity> Previews = new List<Entity>();
        }

        static readonly string[] FieldNames =
        {
            "_buildingObjectId", "_currentLocalPosition", "_currentLocalBound", "_hasDelayDiscardBuild", "_isFreeConstruction",
            "_constructionEntity", "_constructionFarmEntity", "_workLinkEntity", "_modeType", "_lastLocalPos",
        };
        static List<FieldInfo> fields;
        static FieldInfo fAllowed, fEnough;
        static PropertyInfo pBoolValue;
        static readonly Dictionary<int, Ctx> remote = new Dictionary<int, Ctx>();
        static Ctx local;
        static int active = -1;
        static EntityQuery qPreview, qSingleton;
        static World qWorld;

        static bool Init()
        {
            if (fields != null) return fields.Count == FieldNames.Length;
            fields = new List<FieldInfo>();
            var t = typeof(ConstructionSystem);
            foreach (var n in FieldNames)
            {
                var f = t.GetField(n, BindingFlags.Instance | BindingFlags.NonPublic);
                if (f == null) { Log.Err("construction: field " + n + " not found (game update?) - friends' building is disabled"); continue; }
                fields.Add(f);
            }
            fAllowed = t.GetField("_constructionIsAllowed", BindingFlags.Instance | BindingFlags.NonPublic);
            fEnough = t.GetField("_resourceEnough", BindingFlags.Instance | BindingFlags.NonPublic);
            if (fAllowed != null) pBoolValue = fAllowed.FieldType.GetProperty("Value");
            return fields.Count == FieldNames.Length;
        }

        static void EnsureQueries()
        {
            if (qWorld == Ecs.W) return;
            qWorld = Ecs.W;
            qPreview = Ecs.EM.CreateEntityQuery(new EntityQueryDesc
            {
                Any = new[] { ComponentType.ReadOnly<CurrentBuildingForConstruction>(), ComponentType.ReadOnly<CurrentFarmFieldForConstruction>() }
            });
            qSingleton = Ecs.EM.CreateEntityQuery(ComponentType.ReadWrite<CurrentConstructionSingleton>());
            qBuildCmd = Ecs.EM.CreateEntityQuery(ComponentType.ReadOnly<Components.Commands.ConstructionCommands.ConstructionBuildCommand>());
        }

        static Ctx Fresh(ConstructionSystem sys)
        {
            var c = new Ctx();
            foreach (var f in fields)
            {
                object v = f.GetValue(sys);
                switch (f.Name)
                {
                    case "_buildingObjectId": v = -1; break;
                    case "_constructionEntity":
                    case "_constructionFarmEntity":
                    case "_workLinkEntity": v = Entity.Null; break;
                    case "_hasDelayDiscardBuild":
                    case "_isFreeConstruction": v = false; break;
                    case "_lastLocalPos": v = new Unity.Mathematics.int2(-9999); break;
                    case "_modeType": v = Enum.ToObject(f.FieldType, 0); break;   // Single
                }
                c.Vals[f] = v;
            }
            c.Allowed = true; c.Enough = true; c.SingletonObjectId = -1;
            return c;
        }

        static Ctx Capture(ConstructionSystem sys)
        {
            var c = new Ctx();
            foreach (var f in fields) c.Vals[f] = f.GetValue(sys);
            try
            {
                if (fAllowed != null) c.Allowed = (bool)pBoolValue.GetValue(fAllowed.GetValue(sys), null);
                if (fEnough != null) c.Enough = (bool)pBoolValue.GetValue(fEnough.GetValue(sys), null);
            }
            catch { }
            try { if (qSingleton.CalculateEntityCount() == 1) c.SingletonObjectId = qSingleton.GetSingleton<CurrentConstructionSingleton>().ObjectId; } catch { }
            return c;
        }

        static void Apply(ConstructionSystem sys, Ctx c)
        {
            foreach (var kv in c.Vals) kv.Key.SetValue(sys, kv.Value);
            try
            {
                if (fAllowed != null) { object r = fAllowed.GetValue(sys); pBoolValue.SetValue(r, c.Allowed, null); fAllowed.SetValue(sys, r); }
                if (fEnough != null) { object r = fEnough.GetValue(sys); pBoolValue.SetValue(r, c.Enough, null); fEnough.SetValue(sys, r); }
            }
            catch { }
            try { if (qSingleton.CalculateEntityCount() == 1) qSingleton.SetSingleton(new CurrentConstructionSingleton { ObjectId = c.SingletonObjectId }); } catch { }
        }

        static List<Entity> ActivePreviews()
        {
            var a = qPreview.ToEntityArray(Allocator.Temp);
            var l = new List<Entity>(a.Length);
            for (int i = 0; i < a.Length; i++) l.Add(a[i]);
            a.Dispose();
            return l;
        }

        // children of a preview the game itself had disabled stay disabled when we re-enable it
        static readonly Dictionary<Entity, List<Entity>> preDisabled = new Dictionary<Entity, List<Entity>>();

        static void SetDisabled(List<Entity> list, bool off)
        {
            var em = Ecs.EM;
            foreach (var e in list)
            {
                if (!em.Exists(e)) continue;
                bool has = em.HasComponent<Disabled>(e);
                if (off && !has)
                {
                    var keep = new List<Entity>();
                    if (em.HasComponent<LinkedEntityGroup>(e))
                    {
                        var g = em.GetBuffer<LinkedEntityGroup>(e);
                        for (int i = 0; i < g.Length; i++) { var c = g[i].Value; if (c != e && em.Exists(c) && em.HasComponent<Disabled>(c)) keep.Add(c); }
                    }
                    preDisabled[e] = keep;
                    em.SetEnabled(e, false);
                }
                else if (!off && has)
                {
                    em.SetEnabled(e, true);
                    List<Entity> keep;
                    if (preDisabled.TryGetValue(e, out keep))
                    {
                        foreach (var c in keep) if (em.Exists(c) && !em.HasComponent<Disabled>(c)) em.AddComponent<Disabled>(c);
                        preDisabled.Remove(e);
                    }
                }
            }
        }

        /// Wall sections and trap drawing keep extra state we don't swap yet: not shared.
        public static bool LocalModeShareable()
        {
            if (!Init()) return false;
            try
            {
                var sys = Ecs.W.GetExistingSystem<ConstructionSystem>();
                if (sys == null) return true;
                foreach (var f in fields)
                    if (f.Name == "_modeType")
                    {
                        string m = f.GetValue(sys).ToString();
                        return m != "Section" && m != "TrapsDraw";
                    }
            }
            catch { }
            return true;
        }

        static bool EntityOk(Ctx c, string name)
        {
            foreach (var kv in c.Vals)
                if (kv.Key.Name == name) { var e = (Entity)kv.Value; return e == Entity.Null || Ecs.EM.Exists(e); }
            return true;
        }

        /// Prefix of ConstructionSystem.OnUpdateSimulation.
        public static void Before(ConstructionSystem sys)
        {
            if (PendingPlayer < 0) { RecordLocal(sys); return; }
            int who = PendingPlayer; PendingPlayer = -1;
            wantCell = false;
            bool fresh = PendingFresh; PendingFresh = false;
            desiredOrient = fresh ? PendingOrient : -1; PendingOrient = -1;
            attemptBuild = PendingHasBuild; PendingHasBuild = false;
            selectStep = PendingSelectStep; PendingSelectStep = false;
            attemptObj = PendingObj; PendingObj = -1; attemptFrom = who;
            BuildResult = -1;
            if (!Init()) return;
            try
            {
                EnsureQueries();
                if (fresh)
                {
                    Ctx old;
                    if (remote.TryGetValue(who, out old)) { SetDisabled(old.Previews, false); DestroyAll(old.Previews); remote.Remove(who); }
                }
                local = Capture(sys);
                local.Previews = ActivePreviews();
                SetDisabled(local.Previews, true);
                Ctx c;
                if (!remote.TryGetValue(who, out c) || !EntityOk(c, "_constructionEntity") || !EntityOk(c, "_constructionFarmEntity"))
                {
                    if (c != null) { SetDisabled(c.Previews, false); DestroyAll(c.Previews); }
                    c = Fresh(sys);
                    remote[who] = c;
                }
                SetDisabled(c.Previews, false);
                Apply(sys, c);
                active = who;
                wantCell = PendingHasCell; wantX = PendingCellX; wantY = PendingCellY; PendingHasCell = false;
                if (wantCell)
                    foreach (var f in fields)
                        if (f.Name == "_lastLocalPos") f.SetValue(sys, new Unity.Mathematics.int2(wantX, wantY));
            }
            catch (Exception e)
            {
                Log.Throttled("ctxin", "construction swap-in: " + e);
                active = -1;
            }
        }

        /// Postfix (and finalizer) of ConstructionSystem.OnUpdateSimulation.
        public static void After(ConstructionSystem sys)
        {
            if (active < 0) return;
            int who = active; active = -1;
            desiredOrient = -1;
            try
            {
                var c = Capture(sys);
                if (selectStep)
                {
                    selectStep = false; SelectConsumed = true;
                    int sel = -1;
                    foreach (var f in fields) if (f.Name == "_buildingObjectId") sel = (int)c.Vals[f];
                    if (sel != attemptObj) Log.Msg("construction: selecting " + Net.NameOf(who) + "'s building " + attemptObj + " didn't take here (now " + sel + ")");
                }
                if (attemptBuild)
                {
                    attemptBuild = false;
                    string obj = attemptObj >= 0 ? attemptObj.ToString() : "?";
                    if (BuildResult == 1) { RemoteBuildsOk++; Log.Dbg("construction: " + Net.NameOf(who) + "'s building " + obj + " placed"); }
                    else
                    {
                        RemoteBuildsFailed++;
                        int cur = -1;
                        foreach (var f in fields) if (f.Name == "_buildingObjectId") cur = (int)c.Vals[f];
                        string why = BuildResult < 0 ? "the build step did not run" : cur < 0 && attemptObj < 0 ? "no building was selected for them here" : !c.Allowed ? "the spot is blocked here" : !c.Enough ? "not enough resources here" : "the game refused it";
                        Log.Msg("construction: " + Net.NameOf(who) + "'s building " + obj + " was NOT placed here - " + why + " (allowed " + c.Allowed + ", resources " + c.Enough + ", building " + cur + ")");
                        Flight.Add("[build] failed from " + who + " obj " + obj + ": " + why);
                    }
                }
                c.Previews = ActivePreviews();
                SetDisabled(c.Previews, true);
                remote[who] = c;
                SetDisabled(local.Previews, false);
                if (!EntityOk(local, "_constructionEntity") || !EntityOk(local, "_constructionFarmEntity"))
                {
                    Log.Warn("construction: your own placement was interrupted by a friend's build");
                    local = Fresh(sys);
                }
                Apply(sys, local);
            }
            catch (Exception e) { Log.Throttled("ctxout", "construction swap-out: " + e); }
        }

        /// Local player's frame: remember what is being built before the game clears build mode.
        static void RecordLocal(ConstructionSystem sys)
        {
            try
            {
                if (!Net.Active || !Init()) return;
                EnsureQueries();
                if (qBuildCmd.IsEmptyIgnoreFilter) return;
                var info = new LocalBuildInfo { Obj = -1, Orient = -1, Frame = Mod.Frame };
                Entity ce = Entity.Null;
                foreach (var f in fields)
                {
                    if (f.Name == "_buildingObjectId") info.Obj = (int)f.GetValue(sys);
                    else if (f.Name == "_isFreeConstruction") info.Free = (bool)f.GetValue(sys);
                    else if (f.Name == "_constructionEntity") ce = (Entity)f.GetValue(sys);
                }
                var em = Ecs.EM;
                if (ce != Entity.Null && em.Exists(ce) && em.HasComponent<Orientation>(ce)) info.Orient = (int)em.GetComponentData<Orientation>(ce).value;
                LastLocal = info;
            }
            catch (Exception e) { Log.Throttled("ctxrec", "construction record: " + e.Message); }
        }

        /// Prefix of InitializeConstruction: a friend's one-shot build starts in the friend's rotation.
        public static void OverrideOrientation(ref Utility.EnumsStorage.OrientationType o)
        {
            if (active >= 0 && desiredOrient >= 0) o = (Utility.EnumsStorage.OrientationType)desiredOrient;
        }

        public static bool RemoteActive => active >= 0;

        /// Postfix of ProcessBuildCommand (both the local player's and a friend's builds).
        public static void BuildDone(ConstructionSystem sys, bool placed)
        {
            if (!placed || !Init()) return;
            Unity.Mathematics.int2 cell = default(Unity.Mathematics.int2); bool have = false;
            foreach (var f in fields) if (f.Name == "_currentLocalPosition") { cell = (Unity.Mathematics.int2)f.GetValue(sys); have = true; }
            if (!have) return;
            if (active < 0)
            {
                // our own build: remember the cell so it travels with the build
                if (LastLocal.Frame == Mod.Frame) { LastLocal.HasCell = true; LastLocal.CellX = cell.x; LastLocal.CellY = cell.y; }
            }
            else if (wantCell && (cell.x != wantX || cell.y != wantY))
            {
                CellMismatches++;
                Log.Msg("construction: " + Net.NameOf(active) + "'s building " + attemptObj + " placed at cell " + cell.x + "," + cell.y + " but they built it at " + wantX + "," + wantY);
            }
        }

        static void DestroyAll(List<Entity> l)
        {
            var em = Ecs.EM;
            foreach (var e in l) if (em.Exists(e)) em.DestroyEntity(e);
        }

        public static void Drop(int player)
        {
            Ctx c;
            if (!remote.TryGetValue(player, out c)) return;
            remote.Remove(player);
            try { if (Ecs.Ok) DestroyAll(c.Previews); } catch { }
        }

        public static void DropAll()
        {
            foreach (var kv in remote) { try { if (Ecs.Ok) DestroyAll(kv.Value.Previews); } catch { } }
            remote.Clear();
            PendingPlayer = -1; active = -1;
        }
    }
}
