// AutonautsCoop - Menu.cs - F8 window: host / join / status
// Version 0.5.0 - 2026-10-20 - Created with Claude
using System;
using BepInEx.Configuration;
using UnityEngine;

namespace AutonautsCoop
{
    public class Menu : MonoBehaviour
    {
        bool show;
        Rect rect = new Rect(40, 40, 360, 300);
        string ip, port, status = "";
        ConfigEntry<string> cfgIp;
        ConfigEntry<int> cfgPort;
        ConfigEntry<KeyCode> cfgKey;

        void Start()
        {
            cfgIp = Plugin.Instance.Config.Bind("Network", "LastJoinIP", "127.0.0.1", "Last IP joined");
            cfgPort = Plugin.Instance.Config.Bind("Network", "Port", 27015, "TCP port to host on / join");
            cfgKey = Plugin.Instance.Config.Bind("UI", "MenuKey", KeyCode.F8, "Key that opens the co-op window");
            ip = cfgIp.Value; port = cfgPort.Value.ToString();
        }

        void Update()
        {
            if (Input.GetKeyDown(cfgKey.Value)) show = !show;
        }

        static string toast; static float toastUntil;
        public static void Toast(string s) { toast = s; toastUntil = Time.realtimeSinceStartup + 3f; Plugin.Log("toast: " + s); }

        void OnGUI()
        {
            try { Locks.OnGUI(); } catch (Exception e) { Plugin.Log("lock label: " + e.Message); }
            if (toast != null && Time.realtimeSinceStartup < toastUntil)
                GUI.Box(new Rect(Screen.width / 2 - 200, Screen.height / 2 - 120, 400, 28), toast);
            if (Coop.Role != Role.Off)
            {
                string s = Coop.Role == Role.Host ? "Co-op host: " + PeerCount() + " joined (port " + Coop.I.Port + ")"
                         : Coop.I.ClientLoaded ? "Co-op: connected" : "Co-op: joining...";
                if (Coop.Role == Role.Client && (Coop.I.Client?.Host == null || !Coop.I.Client.Host.Alive)) s = "Co-op: disconnected from host";
                GUI.Label(new Rect(10, Screen.height - 26, 600, 22), s + "   [" + cfgKey.Value + "]");
            }
            if (!show) return;
            rect = GUI.Window(0x0C00, rect, Draw, "Autonauts Co-op " + Plugin.Version);
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
                GUILayout.Label("Save as usual; joiners never save.");
                if (SteamNet.SteamReady && GUILayout.Button("Invite Steam friends")) SteamNet.HostLobby();
            }
            else
            {
                GUILayout.Label(Coop.I.ClientLoaded ? "Connected to host." : "Joining...");
                GUILayout.Label("To leave: quit to desktop.");
            }
            GUILayout.Label(status);
            if (SteamNet.Status != "") GUILayout.Label("Steam: " + SteamNet.Status);
            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        int ParsePort() { return int.TryParse(port, out int p) && p > 0 && p < 65536 ? p : 27015; }
    }
}
