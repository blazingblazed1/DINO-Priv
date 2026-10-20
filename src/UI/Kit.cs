// Tiny UGUI builder (TextMeshPro with the game's own font).
using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace DinoNet
{
    public static class Kit
    {
        public static TMP_FontAsset Font;
        public static Material FontMat;

        // DINO-ish palette: dark wood panels, parchment text, warm gold accents
        public static readonly Color PanelBg = new Color(0.09f, 0.08f, 0.065f, 0.92f);
        public static readonly Color PanelBg2 = new Color(0.16f, 0.14f, 0.11f, 0.95f);
        public static readonly Color BoxBg = new Color(0.05f, 0.045f, 0.04f, 0.75f);
        public static readonly Color Btn = new Color(0.52f, 0.36f, 0.20f, 1f);
        public static readonly Color BtnHover = new Color(0.66f, 0.47f, 0.27f, 1f);
        public static readonly Color BtnPress = new Color(0.40f, 0.27f, 0.15f, 1f);
        public static readonly Color BtnOff = new Color(0.30f, 0.26f, 0.22f, 1f);
        public static readonly Color Text = new Color(0.95f, 0.90f, 0.80f, 1f);
        public static readonly Color TextDim = new Color(0.75f, 0.70f, 0.62f, 1f);
        public static readonly Color Gold = new Color(0.93f, 0.78f, 0.42f, 1f);
        public static readonly Color Good = new Color(0.55f, 0.85f, 0.45f, 1f);
        public static readonly Color Bad = new Color(0.95f, 0.45f, 0.35f, 1f);

        public static void FindFont(Component near)
        {
            if (Font != null) return;
            try
            {
                if (near != null)
                    foreach (var t in near.GetComponentsInChildren<TMP_Text>(true))
                        if (t.font != null) { Font = t.font; FontMat = t.fontSharedMaterial; break; }
                if (Font == null)
                {
                    foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                    {
                        if (f == null) continue;
                        if (Font == null) Font = f;
                        string n = f.name.ToLowerInvariant();
                        if (n.Contains("main") || n.Contains("menu") || n.Contains("title")) { Font = f; break; }
                    }
                }
                if (Font == null) Font = TMP_Settings.defaultFontAsset;
                if (Font != null) Log.Msg("UI font: " + Font.name);
            }
            catch (Exception e) { Log.Warn("font: " + e.Message); }
        }

        public static RectTransform Rect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = 5;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return rt;
        }

        /// Position in a 1920x1080 design space; (x, y) = top-left corner measured from the
        /// parent's top-left, y downwards.
        public static RectTransform At(this RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        public static RectTransform Fill(this RectTransform rt, float pad = 0f)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(pad, pad); rt.offsetMax = new Vector2(-pad, -pad);
            return rt;
        }

        public static RectTransform Centered(this RectTransform rt, float w, float h, float dy = 0f)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(0f, dy);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        public static Image Panel(Transform parent, string name, Color c)
        {
            var rt = Rect(parent, name);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = c;
            return img;
        }

        public static TextMeshProUGUI Label(Transform parent, string text, float size, Color c, TextAlignmentOptions align = TextAlignmentOptions.Left)
        {
            var rt = Rect(parent, "Label");
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            if (Font != null) t.font = Font;
            t.text = text; t.fontSize = size; t.color = c; t.alignment = align;
            t.enableWordWrapping = true; t.raycastTarget = false;
            t.overflowMode = TextOverflowModes.Ellipsis;
            return t;
        }

        public class Btn_
        {
            public Button B; public Image Img; public TextMeshProUGUI L; public RectTransform R;
            public bool Enabled { get { return B.interactable; } set { B.interactable = value; L.color = value ? Text : TextDim; } }
            public string Label { get { return L.text; } set { if (L.text != value) L.text = value; } }
        }

        public static Btn_ Button(Transform parent, string text, Action onClick, float size = 30f)
        {
            var img = Panel(parent, "Button", Btn);
            var b = img.gameObject.AddComponent<Button>();
            var cb = b.colors;
            cb.normalColor = Color.white; cb.highlightedColor = new Color(1.25f, 1.2f, 1.15f, 1f); cb.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            cb.selectedColor = Color.white; cb.disabledColor = new Color(0.6f, 0.6f, 0.6f, 0.8f); cb.colorMultiplier = 1.3f; cb.fadeDuration = 0.08f;
            b.colors = cb;
            b.targetGraphic = img;
            var nav = b.navigation; nav.mode = Navigation.Mode.None; b.navigation = nav;
            var l = Label(img.transform, text, size, Text, TextAlignmentOptions.Center);
            l.rectTransform.Fill(6f);
            l.fontStyle = FontStyles.Bold;
            l.characterSpacing = 4f;
            l.enableWordWrapping = false;
            l.enableAutoSizing = true; l.fontSizeMin = 12f; l.fontSizeMax = size;
            if (onClick != null) b.onClick.AddListener(new UnityAction(() =>
            {
                try { onClick(); } catch (Exception e) { Log.Err("button '" + text + "': " + e); }
            }));
            return new Btn_ { B = b, Img = img, L = l, R = img.rectTransform };
        }

        public static TMP_InputField Input(Transform parent, string placeholder, float size = 28f)
        {
            var bg = Panel(parent, "Input", Btn);
            var go = bg.gameObject;
            go.SetActive(false);
            var area = Rect(go.transform, "Text Area");
            area.Fill(10f);
            area.gameObject.AddComponent<RectMask2D>();
            var ph = Label(area, placeholder, size, new Color(1f, 1f, 1f, 0.45f), TextAlignmentOptions.Center);
            ph.rectTransform.Fill(); ph.fontStyle = FontStyles.Italic; ph.enableWordWrapping = false;
            var tx = Label(area, "", size, Text, TextAlignmentOptions.Center);
            tx.rectTransform.Fill(); tx.enableWordWrapping = false; tx.fontStyle = FontStyles.Bold; tx.characterSpacing = 2f;
            tx.overflowMode = TextOverflowModes.Overflow;
            var f = go.AddComponent<TMP_InputField>();
            f.textViewport = area;
            f.textComponent = tx;
            f.placeholder = ph;
            f.fontAsset = Font;
            f.pointSize = size;
            f.targetGraphic = bg;
            f.caretColor = Text; f.customCaretColor = true;
            f.selectionColor = new Color(0.9f, 0.7f, 0.3f, 0.5f);
            f.lineType = TMP_InputField.LineType.SingleLine;
            f.richText = false;
            var nav = f.navigation; nav.mode = Navigation.Mode.None; f.navigation = nav;
            go.SetActive(true);
            return f;
        }

        public static ScrollRect List(Transform parent, out RectTransform content)
        {
            var bg = Panel(parent, "List", BoxBg);
            var sr = bg.gameObject.AddComponent<ScrollRect>();
            var vp = Rect(bg.transform, "Viewport"); vp.Fill(6f);
            vp.gameObject.AddComponent<RectMask2D>();
            var img = vp.gameObject.AddComponent<Image>(); img.color = new Color(0, 0, 0, 0.01f);
            content = Rect(vp, "Content");
            content.anchorMin = new Vector2(0f, 1f); content.anchorMax = new Vector2(1f, 1f); content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero; content.sizeDelta = new Vector2(0f, 0f);
            var vl = content.gameObject.AddComponent<VerticalLayoutGroup>();
            vl.spacing = 6f; vl.childControlHeight = false; vl.childControlWidth = true; vl.childForceExpandHeight = false; vl.childForceExpandWidth = true;
            vl.padding = new RectOffset(4, 4, 4, 4);
            var fit = content.gameObject.AddComponent<ContentSizeFitter>(); fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            sr.viewport = vp; sr.content = content; sr.horizontal = false; sr.vertical = true; sr.scrollSensitivity = 30f;
            sr.movementType = ScrollRect.MovementType.Clamped;
            return sr;
        }

        public static RectTransform Row(Transform content, float h)
        {
            var r = Rect(content, "Row");
            var le = r.gameObject.AddComponent<LayoutElement>(); le.minHeight = h; le.preferredHeight = h;
            r.sizeDelta = new Vector2(0f, h);
            return r;
        }

        public static void Clear(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(t.GetChild(i).gameObject);
        }
    }
}
