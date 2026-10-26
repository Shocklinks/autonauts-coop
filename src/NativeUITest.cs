// AutonautsCoop - NativeUITest.cs - test of the in-game multiplayer buttons (COOP_UITEST=1; test kit mode=ui)
// Version 0.12.38 - 2026-10-26 - Created with Claude
//
// Drives the new buttons through the game's own click path and takes screenshots (BepInEx\coop_ui_*.png) so the
// layout can be checked without anyone at the PC:
//   host: pause menu -> "Host Multiplayer"; joiner: main menu -> "Multiplayer" -> IP -> Join; join notice; the
//   host's pause menu while hosting; "Stop Hosting" -> confirm -> yes; the joiner's "Host has ended the lobby." box.
using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using SimpleJSON;
using UnityEngine;

namespace AutonautsCoop
{
    public static class NativeUITest
    {
        public static bool On = Environment.GetEnvironmentVariable("COOP_UITEST") == "1";

        static void Shot(string name)
        {
            string p = Path.Combine(Paths.BepInExRootPath, "coop_ui_" + TestBot.Mode + "_" + name + ".png");
            try { if (File.Exists(p)) File.Delete(p); ScreenCapture.CaptureScreenshot(p); Plugin.Log("[ui] screenshot " + p); }
            catch (Exception e) { Plugin.Log("[ui] screenshot failed: " + e.Message); }
        }
        static bool Paused() => GameStateManager.Instance.GetActualState() == GameStateManager.State.Paused;
        static void OpenPause() { if (!Paused()) GameStateManager.Instance.PushState(GameStateManager.State.Paused); }
        static void ClosePause() { if (Paused()) GameStateManager.Instance.PopState(); }

        // host <-> joiner: the joiner says when it has looked at everything
        static int joinerStage; static ObjectType spawnType;
        public static bool HostOp(string a, JSONNode n, Peer from)
        {
            if (a == "uit") { joinerStage = n["k"].AsInt; return true; }
            return false;
        }
        static void Tell(int k) { var n = new JSONObject(); n["a"] = "uit"; n["k"] = k; Coop.I.Client.Host.Send(Msg.Input, n.ToString()); }

        // ---------------- host ----------------
        public static void BuildHost(List<TestBot.IEnumeratorStep> s)
        {
            s.Add(el => TestBot.AtMainMenu() && el > 8f);
            // (straight away, like the other tests: a couple of seconds later the menu's own map preview is being made
            //  and a new world started then fails to generate)
            s.Add(el => { TestBot.StartNewGame(GameOptions.GameMode.ModeCreative); return true; });   // creative: the joiner's spawn menu is tested too
            s.Add(el => Coop.InGame() && GameStateManager.Instance.GetActualState() == GameStateManager.State.Normal && el > 10f);
            s.Add(el => { OpenPause(); return el > 1.5f; });
            s.Add(el =>
            {
                bool ok = P_PausedCoopButtons.HostButton && P_PausedCoopButtons.HostButton.gameObject.activeInHierarchy;
                TestBot.Assert("host: pause menu has \"Host Multiplayer\"", ok);
                Shot("2_pause");
                return true;
            });
            s.Add(el => el > 1f);
            s.Add(el => { NativeUI.Click(P_PausedCoopButtons.HostButton); return true; });
            s.Add(el => (Coop.Role == Role.Host && el > 1f) || el > 10f);
            s.Add(el =>
            {
                TestBot.Assert("host: \"Host Multiplayer\" starts hosting", Coop.Role == Role.Host);
                TestBot.Assert("host: back in the game after hosting", !Paused());
                return true;
            });
            s.Add(el => { Shot("3_hosting_notice"); return true; });
            // the joiner joins: "joined the game" notice
            s.Add(el => NativeUI.Players() >= 1 || el > 300f);
            s.Add(el =>
            {
                if (el < 1f) return false;
                TestBot.Assert("host: a joiner joined through the Multiplayer panel", NativeUI.Players() >= 1);
                Shot("4_joined_notice");
                return true;
            });
            s.Add(el => joinerStage >= 1 || el > 300f);
            s.Add(el => { OpenPause(); return el > 1.5f; });
            s.Add(el =>
            {
                TestBot.Assert("host: pause menu while hosting has \"Stop Hosting\"", P_PausedCoopButtons.StopButton && P_PausedCoopButtons.HostButton == null);
                Shot("5_pause_hosting");
                return true;
            });
            s.Add(el => el > 1f);
            s.Add(el => { NativeUI.Click(P_PausedCoopButtons.StopButton); return el > 1.5f; });
            s.Add(el =>
            {
                bool ok = GameStateManager.Instance.GetActualState() == GameStateManager.State.Confirm;
                TestBot.Assert("host: \"Stop Hosting\" asks first", ok);
                Shot("6_stop_confirm");
                return true;
            });
            s.Add(el => el > 1f);
            s.Add(el =>
            {
                var c = UnityEngine.Object.FindObjectOfType<Confirm>();
                if (c) NativeUI.Click(c.transform.Find("BasePanelOptions/YesButton"));
                return el > 2f;
            });
            s.Add(el =>
            {
                TestBot.Assert("host: confirming stops hosting", Coop.Role == Role.Off);
                ClosePause();
                return true;
            });
            s.Add(el => el > 30f);   // the joiner's box
        }

