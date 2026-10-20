// Co-op flow: phases, holds (freeze), host start detection, menu transitions.
using System;
using System.Collections.Generic;
using NetKit;
using UI.MainMenuScripts;
using UnityEngine;

namespace DinoNet
{
    public static class Game
    {
        public const int PhaseLobby = 0, PhaseChoosing = 1, PhaseStarting = 2, PhaseInGame = 3;
        public static int Phase;
        public static bool AllowNextLoad;       // our own LoadRequest (manual loads are blocked for friends)
        static readonly HashSet<string> holds = new HashSet<string>();
        public static bool Held => holds.Count > 0;
        static float runningSince = -1f;
        static bool wasRunning;
        static bool expectLoad, sawUnload;
        static float expectSince;

        public static void Hold(string why, bool on)
        {
            bool before = Held;
            if (on) holds.Add(why); else holds.Remove(why);
            if (before && !Held) ApplySpeed();
            if (!before && Held) Log.Dbg("hold on (" + why + ")");
        }

        static void ApplySpeed()
        {
            if (!Ecs.GameRunning) return;
            Time.timeScale = Ecs.ScaleOf(Ecs.CurrentSpeed) * WorldSync.TimeNudge;
        }

        public static void ExpectLoad() { LoadFailed = false; expectLoad = true; sawUnload = !Ecs.GameRunning; expectSince = Time.realtimeSinceStartup; runningSince = -1f; }

        /// After ExpectLoad(): true once the game went through an unload and has been running for 1.5 s.
        public static bool WorldReadyAfterLoad()
        {
            if (!expectLoad) return false;
            if (!sawUnload) return false;
            if (!Ecs.GameRunning || runningSince < 0f) return false;
            return Time.realtimeSinceStartup - runningSince > 1.5f && !Ecs.LoadBusy;
        }

        public static void NotifyWorldLoaded() { expectLoad = false; }
        public static void ClearExpectLoad() { expectLoad = false; sawUnload = false; }

        /// The host is replacing its world (load / restart): the start detection must wait for the
        /// old world to go away and the new one to run.
        static bool needNewWorld;
        public static void WaitForNewWorld() { needNewWorld = true; runningSince = -1f; needNewWorldSince = Time.realtimeSinceStartup; }
        static float needNewWorldSince;
        /// Set when an expected load never happened (consumed by StartSync).
        public static bool LoadFailed;

        public static void Update()
        {
            bool running = Ecs.GameRunning;
            if (running && !wasRunning) { runningSince = Time.realtimeSinceStartup; Log.Msg("game world running"); Ui.OnGameRunningChanged(true); }
            if (!running && wasRunning) { runningSince = -1f; Log.Msg("game world stopped"); WorldSync.Stop(); Ui.OnGameRunningChanged(false); }
            if (!running && expectLoad) sawUnload = true;
            if (!running) needNewWorld = false;
            if (expectLoad && !sawUnload && Time.realtimeSinceStartup - expectSince > 25f && !Ecs.LoadBusy)
            {
                Log.Warn("expected a reload that never happened - load failed");
                expectLoad = false; LoadFailed = true;
            }
            if (needNewWorld && running && Time.realtimeSinceStartup - needNewWorldSince > 20f && !Ecs.LoadBusy)
            {
                Log.Warn("the host's new game never started - start detection re-armed");
                needNewWorld = false; runningSince = Time.realtimeSinceStartup;
            }
            wasRunning = running;

            if (Net.InSession)
            {
                if (!Net.IsHost) Phase = Net.S.Phase;
                if (Net.IsHost && Phase == PhaseChoosing && running && runningSince > 0f && Time.realtimeSinceStartup - runningSince > 2.5f && !StartSync.Busy && !expectLoad && !needNewWorld && !Ecs.LoadBusy && !Ecs.SaveBusy)
                {
                    // the host started a mission / loaded a save from the vanilla menu
                    var who = new List<int>();
                    foreach (var p in Net.Players) if (p.Id != Net.MyId) who.Add(p.Id);
                    if (Net.Steam != null) Net.Steam.SetState("game");
                    Phase = PhaseStarting;
                    if (who.Count == 0)
                    {
                        Log.Msg("host started a game with nobody connected - playing as host alone");
                        Phase = PhaseInGame;
                        Net.S.SetPhase(PhaseInGame);
                        WorldSync.HostStart(true);
                    }
                    else StartSync.HostBegin(StartSync.Reason.Initial, who);
                }
            }
            StartSync.Update();
            if (Held && running) Time.timeScale = 0f;
            Commands.Update();
            WorldSync.Update();
            Hud.Update();
            try { Pings.Update(); } catch (Exception e) { Log.Throttled("pings", "pings: " + e.Message); }
            try { DesyncCheck.Update(); } catch (Exception e) { Log.Throttled("dcheck", "desync check: " + e.Message); }
        }

