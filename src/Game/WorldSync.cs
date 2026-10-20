// Keeps every PC's world close to the host's while everyone runs the full simulation.
//  * The host registers every unit / enemy / building as a NetKit entity (objectId, x, z, hp).
//    NetKit only sends what changed, at a per-player rate, inside the host's upload budget.
//  * Friends bind each host id to their own copy of that object (same prefab id, nearest
//    position - after loading the same save the positions are identical).
//  * Friends then nudge drifting positions toward the host's, copy hit points, and kill
//    what the host says is dead. Units the host doesn't have are removed after a while.
//  * Shared resources and the game clock follow the host too.
using System;
using System.Collections.Generic;
using Components;
using Components.RawComponents;
using NetKit;
using Systems.GameStateSystems;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using Utility.EnumsStorage;

namespace DinoNet
{
    public static class WorldSync
    {
        public const uint ClsUnit = 1, ClsBuilding = 2, ClsEnemy = 3;
        public static bool Running;
        public static float TimeNudge = 1f;
        public static float StartedAt;

        // host: entity -> id ; client: bindings both ways
        static readonly Dictionary<Entity, uint> toNet = new Dictionary<Entity, uint>();
        static readonly Dictionary<uint, Entity> toLocal = new Dictionary<uint, Entity>();
        static EntityQuery qNet;
        static World qWorld;
        static bool classesDefined;
        static NetSession classSession;

        public static uint NetIdOf(Entity e) { uint id; return toNet.TryGetValue(e, out id) ? id : 0u; }

        /// null = this entity belongs in the shared list; otherwise why it doesn't (same filter as qNet).
        public static string WhyNotShared(EntityManager em, Entity e)
        {
            if (!em.Exists(e)) return "gone";
            if (!em.HasComponent<ObjectId>(e) || !em.HasComponent<Translation>(e) || !em.HasComponent<Health>(e)) return "not a unit or building";
            if (!em.HasComponent<Unit>(e) && !em.HasComponent<BuildingBase>(e)) return "not a unit or building";
            if (em.HasComponent<Dead>(e)) return "dead";
            if (em.HasComponent<Worker>(e)) return "worker";
            if (em.HasComponent<BuildingDestroyRequest>(e) || em.HasComponent<DelayedDestroy>(e)) return "being demolished";
            if (em.HasComponent<CurrentBuildingForConstruction>(e) || em.HasComponent<CurrentFarmFieldForConstruction>(e)) return "construction preview";
            return null;
        }

        /// Host: give a just-made unit its shared id now instead of at the next scan (an order naming it
        /// could otherwise go out first). Returns 0 if it isn't shareable.
        public static uint HostRegisterNow(Entity e)
        {
            if (!Running || !Net.IsHost || Net.S == null || !Ecs.Ok) return 0;
            uint id;
            if (toNet.TryGetValue(e, out id)) return id;
            var em = Ecs.EM;
            if (WhyNotShared(em, e) != null) return 0;
            id = Net.S.NewHostId(); toNet[e] = id; toLocal[id] = e;
            uint cls = ClassOf(em, e); hostCls[e] = cls;
            vals[0] = em.GetComponentData<ObjectId>(e).value;
            var p = em.GetComponentData<Translation>(e).Value;
            vals[1] = p.x; vals[2] = p.z;
            vals[3] = em.GetComponentData<Health>(e).currentHealth;
            Net.S.SetEntity(id, cls, vals);
            RegisteredEarly++;
            return id;
        }
        public static int RegisteredEarly;

        /// Friend: is this host id bound to a local object right now? (false = unknown or not matched yet)
        public static bool IsBound(uint id) { Entity e; return toLocal.TryGetValue(id, out e) && Ecs.Exists(e); }
        /// Friend: worth waiting for - the host just told us about it, or hasn't yet (not given up on).
        public static bool MatchExpected(uint id) => pendingBind.ContainsKey(id) || noState.Contains(id) || (Net.S != null && Net.S.EntityClass(id) < 0);
        /// Friend: try binding on the next update instead of waiting for the 0.25 s timer.
        public static void BindSoon() { bindT = 0f; retryT = 0f; }
        public static Entity EntityOf(uint id) { Entity e; return id != 0 && toLocal.TryGetValue(id, out e) ? e : Entity.Null; }
        public static int Bound => toNet.Count;