        // ---------------- joiner ----------------
        public static void BuildClient(List<TestBot.IEnumeratorStep> s)
        {
            s.Add(el => TestBot.AtMainMenu() && el > 8f);
            // past the title screen ("click to start"), as a player would
            s.Add(el =>
            {
                if (GameStateManager.Instance != null && GameStateManager.Instance.GetActualState() == GameStateManager.State.Start && el > 1f)
                    GameStateManager.Instance.SetState(GameStateManager.State.MainMenu);
                return P_MainMenuCoopButton.Button != null && el > 2f;
            });
            s.Add(el =>
            {
                TestBot.Assert("joiner: main menu has \"Multiplayer\"", P_MainMenuCoopButton.Button && P_MainMenuCoopButton.Button.gameObject.activeInHierarchy);
                Shot("1_mainmenu");
                return true;
            });
            s.Add(el => el > 1f);
            s.Add(el => { NativeUI.Click(P_MainMenuCoopButton.Button); return el > 1.5f; });
            s.Add(el =>
            {
                TestBot.Assert("joiner: \"Multiplayer\" opens the join panel", JoinPanel.IsOpen && JoinPanel.IpField && JoinPanel.NameField);
                Shot("2_join_panel");
                return true;
            });
            // wait for the host to be up, then type its address and press Join
            s.Add(el =>
            {
                if (el < 40f) return false;
                JoinPanel.IpField.SetText(TestBot.Ip);
                JoinPanel.NameField.SetText("Tester");
                return true;
            });
            s.Add(el => { if (el < 1f) return false; Shot("3_join_filled"); return true; });
            s.Add(el =>
            {
                if (el < 1f) return false;
                NativeUI.Click(JoinPanel.JoinButton);
                return true;
            });
            s.Add(el =>
            {
                if (Coop.Role == Role.Off && el > 5f && el < 200f) { if (!JoinPanel.IsOpen) NativeUI.Click(P_MainMenuCoopButton.Button); return false; }
                return (Coop.I.ClientLoaded && Coop.InGame() && GameStateManager.Instance.GetActualState() == GameStateManager.State.Normal && el > 3f) || el > 300f;
            });
            s.Add(el =>
            {
                TestBot.Assert("joiner: joined from the Multiplayer panel", Coop.I.ClientLoaded);
                TestBot.Assert("joiner: name from the panel is used", Menu.PlayerName == "Tester", Menu.PlayerName);
                Shot("4_in_game");
                return true;
            });
            s.Add(el => { OpenPause(); return el > 1.5f; });
            s.Add(el =>
            {
                TestBot.Assert("joiner: no host buttons in a joiner's pause menu", P_PausedCoopButtons.HostButton == null && P_PausedCoopButtons.StopButton == null);
                var load = P_PausedCoopButtons.Last ? P_PausedCoopButtons.Last.transform.Find("LoadButton") : null;
                TestBot.Assert("joiner: no Load button in a joiner's pause menu", load && !load.gameObject.activeSelf);
                Shot("5_pause");
                P_PausedCoopButtons.Last.OnLoad(null);   // and if it is reached anyway, it is refused
                return true;
            });
            s.Add(el =>
            {
                if (el < 1f) return false;
                TestBot.Assert("joiner: Load is refused while connected", GameStateManager.Instance.GetActualState() == GameStateManager.State.Paused, GameStateManager.Instance.GetActualState().ToString());
                return true;
            });
            s.Add(el => { ClosePause(); return el > 1f; });
            // creative mode: the spawn menu makes the item on the host, into my hands
            s.Add(el => { GameStateManager.Instance.PushState(GameStateManager.State.CreativeTools); return el > 1.5f; });
            s.Add(el =>
            {
                var ct = UnityEngine.Object.FindObjectOfType<CreativeTools>();
                var bl = ct ? Traverse.Create(ct).Field("m_ObjectButtons").GetValue<List<BaseButtonImage>>() : null;
                var ol = ct ? Traverse.Create(ct).Field("m_ObjectList").GetValue<List<ObjectInfo>>() : null;
                Shot("5b_creative");
                int pick = -1;
                if (ol != null) for (int i = 0; i < ol.Count; i++) if (ol[i].m_Type == ObjectInfo.Type.Normal && !QuestManager.Instance.m_ObjectsLocked.ContainsKey(ol[i].m_ObjectType)) { pick = i; break; }
                TestBot.Assert("joiner: creative spawn menu opens", ct && pick >= 0);
                if (pick >= 0) { spawnType = ol[pick].m_ObjectType; NativeUI.Click(bl[pick]); }
                return true;
            });
            s.Add(el =>
            {
                var me = ObjectTypeList.Instance.GetObjectFromUniqueID(Sync.MyPlayerUID, false);
                var f = me ? me.GetComponent<Farmer>() : null;
                bool ok = f && f.m_FarmerCarry.GetTopObjectType() == spawnType;
                if (!ok && el < 10f) return false;
                TestBot.Assert("joiner: creative spawn puts the item in my hands (made by the host)", ok, spawnType + ", holding " + (f ? f.m_FarmerCarry.GetTopObjectType().ToString() : "?"));
                return true;
            });
            s.Add(el => { if (el < 2f) return false; Shot("5c_spawned"); Tell(1); return true; });
            // the host stops: back to the main menu with the game's box
            s.Add(el => (Coop.Role == Role.Off && TestBot.AtMainMenu() && EndedBox.Showing) || el > 120f);
            s.Add(el =>
            {
                if (el < 2f) return false;
                TestBot.Assert("joiner: \"Host has ended the lobby.\" in the game's box", EndedBox.Showing && Session.LastDisconnect == Session.EndedMsg, Session.LastDisconnect ?? "");
                Shot("6_ended_box");
                return true;
            });
            s.Add(el =>
            {
                if (el < 1f) return false;
                var c = UnityEngine.Object.FindObjectOfType<Confirm>();
                if (c) NativeUI.Click(c.transform.Find("BasePanelOptions/YesButton"));
                return true;
            });
            s.Add(el =>
            {
                if (el < 2f) return false;
                TestBot.Assert("joiner: OK closes the box", !EndedBox.Showing && Session.LastDisconnect == null);
                Shot("7_after_ok");
                return true;
            });
        }
    }
}
