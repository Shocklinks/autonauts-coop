// AutonautsCoop - NativeUI.cs - multiplayer in the game's own menus (0.12.38)
// Version 0.12.38 - 2026-10-26 - Created with Claude
//
//   - Pause menu (Esc): "Host Multiplayer"; while hosting "Invite Friends" (Steam) and "Stop Hosting" (asks first)
//   - Main menu: "Multiplayer" opens a join panel (the game's own panel and text fields): host IP, your name, Join
//   - Notifications ("X joined the game", lock messages...) are the game's own panels, top centre, a few seconds each;
//     "Host paused the game" stays up while it lasts
//   - "Host has ended the lobby." is the game's own confirm box, with one button
// Everything is built from the game's prefabs (button clones, the sign-rename panel, the warning panel), so it looks
// and sounds like the rest of the game. The F8 window stays for settings and bug reports.
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace AutonautsCoop
{
    public static class NativeUI
    {
        // ---------------- helpers ----------------
        /// Clone a menu button: same look and sounds, new label. Placed where `at` is; everything in that column from
        /// `at` down moves one row lower. Returns null if the menu doesn't look as expected (the game is left alone).
        public static BaseButton AddMenuButton(Transform menu, string at, string label, string[] column)
        {
            var anchor = menu.Find(at);
            if (!anchor) { Plugin.Log("ui: no " + at + " in " + menu.name); return null; }
            var ar = anchor.GetComponent<RectTransform>();
            // the other buttons of that column, top to bottom
            var col = new List<RectTransform>();
            foreach (var n in column) { var t = menu.Find(n); if (t) { var r = t.GetComponent<RectTransform>(); if (Mathf.Abs(r.anchoredPosition.x - ar.anchoredPosition.x) < 5f) col.Add(r); } }
            col.Sort((a, b) => b.anchoredPosition.y.CompareTo(a.anchoredPosition.y));
            float step = 0;
            for (int i = 1; i < col.Count; i++) { float d = col[i - 1].anchoredPosition.y - col[i].anchoredPosition.y; if (d > 1f && (step == 0 || d < step)) step = d; }
            if (step == 0) step = ar.sizeDelta.y + 8f;
            var go = UnityEngine.Object.Instantiate(anchor.gameObject, menu, false);
            go.name = "Coop" + label.Replace(" ", "");
            go.SetActive(true);
            var nr = go.GetComponent<RectTransform>();
            nr.anchoredPosition = ar.anchoredPosition;
            go.transform.SetSiblingIndex(anchor.GetSiblingIndex());
            foreach (var r in col) if (r.anchoredPosition.y <= ar.anchoredPosition.y + 0.5f) r.anchoredPosition -= new Vector2(0, step);
            SetLabel(go, label);
            return go.GetComponent<BaseButton>();
        }

        public static void SetLabel(GameObject button, string label)
        {
            var bt = button.GetComponent<BaseButtonText>();
            if (bt) { bt.SetText(label); return; }
            var t = button.GetComponentInChildren<BaseText>(true);
            if (t) t.SetText(label);
        }

        /// The game's click path (sound, then the button's action) as if the mouse clicked it. Tests use it.
        public static void Click(Component c)
        {
            if (!c) return;
            var ev = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
            ExecuteEvents.Execute(c.gameObject, ev, ExecuteEvents.pointerClickHandler);
        }

        /// A confirm box from the game (title, text, Yes/No). One button only when `ok` is given instead of yes/no.
        public static Confirm Ask(string title, string text, ConfirmCallback yes, ConfirmCallback no = null, bool okOnly = false)
        {
            var gsm = GameStateManager.Instance;
            if (gsm == null || HudManager.Instance == null || !HudManager.Instance.m_MenusRootTransform) return null;
            gsm.PushState(GameStateManager.State.Confirm);
            var st = gsm.GetCurrentState().GetComponent<GameStateConfirm>();
            if (!st) return null;
            st.SetConfirm(yes, "ConfirmQuitGame", "ConfirmQuitGameNoSave", no);   // existing text IDs, replaced below
            var m = st.m_Menu;
            var opts = m.transform.Find("BasePanelOptions");
            opts.Find("Panel/TitleStrip/Title").GetComponent<BaseText>().SetText(title);
            m.SetDescriptionString(text);
            if (okOnly)
            {
                var noB = opts.Find("NoButton"); var yesB = opts.Find("YesButton");
                if (noB) noB.gameObject.SetActive(false);
                if (yesB)
                {
                    SetLabel(yesB.gameObject, "OK");
                    if (noB) { var p = yesB.GetComponent<RectTransform>().anchoredPosition; p.x = (p.x + noB.GetComponent<RectTransform>().anchoredPosition.x) / 2f; yesB.GetComponent<RectTransform>().anchoredPosition = p; }
                }
            }
            return m;
        }

        // ---------------- hosting ----------------
        public static string HostNow()
        {
            if (Coop.Role != Role.Off || !Coop.InGame()) return null;
            try
            {
                Coop.I.StartHost(Menu.Port);
                if (SteamNet.SteamReady) { SteamNet.HostLobby(); return "Hosting. Invite your Steam friends, or they can right-click you in Steam > Join Game."; }
                return "Hosting on port " + Menu.Port + " (Steam isn't running: friends join by IP).";
            }
            catch (Exception e) { Plugin.Log("host failed: " + e); return "Couldn't host: " + e.Message; }
        }

        public static int Players()
        {
            if (Coop.I?.Host == null) return 0;
            int n = 0;
            lock (Coop.I.Host.Peers) foreach (var p in Coop.I.Host.Peers) if (p.Alive && p.Name != "?") n++;
            return n;
        }

        // ---------------- layout dump (UI test) ----------------
        public static void Dump(Transform t, string tag)
        {
            if (!t) return;
            foreach (Transform c in t)
            {
                var r = c.GetComponent<RectTransform>();
                Plugin.Log("[ui] " + tag + " " + c.name + (c.gameObject.activeSelf ? "" : " (hidden)") + (r ? " pos " + r.anchoredPosition + " size " + r.sizeDelta + " anchors " + r.anchorMin + "-" + r.anchorMax : ""));
            }
        }
    }

    // ======================= pause menu =======================
    [HarmonyPatch(typeof(Paused), "Awake")]
    static class P_PausedCoopButtons
    {
        static readonly string[] Column = { "ContinueButton", "LoadButton", "SaveButton", "SettingsButton", "QuitButton" };
        public static Paused Last; public static BaseButton HostButton, InviteButton, StopButton;
        static void Postfix(Paused __instance)
        {
            Last = __instance; HostButton = InviteButton = StopButton = null;
            try
            {
                if (!GeneralUtils.m_InGame) return;   // recordings
                if (TestBot.Active && NativeUITest.On) NativeUI.Dump(__instance.transform, "pause");
                if (Coop.Role == Role.Off)
                {
                    HostButton = NativeUI.AddMenuButton(__instance.transform, "QuitButton", "Host Multiplayer", Column);
                    if (HostButton) __instance.AddAction(HostButton, g =>
                    {
                        string r = NativeUI.HostNow();
                        if (r != null) Menu.Toast(r);
                        GameStateManager.Instance.PopState();   // back to the game
                    });
                }
                else if (Coop.Role == Role.Host)
                {
                    if (SteamNet.SteamReady)
                    {
                        InviteButton = NativeUI.AddMenuButton(__instance.transform, "QuitButton", "Invite Friends", Column);
                        if (InviteButton) __instance.AddAction(InviteButton, g => SteamNet.HostLobby());
                    }
                    int n = NativeUI.Players();
                    StopButton = NativeUI.AddMenuButton(__instance.transform, "QuitButton", "Stop Hosting" + (n > 0 ? " (" + n + (n == 1 ? " player)" : " players)") : ""), Column);
                    if (StopButton) __instance.AddAction(StopButton, g => NativeUI.Ask("Stop hosting?",
                        n > 0 ? "Everyone who joined goes back to their main menu. Their characters are kept with your world when you save."
                              : "Nobody can join after this.",
                        () => Session.StopHost("stopped from the pause menu")));
                }
            }
            catch (Exception e) { Plugin.Log("pause menu buttons: " + e); }
        }
    }

    // ======================= main menu =======================
    [HarmonyPatch(typeof(MainMenu), "Start")]
    static class P_MainMenuCoopButton
    {
        public static MainMenu Last; public static BaseButton Button; static GameObject cont;
        static void Postfix(MainMenu __instance)
        {
            Last = __instance; Button = null;
            try
            {
                if (TestBot.Active && NativeUITest.On) NativeUI.Dump(__instance.transform, "main");
                // "New World" and "Load World" sit at the bottom left and right of centre ("Continue" in the middle
                // when there is a last save): "Multiplayer" goes in the middle, one row up when Continue shows
                var load = __instance.transform.Find("LoadButton");
                if (!load) return;
                var go = UnityEngine.Object.Instantiate(load.gameObject, __instance.transform, false);
                go.name = "CoopMultiplayerButton";
                go.SetActive(true);
                // text only: hide the picture the Load button has, let the text use the whole button
                foreach (Transform c in go.transform)
                    if (c.name != "Shadow" && c.name != "Back" && c.name != "Border" && c.name != "Text") c.gameObject.SetActive(false);
                var tr = go.transform.Find("Text") ? go.transform.Find("Text").GetComponent<RectTransform>() : null;
                if (tr) { tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.offsetMin = new Vector2(10, 5); tr.offsetMax = new Vector2(-10, -5); tr.anchoredPosition = Vector2.zero; }
                NativeUI.SetLabel(go, "Multiplayer");
                Button = go.GetComponent<BaseButton>();
                var ct = __instance.transform.Find("ContinueButton"); cont = ct ? ct.gameObject : null;
                Place();
                __instance.RegisterGadget(Button);
                __instance.AddAction(Button, g => JoinPanel.Open(__instance));
                if (TestBot.Active && NativeUITest.On) NativeUI.Dump(go.transform, "mpbutton");
            }
            catch (Exception e) { Plugin.Log("main menu button: " + e); }
        }
        /// Continue shows or hides as the menu comes back: keep clear of it.
        public static void Place()
        {
            if (!Button) return;
            var r = Button.GetComponent<RectTransform>();
            r.anchoredPosition = new Vector2(0, cont && cont.activeSelf ? 20f + r.sizeDelta.y + 12f : 20f);
        }
    }

    /// The join panel: the game's sign-rename panel with its sign buttons hidden, an IP field and a name field.
    public static class JoinPanel
    {
        static GameObject panel; static MainMenu menu;
        public static BaseInputField IpField, NameField; public static BaseGadget JoinButton, BackButton;
        public static bool IsOpen => panel;

        public static void Open(MainMenu m)
        {
            if (panel || Coop.Role != Role.Off) return;
            try
            {
                menu = m;
                var root = HudManager.Instance.m_MenusRootTransform;
                panel = InstantiationManager.MyInstantiate(AssetManager.LoadPrefab("Prefabs/Hud/RenameSign"), Vector3.zero, Quaternion.identity, root);
                panel.name = "CoopJoinPanel";
                panel.GetComponent<RectTransform>().anchoredPosition = Vector2.zero;
                var rs = panel.GetComponent<RenameSign>();
                foreach (var n in new[] { "AreaLinkedToggle", "EditAreaButton", "ShoutButton", "GroupButton" }) { var t = panel.transform.Find(n); if (t) t.gameObject.SetActive(false); }
                rs.SetTitleText("Join Multiplayer");
                IpField = rs.m_InputField;
                // the sign panel has one field filling its middle: make the panel a row taller and lay out two rows
                var pr = panel.GetComponent<RectTransform>();
                const float row = 80f, gap = 14f, top = 70f;
                pr.sizeDelta = new Vector2(pr.sizeDelta.x, pr.sizeDelta.y + row);
                void Row(BaseInputField f, int i)
                {
                    var r = f.GetComponent<RectTransform>();
                    r.anchorMin = new Vector2(0, 1); r.anchorMax = new Vector2(1, 1); r.pivot = new Vector2(0.5f, 1);
                    r.sizeDelta = new Vector2(-40f, row);
                    r.anchoredPosition = new Vector2(0, -(top + i * (row + gap)));
                }
                IpField.SetText(Menu.LastIp);
                IpField.SetPlaceholderText("Host's IP address");
                IpField.SetCharacterLimit(64);
                IpField.SetRollover("Host's IP address");
                var nf = UnityEngine.Object.Instantiate(IpField.gameObject, IpField.transform.parent, false);
                nf.name = "CoopNameField";
                NameField = nf.GetComponent<BaseInputField>();
                NameField.SetText(Menu.PlayerName);
                NameField.SetPlaceholderText("Your name (empty: Steam name)");
                NameField.SetCharacterLimit(24);
                NameField.SetRollover("Your name (empty: your Steam name)");
                Row(IpField, 0); Row(NameField, 1);
                var ok = panel.transform.Find("StandardAcceptButton");
                JoinButton = ok ? ok.GetComponent<BaseGadget>() : null;
                if (JoinButton) { JoinButton.SetAction(g => Join(), JoinButton); JoinButton.SetRollover("Join the host at this IP address. Steam friends: accept their invite, or right-click them in Steam > Join Game."); }
                BackButton = rs.GetBackButton();
                BackButton.SetAction(g => Close(), BackButton);
                if (TestBot.Active && NativeUITest.On) NativeUI.Dump(panel.transform, "join");
                menu.gameObject.SetActive(false);
            }
            catch (Exception e) { Plugin.Log("join panel: " + e); Close(); }
        }

        public static void Join()
        {
            string ip = IpField ? IpField.GetText().Trim() : "";
            if (NameField) Menu.PlayerName = NameField.GetText().Trim();
            if (ip == "") { Menu.Toast("Type the host's IP address"); return; }
            try
            {
                Coop.I.StartClient(ip, Menu.Port);
                Menu.LastIp = ip;
                Menu.Toast("Connected, loading the world...");
                Close();
            }
            catch (Exception e) { Coop.Role = Role.Off; Menu.Toast("Couldn't join " + ip + ": " + e.Message); Plugin.Log("join failed: " + e); }
        }

        public static void Close()
        {
            if (panel) UnityEngine.Object.Destroy(panel);
            panel = null; IpField = NameField = null; JoinButton = BackButton = null;
            if (menu) menu.gameObject.SetActive(true);
        }

        public static void Tick()
        {
            if (!panel) return;
            if (Input.GetKeyDown(KeyCode.Escape)) Close();
            else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) Join();
        }
    }

    // ======================= notifications =======================
    /// The game's warning panel (the one that says why something can't be done), as a notice at the top of the
    /// screen. Several stack; each goes after a few seconds. Falls back to the plain box when there's no HUD.
    public static class Notify
    {
        class Item { public GameObject Go; public float Until; public string Key; }
        static readonly List<Item> items = new List<Item>();
        static GameObject prefab;
        const float Seconds = 4f, Top = 70f, Gap = 6f;

        static Transform Root() => HudManager.Instance ? HudManager.Instance.m_MenusRootTransform : null;

        static Item Make(string text)
        {
            var root = Root();
            if (!root) return null;
            if (!prefab) prefab = AssetManager.LoadPrefab("Prefabs/Hud/Rollovers/WarningRollover");
            if (!prefab) return null;
            var go = InstantiationManager.MyInstantiate(prefab, Vector3.zero, Quaternion.identity, root);
            go.name = "CoopNotice";
            var wr = go.GetComponent<WarningRollover>();
            var panel = wr ? wr.m_Panel : go.GetComponentInChildren<BasePanel>(true);
            if (wr) UnityEngine.Object.DestroyImmediate(wr);   // keep the look, drop the follow-the-mouse behaviour
            panel.gameObject.SetActive(true);
            panel.transform.localScale = Vector3.one;
            var title = panel.transform.Find("Title").GetComponent<BaseText>();
            title.SetText(text);
            float w = Mathf.Clamp(title.GetPreferredWidth() + 40f, 200f, 900f);
            panel.SetWidth(w);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            // the panel sits inside its root with an offset (rollovers grow down-left of the mouse): centre it
            var pr = panel.GetComponent<RectTransform>();
            pr.anchorMin = pr.anchorMax = pr.pivot = new Vector2(0.5f, 1f);
            pr.anchoredPosition = Vector2.zero;
            go.transform.SetAsLastSibling();
            return new Item { Go = go };
        }

        public static bool Show(string text)
        {
            try
            {
                var it = Make(text);
                if (it == null) return false;
                it.Until = Time.realtimeSinceStartup + Seconds;
                items.Add(it);
                while (items.Count > 4) { var o = items.Find(x => x.Key == null); if (o == null) break; Kill(o); }
                Layout();
                return true;
            }
            catch (Exception e) { Plugin.Log("notice: " + e.Message); return false; }
        }

        /// A notice that stays until cleared (Set(key, null)).
        public static void Set(string key, string text)
        {
            var cur = items.Find(x => x.Key == key);
            if (text == null) { if (cur != null) { Kill(cur); Layout(); } return; }
            if (cur != null) return;
            try { var it = Make(text); if (it == null) return; it.Key = key; it.Until = float.MaxValue; items.Insert(0, it); Layout(); }
            catch (Exception e) { Plugin.Log("notice: " + e.Message); }
        }

        static void Kill(Item it) { if (it.Go) UnityEngine.Object.Destroy(it.Go); items.Remove(it); }

        static void Layout()
        {
            float y = -Top;
            foreach (var it in items)
            {
                if (!it.Go) continue;
                it.Go.GetComponent<RectTransform>().anchoredPosition = new Vector2(0, y);
                var p = it.Go.GetComponentInChildren<BasePanel>(true);
                y -= (p ? p.GetHeight() : 40f) + Gap;
            }
        }

        public static void Tick()
        {
            if (items.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            bool changed = false;
            for (int i = items.Count - 1; i >= 0; i--)
                if (!items[i].Go || now > items[i].Until) { Kill(items[i]); changed = true; }
            if (changed) Layout();
        }

        /// A scene change destroys the HUD with our notices: forget them.
        public static void Clear() { foreach (var it in items) if (it.Go) UnityEngine.Object.Destroy(it.Go); items.Clear(); }
    }

    /// "Host has ended the lobby." on the main menu, as the game's own box.
    public static class EndedBox
    {
        public static bool Showing;
        static string shown;
        public static void Tick()
        {
            if (Coop.Role != Role.Off || Session.LastDisconnect == null) { shown = null; return; }
            if (TestBot.Active && !NativeUITest.On) return;   // the other tests reconnect from the menu: leave it clear
            if (Showing || shown == Session.LastDisconnect) return;
            if (!TestBot.AtMainMenu() || GameStateManager.Instance == null || GameStateManager.Instance.GetActualState() != GameStateManager.State.MainMenu) return;
            if (!P_MainMenuCoopButton.Last || !P_MainMenuCoopButton.Last.gameObject.activeInHierarchy) return;
            shown = Session.LastDisconnect;
            ConfirmCallback done = () => { Showing = false; Session.LastDisconnect = null; };
            Showing = NativeUI.Ask("Multiplayer", Session.LastDisconnect, done, done, true) != null;   // (Esc closes it too)
            if (!Showing) shown = null;   // no HUD yet: the plain box shows it
        }
    }
}