        public static void LateUpdate()
        {
            if (Held && Ecs.GameRunning) Time.timeScale = 0f;
            Hud.LateUpdate();
        }

        public static void OnSceneLoaded(int idx, string name) { }

        // ------------------------------------------------------------------ session events
        public static void OnJoined(bool midGame)
        {
            Phase = Net.S.Phase;
            if (midGame) Ui.Toast("The game is already running - the host is sending it to you...", 5f);
        }

        public static void OnPlayerJoined(int id, bool rejoin)
        {
            if (!Net.IsHost) return;
            if (Phase == PhaseInGame || Phase == PhaseStarting)
                StartSync.HostBegin(StartSync.Reason.Join, new List<int> { id });
        }

        public static void OnPlayerLeft(int id)
        {
            Commands.OnPlayerLeft(id);
            Hud.OnPlayerLeft(id);
        }

        public static void OnPhase(int phase, byte[] data)
        {
            if (Net.IsHost) return;
            Phase = phase;
        }

        public static void OnSessionEnded()
        {
            StartSync.Reset();
            WorldSync.Stop();
            Commands.Reset();
            Hud.Reset(); Pings.Reset(); DesyncCheck.ResetSession();
            holds.Clear();
            ClearExpectLoad(); needNewWorld = false;
            Phase = PhaseLobby;
            if (Ecs.GameRunning) ApplySpeed();
        }

        public static void ClientLostSession()
        {
            if (Ecs.GameRunning) Ui.Toast("You are now playing on your own (the co-op session ended)", 6f);
        }

        // ------------------------------------------------------------------ host menu flow
        public static void HostChooseGame()
        {
            if (!Net.IsHost) return;
            Watch.Mark("START: phase -> choosing");
            Phase = PhaseChoosing;
            Net.S.SetPhase(PhaseChoosing);
            Watch.Mark("START: telling friends");
            Net.ToAll(Msg.Choosing, new byte[0]);
            Ui.ShowVanillaForHost();
        }

        public static void HostBackToLobby()
        {
            if (!Net.IsHost) return;
            Phase = PhaseLobby;
            Net.S.SetPhase(PhaseLobby);
            Ui.ShowLobby();
        }

        /// The host left the mission (quit to menu / to missions / restart): everyone follows.
        public static void OnLocalQuitToMenu()
        {
            if (!Net.InSession) return;
            if (Net.IsHost)
            {
                Log.Msg("host left the mission -> everyone back to the lobby");
                Net.ToAll(Msg.ToMenu, new byte[0]);
                Phase = PhaseLobby;
                Net.S.SetPhase(PhaseLobby);
                if (Net.Steam != null) Net.Steam.SetState("lobby");
                WorldSync.Stop();
                StartSync.Reset();
            }
            else if (!followingHost)
            {
                Log.Msg("friend left the mission -> leaving the session");
                Net.Leave("left the mission");
                Ui.Toast("You left the co-op game");
            }
        }

        static bool followingHost;
        public static void OnHostToMenu()
        {
            if (Net.IsHost) return;
            Phase = PhaseLobby;
            WorldSync.Stop();
            StartSync.Reset();
            Ui.Toast("The host went back to the menu", 4f);
            if (Ecs.GameRunning && MainMenu.Instance != null)
            {
                followingHost = true;
                try { MainMenu.Instance.QuitToMenu(); } catch (Exception e) { Log.Err("QuitToMenu: " + e); }
                followingHost = false;
            }
            Ui.ShowLobbyWhenMenu = true;
        }

        public static void OnHostChoosing()
        {
            if (Net.IsHost) return;
            Ui.Dirty();
        }
    }
}
