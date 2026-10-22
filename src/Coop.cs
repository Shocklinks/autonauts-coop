// AutonautsCoop - Coop.cs - session controller, snapshot join
// Version 0.12.1 - 2026-10-21 - Created with Claude
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
        public const int Protocol = 2;   // 2: hello carries the mod version; mismatches get a reason
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
                if (!NetSim.Take(Host.Inbox, out p)) break;
                switch (p.Type)
                {
                    case Msg.Echo: Diag.OnEcho(p); break;
                    case Msg.Hello:
                        var hello = JSON.Parse(Encoding.UTF8.GetString(p.Data));
                        if (p.From.Name != "?" || VersionCheck.IsRefused(p.From)) break;   // duplicate hello (connectionless wire)
                        string why = VersionCheck.Check(hello);
                        if (why != null) { VersionCheck.Reject(p.From, (string)hello["name"], why); break; }
                        p.From.Name = UniqueName((string)hello["name"], p.From);
                        SendSnapshot(p.From);
                        break;
                    case Msg.Input:
                        Sync.HostApplyInput(p.From, Encoding.UTF8.GetString(p.Data));
                        break;
                }
            }
        }

        /// Two joiners with the same name (e.g. both "player" on IP) must not share a stored character.
        string UniqueName(string want, Peer self)
        {
            want = string.IsNullOrEmpty(want) ? "player" : want.Trim();
            if (want.Length > 32) want = want.Substring(0, 32);
            var taken = new HashSet<string>();
            lock (Host.Peers) foreach (var q in Host.Peers) if (q != self && q.Alive && q.Name != "?") taken.Add(q.Name);
            if (!taken.Contains(want)) return want;
            for (int i = 2; ; i++) { var n = want + " (" + i + ")"; if (!taken.Contains(n)) { Plugin.Log("name " + want + " is taken; using " + n); return n; } }
        }

        void SendSnapshot(Peer peer)
        {
            if (!InGame()) { Plugin.Log("snapshot requested but host not in game"); return; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // pending changes go to the players already here; the new one gets them in its snapshot
            peer.Hold = true;
            try { Sync.FlushBeforeSnapshot(); } finally { peer.Hold = false; }
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
            Client.Host.Send(Msg.Hello, MakeHello().ToString());
        }

        public static JSONObject MakeHello()
        {
            var hello = new JSONObject();
            hello["protocol"] = Protocol;
            hello["version"] = VersionCheck.FakeVersion ?? Plugin.Version;
            hello["game"] = SaveLoadManager.GetVersion();
            hello["name"] = SteamNet.MyName();
            return hello;
        }

        void ClientUpdate()
        {
            if (Client?.Host == null) return;
            while (true)
            {
                Packet p;
                if (!NetSim.Take(Client.Inbox, out p)) break;
                switch (p.Type)
                {
                    case Msg.Echo: Diag.OnEcho(p); break;
                    case Msg.Snapshot:
                        PendingWorld = Gz.Unpack(p.Data);
                        Plugin.Log("snapshot received: " + PendingWorld.Length + " chars, loading");
                        ClientLoaded = false;
                        SessionManager.Instance.m_LoadFileName = CoopSaveName;
                        SessionManager.Instance.LoadLevel(true, "Main");
                        break;
                    case Msg.Info:
                        var inf = JSON.Parse(Encoding.UTF8.GetString(p.Data));
                        if (inf["reject"] != null && !inf["reject"].IsNull) { Session.ClientLost((string)inf["reject"], true); return; }
                        if (inf["end"] != null && !inf["end"].IsNull) { Session.ClientLost((string)inf["end"], true); return; }
                        if (inf["note"] != null && !inf["note"].IsNull) { Menu.Toast(inf["note"]); break; }
                        if (inf["hostlog"] != null && !inf["hostlog"].IsNull) { BugReport.ClientGotHostLog(inf["hostlog"], inf["hostdiag"] != null && !inf["hostdiag"].IsNull ? (string)inf["hostdiag"] : null); break; }
                        if (inf["you"] != null && !inf["you"].IsNull) { Sync.MyPlayerUID = inf["you"].AsInt; Locks.MyKey = inf["key"]; Plugin.Log("my player uid " + Sync.MyPlayerUID + " key " + Locks.MyKey); }
                        else if (inf["locks"] != null && !inf["locks"].IsNull) Locks.ClientReceive(inf["locks"].AsArray);
                        else if (inf["open"] != null && !inf["open"].IsNull) Menus.ClientOpen(inf);
                        else if (inf["wb"] != null && !inf["wb"].IsNull) TestScript.WbUid = inf["wb"].AsInt;
                        else if (inf["wardrobe"] != null && !inf["wardrobe"].IsNull) TestScript.WardrobeUid = inf["wardrobe"].AsInt;
                        else if (inf["unlock"] != null && !inf["unlock"].IsNull) TestScript.UnlockReply = inf["unlock"];
                        else if (inf["conv"] != null && !inf["conv"].IsNull) TestScript.ConvState = inf["conv"];
                        else if (inf["worldtest"] != null && !inf["worldtest"].IsNull) TestScript.WorldReply = inf["worldtest"];
                        else if (inf["tiletest"] != null && !inf["tiletest"].IsNull) TestScript.TileTest = inf["tiletest"];
                        else if (inf["chk"] != null && !inf["chk"].IsNull) TestScript.Chk = inf["chk"];
                        else if (inf["tr"] != null && !inf["tr"].IsNull) TestScript.Reply = inf;
                        else if (inf["undo"] != null && !inf["undo"].IsNull) Build.SetUndoAvailable(inf["undo"].AsBool);
                        else if (inf["blob"] != null && !inf["blob"].IsNull) TestScript.Blob = inf;
                        else if (inf["teach"] != null && !inf["teach"].IsNull) Teach.ClientReceive(inf["teach"].AsArray);
                        else if (inf["botstate"] != null && !inf["botstate"].IsNull) { var bs = inf["botstate"]; if (bs["step"] == null || bs["step"].IsNull || bs["step"].AsInt >= TestScript.askStep) TestScript.BotState = bs; }
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
                if (Role == Role.Host)
                {
                    double a = Prof.Now; HostUpdate(); double b = Prof.Now; Prof.Add("joiner input", b - a);
                    Sync.HostTick(); double c = Prof.Now;
                    Shared.HostTick(); Prof.Add("shared editors", Prof.Now - c);
                }
                else if (Role == Role.Client) { ClientUpdate(); Sync.ClientFrame(); UiRebind.Tick(); Shared.ClientTick(); Look.ClientTick(); }
                Diag.EchoTick();
                Diag.Tick();
                Session.Tick();   // after the inbox: a "lobby ended" message may sit right before the close
                VersionCheck.Tick();
                BugReport.Tick();
                Locks.Tick();
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

    // Joiner saves: autosave/quick save are blocked in Extras.cs; Save As writes the joiner's own copy.

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
