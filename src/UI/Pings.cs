// Middle-click map pings (Stellaris co-op style).
//  * A quick middle click (short press, mouse barely moved) pings the ground under the mouse.
//    Holding / dragging the middle button still moves the camera like normal.
//  * Everyone sees a pulsing ring in the pinger's color with their name for 4 s; off-screen
//    pings sit on the screen edge with an arrow pointing at them.
//  * A short soft chime plays - at most once per 5 s per player (no spamming the sound).
//  * Rings are capped at 3 per 5 s per player.
using System;
using System.Collections.Generic;
using NetKit;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DinoNet
{
    public static class Pings
    {
        const float Life = 4f, SoundCooldown = 5f, CapWindow = 5f;
        const int CapCount = 3;

        class P { public int From; public Vector2 Ground; public float At; public RectTransform Root; public Image Ring, Dot; public TextMeshProUGUI Label; public RectTransform Arrow; }
        static readonly List<P> live = new List<P>();
        static readonly Dictionary<int, float> lastSound = new Dictionary<int, float>();
        static readonly Dictionary<int, List<float>> recent = new Dictionary<int, List<float>>();
        static RectTransform layer;
        static Sprite ring, dot, tri;
        static AudioSource audio;
        static AudioClip chime;
        static float downT = -1f; static Vector3 downPos;
        public static int Sent, Received;

        static bool Allowed(int from, float now)
        {
            List<float> l;
            if (!recent.TryGetValue(from, out l)) { l = new List<float>(); recent[from] = l; }
            l.RemoveAll(t => now - t > CapWindow);
            if (l.Count >= CapCount) return false;
            l.Add(now);
            return true;
        }

        /// Senders already cap themselves; network jitter can bunch pings, so remotes get some slack.
        static bool AllowedRemote(int from, float now)
        {
            List<float> l;
            if (!recent.TryGetValue(from, out l)) { l = new List<float>(); recent[from] = l; }
            l.RemoveAll(t => now - t > CapWindow);
            if (l.Count >= CapCount + 2) return false;
            l.Add(now);
            return true;
        }

        // ------------------------------------------------------------------ input
        public static void Update()
        {
            bool inGame = Net.InSession && Ecs.GameRunning && WorldSync.Running;
            float now = Time.realtimeSinceStartup;
            if (inGame)
            {
                if (Input.GetMouseButtonDown(2)) { downT = now; downPos = Input.mousePosition; }
                if (Input.GetMouseButtonUp(2) && downT > 0f)
                {
                    bool quick = now - downT < 0.3f && (Input.mousePosition - downPos).magnitude < 10f;
                    downT = -1f;
                    if (quick) LocalPing(now);
                }
            }
            else downT = -1f;
            Animate(now);
        }

        static void LocalPing(float now)
        {
            Unity.Mathematics.float2 g;
            try { g = Systems.InputSystems.PlayerInputSystem.PlayerInputState.groundPosition; } catch { return; }
            if (!Allowed(Net.MyId, now)) return;
            recent[Net.MyId].RemoveAt(recent[Net.MyId].Count - 1);   // Show() counts it
            Net.ToAll(Msg.MapPing, new Packer().F32(g.x).F32(g.y).ToArray());
            Sent++;
            Show(Net.MyId, new Vector2(g.x, g.y), now);
        }

        public static void OnMessage(int from, byte[] d)
        {
            if (from == Net.MyId) return;
            try
            {
                var u = new Unpacker(d);
                var g = new Vector2(u.F32(), u.F32());
                Received++;
                Show(from, g, Time.realtimeSinceStartup);
            }
            catch (Exception e) { Log.Throttled("ping", "ping: " + e.Message); }
        }

        // ------------------------------------------------------------------ visuals
        static void Ensure()
        {
            if (layer != null) return;
            var c = GameObject.Find("DinoNetUI");
            if (c == null) return;
            var go = new GameObject("Pings", typeof(RectTransform));
            go.transform.SetParent(c.transform, false);
            layer = (RectTransform)go.transform;
            layer.anchorMin = Vector2.zero; layer.anchorMax = Vector2.one; layer.offsetMin = layer.offsetMax = Vector2.zero;
            layer.SetAsFirstSibling();
            ring = MakeSprite(64, (x, y, r) => { float d = Mathf.Abs(r - 26f); return Mathf.Clamp01(3.5f - d); });
            dot = MakeSprite(32, (x, y, r) => Mathf.Clamp01(12f - r));
            tri = MakeSprite(32, (x, y, r) => { float yy = y + 12f; float half = (24f - yy) * 0.5f; return yy >= 0f && yy <= 24f && Mathf.Abs(x) <= half ? 1f : 0f; });
            audio = go.AddComponent<AudioSource>();
            audio.playOnAwake = false; audio.spatialBlend = 0f; audio.volume = 0.35f;
            chime = MakeChime();
        }

        static Sprite MakeSprite(int size, Func<float, float, float, float> alpha)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false);
            t.wrapMode = TextureWrapMode.Clamp;
            var px = new Color32[size * size];
            float c = (size - 1) * 0.5f, k = 64f / size;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - c) * k, dy = (y - c) * k;
                    float a = alpha(dx, dy, Mathf.Sqrt(dx * dx + dy * dy) * (size == 64 ? 1f : 1f));
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255));
                }
            t.SetPixels32(px); t.Apply();
            return Sprite.Create(t, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        /// Two soft notes (E6 then A6), quick decay - short and quiet.
        static AudioClip MakeChime()
        {
            const int rate = 44100; float len = 0.42f;
            int n = (int)(rate * len);
            var data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / rate;
                float a = Note(t, 1318.5f, 0f) + Note(t, 1760f, 0.11f);
                data[i] = a * 0.5f;
            }
            var clip = AudioClip.Create("DinoNetPing", n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        static float Note(float t, float f, float start)
        {
            float tt = t - start;
            if (tt < 0f) return 0f;
            float env = Mathf.Min(1f, tt / 0.008f) * Mathf.Exp(-tt * 9f);
            return env * (Mathf.Sin(2f * Mathf.PI * f * tt) + 0.25f * Mathf.Sin(4f * Mathf.PI * f * tt));
        }

        static void Show(int from, Vector2 ground, float now)
        {
            Ensure();
            if (layer == null) return;
            if (from == Net.MyId ? !Allowed(from, now) : !AllowedRemote(from, now)) return;
            float ls;
            if (!lastSound.TryGetValue(from, out ls) || now - ls >= SoundCooldown)
            {
                lastSound[from] = now;
                try { if (audio != null && chime != null) audio.PlayOneShot(chime); } catch { }
            }
            var col = Net.ColorOf(from);
            var root = new GameObject("Ping", typeof(RectTransform));
            root.transform.SetParent(layer, false);
            var p = new P { From = from, Ground = ground, At = now, Root = (RectTransform)root.transform };
            p.Root.anchorMin = p.Root.anchorMax = new Vector2(0.5f, 0.5f);
            p.Root.sizeDelta = new Vector2(10f, 10f);
            p.Ring = NewImage(p.Root, ring, col, 90f);
            p.Dot = NewImage(p.Root, dot, col, 16f);
            p.Label = Kit.Label(p.Root, Net.NameOf(from), 18f, col, TextAlignmentOptions.Center);
            p.Label.rectTransform.sizeDelta = new Vector2(240f, 26f);
            p.Label.rectTransform.anchoredPosition = new Vector2(0f, 38f);
            p.Label.enableWordWrapping = false; p.Label.fontStyle = FontStyles.Bold;
            p.Label.outlineWidth = 0.2f; p.Label.outlineColor = new Color32(0, 0, 0, 200);
            var arrowImg = NewImage(p.Root, tri, col, 26f);
            p.Arrow = arrowImg.rectTransform;
            live.Add(p);
            Flight.Add("[ping] " + Net.NameOf(from) + " at " + ground.x.ToString("0") + "," + ground.y.ToString("0"));
        }

        static Image NewImage(Transform parent, Sprite s, Color c, float size)
        {
            var go = new GameObject("img", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var im = go.GetComponent<Image>();
            im.sprite = s; im.color = c; im.raycastTarget = false;
            im.rectTransform.sizeDelta = new Vector2(size, size);
            return im;
        }

        static void Animate(float now)
        {
            if (live.Count == 0) return;
            var cam = Camera.main;
            var canvasRt = layer != null ? (RectTransform)layer.parent : null;
            for (int i = live.Count - 1; i >= 0; i--)
            {
                var p = live[i];
                float age = now - p.At;
                if (age > Life || p.Root == null || cam == null || canvasRt == null || !Ecs.GameRunning)
                {
                    if (p.Root != null) UnityEngine.Object.Destroy(p.Root.gameObject);
                    live.RemoveAt(i);
                    continue;
                }
                float fade = age > Life - 0.8f ? (Life - age) / 0.8f : 1f;
                // ring: three expanding pulses
                float ph = (age % 1.0f);
                p.Ring.rectTransform.sizeDelta = Vector2.one * Mathf.Lerp(20f, 110f, ph);
                var rc = p.Ring.color; rc.a = (1f - ph) * fade; p.Ring.color = rc;
                var dc = p.Dot.color; dc.a = fade; p.Dot.color = dc;
                var lc = p.Label.color; lc.a = fade; p.Label.color = lc;
                var sp = cam.WorldToScreenPoint(new Vector3(p.Ground.x, 0f, p.Ground.y));
                bool behind = sp.z < 0f;
                if (behind) { sp.x = Screen.width - sp.x; sp.y = Screen.height - sp.y; }
                const float m = 40f;
                bool off = behind || sp.x < m || sp.x > Screen.width - m || sp.y < m || sp.y > Screen.height - m;
                Vector3 clamped = sp;
                if (off)
                {
                    clamped.x = Mathf.Clamp(sp.x, m, Screen.width - m);
                    clamped.y = Mathf.Clamp(sp.y, m, Screen.height - m);
                }
                Vector2 local;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRt, new Vector2(clamped.x, clamped.y), null, out local);
                p.Root.anchoredPosition = local;
                p.Arrow.gameObject.SetActive(off);
                p.Ring.gameObject.SetActive(!off);
                if (off)
                {
                    var dir = new Vector2(sp.x - Screen.width * 0.5f, sp.y - Screen.height * 0.5f);
                    float ang = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f;
                    p.Arrow.localRotation = Quaternion.Euler(0f, 0f, ang);
                    p.Arrow.anchoredPosition = dir.normalized * 22f;
                    var ac = p.Arrow.GetComponent<Image>().color; ac.a = fade; p.Arrow.GetComponent<Image>().color = ac;
                }
            }
        }

        public static void Reset()
        {
            foreach (var p in live) if (p.Root != null) UnityEngine.Object.Destroy(p.Root.gameObject);
            live.Clear(); recent.Clear(); lastSound.Clear();
        }
    }
}