        static void EnsureQuery()
        {
            if (qWorld == Ecs.W) return;
            qWorld = Ecs.W;
            qNet = Ecs.EM.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<ObjectId>(), ComponentType.ReadOnly<Translation>(), ComponentType.ReadOnly<Health>() },
                Any = new[] { ComponentType.ReadOnly<Unit>(), ComponentType.ReadOnly<BuildingBase>() },
                None = new[]
                {
                    // NOT Citizen: loading a save gives every non-enemy unit (soldiers too) the Citizen tag,
                    // so excluding it left the whole army unshared (0.0.3.8 test: "no shared id" for every order)
                    ComponentType.ReadOnly<Dead>(), ComponentType.ReadOnly<Worker>(),
                    // a building being demolished leaves the shared list right away (it lingers for its animation)
                    ComponentType.ReadOnly<BuildingDestroyRequest>(), ComponentType.ReadOnly<DelayedDestroy>(),
                    ComponentType.ReadOnly<CurrentBuildingForConstruction>(), ComponentType.ReadOnly<CurrentFarmFieldForConstruction>(),
                },
            });
        }

        public static void EnsureClasses() { if (Net.S != null) DefineClasses(); }

        static void DefineClasses()
        {
            if (classesDefined && classSession == Net.S) return;
            var steps = new double[] { 0, 0.25, 0.25, 1.0 };   // corrections start at 2 units: finer steps only cost bandwidth
            var fields = new[] { Field.Int, Field.FloatStep, Field.FloatStep, Field.FloatStep };
            Net.S.DefineClass(new EntityClassDef { Id = ClsUnit, Fields = fields, Steps = steps, Position = new[] { 1, 2 }, SnapDistance = 8, AlwaysRelevant = true, Priority = 1f });
            Net.S.DefineClass(new EntityClassDef { Id = ClsBuilding, Fields = fields, Steps = steps, Position = new[] { 1, 2 }, SnapDistance = 1000, AlwaysRelevant = true, Priority = 0.6f });
            Net.S.DefineClass(new EntityClassDef { Id = ClsEnemy, Fields = fields, Steps = steps, Position = new[] { 1, 2 }, SnapDistance = 8, AlwaysRelevant = true, Priority = 1.2f });
            classesDefined = true; classSession = Net.S;
        }

        /// Units / enemies / buildings this PC has (same filter as the shared objects).
        public static void Count(out int units, out int enemies, out int buildings)
        {
            units = enemies = buildings = 0;
            if (!Ecs.Ok) return;
            EnsureQuery();
            var em = Ecs.EM;
            var ents = qNet.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < ents.Length; i++)
                {
                    var c = ClassOf(em, ents[i]);
                    if (c == ClsBuilding) buildings++; else if (c == ClsEnemy) enemies++; else units++;
                }
            }
            finally { ents.Dispose(); }
        }

        static uint ClassOf(EntityManager em, Entity e)
        {
            if (em.HasComponent<BuildingBase>(e)) return ClsBuilding;
            if (em.HasComponent<Enemy>(e)) return ClsEnemy;
            return ClsUnit;
        }

        // ------------------------------------------------------------------ lifecycle
        public static void HostStart(bool fresh)
        {
            if (!Net.IsHost) return;
            DefineClasses();
            if (fresh)
            {
                foreach (var id in toLocal.Keys) Net.S.RemoveEntity(id);
                toNet.Clear(); toLocal.Clear();
            }
            else
            {
                // entities kept their ids; drop ones that no longer exist (the world was not reloaded)
                var gone = new List<Entity>();
                foreach (var kv in toNet) if (!Ecs.Exists(kv.Key)) gone.Add(kv.Key);
                foreach (var e in gone) { uint id = toNet[e]; toNet.Remove(e); toLocal.Remove(id); Net.S.RemoveEntity(id); }
            }
            Running = true; StartedAt = Time.realtimeSinceStartup;
            hostScanT = 0f;
            Log.Msg("world sync (host) started");
        }

        public static void ClientStart()
        {
            if (Net.IsHost || Net.S == null) return;
            DefineClasses();
            toNet.Clear(); toLocal.Clear(); pendingBind.Clear(); firstSeen.Clear(); unmatched.Clear(); noState.Clear();
            Running = true; StartedAt = Time.realtimeSinceStartup; progressT = 0f;
            TimeNudge = 1f; hostCount = -1; hostElapsedAt = -1f;
            // bind everything the host already registered
            foreach (var id in Net.S.EntityIds()) pendingBind[id] = Time.realtimeSinceStartup;
            Log.Msg("world sync (friend) started, " + pendingBind.Count + " host objects to match");
        }

        /// Called when the game unfreezes after matching: forget clock samples taken while frozen.
        public static void OnUnfreeze() { hostElapsedAt = -1f; TimeNudge = 1f; StartedAt = Time.realtimeSinceStartup; }

        public static void ClientReset()
        {
            Running = false;
            toNet.Clear(); toLocal.Clear(); pendingBind.Clear(); firstSeen.Clear(); unmatched.Clear(); noState.Clear();
        }

        public static void Stop()
        {
            if (Running) Log.Msg("world sync stopped");
            Running = false;
            TimeNudge = 1f;
            if (Net.IsHost && Net.S != null) foreach (var id in toLocal.Keys) Net.S.RemoveEntity(id);
            hostCls.Clear();
            toNet.Clear(); toLocal.Clear(); pendingBind.Clear(); firstSeen.Clear(); unmatched.Clear(); noState.Clear(); puppetIds.Clear(); puppetLast.Clear(); puppetAnnounced = false; puppetPrevT = 0f;
        }

        public static void Update()
        {
            if (!Running) return;
            if (!Net.Active || !Ecs.GameRunning) return;
            EnsureQuery();
            if (Net.IsHost) HostUpdate(); else ClientUpdate();
        }

        // ------------------------------------------------------------------ host
        static float hostScanT, hostTimeT, hostResT;
        static readonly double[] vals = new double[4];
        static readonly Dictionary<Entity, uint> hostCls = new Dictionary<Entity, uint>();

        static void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            var em = Ecs.EM;
            if (now >= hostScanT)
            {
                hostScanT = now + 0.1f;
                var ents = qNet.ToEntityArray(Allocator.Temp);
                var objs = qNet.ToComponentDataArray<ObjectId>(Allocator.Temp);
                var trs = qNet.ToComponentDataArray<Translation>(Allocator.Temp);
                var hps = qNet.ToComponentDataArray<Health>(Allocator.Temp);
                var seen = new HashSet<Entity>();
                try
                {
                    for (int i = 0; i < ents.Length; i++)
                    {
                        var e = ents[i];
                        seen.Add(e);
                        uint id, cls;
                        if (!toNet.TryGetValue(e, out id))
                        {
                            id = Net.S.NewHostId(); toNet[e] = id; toLocal[id] = e;
                            cls = ClassOf(em, e); hostCls[e] = cls;
                        }
                        else if (!hostCls.TryGetValue(e, out cls)) { cls = ClassOf(em, e); hostCls[e] = cls; }
                        vals[0] = objs[i].value;
                        vals[1] = trs[i].Value.x; vals[2] = trs[i].Value.z;
                        vals[3] = hps[i].currentHealth;
                        Net.S.SetEntity(id, cls, vals);
                    }
                }
                finally { ents.Dispose(); objs.Dispose(); trs.Dispose(); hps.Dispose(); }
                if (toNet.Count != seen.Count)
                {
                    var gone = new List<Entity>();
                    foreach (var kv in toNet) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
                    foreach (var e in gone)
                    {
                        uint id = toNet[e];
                        bool died = !em.Exists(e) || em.HasComponent<Dead>(e)
                                    || em.HasComponent<BuildingDestroyRequest>(e) || em.HasComponent<DelayedDestroy>(e);
                        toNet.Remove(e); toLocal.Remove(id); hostCls.Remove(e);
                        Net.S.RemoveEntity(id, died ? DespawnReason.Died : DespawnReason.Removed);
                    }
                }
            }
            if (now >= hostTimeT)
            {
                hostTimeT = now + 0.5f;
                CurrentSessionTimeSingleton_Send();
            }
            if (now >= hostResT)
            {
                hostResT = now + 1f;
                var p = new Packer();
                Res.Write(p);
                Net.ToAll(Msg.Res, p.ToArray(), false);
            }
        }

        static void CurrentSessionTimeSingleton_Send()
        {
            Components.SingletonComponents.CurrentSessionTimeSingleton t;
            if (!Ecs.TryTime(out t)) return;
            var p = new Packer().F64(t.elapsedTime).U8((int)t.timeSpeed).UV(Net.S.NowMs).UV((ulong)toNet.Count);
            Net.ToAll(Msg.Time, p.ToArray(), false);
        }

        // ------------------------------------------------------------------ client
        static readonly Dictionary<uint, float> pendingBind = new Dictionary<uint, float>();
        static readonly Dictionary<Entity, float> firstSeen = new Dictionary<Entity, float>();
        static readonly HashSet<uint> unmatched = new HashSet<uint>();
        static readonly HashSet<uint> noState = new HashSet<uint>();   // known id, its values haven't arrived yet
        static float retryT;
        static float bindT, corrT, phantomT;
        static int corrCursor;
        static List<uint> corrIds = new List<uint>();
        public static int Corrections, Snaps, HpFixes, Phantoms, UnitFixes, PuppetMoves;
        public static int Unmatched => unmatched.Count;
        static float hostElapsed, hostElapsedAt = -1f;
        static TimeSpeed hostSpeed = TimeSpeed.FirstSpeed;
        static int resAgree;
        static int hostCount = -1;

        public static void OnSpawned(uint id, uint cls) { if (Running && !Net.IsHost) pendingBind[id] = Time.realtimeSinceStartup; }

        public static void OnDespawned(uint id, int reason)
        {
            if (Net.IsHost) return;
            pendingBind.Remove(id); unmatched.Remove(id); noState.Remove(id);
            Entity e;
            if (!toLocal.TryGetValue(id, out e)) return;
            toLocal.Remove(id); toNet.Remove(e);
            if (!Running || !Ecs.GameRunning) return;
            var em = Ecs.EM;
            if (!em.Exists(e) || em.HasComponent<Dead>(e)) return;
            if (reason != DespawnReason.Died) return;   // Removed = just forget the id
            if (em.HasComponent<BuildingBase>(e))
            {
                // demolished / destroyed on the host: the game's own demolish (same effect, dust and all)
                if (em.HasComponent<BuildingDestroyRequest>(e) || em.HasComponent<DelayedDestroy>(e)) return;
                try { em.AddComponentData(e, new BuildingDestroyRequest()); BuildingsRemoved++; Log.Dbg("building " + e.Index + " removed (gone on the host)"); }
                catch (Exception ex) { Log.Throttled("bdestroy", "building remove: " + ex.Message); Kill(em, e, "host: building gone"); }
                return;
            }
            Kill(em, e, "host: died");
        }

        static void Kill(EntityManager em, Entity e, string why)
        {
            try
            {
                if (!em.HasComponent<Health>(e)) return;
                var h = em.GetComponentData<Health>(e);
                if (h.currentHealth <= 0f) return;
                h.currentHealth = 0f;
                em.SetComponentData(e, h);
                Log.Dbg("kill " + e.Index + " (" + why + ")");
            }
            catch (Exception ex) { Log.Throttled("kill", "kill: " + ex.Message); }
        }

        static float progressT;
        public static int HostObjects => Net.S != null ? Net.S.EntityCount : 0;

        static void ClientUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (!Ecs.Ok) return;
            var em = Ecs.EM;
            if (now >= progressT)
            {
                progressT = now + (now - StartedAt < 40f ? 5f : 60f);
                Log.Msg("sync: matched " + toLocal.Count + " of " + HostObjects + " host objects (waiting " + pendingBind.Count + ", no match " + unmatched.Count
                        + ", host says " + hostCount + ") fixes pos " + Corrections + " (units " + UnitFixes + ") snap " + Snaps + " enemy smoothing " + PuppetMoves + " hp " + HpFixes + " phantom " + Phantoms + " (buildings " + PhantomBuildings + ") far matches " + FarBinds + " res " + Res.Fixes);
            }
            // ---- time follow (not while frozen: the clock is stopped on purpose)
            if (Game.Held) TimeNudge = 1f;
            else if (hostElapsedAt > 0f)
            {
                float scale = Ecs.ScaleOf(hostSpeed);
                float predicted = hostElapsed + (now - hostElapsedAt) * scale;
                float local = Ecs.ElapsedGameTime;
                float off = predicted - local;
                if (scale <= 0f || Math.Abs(off) < 0.08f) TimeNudge = 1f;
                else TimeNudge = Mathf.Clamp(1f + off * 0.35f, 0.75f, 1.35f);
                if (!Game.Held)
                {
                    var cur = Ecs.CurrentSpeed;
                    if (cur != hostSpeed && now - lastSpeedFix > 1f)
                    {
                        lastSpeedFix = now;
                        Ecs.RequestSpeed(SpeedTask(hostSpeed));
                    }
                    Time.timeScale = Ecs.ScaleOf(cur) * TimeNudge;
                }
            }
            // ---- unmatched / no-data ids are retried every 2 s
            if (now >= retryT)
            {
                retryT = now + 2f;
                foreach (var id in unmatched) if (!pendingBind.ContainsKey(id)) pendingBind[id] = now - 2f;
                foreach (var id in noState) if (!pendingBind.ContainsKey(id)) pendingBind[id] = now;
                noState.Clear();
            }
            // ---- bind host ids to local objects
            if (now >= bindT && pendingBind.Count > 0)
            {
                bindT = now + 0.25f;
                BindPending(em, now);
            }
            // ---- corrections (slice of bound objects every 0.1 s, whole world ~ every 1.5 s)
            if (now >= corrT)
            {
                corrT = now + 0.1f;
                try { Correct(em); } catch (Exception e) { if (Ecs.GameRunning) Log.Throttled("correct", "sync correct: " + e); }
            }
            // ---- phantoms: our units the host doesn't have
            if (now >= phantomT)
            {
                phantomT = now + 1f;
                FindPhantoms(em, now);
            }
        }

        static float lastSpeedFix;

        static TimeScaleTaskType SpeedTask(TimeSpeed s)
        {
            switch (s)
            {
                case TimeSpeed.Pause: return TimeScaleTaskType.Pause;
                case TimeSpeed.SecondSpeed: return TimeScaleTaskType.SecondSpeed;
                case TimeSpeed.ThirdSpeed: return TimeScaleTaskType.ThirdSpeed;
                default: return TimeScaleTaskType.FirstSpeed;
            }
        }

        // spatial buckets of unbound local objects by prefab id
        static readonly Dictionary<int, List<Entity>> freeByObj = new Dictionary<int, List<Entity>>();

        static void BindPending(EntityManager em, float now)
        {
            freeByObj.Clear();
            var ents = qNet.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < ents.Length; i++)
                {
                    var e = ents[i];
                    if (toNet.ContainsKey(e)) continue;
                    int obj = em.GetComponentData<ObjectId>(e).value;
                    List<Entity> l;
                    if (!freeByObj.TryGetValue(obj, out l)) { l = new List<Entity>(); freeByObj[obj] = l; }
                    l.Add(e);
                }
            }
            finally { ents.Dispose(); }
            var done = new List<uint>();
            foreach (var kv in pendingBind)
            {
                uint id = kv.Key;
                var v = Net.S.LatestEntity(id);
                if (v == null || v.Length < 4) { if (now - kv.Value > 10f) { noState.Add(id); done.Add(id); } continue; }
                int obj = (int)Math.Round(v[0]);
                int cls = Net.S.EntityClass(id);
                float radius = cls == (int)ClsBuilding ? 2f : (now - kv.Value < 2f ? 3f : 8f);
                List<Entity> l;
                Entity best = Entity.Null; float bestD = radius * radius; int bestI = -1;
                if (freeByObj.TryGetValue(obj, out l))
                {
                    for (int i = 0; i < l.Count; i++)
                    {
                        var p = em.GetComponentData<Translation>(l[i]).Value;
                        float dx = p.x - (float)v[1], dz = p.z - (float)v[2];
                        float d = dx * dx + dz * dz;
                        if (d <= bestD) { bestD = d; best = l[i]; bestI = i; }
                    }
                }
                if (best != Entity.Null)
                {
                    l.RemoveAt(bestI);
                    toNet[best] = id; toLocal[id] = best;
                    firstSeen.Remove(best);
                    unmatched.Remove(id);
                    done.Add(id);
                }
                else if (cls == (int)ClsEnemy && now - kv.Value > 3f && hostCount >= 0 && Net.S.EntityCount >= hostCount - 5)
                {
                    // an enemy the host has and we don't (its wave rolled differently here): make it
                    var e = SpawnEnemy(em, obj, (float)v[1], (float)v[2], (float)v[3]);
                    if (e != Entity.Null)
                    {
                        toNet[e] = id; toLocal[id] = e;
                        unmatched.Remove(id);
                        Spawned++;
                    }
                    else unmatched.Add(id);
                    done.Add(id);
                }
                else if (cls == (int)ClsUnit && now - kv.Value > 6f && l != null && l.Count > 0)
                {
                    // the same unit exists here but walked (or was trained) somewhere else: take the
                    // nearest one of that type and put it where the host has it
                    float nd = float.MaxValue; int ni = -1;
                    for (int i = 0; i < l.Count; i++)
                    {
                        var p = em.GetComponentData<Translation>(l[i]).Value;
                        float dx = p.x - (float)v[1], dz = p.z - (float)v[2];
                        float d = dx * dx + dz * dz;
                        if (d < nd) { nd = d; ni = i; }
                    }
                    var far = l[ni]; l.RemoveAt(ni);
                    var tr = em.GetComponentData<Translation>(far);
                    tr.Value.x = (float)v[1]; tr.Value.z = (float)v[2];
                    em.SetComponentData(far, tr);
                    toNet[far] = id; toLocal[id] = far;
                    firstSeen.Remove(far);
                    unmatched.Remove(id);
                    done.Add(id);
                    FarBinds++;
                    Log.Dbg("unit " + id + " (prefab " + obj + ") matched " + Math.Sqrt(nd).ToString("0") + " away - moved to the host's spot");
                }
                else if (now - kv.Value > 12f)
                {
                    unmatched.Add(id);
                    done.Add(id);
                    Log.Dbg("no local match for host object " + id + " (prefab " + obj + ", class " + cls + ")");
                }
            }
            foreach (var id in done) pendingBind.Remove(id);

        }

        public static int Spawned, BuildingsRemoved, FarBinds, PhantomBuildings;

        /// Same components the game's wave spawner gives a new enemy (minus the wave hold).
        static Entity SpawnEnemy(EntityManager em, int objectId, float x, float z, float hp)
        {
            try
            {
                var map = GameState.PrefabsMap;
                Entity prefab;
                if (!map.IsCreated || !map.TryGetValue(objectId, out prefab) || !em.Exists(prefab)) return Entity.Null;
                var e = em.Instantiate(prefab);
                em.SetComponentData(e, new Translation { Value = new float3(x, 0f, z) });
                if (em.HasComponent<Health>(e) && hp > 0f) em.SetComponentData(e, new Health { currentHealth = hp });
                if (!em.HasComponent<HideUnitInFog>(e)) em.AddComponentData(e, new HideUnitInFog { Value = 1f });
                if (!em.HasComponent<InDefend>(e)) em.AddComponentData(e, InDefend.Null);
                var fs = new Components.RawComponents.FightComponents.UnitFightState(Utility.EnumsStorage.UnitFightStates.InDefend);
                if (em.HasComponent<Components.RawComponents.FightComponents.UnitFightState>(e)) em.SetComponentData(e, fs);
                else em.AddComponentData(e, fs);
                Log.Dbg("spawned host enemy (prefab " + objectId + ") at " + x + "," + z);
                return e;
            }
            catch (Exception ex) { Log.Throttled("spawn", "spawn enemy: " + ex.Message); return Entity.Null; }
        }

        static void Correct(EntityManager em)
        {
            if (corrCursor >= corrIds.Count)
            {
                corrIds = new List<uint>(toLocal.Keys);
                corrCursor = 0;
            }
            int per = Math.Max(40, (corrIds.Count + 1) / 2);   // every object checked every 0.2 s
            int end = Math.Min(corrIds.Count, corrCursor + per);
            while (corrCursor < end)
            {
                uint id = corrIds[corrCursor++];
                try { CorrectOne(em, id); }
                catch (Exception ex) { if (Ecs.GameRunning) Log.Throttled("correct1", "sync correct (object " + id + "): " + ex); }
            }
        }

        static void CorrectOne(EntityManager em, uint id)
        {
            {
                Entity e;
                if (!toLocal.TryGetValue(id, out e)) return;
                if (!em.Exists(e) || em.HasComponent<Dead>(e))
                {
                    toLocal.Remove(id); toNet.Remove(e);
                    pendingBind[id] = Time.realtimeSinceStartup;   // our copy died early: try to re-match
                    return;
                }
                var v = Net.S.LatestEntity(id);
                if (v == null || v.Length < 4) return;
                int cls = Net.S.EntityClass(id);
                // hit points
                var h = em.GetComponentData<Health>(e);
                float hostHp = (float)v[3];
                if (Math.Abs(h.currentHealth - hostHp) > 1f)
                {
                    if (hostHp <= 0f) Kill(em, e, "host hp 0");
                    else { h.currentHealth = hostHp; em.SetComponentData(e, h); HpFixes++; }
                }
                if (cls == (int)ClsBuilding) return;
                // enemies follow the host's smoothed position every frame (BeforeTransforms) - no nudging here
                if (cls == (int)ClsEnemy && PuppetEnemies) return;
                // position
                var tr = em.GetComponentData<Translation>(e);
                float dx = (float)v[1] - tr.Value.x, dz = (float)v[2] - tr.Value.z;
                float d2 = dx * dx + dz * dz;
                // gentle: small differences (a walk a ping behind) are left alone, medium ones are blended in
                // over about a second, only big gaps snap (0.0.3.9 pulled half the gap every 0.2 s = stutter)
                if (d2 > 225f) { tr.Value.x = (float)v[1]; tr.Value.z = (float)v[2]; em.SetComponentData(e, tr); Snaps++; }
                else if (d2 > 9f) { tr.Value.x += dx * 0.2f; tr.Value.z += dz * 0.2f; em.SetComponentData(e, tr); Corrections++; if (cls == (int)ClsUnit) UnitFixes++; }
            }
        }

        // ------------------------------------------------------------------ enemy smoothing (friends)
        // Enemy AI runs on every PC and drifts apart (0.0.5.1: ~500 position fixes a second, enemies
        // bouncing on every friend's screen). Friends now draw every enemy exactly where the host's
        // (interpolated) copy is, written each frame right before the game turns positions into
        // what's drawn. The local AI still runs (for attacks/animations) but can't move them.
        public static bool PuppetEnemies = true;
        static readonly List<uint> puppetIds = new List<uint>();
        static readonly Dictionary<uint, float2> puppetLast = new Dictionary<uint, float2>();
        static float puppetListT, puppetPrevT;
        static bool puppetAnnounced;

        public static void BeforeTransforms()
        {
            if (!PuppetEnemies || !Running || Net.S == null || Net.IsHost || !Net.Active || !Ecs.Ok || !Ecs.GameRunning || Game.Held) return;
            var em = Ecs.EM;
            float now = Time.realtimeSinceStartup;
            float dt = puppetPrevT > 0f ? Math.Min(0.25f, now - puppetPrevT) : 0f;
            puppetPrevT = now;
            if (now >= puppetListT)
            {
                puppetListT = now + 0.5f;
                puppetIds.Clear();
                foreach (var kv in toLocal) if (Net.S.EntityClass(kv.Key) == (int)ClsEnemy) puppetIds.Add(kv.Key);
                if (puppetLast.Count > puppetIds.Count + 300) puppetLast.Clear();
                if (!puppetAnnounced && puppetIds.Count > 0) { puppetAnnounced = true; Log.Msg("enemy smoothing: following " + puppetIds.Count + " host enemies every frame"); }
            }
            for (int i = 0; i < puppetIds.Count; i++)
            {
                uint id = puppetIds[i];
                Entity e;
                if (!toLocal.TryGetValue(id, out e) || !em.Exists(e) || em.HasComponent<Dead>(e) || !em.HasComponent<Translation>(e)) continue;
                double[] v;
                if (Net.S.GetEntity(id, out v) < 3) continue;
                float x = (float)v[1], z = (float)v[2];
                float2 last;
                bool had = puppetLast.TryGetValue(id, out last);
                puppetLast[id] = new float2(x, z);
                var tr = em.GetComponentData<Translation>(e);
                if (tr.Value.x == x && tr.Value.z == z) continue;
                tr.Value.x = x; tr.Value.z = z;
                em.SetComponentData(e, tr);
                PuppetMoves++;
                // face the way the host's copy is walking (standing still: leave the local facing)
                if (had && dt > 0f && em.HasComponent<Rotation>(e))
                {
                    float2 d = new float2(x, z) - last;
                    if (math.lengthsq(d) > 0.36f * dt * dt)
                    {
                        var r = em.GetComponentData<Rotation>(e);
                        var to = quaternion.LookRotationSafe(new float3(d.x, 0f, d.y), math.up());
                        r.Value = math.slerp(r.Value, to, Math.Min(1f, dt * 10f));
                        em.SetComponentData(e, r);
                    }
                }
            }
        }

        static readonly List<Entity> phantomBuildings = new List<Entity>();

        static void FindPhantoms(EntityManager em, float now)
        {
            // Only when the host's object list has fully arrived and nearly everything is matched:
            // otherwise "unbound" just means "not received yet".
            if (Game.Held || now - StartedAt < 10f || pendingBind.Count > 20 || noState.Count > 20 || hostCount < 0) return;
            int known = Net.S.EntityCount;
            if (known < hostCount - 5) return;
            var ents = qNet.ToEntityArray(Allocator.Temp);
            var alive = new HashSet<Entity>();
            var candidates = new List<Entity>();
            int total = ents.Length;
            try
            {
                for (int i = 0; i < ents.Length; i++)
                {
                    var e = ents[i];
                    if (toNet.ContainsKey(e)) continue;
                    alive.Add(e);
                    float t;
                    if (!firstSeen.TryGetValue(e, out t)) { firstSeen[e] = now; continue; }
                    if (now - t < 10f) continue;
                    if (em.HasComponent<BuildingBase>(e))
                    {
                        // a building the host doesn't have after 30 s (the host's copy of a build failed or
                        // went elsewhere): demolish ours - the host's world is the real one
                        if (now - t >= 30f) phantomBuildings.Add(e);
                        continue;
                    }
                    candidates.Add(e);
                }
            }
            finally { ents.Dispose(); }
            // buildings: a few at a time, never a whole town (that means something else is wrong)
            if (phantomBuildings.Count > 0)
            {
                if (phantomBuildings.Count <= 3)
                    foreach (var b in phantomBuildings)
                    {
                        try
                        {
                            em.AddComponentData(b, new BuildingDestroyRequest());
                            PhantomBuildings++;
                            firstSeen.Remove(b);
                            Log.Msg("sync: removed a building the host doesn't have (prefab " + em.GetComponentData<ObjectId>(b).value + ")");
                        }
                        catch (Exception ex) { Log.Throttled("phb", "remove building: " + ex.Message); }
                    }
                else Log.Throttled("phantomb", "sync: " + phantomBuildings.Count + " of your buildings have no match on the host - not removing them (press F9 to re-sync)");
                phantomBuildings.Clear();
            }
            // a big batch of OUR units means something else is wrong: don't wipe the army
            int own = 0;
            foreach (var e in candidates) if (!em.HasComponent<Enemy>(e)) own++;
            bool ownOk = own <= Math.Max(10, total / 4);
            if (!ownOk) Log.Throttled("phantom", "sync: " + own + " of your units have no match on the host - not removing them (press F9 to re-sync)");
            int n = 0;
            foreach (var e in candidates)
            {
                bool enemy = em.HasComponent<Enemy>(e);
                if (!enemy && !ownOk) continue;
                if (n++ >= (enemy ? 80 : 20)) break;
                Kill(em, e, "host doesn't have it");
                Phantoms++;
                firstSeen.Remove(e);
            }
            if (firstSeen.Count > alive.Count + 200)
            {
                var gone = new List<Entity>();
                foreach (var kv in firstSeen) if (!alive.Contains(kv.Key)) gone.Add(kv.Key);
                foreach (var e in gone) firstSeen.Remove(e);
            }
        }

        // ------------------------------------------------------------------ messages
        public static void OnMessage(int from, uint kind, byte[] d)
        {
            if (Net.IsHost) return;
            var u = new Unpacker(d);
            switch (kind)
            {
                case Msg.Time:
                    hostElapsed = (float)u.F64();
                    hostSpeed = (TimeSpeed)u.U8();
                    u.UV();
                    if (!u.End) hostCount = (int)u.UV();
                    // account for half the round trip
                    int rtt = Math.Max(0, Net.S.RttMs(0));
                    hostElapsed += rtt * 0.0005f * Ecs.ScaleOf(hostSpeed);
                    hostElapsedAt = Time.realtimeSinceStartup;
                    break;
                case Msg.Res:
                    if (!Running || Game.Held) return;
                    if (Res.ReadAndApply(u, ref resAgree)) { }
                    break;
            }
        }
    }

    /// The shared stockpile (faction resources) - host values win.
    public static class Res
    {
        abstract class R
        {
            public abstract bool Has();
            public abstract int Get();
            public abstract void Set(int v);
        }

        sealed class R<T> : R where T : struct, IComponentData
        {
            EntityQuery q; World w;
            readonly System.Reflection.FieldInfo fCont = typeof(T).GetField("valueContainer");
            void E() { if (w != Ecs.W) { w = Ecs.W; q = Ecs.EM.CreateEntityQuery(ComponentType.ReadWrite<T>()); } }
            public override bool Has() { E(); return q.CalculateEntityCount() == 1; }
            public override int Get()
            {
                E();
                object box = q.GetSingleton<T>();
                var c = (CurrentResources)fCont.GetValue(box);
                return c.value;
            }
            public override void Set(int v)
            {
                E();
                object box = q.GetSingleton<T>();
                var c = (CurrentResources)fCont.GetValue(box);
                c.value = v;
                fCont.SetValue(box, c);
                q.SetSingleton((T)box);
            }
        }

        static readonly R[] all =
        {
            new R<CurrentFood>(), new R<CurrentWood>(), new R<CurrentStone>(), new R<CurrentIron>(), new R<CurrentMoney>(),
            new R<CurrentSouls>(), new R<CurrentBones>(), new R<CurrentSpirit>(), new R<CurrentDeathMetal>(),
        };
        static readonly int[] lastDiff = new int[9];
        public static int Fixes;

        public static void Write(Packer p)
        {
            p.UV((ulong)all.Length);
            foreach (var r in all)
            {
                bool has = false; int v = 0;
                try { has = r.Has(); if (has) v = r.Get(); } catch { has = false; }
                p.Bool(has); if (has) p.SV(v);
            }
        }

        /// A value is corrected only when it differed the same way twice in a row (a delivery
        /// a few frames apart on two PCs is not a desync).
        public static bool ReadAndApply(Unpacker u, ref int agree)
        {
            int n = (int)u.UV();
            bool any = false;
            for (int i = 0; i < n; i++)
            {
                bool has = u.Bool();
                if (!has) continue;
                int hv = (int)u.SV();
                if (i >= all.Length) continue;
                try
                {
                    var r = all[i];
                    if (!r.Has()) continue;
                    int lv = r.Get();
                    int diff = hv - lv;
                    if (diff == 0) { lastDiff[i] = 0; continue; }
                    if (lastDiff[i] != 0 && Math.Sign(lastDiff[i]) == Math.Sign(diff))
                    {
                        r.Set(hv); Fixes++; any = true; lastDiff[i] = 0;
                    }
                    else lastDiff[i] = diff;
                }
                catch (Exception e) { Log.Throttled("res" + i, "resource " + i + ": " + e.Message); }
            }
            return any;
        }
    }
}
