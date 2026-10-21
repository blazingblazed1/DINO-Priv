// Harmony patches into the game.
using System;
using HarmonyLib;
using Systems;
using Systems.ConstructionSystems;
using Systems.GameStateSystems;
using UI.MainMenuScripts;
using Utility.EnumsStorage;

namespace DinoNet
{
    // ---------------------------------------------------------------- enemy smoothing
    // TransformSystemGroup has no OnUpdate of its own: patch the base group method and pick it out.
    [HarmonyPatch(typeof(Unity.Entities.ComponentSystemGroup), "OnUpdate")]
    static class P_Transforms
    {
        static void Prefix(Unity.Entities.ComponentSystemGroup __instance)
        {
            if (!(__instance is Unity.Transforms.TransformSystemGroup) || __instance.World != Ecs.W) return;
            try { WorldSync.BeforeTransforms(); } catch (Exception e) { Log.Throttled("puppet", "enemy smoothing: " + e); }
        }
    }

    // ---------------------------------------------------------------- commands
    [HarmonyPatch(typeof(UIInputHandler), "OnUpdateSimulation")]
    static class P_UIInput
    {
        static void Prefix() { try { Commands.BeforeInput(); } catch (Exception e) { Log.Throttled("p_in", "BeforeInput: " + e); } }
        static void Postfix() { try { Commands.AfterInput(); } catch (Exception e) { Log.Throttled("p_out", "AfterInput: " + e); } }
    }

    [HarmonyPatch(typeof(ConstructionSystem), "OnUpdateSimulation")]
    static class P_Construction
    {
        static void Prefix(ConstructionSystem __instance) { ConstructionCtx.Before(__instance); }
        static Exception Finalizer(ConstructionSystem __instance, Exception __exception)
        {
            ConstructionCtx.After(__instance);
            if (__exception != null) Log.Throttled("constr", "ConstructionSystem: " + __exception.Message);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(ConstructionSystem), "InitializeConstruction")]
    static class P_ConstructionInit
    {
        static void Prefix(ref Utility.EnumsStorage.OrientationType __0) { try { ConstructionCtx.OverrideOrientation(ref __0); } catch { } }
    }

    [HarmonyPatch(typeof(ConstructionSystem), "ProcessBuildCommand")]
    static class P_ConstructionBuild
    {
        static void Postfix(ConstructionSystem __instance, bool __result)
        {
            if (ConstructionCtx.RemoteActive) ConstructionCtx.BuildResult = __result ? 1 : 0;
            try { ConstructionCtx.BuildDone(__instance, __result); } catch (Exception e) { Log.Throttled("blddone", "build cell: " + e.Message); }
        }
    }

    /// A friend's spell is cast at the friend's point: the spell system reads the mouse ground
    /// position directly, so swap it in only while that system runs.
    [HarmonyPatch(typeof(Systems.SpellTargetSystem), "OnUpdateSimulation")]
    static class P_Spell
    {
        static Unity.Mathematics.float2 saved;
        static bool swapped;
        static void Prefix()
        {
            if (Commands.SpellOverrideFrames <= 0) return;
            var s = Systems.InputSystems.PlayerInputSystem.PlayerInputState;
            saved = s.groundPosition;
            s.groundPosition = Commands.SpellOverridePos;
            Systems.InputSystems.PlayerInputSystem.PlayerInputState = s;
            swapped = true;
        }
        static Exception Finalizer(Exception __exception)
        {
            if (swapped)
            {
                swapped = false;
                var s = Systems.InputSystems.PlayerInputSystem.PlayerInputState;
                s.groundPosition = saved;
                Systems.InputSystems.PlayerInputSystem.PlayerInputState = s;
            }
            return __exception;
        }
    }

    // ---------------------------------------------------------------- time
    /// Opening a menu / research / mission panel pauses the game locally - not in co-op.
    [HarmonyPatch(typeof(SessionTimeUpdateSystem), "ProcessTimeTask")]
    static class P_TimeTask
    {
        static bool Prefix(TimeScaleTaskType scaleTaskType)
        {
            if (scaleTaskType == TimeScaleTaskType.ToggleWindow && Net.InSession && Game.Phase >= Game.PhaseStarting) return false;
            return true;
        }
    }

    // ---------------------------------------------------------------- menus
    [HarmonyPatch(typeof(MainMenu), "Awake")]
    static class P_MenuAwake
    {
        static void Postfix(MainMenu __instance) { try { Ui.OnMainMenuAwake(__instance); } catch (Exception e) { Log.Err("menu awake: " + e); } }
    }

    [HarmonyPatch(typeof(MainMenu), "ShowAsInitialState")]
    static class P_MenuInitial
    {
        static void Postfix(MainMenu __instance) { try { Ui.OnMenuInitialState(__instance); } catch (Exception e) { Log.Err("menu initial: " + e); } }
    }

    [HarmonyPatch(typeof(MainMenu), "UpdateProfileState")]
    static class P_MenuProfile
    {
        static void Postfix(MainMenu __instance) { try { Ui.OnMenuInitialState(__instance); } catch (Exception e) { Log.Err("menu profile: " + e); } }
    }

    [HarmonyPatch(typeof(MainMenu), "QuitToMenu")]
    static class P_QuitToMenu
    {
        static void Prefix() { try { Game.OnLocalQuitToMenu(); } catch (Exception e) { Log.Err("quit to menu: " + e); } }
    }

    [HarmonyPatch(typeof(MainMenu), "StartNewMissionFromGame", new Type[] { typeof(Components.SingletonComponents.CurrentSettingsSingleton), typeof(bool) })]
    static class P_Restart1
    {
        static bool Prefix() => Restart.Allow();
    }

    [HarmonyPatch(typeof(MainMenu), "StartNewMissionFromGame", new Type[] { typeof(int), typeof(int), typeof(int) })]
    static class P_Restart2
    {
        static bool Prefix() => Restart.Allow();
    }

    static class Restart
    {
        /// Host restarting / starting the next mission: everyone gets the new mission by a fresh sync.
        /// Friends can't restart the shared game.
        public static bool Allow()
        {
            if (!Net.InSession || Game.Phase < Game.PhaseStarting) return true;
            if (Net.IsHost)
            {
                Log.Msg("host restarts / changes the mission -> new sync after loading");
                WorldSync.Stop();
                StartSync.Reset();
                Game.Phase = Game.PhaseChoosing;
                Game.WaitForNewWorld();
                Net.S.SetPhase(Game.PhaseChoosing);
                Net.ToAll(Msg.Choosing, new byte[0]);
                Net.Say("The host is restarting the mission...");
                return true;
            }
            Ui.Toast("Only the host can restart or change the mission");
            return false;
        }
    }

    [HarmonyPatch(typeof(global::SavedGames), "LoadGame")]
    static class P_LoadGame
    {
        static bool Prefix()
        {
            if (!Net.InSession) return true;
            if (Net.IsHost)
            {
                if (Game.Phase >= Game.PhaseStarting)
                {
                    WorldSync.Stop(); StartSync.Reset();
                    Game.Phase = Game.PhaseChoosing;
                    Game.WaitForNewWorld();
                    Net.S.SetPhase(Game.PhaseChoosing);
                    Net.ToAll(Msg.Choosing, new byte[0]);
                }
                return true;   // the host may load any save: it is sent to everyone
            }
            Ui.Toast("In co-op only the host can load a save");
            return false;
        }
    }
}
