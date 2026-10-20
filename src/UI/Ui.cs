// DinoNet menus: main menu (SINGLE PLAYER / MULTIPLAYER / OPTIONS / QUIT), the
// Multiplayer screen (IROH panel left, STEAM panel right - same layout as the other
// DinoNet-family mods), the co-op lobby, the host's "pick a game" banner, toasts and
// the sync overlay.
using System;
using System.Collections.Generic;
using System.Reflection;
using NetKit;
using TMPro;
using UI.MainMenuScripts;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace DinoNet
{
    public static class Ui
    {
        static Canvas canvas;
        static RectTransform root;
        static bool dirty = true;
        public static bool ShowLobbyWhenMenu;

        enum Screen_ { None, Multiplayer, Lobby }
        static Screen_ screen;
        enum MenuMode { Root, Single, HostChoose }
        static MenuMode menuMode = MenuMode.Root;

        // ------------------------------------------------------------------ canvas
        static void EnsureCanvas()
        {
            if (canvas != null) return;
            var go = new GameObject("DinoNetUI");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.layer = 5;
            canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            var sc = go.AddComponent<CanvasScaler>();
            sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1920, 1080);
            sc.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();
            root = (RectTransform)go.transform;
            Kit.FindFont(null);
            BuildToasts();
            BuildOverlay();
        }

        public static void Dirty() { dirty = true; }

        // ------------------------------------------------------------------ toasts
        static RectTransform toastBox;
        class ToastItem { public TextMeshProUGUI T; public float Until; }
        static readonly List<ToastItem> toasts = new List<ToastItem>();

        static void BuildToasts()
        {
            toastBox = Kit.Rect(root, "Toasts");
            toastBox.anchorMin = toastBox.anchorMax = toastBox.pivot = new Vector2(0.5f, 0f);
            toastBox.anchoredPosition = new Vector2(0f, 120f);
            toastBox.sizeDelta = new Vector2(1100f, 300f);
            var vl = toastBox.gameObject.AddComponent<VerticalLayoutGroup>();
            vl.childAlignment = TextAnchor.LowerCenter; vl.spacing = 6f;
            vl.childControlHeight = true; vl.childControlWidth = true; vl.childForceExpandHeight = false;
        }

        public static void Toast(string text, float secs = 3.5f)
        {
            Log.Msg("[toast] " + text);
            try
            {
                EnsureCanvas();
                var bg = Kit.Panel(toastBox, "Toast", new Color(0.08f, 0.07f, 0.06f, 0.88f));
                var le = bg.gameObject.AddComponent<LayoutElement>(); le.minHeight = 52f;
                var t = Kit.Label(bg.transform, text, 24f, Kit.Text, TextAlignmentOptions.Center);
                t.rectTransform.Fill(4f);
                // the 0.0.4.x toasts showed an empty band: one 24 pt line didn't fit the 28 px left inside,
                // and with "ellipsis" overflow TextMesh Pro then draws nothing at all
                t.overflowMode = TextOverflowModes.Overflow;
                t.enableAutoSizing = true; t.fontSizeMin = 14f; t.fontSizeMax = 24f;
                bg.raycastTarget = false;
                toasts.Add(new ToastItem { T = t, Until = Time.realtimeSinceStartup + secs });
                while (toasts.Count > 5) { UnityEngine.Object.Destroy(toasts[0].T.transform.parent.gameObject); toasts.RemoveAt(0); }
            }
            catch (Exception e) { Log.Warn("toast: " + e.Message); }
        }

        static void UpdateToasts()
        {
            float now = Time.realtimeSinceStartup;
            for (int i = toasts.Count - 1; i >= 0; i--)
            {
                var it = toasts[i];
                if (it.T == null) { toasts.RemoveAt(i); continue; }
                float left = it.Until - now;
                if (left <= 0f) { UnityEngine.Object.Destroy(it.T.transform.parent.gameObject); toasts.RemoveAt(i); continue; }
                float a = Mathf.Clamp01(left / 0.5f);
                it.T.alpha = a;
                it.T.transform.parent.GetComponent<Image>().color = new Color(0.08f, 0.07f, 0.06f, 0.88f * a);
            }
        }

        // ------------------------------------------------------------------ sync overlay
        static GameObject overlay;
        static TextMeshProUGUI overlayText;
        static RectTransform overlayBar;

        static void BuildOverlay()
        {
            var bg = Kit.Panel(root, "SyncOverlay", new Color(0.05f, 0.045f, 0.04f, 0.92f));
            bg.rectTransform.Centered(820f, 170f, 120f);
            overlay = bg.gameObject;
            overlayText = Kit.Label(bg.transform, "", 30f, Kit.Gold, TextAlignmentOptions.Center);
            overlayText.rectTransform.At(20f, 20f, 780f, 80f);
            var barBg = Kit.Panel(bg.transform, "Bar", new Color(0.2f, 0.18f, 0.15f, 1f));
            barBg.rectTransform.At(60f, 115f, 700f, 18f);
            var bar = Kit.Panel(barBg.transform, "Fill", Kit.Gold);
            overlayBar = bar.rectTransform;
            overlayBar.anchorMin = new Vector2(0f, 0f); overlayBar.anchorMax = new Vector2(0f, 1f); overlayBar.pivot = new Vector2(0f, 0.5f);
            overlayBar.offsetMin = Vector2.zero; overlayBar.offsetMax = Vector2.zero;
            overlay.SetActive(false);
        }

        static void UpdateOverlay()
        {
            string t = StartSync.OverlayText;
            bool show = Net.InSession && !string.IsNullOrEmpty(t);
            if (overlay.activeSelf != show) overlay.SetActive(show);
            if (!show) return;
            overlay.transform.SetAsLastSibling();
            if (overlayText.text != t) overlayText.text = t;
            float p = StartSync.Progress;
            overlayBar.parent.gameObject.SetActive(p >= 0f);
            if (p >= 0f) overlayBar.sizeDelta = new Vector2(700f * Mathf.Clamp01(p), 0f);
        }

        // ------------------------------------------------------------------ main menu integration
        static MainMenu menu;
        static GameObject btnSingle, btnMulti, btnBack;
        static List<GameObject> vanillaPlay = new List<GameObject>();
        static GameObject optionsGo, quitGo;
        static Canvas menuCanvas;
        static GameObject chooseBanner;

        static object F(object o, string name)
        {
            var f = o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return f?.GetValue(o);
        }

        static GameObject Go(object o, string name)
        {
            var c = F(o, name) as Component;
            return c != null ? c.gameObject : (F(o, name) as GameObject);
        }

        public static void OnMainMenuAwake(MainMenu m)
        {
            if (m.inGame || m.inMapEditor) return;
            menu = m;
            EnsureCanvas();
            Kit.Font = null; Kit.FindFont(m);
            menuCanvas = F(m, "menuButtons") as Canvas;
            var quit = F(m, "quitButton") as MainMenuButton;
            if (quit == null) { Log.Err("menu: quit button not found - menu buttons not added"); return; }
            quitGo = quit.gameObject;
            // the game runs this setup twice: remove buttons left over from the first run
            var parent = quit.transform.parent;
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                var c = parent.GetChild(i);
                if (c != null && c.name.StartsWith("DinoNet_")) { c.gameObject.SetActive(false); UnityEngine.Object.Destroy(c.gameObject); }
            }
            optionsGo = Go(m, "optionsButton");
            vanillaPlay.Clear(); vanillaKeys.Clear();
            foreach (var n in new[] { "tutorialButton", "creditsButton", "continueButton", "endlessGameButton", "customMapButton", "campaignButton",
                                      "newGameButton", "currentProfileButton", "specialMissionButton", "sandboxButton", "loadButton", "_emptyObject" })
            {
                var g = Go(m, n);
                if (g != null) { vanillaPlay.Add(g); vanillaKeys[g] = n; }
            }
            btnSingle = CloneMenuButton(quit, "DinoNet_SinglePlayer", "SINGLE PLAYER", () => { menuMode = MenuMode.Single; ApplyMenuMode(); });
            btnMulti = CloneMenuButton(quit, "DinoNet_Multiplayer", "MULTIPLAYER", ShowMultiplayer);
            btnBack = CloneMenuButton(quit, "DinoNet_Back", "< BACK", OnMenuBack);
            // order: SINGLE PLAYER, MULTIPLAYER first; BACK just above QUIT
            int first = int.MaxValue;
            foreach (var g in vanillaPlay) if (g.transform.parent == quitGo.transform.parent) first = Math.Min(first, g.transform.GetSiblingIndex());
            if (first == int.MaxValue) first = 0;
            btnSingle.transform.SetSiblingIndex(first);
            btnMulti.transform.SetSiblingIndex(first + 1);
            btnBack.transform.SetSiblingIndex(quitGo.transform.GetSiblingIndex());
            BuildChooseBanner();
            if (Net.InSession)
            {
                if (Net.IsHost && Game.Phase == Game.PhaseChoosing) menuMode = MenuMode.HostChoose;
                else { menuMode = MenuMode.Root; ShowLobbyWhenMenu = true; }
            }
            else menuMode = MenuMode.Root;
            ApplyMenuMode();
            Log.Msg("main menu: co-op buttons added");
        }

        static GameObject CloneMenuButton(MainMenuButton src, string name, string text, Action onClick)
        {
            var go = UnityEngine.Object.Instantiate(src.gameObject, src.transform.parent, false);
            go.name = name;
            var b = go.GetComponent<Button>() ?? go.GetComponentInChildren<Button>(true);
            if (b == null) b = go.AddComponent<Button>();
            b.onClick = new Button.ButtonClickedEvent();
            b.onClick.AddListener(() => { try { onClick(); } catch (Exception e) { Log.Err(name + ": " + e); } });
            // strip anything that could rewrite the label (localisation helpers etc.)
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                var tn = mb.GetType().Name;
                if (mb is MainMenuButton || mb is Button || mb is Graphic || mb is LayoutElement || mb is ContentSizeFitter || mb is LayoutGroup) continue;
                if (tn.IndexOf("Locali", StringComparison.OrdinalIgnoreCase) >= 0 || tn.IndexOf("Text", StringComparison.OrdinalIgnoreCase) >= 0 && !(mb is TMP_Text))
                    UnityEngine.Object.Destroy(mb);
            }
            var mmb = go.GetComponent<MainMenuButton>();
            if (mmb != null) { try { mmb.Init(src.selectedColor, src.deselectedColor, src.notInteractableColor); } catch { } }
            SetButtonText(go, text);
            go.SetActive(true);
            return go;
        }

        static void SetButtonText(GameObject go, string text)
        {
            if (go == null) return;
            var t = go.GetComponentInChildren<TMP_Text>(true);
            if (t != null && t.text != text) t.text = text;
        }

        public static void OnMenuInitialState(MainMenu m)
        {
            if (m.inGame || m.inMapEditor || m != menu) return;
            ApplyMenuMode();
        }

        static void OnMenuBack()
        {
            if (menuMode == MenuMode.HostChoose && Net.IsHost) { Game.HostBackToLobby(); return; }
            menuMode = MenuMode.Root;
            ApplyMenuMode();
        }

        static void ApplyMenuMode()
        {
            if (menu == null || btnSingle == null) return;
            bool root = menuMode == MenuMode.Root;
            btnSingle.SetActive(root);
            btnMulti.SetActive(root);
            btnBack.SetActive(!root);
            SetButtonText(btnBack, menuMode == MenuMode.HostChoose ? "< BACK TO LOBBY" : "< BACK");
            if (root) foreach (var g in vanillaPlay) g.SetActive(false);
            else
            {
                ShowVanillaButtons();
                btnSingle.SetActive(false); btnMulti.SetActive(false); btnBack.SetActive(true);
            }
            if (optionsGo != null) optionsGo.SetActive(true);
            if (quitGo != null) quitGo.SetActive(true);
            if (chooseBanner != null) chooseBanner.SetActive(menuMode == MenuMode.HostChoose && Net.IsHost && !Ecs.GameRunning);
        }

        /// Same choice of buttons as the game's UpdateProfileState, set directly (no calls
        /// into the game's menu code, which re-entered our hooks).
        static void ShowVanillaButtons()
        {
            bool prof = true, saves = false;
            try { prof = !string.IsNullOrEmpty(global::UI.Profiles.ProfileName); } catch { }
            try { saves = global::SavedGames.HasSaves(); } catch { }
            foreach (var g in vanillaPlay)
            {
                if (g == null) continue;
                bool on;
                switch (g.name == null ? "" : VanillaKey(g))
                {
                    case "newGameButton": on = !prof; break;
                    case "loadButton": case "continueButton": on = saves; break;
                    case "creditsButton": on = true; break;
                    default: on = prof; break;
                }
                if (g.activeSelf != on) g.SetActive(on);
            }
        }
        static readonly Dictionary<GameObject, string> vanillaKeys = new Dictionary<GameObject, string>();
        static string VanillaKey(GameObject g) { string k; return vanillaKeys.TryGetValue(g, out k) ? k : ""; }

        public static void ShowVanillaForHost()
        {
            Watch.Mark("START: hiding the lobby");
            HideScreens();
            menuMode = MenuMode.HostChoose;
            Watch.Mark("START: showing the game menu");
            ApplyMenuMode();
            Watch.Mark("START: menu shown - pick a game");
            Toast("Pick any mission, endless game, sandbox, custom map or a save - your friends come along", 6f);
        }

        static void BuildChooseBanner()
        {
            if (chooseBanner != null) UnityEngine.Object.Destroy(chooseBanner);
            var bg = Kit.Panel(root, "ChooseBanner", Kit.PanelBg);
            bg.rectTransform.anchorMin = bg.rectTransform.anchorMax = bg.rectTransform.pivot = new Vector2(0.5f, 1f);
            bg.rectTransform.anchoredPosition = new Vector2(0f, -20f);
            bg.rectTransform.sizeDelta = new Vector2(1180f, 70f);
            var t = Kit.Label(bg.transform, "CO-OP: start any game - campaign, endless, sandbox, custom map or a saved game. Your friends will load it with you.", 22f, Kit.Gold, TextAlignmentOptions.Center);
            t.rectTransform.Fill(10f);
            bg.raycastTarget = false;
            chooseBanner = bg.gameObject;
            chooseBanner.SetActive(false);
        }

        static void HideVanillaMenu(bool hide)
        {
            if (menuCanvas != null) menuCanvas.enabled = !hide;
        }

        public static void HideMenus()
        {
            HideScreens();
            if (chooseBanner != null) chooseBanner.SetActive(false);
        }

        public static void OnGameRunningChanged(bool running)
        {
            if (running) { HideScreens(); if (chooseBanner != null) chooseBanner.SetActive(false); }
            dirty = true;
        }

        public static void OnSceneLoaded(int idx, string name)
        {
            EnsureCanvas();
            dirty = true;
        }

        // ------------------------------------------------------------------ screens
        static GameObject mpScreen, lobbyScreen;

        static void HideScreens()
        {
            if (mpScreen != null) mpScreen.SetActive(false);
            if (lobbyScreen != null) lobbyScreen.SetActive(false);
            if (screen != Screen_.None) { screen = Screen_.None; HideVanillaMenu(false); }
        }

        static RectTransform ScreenRoot(string name, string title)
        {
            var bg = Kit.Panel(root, name, new Color(0f, 0f, 0f, 0.35f));
            bg.rectTransform.Fill();
            var frame = Kit.Rect(bg.transform, "Frame");
            frame.Centered(1920f, 1080f);
            var t = Kit.Label(frame, title, 86f, Kit.Text, TextAlignmentOptions.Center);
            t.rectTransform.At(0f, 40f, 1920f, 110f);
            t.fontStyle = FontStyles.Bold; t.characterSpacing = 8f;
            t.enableWordWrapping = false;
            return frame;
        }

        // ---- Multiplayer
        static TMP_InputField nameField, codeField;
        static Kit.Btn_ lanToggle, steamVis, steamRefresh;
        static RectTransform steamList;
        static TextMeshProUGUI steamListInfo, steamNote;
        static int steamListVersion = -1;
        static GameObject codeRow;

        public static void ShowMultiplayer()
        {
            EnsureCanvas();
            if (mpScreen == null) BuildMultiplayer();
            if (lobbyScreen != null) lobbyScreen.SetActive(false);
            mpScreen.SetActive(true);
            mpScreen.transform.SetAsFirstSibling();
            screen = Screen_.Multiplayer;
            HideVanillaMenu(true);
            nameField.text = Prefs.Name.Length > 0 ? Prefs.Name : Prefs.DisplayName;
            codeRow.SetActive(false);
            TryPasteCode(false);
            if (SteamLink.Available) SteamLink.RefreshList();
            dirty = true;
        }

        static void BuildMultiplayer()
        {
            var f = ScreenRoot("Multiplayer", "MULTIPLAYER");
            mpScreen = f.parent.gameObject;
            var nl = Kit.Label(f, "Your name", 30f, Kit.TextDim, TextAlignmentOptions.Right);
            nl.rectTransform.At(560f, 168f, 300f, 54f);
            nameField = Kit.Input(f, "Your name", 30f);
            nameField.characterLimit = 24;
            ((RectTransform)nameField.transform).At(880f, 160f, 480f, 58f);
            nameField.onEndEdit.AddListener(s => { Prefs.Name = (s ?? "").Trim(); Prefs.Save(); });

            // ---- IROH
            var ip = Kit.Panel(f, "Iroh", Kit.PanelBg2).rectTransform.At(115f, 250f, 855f, 720f);
            var it = Kit.Label(ip, "IROH", 72f, Kit.Text, TextAlignmentOptions.Center); it.rectTransform.At(0f, 30f, 855f, 90f); it.fontStyle = FontStyles.Bold; it.characterSpacing = 6f;
            var id = Kit.Label(ip, "Direct peer-to-peer connection with low latency. No Steam needed - share a join code with your friends. Also works on LAN and over Hamachi.", 25f, Kit.Text, TextAlignmentOptions.Center);
            id.rectTransform.At(60f, 125f, 735f, 110f);
            Kit.Button(ip, "HOST GAME", () => { SavedName(); if (Net.HostIroh()) ShowLobby(); }, 34f).R.At(62f, 240f, 355f, 82f);
            Kit.Button(ip, "JOIN GAME", () => { codeRow.SetActive(true); TryPasteCode(true); codeField.ActivateInputField(); }, 34f).R.At(437f, 240f, 355f, 82f);
            lanToggle = Kit.Button(ip, "", () => { Prefs.LanOnly = !Prefs.LanOnly; Prefs.Save(); dirty = true; }, 22f);
            lanToggle.R.At(62f, 344f, 730f, 46f);
            var gl = Kit.Label(ip, "Public games", 30f, Kit.Text); gl.rectTransform.At(62f, 412f, 400f, 44f);
            var box = Kit.Panel(ip, "IrohBox", Kit.BoxBg).rectTransform.At(62f, 466f, 730f, 220f);
            var bt = Kit.Label(box, "Iroh games are private: join with the host's code.\nFor a public game list, use Steam.", 23f, Kit.TextDim);
            bt.rectTransform.At(26f, 18f, 680f, 80f);
            // join-code row (appears after JOIN GAME)
            var cr = Kit.Rect(box, "CodeRow").At(18f, 110f, 694f, 96f);
            codeRow = cr.gameObject;
            codeField = Kit.Input(cr, "paste the host's join code here", 20f);
            ((RectTransform)codeField.transform).At(0f, 18f, 500f, 60f);
            codeField.onSubmit.AddListener(s => ConnectCode());
            Kit.Button(cr, "CONNECT", ConnectCode, 26f).R.At(512f, 18f, 182f, 60f);

            // ---- STEAM
            var sp = Kit.Panel(f, "Steam", Kit.PanelBg2).rectTransform.At(1031f, 250f, 855f, 720f);
            var st = Kit.Label(sp, "STEAM", 72f, Kit.Text, TextAlignmentOptions.Center); st.rectTransform.At(0f, 30f, 855f, 90f); st.fontStyle = FontStyles.Bold; st.characterSpacing = 6f;
            var sd = Kit.Label(sp, "Easy access - friends right-click you on Steam and pick \"Join Game\". Public lobby list so anyone with the mod can find your game.", 25f, Kit.Text, TextAlignmentOptions.Center);
            sd.rectTransform.At(60f, 125f, 735f, 110f);
            Kit.Button(sp, "HOST GAME", () => { SavedName(); if (Net.HostSteam(Prefs.SteamPublic)) ShowLobby(); }, 34f).R.At(62f, 240f, 355f, 82f);
            Kit.Button(sp, "JOIN GAME", JoinSteamPick, 34f).R.At(437f, 240f, 355f, 82f);
            steamVis = Kit.Button(sp, "", () => { Prefs.SteamPublic = !Prefs.SteamPublic; Prefs.Save(); dirty = true; }, 22f);
            steamVis.R.At(62f, 344f, 730f, 46f);
            var sl = Kit.Label(sp, "Public games", 30f, Kit.Text); sl.rectTransform.At(62f, 412f, 400f, 44f);
            steamRefresh = Kit.Button(sp, "REFRESH", () => SteamLink.RefreshList(), 22f);
            steamRefresh.R.At(612f, 408f, 180f, 46f);
            ((RectTransform)Kit.List(sp, out steamList).transform).At(62f, 466f, 730f, 220f);
            steamListInfo = Kit.Label(sp, "", 22f, Kit.TextDim); steamListInfo.rectTransform.At(88f, 484f, 680f, 60f);
            steamNote = Kit.Label(sp, "", 20f, Kit.Bad, TextAlignmentOptions.Center); steamNote.rectTransform.At(62f, 690f, 730f, 28f);

            Kit.Button(f, "BACK", () => { HideScreens(); if (menu != null) { menuMode = MenuMode.Root; menu.ShowAsInitialState(); ApplyMenuMode(); } }, 34f).R.At(115f, 1000f, 240f, 66f);
            var ver = Kit.Label(f, "DinoNet co-op " + Mod.Version + (Mod.NetKitOk ? "" : "  -  netkit.dll missing!"), 18f, Kit.TextDim, TextAlignmentOptions.Right);
            ver.rectTransform.At(1300f, 1030f, 586f, 30f);
        }

        static void SavedName()
        {
            if (nameField != null) { Prefs.Name = (nameField.text ?? "").Trim(); Prefs.Save(); }
        }

        static void ConnectCode()
        {
            SavedName();
            string c = (codeField.text ?? "").Trim();
            if (c.Length == 0) { TryPasteCode(true); c = (codeField.text ?? "").Trim(); }
            if (c.Length == 0) { Toast("Paste the host's join code first"); return; }
            if (Net.JoinIroh(c)) ShowLobby();
        }

        static void TryPasteCode(bool loud)
        {
            try
            {
                string clip = GUIUtility.systemCopyBuffer;
                if (!string.IsNullOrEmpty(clip) && Mod.NetKitOk && NetKitLib.IsJoinCode(clip.Trim()))
                {
                    if (codeField.text != clip.Trim())
                    {
                        codeField.text = clip.Trim();
                        codeRow.SetActive(true);
                        Toast("Join code pasted from the clipboard", 4f);
                    }
                }
                else if (loud && string.IsNullOrEmpty(codeField.text)) Toast("Copy the host's join code, then press JOIN GAME again");
            }
            catch { }
        }

        static void JoinSteamPick()
        {
            SavedName();
            if (!SteamLink.Available) { Toast("Steam isn't running"); return; }
            // one friend hosting -> join right away; otherwise refresh the list to pick from
            var friends = SteamLink.ListResults.FindAll(l => l.Friend);
            if (friends.Count == 1 && friends[0].Version == Mod.Version) { if (Net.JoinSteam(friends[0].Id)) ShowLobby(); return; }
            SteamLink.RefreshList();
            Toast(friends.Count == 0 ? "Looking for games... friends' games show up in the list below" : "Pick a game from the list below");
        }

        static void UpdateMultiplayer()
        {
            lanToggle.Label = (Prefs.LanOnly ? "[X]" : "[  ]") + "  LAN / direct only (no internet relays)";
            steamVis.Label = Prefs.SteamPublic ? "PUBLIC:  anyone can join you,  list shows all games" : "FRIENDS ONLY:  only Steam friends can join you";
            bool steamOk = SteamLink.Available;
            steamNote.text = steamOk ? "" : "Steam isn't running - use Iroh";
            steamRefresh.Enabled = steamOk && !SteamLink.ListBusy;
            int v = SteamLink.ListResults.Count * 1000 + (SteamLink.ListBusy ? 1 : 0) + (int)(SteamLink.ListTime * 10) % 997;
            if (v == steamListVersion) return;
            steamListVersion = v;
            Kit.Clear(steamList);
            if (SteamLink.ListBusy) steamListInfo.text = "Searching...";
            else if (SteamLink.ListResults.Count == 0) steamListInfo.text = "No public games right now. Press REFRESH to look again.";
            else steamListInfo.text = "";
            foreach (var l in SteamLink.ListResults)
            {
                var row = Kit.Row(steamList, 52f);
                var bg = Kit.Panel(row, "bg", new Color(1f, 1f, 1f, 0.05f)); bg.rectTransform.Fill();
                bool verOk = l.Version == Mod.Version;
                string state = l.State == "game" ? "in game" : "in lobby";
                string label = (l.Friend ? "<color=#EBC66B>FRIEND</color>  " : "") + (string.IsNullOrEmpty(l.Name) ? l.Host + "'s game" : l.Name)
                               + "   " + l.Members + "/" + (l.Max > 0 ? l.Max : 8) + "  -  " + state + (verOk ? "" : "  <color=#F07259>(v" + l.Version + ")</color>");
                var t = Kit.Label(row, label, 22f, Kit.Text); t.richText = true; t.rectTransform.At(12f, 6f, 520f, 40f);
                ulong id = l.Id;
                var b = Kit.Button(row, "JOIN", () => { SavedName(); if (Net.JoinSteam(id)) ShowLobby(); }, 22f);
                b.R.At(556f, 4f, 150f, 44f);
                b.Enabled = verOk;
            }
        }

        // ---- Lobby
        static TextMeshProUGUI lobbyStatus, lobbyInfo, codeText;
        static RectTransform playerList;
        static Kit.Btn_ readyBtn, startBtn, copyBtn, inviteBtn, leaveBtn;
        static int lobbyVersion = -1;
        static float startHold;
        static bool pendingChoose;
        static int pendingChooseFrame = -1;

        public static void ShowLobby()
        {
            EnsureCanvas();
            if (lobbyScreen == null) BuildLobby();
            if (mpScreen != null) mpScreen.SetActive(false);
            lobbyScreen.SetActive(true);
            lobbyScreen.transform.SetAsFirstSibling();
            screen = Screen_.Lobby;
            HideVanillaMenu(true);
            if (chooseBanner != null) chooseBanner.SetActive(false);
            dirty = true;
        }

        static void BuildLobby()
        {
            var f = ScreenRoot("Lobby", "CO-OP LOBBY");
            lobbyScreen = f.parent.gameObject;
            lobbyStatus = Kit.Label(f, "", 28f, Kit.Gold, TextAlignmentOptions.Center);
            lobbyStatus.rectTransform.At(0f, 160f, 1920f, 44f);
            var lp = Kit.Panel(f, "Players", Kit.PanelBg2).rectTransform.At(115f, 230f, 1000f, 700f);
            var lt = Kit.Label(lp, "PLAYERS", 40f, Kit.Text, TextAlignmentOptions.Center); lt.rectTransform.At(0f, 20f, 1000f, 60f); lt.fontStyle = FontStyles.Bold; lt.characterSpacing = 6f;
            ((RectTransform)Kit.List(lp, out playerList).transform).At(40f, 95f, 920f, 570f);

            var rp = Kit.Panel(f, "Info", Kit.PanelBg2).rectTransform.At(1150f, 230f, 655f, 700f);
            var rt = Kit.Label(rp, "INVITE", 40f, Kit.Text, TextAlignmentOptions.Center); rt.rectTransform.At(0f, 20f, 655f, 60f); rt.fontStyle = FontStyles.Bold; rt.characterSpacing = 6f;
            lobbyInfo = Kit.Label(rp, "", 24f, Kit.Text, TextAlignmentOptions.Center);
            lobbyInfo.rectTransform.At(40f, 95f, 575f, 220f);
            codeText = Kit.Label(rp, "", 18f, Kit.TextDim, TextAlignmentOptions.Center);
            codeText.rectTransform.At(40f, 320f, 575f, 60f);
            codeText.enableWordWrapping = true; codeText.overflowMode = TextOverflowModes.Truncate;
            copyBtn = Kit.Button(rp, "COPY JOIN CODE", () =>
            {
                if (string.IsNullOrEmpty(Net.JoinCode)) { Toast("The join code isn't ready yet"); return; }
                GUIUtility.systemCopyBuffer = Net.JoinCode; Toast("Join code copied - send it to your friends");
            }, 28f);
            copyBtn.R.At(80f, 400f, 495f, 72f);
            inviteBtn = Kit.Button(rp, "INVITE STEAM FRIENDS", () => Net.Steam?.InviteFriends(), 28f);
            inviteBtn.R.At(80f, 490f, 495f, 72f);

            readyBtn = Kit.Button(f, "READY", () => { if (Net.Active) { bool r = !MeReady(); Net.S.SetReady(r); Net.RefreshRoster(); dirty = true; } }, 34f);
            readyBtn.R.At(115f, 970f, 360f, 80f);
            startBtn = Kit.Button(f, "START", null, 34f);
            startBtn.R.At(780f, 970f, 360f, 80f);
            var trig = startBtn.B.gameObject.AddComponent<HoldButton>();
            trig.OnDown = () => { startHold = Time.realtimeSinceStartup; };
            trig.OnUp = () => { startHold = 0f; };
            startBtn.B.onClick.AddListener(() =>
            {
                if (!Net.IsHost) return;
                if (Net.AllReadyOrAlone()) { pendingChoose = true; Watch.Mark("START clicked - queued for next frame"); }
                else Toast("Not everyone is ready - hold START for 2 seconds to start anyway");
            });
            leaveBtn = Kit.Button(f, "LEAVE", () => { Net.Leave("left the lobby"); ShowMultiplayer(); }, 34f);
            leaveBtn.R.At(1445f, 970f, 360f, 80f);
        }

        static bool MeReady()
        {
            foreach (var p in Net.Players) if (p.Id == Net.MyId) return p.Ready;
            return false;
        }

        static void UpdateLobby()
        {
            if (!Net.InSession)
            {
                lobbyStatus.text = string.IsNullOrEmpty(Net.Status) ? "Not connected" : Net.Status;
            }
            else if (Net.Connecting)
            {
                lobbyStatus.text = Net.IsSteam ? (Net.Steam?.Status ?? "Connecting...") : "Connecting to the host...";
            }
            else
            {
                string phase = Game.Phase == Game.PhaseChoosing ? (Net.IsHost ? "You are choosing a game" : "The host is choosing a game...")
                    : Game.Phase >= Game.PhaseStarting ? "The game is starting..." : (Net.IsHost ? "Waiting for friends - press START when everyone is ready" : "Waiting for the host to start");
                lobbyStatus.text = (Net.IsHost ? "HOSTING" : "CONNECTED") + " via " + (Net.IsSteam ? "STEAM" : "IROH") + "   -   " + phase;
            }
            bool host = Net.IsHost && Net.InSession;
            copyBtn.R.gameObject.SetActive(host && !Net.IsSteam);
            inviteBtn.R.gameObject.SetActive(host && Net.IsSteam);
            codeText.gameObject.SetActive(host && !Net.IsSteam);
            if (host && !Net.IsSteam)
            {
                string c = Net.JoinCode ?? "";
                codeText.text = c.Length == 0 ? "creating the join code..." : (c.Length > 64 ? c.Substring(0, 64) + "..." : c);
            }
            if (!Net.InSession) lobbyInfo.text = "";
            else if (host) lobbyInfo.text = Net.IsSteam
                ? "Friends can right-click you in their Steam friends list and pick \"Join Game\", or find your game in the Steam list of the Multiplayer screen." + (Prefs.SteamPublic ? "\n\nYour game is PUBLIC." : "\n\nOnly Steam friends can join.")
                : "Copy the join code and send it to your friends (Discord etc). They paste it in the IROH panel and press CONNECT.";
            else lobbyInfo.text = "You're in! When the host starts, the game loads for you automatically.\n\nYou all share one town: same buildings, army and resources. Each of you has your own camera.";
            readyBtn.Enabled = Net.Active;
            readyBtn.Label = MeReady() ? "NOT READY" : "READY";
            startBtn.R.gameObject.SetActive(host);
            if (host)
            {
                bool ok = Net.AllReadyOrAlone();
                startBtn.Label = ok ? "START" : (startHold > 0f ? "HOLD... " + Mathf.CeilToInt(Mathf.Max(0f, 2f - (Time.realtimeSinceStartup - startHold))) : "START (hold)");
                startBtn.Img.color = ok ? Kit.Btn : Kit.BtnOff;
                if (startHold > 0f && Time.realtimeSinceStartup - startHold > 2f) { startHold = 0f; pendingChoose = true; Watch.Mark("START held 2 s - queued for next frame"); }
            }
            if (Net.Version == lobbyVersion) { UpdatePings(); return; }
            lobbyVersion = Net.Version;
            Kit.Clear(playerList);
            pingLabels.Clear();
            foreach (var p in Net.Players)
            {
                var row = Kit.Row(playerList, 62f);
                var bg = Kit.Panel(row, "bg", new Color(1f, 1f, 1f, 0.05f)); bg.rectTransform.Fill();
                var sw = Kit.Panel(row, "color", Net.ColorOf(p.Id)); sw.rectTransform.At(14f, 14f, 34f, 34f);
                string nm = (string.IsNullOrEmpty(p.Name) ? "Player " + (p.Id + 1) : p.Name) + (p.IsHost ? "  (host)" : "") + (p.Id == Net.MyId ? "  (you)" : "");
                var t = Kit.Label(row, nm, 28f, Net.ColorOf(p.Id)); t.rectTransform.At(64f, 10f, 430f, 44f); t.enableWordWrapping = false;
                string ping = p.Id == Net.MyId ? "" : (p.Away ? "away" : p.PingMs + " ms" + (p.Path == 2 ? " (relay)" : ""));
                var pt = Kit.Label(row, ping, 22f, Kit.TextDim, TextAlignmentOptions.Right); pt.rectTransform.At(500f, 14f, 150f, 36f);
                pingLabels[p.Id] = pt;
                var rt = Kit.Label(row, p.Ready ? "READY" : "not ready", 24f, p.Ready ? Kit.Good : Kit.TextDim, TextAlignmentOptions.Center); rt.rectTransform.At(660f, 12f, 130f, 40f);
                if (Net.IsHost && p.Id != Net.MyId)
                {
                    int pid = p.Id;
                    Kit.Button(row, "KICK", () => Net.S?.Kick(pid, "kicked by the host"), 20f).R.At(800f, 8f, 100f, 46f);
                }
            }
        }

        static readonly Dictionary<int, TMP_Text> pingLabels = new Dictionary<int, TMP_Text>();
        static void UpdatePings()
        {
            foreach (var p in Net.Players)
            {
                TMP_Text t;
                if (!pingLabels.TryGetValue(p.Id, out t) || t == null) continue;
                string ping = p.Id == Net.MyId ? "" : (p.Away ? "away" : p.PingMs + " ms" + (p.Path == 2 ? " (relay)" : ""));
                if (t.text != ping) t.text = ping;
            }
        }

        // ------------------------------------------------------------------ per frame
        public static void Update()
        {
            if (canvas == null) { if (Mod.Frame > 30) EnsureCanvas(); return; }
            UpdateToasts();
            UpdateOverlay();
            if (pendingChoose)
            {
                // run the START action on the frame after the click, outside the button's event
                if (pendingChooseFrame < 0) pendingChooseFrame = Mod.Frame;
                else if (Mod.Frame > pendingChooseFrame)
                {
                    pendingChoose = false; pendingChooseFrame = -1;
                    if (Net.IsHost && Net.InSession && Game.Phase == Game.PhaseLobby) Game.HostChooseGame();
                    else Log.Msg("START ignored (host=" + Net.IsHost + " session=" + Net.InSession + " phase=" + Game.Phase + ")");
                }
            }
            bool inGame = Ecs.GameRunning;
            if (ShowLobbyWhenMenu && !inGame && menu != null && Net.InSession) { ShowLobbyWhenMenu = false; ShowLobby(); }
            if (screen == Screen_.Multiplayer && mpScreen != null && mpScreen.activeSelf) UpdateMultiplayer();
            if (screen == Screen_.Lobby && lobbyScreen != null && lobbyScreen.activeSelf)
            {
                if (inGame) HideScreens();
                else UpdateLobby();
            }
            if (menu != null && !inGame)
            {
                SetButtonText(btnSingle, "SINGLE PLAYER");
                SetButtonText(btnMulti, "MULTIPLAYER");
                if (btnBack != null && btnBack.activeSelf) SetButtonText(btnBack, menuMode == MenuMode.HostChoose ? "< BACK TO LOBBY" : "< BACK");
                if (menuMode == MenuMode.HostChoose && !(Net.IsHost && Game.Phase == Game.PhaseChoosing)) { menuMode = MenuMode.Root; ApplyMenuMode(); }
                if (chooseBanner != null) chooseBanner.SetActive(menuMode == MenuMode.HostChoose && screen == Screen_.None);
            }
            if (Input.GetKeyDown(KeyCode.Escape) && screen != Screen_.None && !inGame)
            {
                if (screen == Screen_.Multiplayer) { HideScreens(); if (menu != null) { menuMode = MenuMode.Root; menu.ShowAsInitialState(); ApplyMenuMode(); } }
            }
            dirty = false;
        }
    }

    /// Press/release callbacks for hold-to-confirm buttons.
    public class HoldButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        public Action OnDown, OnUp;
        public void OnPointerDown(PointerEventData e) { OnDown?.Invoke(); }
        public void OnPointerUp(PointerEventData e) { OnUp?.Invoke(); }
        public void OnPointerExit(PointerEventData e) { OnUp?.Invoke(); }
    }
}
