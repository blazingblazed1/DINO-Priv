// Render-scale guard. During a cutscene the game drops the render scale to 10 % and puts it
// back when the cutscene ends. Co-op reloads the mission while its intro cutscene is still
// playing, so "cutscene ended" never ran and the game stayed blurry until restart (0.0.3.3).
// This remembers each player's normal render scale and restores it whenever it is stuck low
// with no cutscene on screen.
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace DinoNet
{
    public static class Gfx
    {
        const float Low = 0.15f;
        static float good = -1f;
        static int goodMsaa = 1;
        static float checkT, lowSince = -1f;
        public static int Restores;

        static UniversalRenderPipelineAsset Asset()
        {
            try { return QualitySettings.GetRenderPipelineAssetAt(QualitySettings.GetQualityLevel()) as UniversalRenderPipelineAsset; }
            catch { return null; }
        }

        public static float CurrentScale { get { var a = Asset(); return a != null ? a.renderScale : -1f; } }

        static bool CutsceneOnScreen()
        {
            try
            {
                if (!Systems.GameStateSystems.GameState.UIState.cutsceneIsPlaying) return false;
                var c = UnityEngine.Object.FindObjectOfType<CutScene>();
                return c != null && c.gameObject.activeInHierarchy;
            }
            catch { return false; }
        }

        public static void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (now < checkT) return;
            checkT = now + 1f;
            var a = Asset();
            if (a == null) return;
            float s = a.renderScale;
            if (s > Low) { good = s; goodMsaa = a.msaaSampleCount; lowSince = -1f; return; }
            if (CutsceneOnScreen()) { lowSince = -1f; return; }
            if (lowSince < 0f) { lowSince = now; return; }
            if (now - lowSince < 2f) return;
            // stuck low with no cutscene: put the player's own value back (1.0 if we never saw one,
            // e.g. the low value was saved into the options by 0.0.3.3)
            float to = good > Low ? good : 1f;
            a.renderScale = to;
            if (good > Low) a.msaaSampleCount = goodMsaa;
            lowSince = -1f;
            Restores++;
            Log.Msg("render scale was stuck at " + s.ToString("0.00") + " after a cutscene - restored to " + to.ToString("0.00"));
        }
    
        /// Windowed / borderless: the game confines the mouse to its window (and puts it back every time the
        /// window gets focus), so moving to another window meant alt-tabbing. Always free in windowed modes;
        /// exclusive fullscreen and direct unit control (which locks the mouse) are left alone.
        public static void FreeCursor()
        {
            try
            {
                if (Screen.fullScreenMode == FullScreenMode.ExclusiveFullScreen) return;
                if (Cursor.lockState == CursorLockMode.Confined) Cursor.lockState = CursorLockMode.None;
            }
            catch { }
        }
}
}
