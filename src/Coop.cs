// AutonautsCoop - Coop.cs - session controller, snapshot join
// Version 0.2.0 - 2026-10-20 - Created with Claude
using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using SimpleJSON;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AutonautsCoop
{
    public enum Role { Off, Host, Client }

    public class Coop : MonoBehaviour
    {
        public const int Protocol = 1;
        public static Coop I;
        public static Role Role = Role.Off;
        public static string PendingWorld;          // client: world JSON to load from memory
        public const string CoopSaveName = "__coop_session__";

        public NetHost Host;
        public NetClient Client;
        public int Port = 27015;
        public bool ClientLoaded;
        public JSONNode LastInfo;

        void Awake() { I = this; }

        public static bool InGame()
        {
            if (!(GeneralUtils.m_InGame && SaveLoadManager.Instance != null && !SaveLoadManager.Instance.m_Loading
                   && GameStateManager.Instance != null && TileManager.Instance != null)) return false;
            var st = GameStateManager.Instance.GetActualState();
            return st != GameStateManager.State.CreateWorld && st != GameStateManager.State.Loading && st != GameStateManager.State.SceneChange;
        }

        // ---------- host ----------
        public void StartHost(int port)
        {
            Role = Role.Host; Port = port;
            Host = new NetHost(); Host.StartTcp(port);
            Sync.OnHostStart();
        }

        public static string CaptureWorld()
        {
            var t = Traverse.Create(SaveLoadManager.Instance);
            t.Method("ReadySave").GetValue();
            try
            {
                GameOptionsManager.Instance.m_Options.SetMapData(TileManager.Instance.m_Tiles, TileManager.Instance.m_TilesWide, TileManager.Instance.m_TilesHigh);
                var root = new JSONObject();
                JSONUtils.Set(root, "AutonautsWorld", 1);
                SaveJSON.Capture(root);
                return root.ToString();
            }
            finally { t.Method("SaveFinished").GetValue(); }
        }

        public static int ObjectCount()
        {
            var d = Traverse.Create(typeof(ObjectTypeList)).Field("m_UniqueIDList").GetValue<Dictionary<int, BaseClass>>();
            return d == null ? -1 : d.Count;
        }

        void HostUpdate()
        {
            while (true)
            {
                Packet p;
                lock (Host.Inbox) { if (Host.Inbox.Count == 0) break; p = Host.Inbox.Dequeue(); }
                switch (p.Type)
                {
                    case Msg.Hello:
                        var hello = JSON.Parse(Encoding.UTF8.GetString(p.Data));
                        if (hello["protocol"].AsInt != Protocol) { Plugin.Log("peer protocol mismatch"); p.From.Close(); break; }
                        if (p.From.Name != "?") break;   // duplicate hello (connectionless wire)
                        p.From.Name = hello["name"];
                        SendSnapshot(p.From);
                        break;
                    case Msg.Input:
                        Sync.HostApplyInput(p.From, Encoding.UTF8.GetString(p.Data));
                        break;
                }
            }
        }

        void SendSnapshot(Peer peer)
        {
            if (!InGame()) { Plugin.Log("snapshot requested but host not in game"); return; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Sync.FlushBeforeSnapshot();
            string world = CaptureWorld();
            var packed = Gz.Pack(world);
            Plugin.Log("snapshot for peer " + peer.Id + ": " + world.Length + " chars, " + packed.Length + " bytes gz, " + sw.ElapsedMilliseconds + " ms, objects " + ObjectCount());
            peer.Send(Msg.Snapshot, packed);
            var info = new JSONObject();
            info["objects"] = ObjectCount();
            peer.Send(Msg.Info, info.ToString());
            Sync.OnPeerJoined(peer);
            if (TestBot.Active && peer is WirePeer) TestScript.HostSendBlob(peer);
        }

        // ---------- client ----------
        public void StartClient(string ip, int port)
        {
            var c = new NetClient(); c.Connect(ip, port);
            BeginClient(c);
        }

        /// Client side of any transport: the connection exists, say hello and wait for the snapshot.
        public void BeginClient(NetClient c)
        {
            Role = Role.Client;
            Client = c;
            var hello = new JSONObject();
            hello["protocol"] = Protocol;
            hello["name"] = SteamNet.MyName();
            Client.Host.Send(Msg.Hello, hello.ToString());
        }

        void ClientUpdate()
        {
            if (Client?.Host == null) return;
            while (true)
            {
                Packet p;
                lock (Client.Inbox) { if (Client.Inbox.Count == 0) break; p = Client.Inbox.Dequeue(); }
                switch (p.Type)
                {
                    case Msg.Snapshot:
                        PendingWorld = Gz.Unpack(p.Data);
                        Plugin.Log("snapshot received: " + PendingWorld.Length + " chars, loading");
                        ClientLoaded = false;
                        SessionManager.Instance.m_LoadFileName = CoopSaveName;
                        SessionManager.Instance.LoadLevel(true, "Main");
                        break;
                    case Msg.Info:
                        var inf = JSON.Parse(Encoding.UTF8.GetString(p.Data));
                        if (inf["you"] != null && !inf["you"].IsNull) { Sync.MyPlayerUID = inf["you"].AsInt; Plugin.Log("my player uid " + Sync.MyPlayerUID); }
                        else if (inf["tiletest"] != null && !inf["tiletest"].IsNull) TestScript.TileTest = inf["tiletest"];
                        else if (inf["chk"] != null && !inf["chk"].IsNull) TestScript.Chk = inf["chk"];
                        else if (inf["blob"] != null && !inf["blob"].IsNull) TestScript.Blob = inf;
                        else if (inf["teach"] != null && !inf["teach"].IsNull) Teach.ClientReceive(inf["teach"].AsArray);
                        else if (inf["botstate"] != null && !inf["botstate"].IsNull) TestScript.BotState = inf["botstate"];
                        else LastInfo = inf;
                        break;
                    case Msg.Delta:
                        if (ClientLoaded) Sync.ClientApplyDelta(Gz.Unpack(p.Data));
                        else Sync.QueueEarlyDelta(p.Data);
                        break;
                }
            }
            if (!ClientLoaded && PendingWorld != null && InGame() && SceneManager.GetActiveScene().name == "Main")
            {
                ClientLoaded = true;
                Plugin.Log("client world loaded, objects " + ObjectCount());
                Sync.OnClientLoaded();
            }
        }

        void Update()
        {
            try
            {
                SteamNet.Pump();
                if (Role == Role.Host) { HostUpdate(); Sync.HostTick(); }
                else if (Role == Role.Client) { ClientUpdate(); Sync.ClientFrame(); }
                TestBot.Tick();
            }
            catch (Exception e) { Plugin.Log("Update error: " + e); }
        }

        void OnApplicationQuit()
        {
            Host?.Stop();
            Client?.Host?.Close();
            SteamNet.Shutdown();
        }
    }

    // Load the client's world from memory instead of disk.
    [HarmonyPatch(typeof(MyFile), nameof(MyFile.Exists))]
    static class P_MyFileExists
    {
        static bool Prefix(string NewPath, ref bool __result)
        {
            if (Coop.PendingWorld != null && NewPath != null && NewPath.Contains(Coop.CoopSaveName)) { __result = true; return false; }
            return true;
        }
    }

    [HarmonyPatch(typeof(MyFile), nameof(MyFile.Load))]
    static class P_MyFileLoad
    {
        static bool Prefix(string FileName, ref string __result)
        {
            if (Coop.PendingWorld != null && FileName != null && FileName.Contains(Coop.CoopSaveName)) { __result = Coop.PendingWorld; return false; }
            return true;
        }
    }

    // Clients never write saves (the host owns the world).
    [HarmonyPatch(typeof(SaveLoadManager), nameof(SaveLoadManager.Save))]
    static class P_NoClientSave
    {
        static bool Prefix(ref bool __result) { if (Coop.Role == Role.Client) { __result = false; return false; } return true; }
    }

    [HarmonyPatch(typeof(SaveLoadManager), "DoAutosave")]
    static class P_NoClientAutosave
    {
        static bool Prefix() { return Coop.Role != Role.Client; }
    }
}

namespace AutonautsCoop
{
    // Client mirrors the host; it must not roll its own offworld missions.
    // Without this, loading a fresh world on the client threw in GetMissionObjectType (empty list) and the load stalled.
    [HarmonyLib.HarmonyPatch(typeof(OffworldMissionsManager), "CheckDailyMission")]
    static class P_NoClientDailyMission
    {
        static bool Prefix() { return Coop.Role != Role.Client; }
    }
}
