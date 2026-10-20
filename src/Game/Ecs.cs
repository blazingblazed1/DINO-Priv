// Small helpers around the game's DOTS world.
using System;
using System.Reflection;
using Components;
using Components.Commands;
using Components.RawComponents;
using Components.SingletonComponents;
using Systems;
using Systems.GameStateSystems;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using Utility.EnumsStorage;

namespace DinoNet
{
    public static class Ecs
    {
        public static World W => World.DefaultGameObjectInjectionWorld;
        public static bool Ok => W != null && W.IsCreated;
        public static EntityManager EM => W.EntityManager;

        static EntityQuery qRunning, qTime, qSaveReq, qLoadReq, qThreadRandom;
        static World qWorld;

        static void EnsureQueries()
        {
            if (qWorld == W) return;
            qWorld = W;
            var em = EM;
            qRunning = em.CreateEntityQuery(ComponentType.ReadOnly<GameRunningSingleton>());
            qTime = em.CreateEntityQuery(ComponentType.ReadWrite<CurrentSessionTimeSingleton>());
            qSaveReq = em.CreateEntityQuery(ComponentType.ReadOnly<SaveRequest>());
            qLoadReq = em.CreateEntityQuery(ComponentType.ReadOnly<LoadRequest>());
            qThreadRandom = em.CreateEntityQuery(ComponentType.ReadWrite<ThreadRandomSingleton>());
        }

        public static EntityQuery Query(params ComponentType[] types) => EM.CreateEntityQuery(types);
        public static EntityQuery Query(EntityQueryDesc d) => EM.CreateEntityQuery(d);

        public static bool GameRunning
        {
            get
            {
                if (!Ok) return false;
                try { EnsureQueries(); return !qRunning.IsEmptyIgnoreFilter; } catch { return false; }
            }
        }

        public static bool SaveBusy { get { if (!Ok) return false; EnsureQueries(); return !qSaveReq.IsEmptyIgnoreFilter; } }
        public static bool LoadBusy { get { if (!Ok) return false; EnsureQueries(); return !qLoadReq.IsEmptyIgnoreFilter; } }

        public static bool TryTime(out CurrentSessionTimeSingleton t)
        {
            t = default(CurrentSessionTimeSingleton);
            if (!Ok) return false;
            EnsureQueries();
            if (qTime.CalculateEntityCount() != 1) return false;
            t = qTime.GetSingleton<CurrentSessionTimeSingleton>();
            return true;
        }

        public static float ElapsedGameTime { get { CurrentSessionTimeSingleton t; return TryTime(out t) ? t.elapsedTime : 0f; } }

        public static float ScaleOf(TimeSpeed s)
        {
            switch (s)
            {
                case TimeSpeed.FirstSpeed: return 1f;
                case TimeSpeed.SecondSpeed: return 2f;
                case TimeSpeed.ThirdSpeed: return 4f;
                default: return 0f;
            }
        }

        public static TimeSpeed CurrentSpeed { get { CurrentSessionTimeSingleton t; return TryTime(out t) ? t.timeSpeed : TimeSpeed.FirstSpeed; } }

        /// Ask the game to switch speed the way its own UI does (TimeControlCommand without the
        /// command tag: SessionTimeUpdateSystem applies it and destroys it).
        public static void RequestSpeed(TimeScaleTaskType task)
        {
            if (!GameRunning) return;
            var em = EM;
            var e = em.CreateEntity(typeof(TimeControlCommand), typeof(CommandParameterData));
            em.SetComponentData(e, new CommandParameterData { parameter = (int)task });
        }

        public static void RequestSave(string name)
        {
            var em = EM;
            var e = em.CreateEntity(typeof(SaveRequest));
            em.SetComponentData(e, new SaveRequest { SaveName = new FixedString128Bytes(name), IsAutoSave = true });
        }

        public static void RequestLoad(string name, bool fromMenu)
        {
            var em = EM;
            var e = em.CreateEntity(typeof(LoadRequest));
            em.SetComponentData(e, new LoadRequest { NameToLoad = new FixedString128Bytes(name), FromMenu = fromMenu });
        }

        /// Re-seed the game's per-thread random generators so every PC rolls the same
        /// numbers from here on (waves, sides, loot...). Best effort.
        public static unsafe void Reseed(uint seed)
        {
            try
            {
                UnityEngine.Random.InitState((int)seed);
                if (!Ok) return;
                EnsureQueries();
                if (qThreadRandom.CalculateEntityCount() != 1) return;
                var tr = qThreadRandom.GetSingleton<ThreadRandomSingleton>().Random;
                var f = typeof(ThreadRandom).GetField("buffer", BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null) return;
                object boxed = tr;
                var ptr = (IntPtr)System.Reflection.Pointer.Unbox(f.GetValue(boxed));
                if (ptr == IntPtr.Zero) return;
                seed = Math.Max(1u, Math.Min(seed, 4294967166u - 200));
                // Randoms is a 64-byte padded struct holding one Unity.Mathematics.Random (uint state) at offset 0.
                for (int i = 0; i < 128; i++)
                {
                    var r = Unity.Mathematics.Random.CreateFromIndex(seed + (uint)i);
                    *(Unity.Mathematics.Random*)((byte*)ptr + i * 64) = r;
                }
                Log.Msg("random generators re-seeded (" + seed + ")");
            }
            catch (Exception e) { Log.Warn("reseed failed: " + e.Message); }
        }

        public static bool Exists(Entity e)
        {
            return e != Entity.Null && EM.Exists(e);
        }
    }
}
