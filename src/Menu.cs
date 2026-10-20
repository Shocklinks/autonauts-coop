// AutonautsCoop - Menu.cs - F8 window: host / join / status
// Version 0.1.0 - 2026-10-20 - Created with Claude
using System;
using BepInEx.Configuration;
using UnityEngine;

namespace AutonautsCoop
{
    public class Menu : MonoBehaviour
    {
        bool show;
        Rect rect = new Rect(40, 40, 340, 230);
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

        void OnGUI()
        {
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
                    try { Coop.I.StartHost(ParsePort()); cfgPort.Value = ParsePort(); status = "Hosting. Friends join with your IP."; }
                    catch (Exception e) { status = "Host failed: " + e.Message; Plugin.Log(status); }
                }
                GUI.enabled = TestBot.AtMainMenu();
                GUILayout.Label("Host IP");
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
            }
            else
            {
                GUILayout.Label(Coop.I.ClientLoaded ? "Connected to host." : "Joining...");
                GUILayout.Label("To leave: quit to desktop.");
            }
            GUILayout.Label(status);
            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        int ParsePort() { return int.TryParse(port, out int p) && p > 0 && p < 65536 ? p : 27015; }
    }
}
