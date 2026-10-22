// AutonautsCoop - Menu.cs - F8 window: host / join / status
// Version 0.5.0 - 2026-10-20 - Created with Claude
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AutonautsCoop
{
    public class Menu : MonoBehaviour
    {
        bool show;
        Rect rect = new Rect(40, 40, 380, 400);
        string ip, port, status = "";
        ConfigEntry<string> cfgIp;
        ConfigEntry<int> cfgPort;
        ConfigEntry<KeyCode> cfgKey;
        static ConfigEntry<string> cfgName;
        string nameField = "";
        /// Name other players see; empty = Steam name (or "player" without Steam).
        public static string PlayerName => cfgName != null ? cfgName.Value.Trim() : "";

        void Start()
        {
            cfgIp = Plugin.Instance.Config.Bind("Network", "LastJoinIP", "127.0.0.1", "Last IP joined");
            cfgPort = Plugin.Instance.Config.Bind("Network", "Port", 27015, "TCP port to host on / join");
            cfgKey = Plugin.Instance.Config.Bind("UI", "MenuKey", KeyCode.F8, "Key that opens the co-op window");
            cfgName = Plugin.Instance.Config.Bind("Player", "Name", "", "Name other players see (empty = your Steam name). Needed for joining by IP without Steam.");
            ip = cfgIp.Value; port = cfgPort.Value.ToString(); nameField = cfgName.Value;
        }

        void Update()
        {
            if (Input.GetKeyDown(cfgKey.Value)) show = !show;
        }

        static string toast; static float toastUntil;
        public static string LastToast;
        public static readonly List<string> Toasts = new List<string>();   // tests
        public static void Toast(string s) { toast = s; LastToast = s; Toasts.Add(s); toastUntil = Time.realtimeSinceStartup + 4f; Plugin.Log("toast: " + s); Diag.Log("toast: " + s); }

        void OnGUI()
        {
            try { Locks.OnGUI(); } catch (Exception e) { Plugin.Log("lock label: " + e.Message); }
            if (toast != null && Time.realtimeSinceStartup < toastUntil)
                GUI.Box(new Rect(Screen.width / 2 - 200, Screen.height / 2 - 120, 400, 28), toast);
            if (Coop.Role == Role.Off && Session.LastDisconnect != null && TestBot.AtMainMenu())
                GUI.Window(0x0C01, new Rect(Screen.width / 2 - 260, Screen.height / 2 - 70, 520, 140), DrawEnded, "Co-op");
            if (Coop.Role != Role.Off)
            {
                string s = Coop.Role == Role.Host ? "Co-op host: " + PeerCount() + " joined (port " + Coop.I.Port + ")"
                         : Coop.I.ClientLoaded ? "Co-op: connected" : "Co-op: joining...";
                if (Coop.Role == Role.Client && (Coop.I.Client?.Host == null || !Coop.I.Client.Host.Alive)) s = "Co-op: disconnected from host";
                else if (Coop.Role == Role.Client && Coop.I.ClientLoaded && Speed.HostPaused) s += "   (host paused the game)";
                if (Coop.Role == Role.Client && Coop.I.ClientLoaded && Speed.HostPaused)
                    GUI.Box(new Rect(Screen.width / 2 - 120, 40, 240, 28), "Host paused the game");
                GUI.Label(new Rect(10, Screen.height - 26, 600, 22), s + "   [" + cfgKey.Value + "]");
            }
            if (!show) return;
            rect = GUI.Window(0x0C00, rect, Draw, "Autonauts Co-op " + Plugin.Version);
        }

        void DrawEnded(int id)
        {
            GUILayout.BeginVertical();
            GUILayout.FlexibleSpace();
            GUILayout.Label(Session.LastDisconnect ?? "");
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("OK", GUILayout.Height(30))) Session.LastDisconnect = null;
            GUILayout.EndVertical();
        }

        int PeerCount()
        {
            if (Coop.I.Host == null) return 0;
            int n = 0;
            lock (Coop.I.Host.Peers) foreach (var p in Coop.I.Host.Peers) if (p.Alive && p.Name != "?") n++;
            return n;
        }

        void Draw(int id)
        {
            GUILayout.BeginVertical();
            if (Coop.Role == Role.Off)
            {
                GUILayout.Label("Your name (empty = Steam name)");
                string nf = GUILayout.TextField(nameField, 24);
                if (nf != nameField) { nameField = nf; cfgName.Value = nf.Trim(); }
                GUILayout.Label("Port");
                port = GUILayout.TextField(port, 6);
                bool inGame = Coop.InGame();
                GUI.enabled = inGame;
                if (GUILayout.Button(inGame ? "Host this world" : "Host (load a world first)"))
                {
                    try
                    {
                        Coop.I.StartHost(ParsePort()); cfgPort.Value = ParsePort();
                        if (SteamNet.SteamReady) { SteamNet.HostLobby(); status = "Hosting. Invite Steam friends, or share your IP."; }
                        else status = "Hosting on IP only (Steam not running).";
                    }
                    catch (Exception e) { status = "Host failed: " + e.Message; Plugin.Log(status); }
                }
                GUI.enabled = true;
                GUILayout.Label("Join: accept a Steam invite, or right-click the host in Steam > Join Game.");
                GUI.enabled = TestBot.AtMainMenu();
                GUILayout.Label("...or join by IP");
                ip = GUILayout.TextField(ip, 64);
                if (GUILayout.Button(TestBot.AtMainMenu() ? "Join" : "Join (from the main menu)"))
                {
                    try { Coop.I.StartClient(ip.Trim(), ParsePort()); cfgIp.Value = ip.Trim(); cfgPort.Value = ParsePort(); status = "Connected, loading world..."; }
                    catch (Exception e) { status = "Join failed: " + e.Message; Plugin.Log(status); Coop.Role = Role.Off; }
                }
                GUI.enabled = true;
            }
            else if (Coop.Role == Role.Host)
            {
                GUILayout.Label("Hosting on port " + Coop.I.Port + ". Players: " + PeerCount());
                GUILayout.Label("Save as usual. Joiners' characters are stored with your save.");
                if (SteamNet.SteamReady && GUILayout.Button("Invite Steam friends")) SteamNet.HostLobby();
            }
            else
            {
                GUILayout.Label(Coop.I.ClientLoaded ? "Connected to host." : "Joining...");
                GUILayout.Label("To leave: quit to the main menu. Autosave is off for joiners; Save As makes your own copy.");
            }
            GUILayout.Label(status);
            if (SteamNet.Status != "") GUILayout.Label("Steam: " + SteamNet.Status);
            if (GUILayout.Button("Save bug report")) BugReport.Begin();
            if (BugReport.Status != "") GUILayout.Label(BugReport.Status);
            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        int ParsePort() { return int.TryParse(port, out int p) && p > 0 && p < 65536 ? p : 27015; }
    }
}
