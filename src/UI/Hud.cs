// In-game co-op HUD: player list (top-left), friends' pointers on the map, hotkeys.
//   F9  host: re-sync everyone from the host's game / friend: ask the host to re-sync you
//   F8  show / hide sync statistics
using System;
using System.Collections.Generic;
using NetKit;
using Systems.InputSystems;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DinoNet
{
    public static class Hud
    {
        static GameObject panel;
        static TextMeshProUGUI text;
        static RectTransform cursorLayer;
        static bool stats;
        static float textT, cursorSendT;
        static float lastResyncKey;

        class Pointer { public Vector2 Ground; public float At; public TextMeshProUGUI Tag; }
        static readonly Dictionary<int, Pointer> pointers = new Dictionary<int, Pointer>();

        static void Build()
        {
            var c = GameObject.Find("DinoNetUI");
            if (c == null) return;
            var bg = Kit.Panel(c.transform, "Hud", new Color(0.06f, 0.05f, 0.045f, 0.72f));
            bg.raycastTarget = false;
            var rt = bg.rectTransform;
            // top-right corner: the top-left holds the mission objectives
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(-12f, -16f);
            rt.sizeDelta = new Vector2(360f, 60f);
            var fit = bg.gameObject.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var vl = bg.gameObject.AddComponent<VerticalLayoutGroup>();
            vl.padding = new RectOffset(10, 10, 8, 8); vl.childControlHeight = true; vl.childControlWidth = true;
            text = Kit.Label(bg.transform, "", 19f, Kit.Text);
            text.richText = true;
            panel = bg.gameObject;
            cursorLayer = Kit.Rect(c.transform, "Pointers");
            cursorLayer.Fill();
            cursorLayer.SetAsFirstSibling();
        }

        public static void Update()
        {
            bool show = Net.InSession && Ecs.GameRunning;
            if (panel == null) { if (show) Build(); if (panel == null) return; }
            if (panel.activeSelf != show) panel.SetActive(show);
            cursorLayer.gameObject.SetActive(show);
            if (!show) return;
            HotKeys();
            float now = Time.realtimeSinceStartup;
            if (now >= textT)
            {
                textT = now + 0.5f;
                var sb = new System.Text.StringBuilder();
                sb.Append("<color=#EBC66B><b>DinoNet co-op</b></color>  <size=15>").Append(Net.IsHost ? "host" : "friend").Append("</size>\n");
                foreach (var p in Net.Players)
                {
                    string hex = ColorUtility.ToHtmlStringRGB(Net.ColorOf(p.Id));
                    sb.Append("<color=#").Append(hex).Append(">■ ").Append(string.IsNullOrEmpty(p.Name) ? "Player " + (p.Id + 1) : p.Name).Append("</color>");
                    if (p.Id != Net.MyId) sb.Append("  <size=15><color=#BBB2A0>").Append(p.Away ? "" : p.PingMs + " ms").Append("</color> ").Append(Quality(p.PingMs, p.Away)).Append("</size>");
                    sb.Append('\n');
                }
                if (stats)
                {
                    var s = Net.S != null ? Net.S.Stats : default(NkStats);
                    sb.Append("<size=14><color=#BBB2A0>");
                    if (Net.IsHost) sb.Append("objects shared ").Append(WorldSync.Bound).Append('\n');
                    else sb.Append("matched ").Append(WorldSync.Bound).Append(" of ").Append(WorldSync.HostObjects).Append("  no match ").Append(WorldSync.Unmatched).Append('\n');
                    if (Net.Steam != null) sb.Append(Net.Steam.LinkStats().Replace(" | ", "\n")).Append('\n');
                    sb.Append("desync check ").Append(Net.IsHost ? "(host sends)" : DesyncCheck.LastResult).Append("  pings ").Append(Pings.Sent).Append('/').Append(Pings.Received).Append('\n');
                    sb.Append("cmds sent ").Append(Commands.SentCount).Append(" recv ").Append(Commands.RecvCount).Append(" applied ").Append(Commands.AppliedCount).Append('\n');
                    sb.Append("fix pos ").Append(WorldSync.Corrections).Append(" snap ").Append(WorldSync.Snaps).Append(" hp ").Append(WorldSync.HpFixes).Append(" phantom ").Append(WorldSync.Phantoms).Append(" made ").Append(WorldSync.Spawned).Append(" res ").Append(Res.Fixes).Append('\n');
                    sb.Append("clock x").Append(WorldSync.TimeNudge.ToString("0.00")).Append("  up ").Append((s.SentBytes / 1024)).Append(" KB  down ").Append((s.RecvBytes / 1024)).Append(" KB\n");
                    sb.Append("</color></size>");
                }
                sb.Append("<size=14><color=#9C9384>F9 ").Append(Net.IsHost ? "re-sync everyone" : "ask for a re-sync").Append("  ·  F8 stats  ·  F10 report  ·  middle-click ping</color></size>");
                text.text = sb.ToString();
            }
            // send my pointer
            if (now >= cursorSendT && Net.Active && WorldSync.Running)
            {
                cursorSendT = now + 0.2f;
                try
                {
                    var g = PlayerInputSystem.PlayerInputState.groundPosition;
                    Net.ToAll(Msg.Cursor, new Packer().F32(g.x).F32(g.y).ToArray(), false);
                }
                catch { }
            }
        }

        /// A word anyone can read at a glance (from the round trip to the host).
        static string Quality(int ping, bool away)
        {
            if (away) return "<color=#E05A47>lost</color>";
            if (ping <= 0) return "";
            if (ping <= 120) return "<color=#7CC36E>good</color>";
            if (ping <= 250) return "<color=#C9C36E>ok</color>";
            if (ping <= 600) return "<color=#E0A047>slow</color>";
            return "<color=#E05A47>lagging</color>";
        }

        static void HotKeys()
        {
            if (Input.GetKeyDown(KeyCode.F8)) stats = !stats;
            if (Input.GetKeyDown(KeyCode.F9) && Time.realtimeSinceStartup - lastResyncKey > 5f)
            {
                lastResyncKey = Time.realtimeSinceStartup;
                if (Net.IsHost) { Ui.Toast("Re-syncing everyone from your game..."); StartSync.HostRequestResync(); }
                else if (Net.Active) { Ui.Toast("Asked the host to re-sync you"); Net.ToHost(Msg.ResyncRequest, new byte[0]); }
            }
        }

        public static void OnCursor(int from, byte[] d)
        {
            if (from == Net.MyId) return;
            var u = new Unpacker(d);
            Pointer p;
            if (!pointers.TryGetValue(from, out p)) { p = new Pointer(); pointers[from] = p; }
            p.Ground = new Vector2(u.F32(), u.F32());
            p.At = Time.realtimeSinceStartup;
        }

        public static void LateUpdate()
        {
            if (cursorLayer == null || !cursorLayer.gameObject.activeSelf) return;
            var cam = Camera.main;
            float now = Time.realtimeSinceStartup;
            foreach (var kv in pointers)
            {
                var p = kv.Value;
                if (p.Tag == null)
                {
                    p.Tag = Kit.Label(cursorLayer, "▼ " + Net.NameOf(kv.Key), 20f, Net.ColorOf(kv.Key), TextAlignmentOptions.Center);
                    p.Tag.rectTransform.sizeDelta = new Vector2(260f, 30f);
                    p.Tag.enableWordWrapping = false;
                    p.Tag.fontStyle = FontStyles.Bold;
                    p.Tag.outlineWidth = 0.2f; p.Tag.outlineColor = new Color32(0, 0, 0, 200);
                }
                bool vis = cam != null && now - p.At < 2f;
                Vector3 sp = Vector3.zero;
                if (vis)
                {
                    sp = cam.WorldToScreenPoint(new Vector3(p.Ground.x, 0f, p.Ground.y));
                    vis = sp.z > 0f;
                }
                if (p.Tag.gameObject.activeSelf != vis) p.Tag.gameObject.SetActive(vis);
                if (!vis) continue;
                var canvasRt = (RectTransform)cursorLayer.parent;
                Vector2 local;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRt, sp, null, out local);
                p.Tag.rectTransform.anchorMin = p.Tag.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
                p.Tag.rectTransform.anchoredPosition = local + new Vector2(0f, 16f);
            }
        }

        public static void OnPlayerLeft(int id)
        {
            Pointer p;
            if (pointers.TryGetValue(id, out p)) { if (p.Tag != null) UnityEngine.Object.Destroy(p.Tag.gameObject); pointers.Remove(id); }
        }

        public static void Reset()
        {
            foreach (var kv in pointers) if (kv.Value.Tag != null) UnityEngine.Object.Destroy(kv.Value.Tag.gameObject);
            pointers.Clear();
        }
    }
}
