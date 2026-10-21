using System;
// AutonautsCoop - TestScript.cs - the QA scenario steps (driven through the real input path)
// Version 0.1.0 - 2026-10-20 - Created with Claude
using System.Collections.Generic;
using HarmonyLib;
using SimpleJSON;
using UnityEngine;

namespace AutonautsCoop
{
    public static class TestScript
    {
        // ---------- helpers ----------
        static BaseClass Obj(int uid) => uid < 0 ? null : ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false);
        static FarmerPlayer Me() => CollectionManager.Instance.GetPlayers()[0].GetComponent<FarmerPlayer>();
        static TileCoord Tile(BaseClass b) => b.GetComponent<TileCoordObject>().m_TileCoord;
        static int Dist(TileCoord a, TileCoord b) => Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));
        static void Click(TileCoord t, BaseClass target, AFO.AT k)
        {
            var act = target ? target.GetComponent<Actionable>() : PlotManager.Instance.GetPlotAtTile(t).GetComponent<Actionable>();
            Me().GoToAndAction(t, act, k);
        }
        static bool Carrying(FarmerPlayer p, int uid) { foreach (var h in p.m_FarmerCarry.m_CarryObject) if (h && h.m_UniqueID == uid) return true; return false; }
        static TileCoord Walkable(TileCoord from, int dx, int dy) => Sync.FreeTileNear(new TileCoord(from.x + dx, from.y + dy), true);

        public static int WbUid = -1; static string pickedRecipe; public static JSONNode ConvState, WorldReply;
        static void HostReq(string a, Action<JSONObject> args = null) { var n = new JSONObject(); n["a"] = a; args?.Invoke(n); Coop.I.Client.Host.Send(Msg.Input, n.ToString()); }
        static int axeUid = -1, treeUid = -1, logsBefore, bpUid = -1, dragCount, botUid = -1;
        public static JSONNode BotState; static JSONNode lastGood; static float nextBotQuery; static int carriedUid = -1; static float nextDragLog;
        static void AskBot() { BotState = null; var n = new JSONObject(); n["a"] = "test_botstate"; n["w"] = botUid; Coop.I.Client.Host.Send(Msg.Input, n.ToString()); }
        /// Drive the inventory screen's own drag: hands of one bar -> hands of the other.
        static bool DragHands(bool toBot)
        {
            var inv = GameStateManager.Instance.GetCurrentState().GetComponent<GameStateInventory>();
            if (inv == null) { TestBot.Assert("client: inventory screen open", false); return true; }
            var from = toBot ? inv.m_Bar : inv.m_SecondBar; var to = toBot ? inv.m_SecondBar : inv.m_Bar;
            if (!from || !to || from.m_CarrySlots == null || to.m_CarrySlots == null) return false;
            InventorySlot src = null;
            foreach (var g in from.m_CarrySlots.m_Buttons) { var sl = g.GetComponent<InventorySlot>(); if (sl && sl.m_Object && sl.m_Object.m_UniqueID == carriedUid) src = sl; }
            if (!src)
            {
                if (UnityEngine.Time.realtimeSinceStartup > nextDragLog)
                {
                    nextDragLog = UnityEngine.Time.realtimeSinceStartup + 3f;
                    var names = new List<string>();
                    foreach (var g in from.m_CarrySlots.m_Buttons) { var sl = g.GetComponent<InventorySlot>(); names.Add(sl && sl.m_Object ? sl.m_Object.m_UniqueID.ToString() : "-"); }
                    var fo = from.m_Object;
                    Plugin.Log("[test] drag waiting: bar holder " + (fo ? fo.m_UniqueID + " " + fo.m_TypeIdentifier : "null") + " slots [" + string.Join(",", names.ToArray()) + "] want " + carriedUid
                        + " holder carries " + (fo && fo.GetComponent<Farmer>() ? fo.GetComponent<Farmer>().m_FarmerCarry.m_CarryObject.Count : -1));
                }
                return false;   // wait for the bar to show it
            }
            var dst = to.m_CarrySlots.m_Buttons[0].GetComponent<InventorySlot>();
            from.DragStart(src);
            var drag = GameStateManager.Instance.GetCurrentState().GetComponent<GameStateDragInventorySlot>();
            if (drag == null) return false;   // holder busy (mid-animation): the game refuses the drag; try again
            drag.SetDragTarget(dst);
            from.DragEnd(dst);
            Plugin.Log("[test] dragged " + carriedUid + (toBot ? " to bot" : " to me") + " (inv ops sent " + Inv.Sent + ")");
            return true;
        }
        static TileCoord bpTile, movedTile, dragStart;
        static Building BuildingAt(TileCoord t) { var tile = TileManager.Instance.GetTile(t); return tile != null && tile.m_Building ? tile.m_Building : null; }
        static Building FloorAt(TileCoord t) { var tile = TileManager.Instance.GetTile(t); return tile != null && tile.m_Floor ? tile.m_Floor : null; }
        /// A buildable-looking tile near c: in bounds, not solid, no building or floor.
        static TileCoord EmptyTile(TileCoord c)
        {
            for (int r = 0; r < 12; r++)
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        var t = new TileCoord(c.x + dx, c.y + dy);
                        var tile = TileManager.Instance.GetTile(t);
                        if (tile == null || global::Tile.GetIsSolid(tile.m_TileType) || tile.m_Building || tile.m_Floor) continue;
                        bool clear = true;
                        for (int k = 0; k <= 4 && clear; k++) { var t2 = TileManager.Instance.GetTile(new TileCoord(t.x + k, t.y)); clear = t2 != null && !t2.m_Building && !t2.m_Floor && !global::Tile.GetIsSolid(t2.m_TileType); }
                        if (clear) return t;
                    }
            return c;
        }
        static BaseClass Nearest(ObjectType t, int maxDist)
        {
            BaseClass best = null; int bd = maxDist + 1; var me = Tile(Me());
            foreach (var kv in Sync.Savables())
            {
                var b = kv.Key;
                if (!b || b.m_TypeIdentifier != t || !b.GetComponent<Savable>().GetIsSavable()) continue;
                int d = Dist(Tile(b), me); if (d < bd) { bd = d; best = b; }
            }
            return best;
        }
        static int CountType(ObjectType t)
        {
            int n = 0;
            foreach (var kv in Sync.Savables()) if (kv.Key && kv.Key.m_TypeIdentifier == t && kv.Key.GetComponent<Savable>().GetIsSavable()) n++;
            return n;
        }

        public static Dictionary<int, int> Hashes()
        {
            var d = new Dictionary<int, int>();
            foreach (var e in Sync.TopLevel()) d[e.B.m_UniqueID] = e.Hash;
            return d;
        }

        public static void Checksum(out int count, out long sum)
        {
            count = 0; sum = 0;
            foreach (var e in Sync.TopLevel()) { count++; sum += e.Hash; }
        }

        // ---------- host ----------
        public static void BuildHost(List<TestBot.IEnumeratorStep> s)
        {
            s.Add(el => TestBot.AtMainMenu() && el > 8f);
            s.Add(el => { TestBot.StartNewGame(); return true; });
            s.Add(el => Coop.InGame() && GameStateManager.Instance.GetActualState() == GameStateManager.State.Normal && el > 10f);
            s.Add(el => GameStateManager.Instance.GetActualState() == GameStateManager.State.Normal && el > 5f);
            s.Add(el => { Coop.I.StartHost(TestBot.Port); if (TestBot.Transport == "udp") SteamNet.UseUdp(TestBot.Port + 100); return true; });
            s.Add(el => { lock (Coop.I.Host.Peers) return Coop.I.Host.Peers.Exists(p => p.Name != "?") && el > 1f; });
            s.Add(el => { TestBot.Assert("host: client joined", true); return true; });
            s.Add(el => HostActRequested);
            s.Add(el => { HostAct(); return true; });
            s.Add(el => { if (el > 6f) { Plugin.Log("[test] host now at " + Tile(Me()).x + "," + Tile(Me()).y + " state " + Me().m_State); return true; } return false; });
            s.Add(el => el > 900f || ClientGone() || HostQuitRequested);
            s.Add(el =>
            {
                if (!HostQuitRequested) return true;
                if (el < 1f) return false;   // let the request's reply/flush go out
                Session.StopHost("test: host leaves");
                TestBot.Assert("host: stopped hosting on request", Coop.Role == Role.Off);
                return true;
            });
            s.Add(el =>
            {
                Plugin.Log("[test] host scan ms " + Sync.LastScanMs.ToString("F1"));
                return true;
            });
        }

        public static bool HostTestOp(string a, JSONNode n, Peer from)
        {
            switch (a)
            {
                case "test_workbench":
                {
                    Sync.PeerPlayers.TryGetValue(from, out int pu);
                    var pl = ObjectTypeList.Instance.GetObjectFromUniqueID(pu, false);
                    var pt = pl.GetComponent<TileCoordObject>().m_TileCoord;
                    Building b = null; TileCoord t = pt;
                    // the joiner may stand near the map edge; try spots around it until the footprint fits
                    foreach (var off in new[] { new TileCoord(3, -3), new TileCoord(-3, -3), new TileCoord(3, 3), new TileCoord(-3, 3), new TileCoord(0, -5), new TileCoord(-5, 0) })
                    {
                        t = Sync.FreeTileNear(new TileCoord(pt.x + off.x, pt.y + off.y));
                        b = BuildingManager.Instance.AddBuilding(t, ObjectType.Workbench, 0, false, null, true);
                        if (b) break;
                    }
                    QuestManager.Instance.UnlockObject(ObjectType.ToolAxeStone);   // make sure there's a recipe (reaches the joiner via world sync)
                    var r = new JSONObject(); r["wb"] = b ? b.m_UniqueID : -1; from.Send(Msg.Info, r.ToString());
                    Plugin.Log("[test] built workbench " + (b ? b.m_UniqueID : -1) + " at " + t.x + "," + t.y);
                    return true;
                }
                case "test_convstate":
                {
                    var o = ObjectTypeList.Instance.GetObjectFromUniqueID(n["w"].AsInt, false);
                    var c = o ? o.GetComponent<Converter>() : null;
                    Sync.PeerPlayers.TryGetValue(from, out int pu);
                    var fp = ObjectTypeList.Instance.GetObjectFromUniqueID(pu, false)?.GetComponent<FarmerPlayer>();
                    var st = new JSONObject();
                    st["index"] = c ? c.m_ResultsToCreate : -1;
                    st["result"] = c && c.m_ResultsToCreate >= 0 && c.m_ResultsToCreate < c.m_Results.Count ? ObjectTypeList.Instance.GetSaveNameFromIdentifier(c.m_Results[c.m_ResultsToCreate][0].m_Type) : "";
                    st["engaged"] = fp && fp.m_EngagedObject;
                    st["hoststate"] = GameStateManager.Instance.GetActualState().ToString();
                    var r = new JSONObject(); r["conv"] = st; from.Send(Msg.Info, r.ToString());
                    return true;
                }
                case "test_world":
                {
                    // complete a few active quests the way the game does (CompleteAll throws in this build)
                    var qm = QuestManager.Instance;
                    var active = Traverse.Create(qm).Field("m_ActiveQuests").GetValue<List<Quest>>();
                    int done = 0;
                    foreach (var q in new List<Quest>(active))
                    {
                        if (done >= 3 || q == null || qm.m_CompletedQuests.ContainsKey(q.m_ID)) continue;
                        try
                        {
                            q.Complete();
                            Traverse.Create(qm).Method("DoUnlocked", new[] { typeof(Quest), typeof(bool) }).GetValue(q, false);
                            qm.m_CompletedQuests.Add(q.m_ID, q); active.Remove(q); done++;
                        }
                        catch (Exception e) { Plugin.Log("[test] quest complete failed: " + e.Message); }
                    }
                    Plugin.Log("[test] completed " + done + " quests on host");
                    DayNightManager.Instance.SetTime(0.5f);
                    var st = new JSONObject();
                    st["completed"] = QuestManager.Instance.m_CompletedQuests.Count;
                    st["locked"] = QuestManager.Instance.m_ObjectsLocked.Count;
                    st["tod"] = DayNightManager.Instance.GetTime();
                    var r = new JSONObject(); r["worldtest"] = st; from.Send(Msg.Info, r.ToString());
                    return true;
                }
                case "test_botdrop":
                {
                    var o = ObjectTypeList.Instance.GetObjectFromUniqueID(n["w"].AsInt, false);
                    var w = o ? o.GetComponent<Worker>() : null;
                    if (w) { w.StopAllScripts(); w.SendAction(new ActionInfo(ActionType.DropAll, w.m_TileCoord)); }
                    return true;
                }
                case "test_hostquit":
                    HostQuitRequested = true;
                    return true;
            }
            return false;
        }
        public static bool HostQuitRequested;

        public static bool HostActRequested;
        /// Host acts in the world (walk + tile change) when the client test asks for it.
        static void HostAct()
        {
            var me = Me(); var t = Tile(me);
            var hd = Walkable(t, -4, -1);
            Click(hd, null, AFO.AT.Primary);
            Plugin.Log("[test] host at " + t.x + "," + t.y + " walking to " + hd.x + "," + hd.y);
            var tt = Sync.FreeTileNear(new TileCoord(t.x - 2, t.y - 2));
            var cur = TileManager.Instance.GetTile(tt).m_TileType;
            var nt = cur == global::Tile.TileType.Sand ? global::Tile.TileType.Soil : global::Tile.TileType.Sand;
            TileManager.Instance.SetTileType(tt, nt);
            var info = new JSONObject(); var a = new JSONArray(); a.Add(tt.x); a.Add(tt.y); a.Add((int)nt);
            info["tiletest"] = a;
            lock (Coop.I.Host.Peers) foreach (var p in Coop.I.Host.Peers) p.Send(Msg.Info, info.ToString());
            Plugin.Log("[test] host set tile " + tt.x + "," + tt.y + " -> " + nt);
        }

        static bool ClientGone() { lock (Coop.I.Host.Peers) return !Coop.I.Host.Peers.Exists(p => p.Alive); }

        // ---------- client ----------
        static TileCoord dest; static int pickUid = -1; static TileCoord hostStart; static int hostUid = -1;
        static int chkTries;
        public static JSONNode TileTest; public static JSONNode Chk; public static JSONNode Blob;
        static bool udpStarted; static float lastHello;
        static void ResendHello()
        {
            if (UnityEngine.Time.realtimeSinceStartup - lastHello < 3f) return;
            lastHello = UnityEngine.Time.realtimeSinceStartup;
            var hello = new JSONObject(); hello["protocol"] = Coop.Protocol; hello["name"] = "player";
            Coop.I.Client.Host.Send(Msg.Hello, hello.ToString());
            Plugin.Log("[test] resent hello");
        }
        public static void HostSendBlob(Peer p)
        {
            // 400 KB pseudo-random payload: forces multi-piece framing on the wire transport
            var r = new System.Random(7); var b = new byte[400000]; r.NextBytes(b);
            var o = new JSONObject(); o["blob"] = System.Convert.ToBase64String(b); o["len"] = b.Length; o["sum"] = Sum(b).ToString();
            p.Send(Msg.Info, o.ToString());
        }
        public static long Sum(byte[] b) { long s = 0; for (int i = 0; i < b.Length; i++) s = s * 31 + b[i]; return s; }

        public static void BuildClient(List<TestBot.IEnumeratorStep> s)
        {
            s.Add(el => TestBot.AtMainMenu() && el > 8f);
            s.Add(el =>
            {
                if (el < 2f) return false;
                if (TestBot.Transport == "udp")
                {
                    // The wire is connectionless: (re)send hello until the host answers with the snapshot.
                    if (!udpStarted) { udpStarted = true; SteamNet.UseUdp(TestBot.Port + 101); SteamNet.ConnectTo((ulong)(TestBot.Port + 100)); return false; }
                    if (Coop.PendingWorld != null) { TestBot.Assert("client: joined over the Steam-style wire (" + TestBot.Transport + ")", true); return true; }
                    if (Coop.I.Client == null || !Coop.I.Client.Host.Alive || (el > 25f && Coop.PendingWorld == null && el % 25f < 0.05f)) { Plugin.Log("[test] host silent, reconnecting"); SteamNet.Peers.Clear(); SteamNet.ConnectTo((ulong)(TestBot.Port + 100)); return false; }
                    if (el > 4f) { ResendHello(); return false; }
                    return false;
                }
                try { Coop.I.StartClient(TestBot.Ip, TestBot.Port); return true; }
                catch (System.Exception e) { Plugin.Log("connect retry: " + e.Message); return false; }
            });
            s.Add(el => Coop.I.ClientLoaded && el > 3f);
            s.Add(el =>
            {
                int hostObjs = Coop.I.LastInfo != null ? Coop.I.LastInfo["objects"].AsInt : -1;
                int mine = Coop.ObjectCount();
                TestBot.Assert("client: snapshot loaded", Coop.I.ClientLoaded);
                TestBot.Assert("client: object count matches host", hostObjs > 0 && System.Math.Abs(hostObjs - mine) <= hostObjs / 100 + 2, "host " + hostObjs + " client " + mine);
                return true;
            });
            if (TestBot.Transport == "udp")
                s.Add(el =>
                {
                    if (Blob == null && el < 20f) return false;
                    bool ok = false;
                    if (Blob != null) { var b = System.Convert.FromBase64String(Blob["blob"]); ok = b.Length == Blob["len"].AsInt && Sum(b).ToString() == Blob["sum"].Value; }
                    TestBot.Assert("client: 400 KB message reassembled from pieces", ok);
                    return true;
                });
            // 1. my own avatar arrives
            s.Add(el =>
            {
                bool ok = Obj(Sync.MyPlayerUID) != null && Me().m_UniqueID == Sync.MyPlayerUID;
                if (!ok && el < 20f) return false;
                TestBot.Assert("client: own FarmerPlayer replicated and is player 0", ok, "uid " + Sync.MyPlayerUID);
                var ps = CollectionManager.Instance.GetPlayers();
                // GetPlayers() lists each player once per collection it joined, so look for the first other UID.
                var host = ps.Find(p => p && p.m_UniqueID != Sync.MyPlayerUID);
                if (host) { hostUid = host.m_UniqueID; hostStart = Tile(host); }
                return true;
            });
            // 2. walk
            s.Add(el => { dest = Walkable(Tile(Me()), -4, -3); Click(dest, null, AFO.AT.Primary); return true; });
            s.Add(el =>
            {
                int d = Dist(Tile(Me()), dest);
                if (d > 1 && el < 20f) return false;
                TestBot.Assert("client: walk command moves my avatar", d <= 1, "dist " + d);
                return true;
            });
            // 3. pick up the nearest stick or rock
            s.Add(el =>
            {
                BaseClass best = null; int bd = 999; var me = Tile(Me());
                foreach (var kv in Sync.Savables())
                {
                    var b = kv.Key;
                    if (!b || (b.m_TypeIdentifier != ObjectType.Stick && b.m_TypeIdentifier != ObjectType.Rock)) continue;
                    if (!b.GetComponent<Savable>().GetIsSavable()) continue;
                    int d = Dist(Tile(b), me); if (d < bd) { bd = d; best = b; }
                }
                TestBot.Assert("client: found a stick/rock to pick up", best != null, best ? best.m_TypeIdentifier + " at " + bd : "none");
                if (best) { pickUid = best.m_UniqueID; Click(Tile(best), best, AFO.AT.Primary); }
                return true;
            });
            s.Add(el =>
            {
                bool ok = pickUid >= 0 && Carrying(Me(), pickUid);
                if (!ok && el < 40f) return false;
                TestBot.Assert("client: pick-up replicated (item in my hands)", ok, "uid " + pickUid);
                return true;
            });
            // 4. drop it
            s.Add(el => { dest = Walkable(Tile(Me()), -1, -2); Click(dest, null, AFO.AT.Secondary); return true; });
            s.Add(el =>
            {
                var b = Obj(pickUid);
                bool ok = b && b.GetComponent<Savable>().GetIsSavable() && !Carrying(Me(), pickUid);
                if (!ok && el < 30f) return false;
                TestBot.Assert("client: drop replicated (item back on ground)", ok, b ? "at " + Tile(b).x + "," + Tile(b).y : "missing");
                return true;
            });
            // 5. host actions visible on client
            s.Add(el => { var n = new JSONObject(); n["a"] = "test_hostact"; Coop.I.Client.Host.Send(Msg.Input, n.ToString()); return true; });
            s.Add(el =>
            {
                bool tileOk = false;
                if (TileTest != null)
                {
                    var t = TileManager.Instance.GetTile(new TileCoord(TileTest[0].AsInt, TileTest[1].AsInt)).m_TileType;
                    tileOk = (int)t == TileTest[2].AsInt;
                }
                var h = Obj(hostUid);
                bool moved = h && Dist(Tile(h), hostStart) >= 2;
                if ((!tileOk || !moved) && el < 40f) return false;
                TestBot.Assert("client: host tile change visible", tileOk);
                TestBot.Assert("client: host player movement visible", moved, h ? "dist " + Dist(Tile(h), hostStart) : "no host player");
                return true;
            });
            // 6. tool use: pick up an axe and chop the nearest tree
            s.Add(el => { var n = new JSONObject(); n["a"] = "test_giveaxe"; Coop.I.Client.Host.Send(Msg.Input, n.ToString()); return true; });
            s.Add(el =>
            {
                var axe = Nearest(ObjectType.ToolAxeStone, 4);
                if (!axe && el < 10f) return false;
                TestBot.Assert("client: host-spawned axe visible", axe != null);
                if (axe) { axeUid = axe.m_UniqueID; Click(Tile(axe), axe, AFO.AT.Primary); }
                return true;
            });
            s.Add(el =>
            {
                bool ok = axeUid >= 0 && Carrying(Me(), axeUid);
                if (!ok && el < 30f) return false;
                TestBot.Assert("client: picked up axe", ok);
                var tree = Nearest(ObjectType.TreePine, 30);
                if (tree) { treeUid = tree.m_UniqueID; logsBefore = CountType(ObjectType.Log); Click(Tile(tree), tree, AFO.AT.Primary); Plugin.Log("[test] chopping tree " + treeUid + " at dist " + Dist(Tile(tree), Tile(Me()))); }
                TestBot.Assert("client: found a pine tree", tree != null);
                return true;
            });
            s.Add(el =>
            {
                var tree = Obj(treeUid);
                bool chopped = treeUid >= 0 && (!tree || tree.m_TypeIdentifier != ObjectType.TreePine);
                int logs = CountType(ObjectType.Log);
                if ((!chopped || logs <= logsBefore) && el < 90f) return false;
                TestBot.Assert("client: chopping a tree replicated (tree gone, logs appeared)", chopped && logs > logsBefore, "logs " + logsBefore + " -> " + logs);
                return true;
            });
            // 7. building: joiner uses edit mode to place, move, drag-place and delete (forwarded to the host)
            s.Add(el => { GameStateManager.Instance.SetState(GameStateManager.State.Edit); return true; });
            s.Add(el =>
            {
                if (GameStateEdit.Instance == null && el < 10f) return false;
                TestBot.Assert("client: entered edit mode", GameStateEdit.Instance != null);
                var me = Tile(Me());
                bpTile = EmptyTile(new TileCoord(me.x - 6, me.y - 6));
                GameStateEdit.Instance.SetCurrentObjectType(ObjectType.Workbench);
                Traverse.Create(GameStateEdit.Instance).Method("AddBuilding", new[] { typeof(TileCoord), typeof(ObjectType), typeof(int), typeof(bool), typeof(bool), typeof(Building), typeof(bool) })
                    .GetValue(bpTile, ObjectType.Workbench, 0, false, false, null, true);
                Plugin.Log("[test] requested workbench at " + bpTile.x + "," + bpTile.y + " (sent " + Build.Sent + ")");
                return true;
            });
            s.Add(el =>
            {
                var b = BuildingAt(bpTile);
                bool ok = b && (b.m_TypeIdentifier == ObjectType.Workbench || (b.GetComponent<ConverterFoundation>() && b.GetComponent<ConverterFoundation>().m_NewBuilding.m_TypeIdentifier == ObjectType.Workbench));
                if (!ok && el < 20f) return false;
                TestBot.Assert("client: placed workbench blueprint appears (via host)", ok, b ? b.m_TypeIdentifier + " uid " + b.m_UniqueID : "none");
                if (ok) { bpUid = b.m_UniqueID; movedTile = EmptyTile(new TileCoord(bpTile.x - 4, bpTile.y)); BuildingManager.Instance.MoveBuilding(b, movedTile, 1, false); }
                return true;
            });
            s.Add(el =>
            {
                var b = Obj(bpUid) ? Obj(bpUid).GetComponent<Building>() : null;
                bool ok = b && b.m_TileCoord.x == movedTile.x && b.m_TileCoord.y == movedTile.y && b.m_Rotation == 1 && BuildingAt(movedTile) == b && BuildingAt(bpTile) != b;
                if (!ok && el < 20f) return false;
                TestBot.Assert("client: moved blueprint (new tile, rotation, tile map updated)", ok, b ? b.m_TileCoord.x + "," + b.m_TileCoord.y + " r" + b.m_Rotation : "missing");
                // drag-place a sand path line
                var t = Traverse.Create(GameStateEdit.Instance);
                GameStateEdit.Instance.SetCurrentObjectType(ObjectType.SandPath);
                dragStart = EmptyTile(new TileCoord(bpTile.x, bpTile.y + 4));
                t.Field("m_DragStartPosition").SetValue(dragStart);
                t.Method("CreateDragModels", new[] { typeof(TileCoord) }).GetValue(new TileCoord(dragStart.x + 4, dragStart.y));
                dragCount = t.Field("m_DragModels").GetValue<List<Building>>().Count;
                t.Method("ConvertDragModels").GetValue();
                t.Method("DestroyDragModels").GetValue();
                Plugin.Log("[test] drag requested " + dragCount + " tiles");
                return true;
            });
            s.Add(el =>
            {
                int n = 0;
                for (int dx = 0; dx <= 4; dx++) if (FloorAt(new TileCoord(dragStart.x + dx, dragStart.y))) n++;
                bool ok = dragCount > 0 && n >= dragCount;
                if (!ok && el < 20f) return false;
                TestBot.Assert("client: drag-placed path tiles appear", ok, n + "/" + dragCount);
                // delete the workbench blueprint through edit mode's own delete
                var b = Obj(bpUid);
                if (b) Traverse.Create(GameStateEdit.Instance).Method("DestroyBuilding", new[] { typeof(Actionable) }).GetValue(b.GetComponent<Actionable>());
                return true;
            });
            s.Add(el =>
            {
                bool ok = !Obj(bpUid) && BuildingAt(movedTile) == null;
                if (!ok && el < 20f) return false;
                TestBot.Assert("client: deleted blueprint is gone (object and tile)", ok);
                GameStateManager.Instance.SetState(GameStateManager.State.Normal);
                return true;
            });
            // 8. bots: joiner teaches a bot to pick up a stick, presses play; the host's bot runs it
            s.Add(el => { var n = new JSONObject(); n["a"] = "test_givebot"; Coop.I.Client.Host.Send(Msg.Input, n.ToString()); return true; });
            s.Add(el =>
            {
                var bot = Nearest(ObjectType.Worker, 6);
                if (!bot && el < 15f) return false;
                TestBot.Assert("client: host-spawned bot visible", bot != null);
                if (!bot) return true;
                botUid = bot.m_UniqueID;
                GameStateManager.Instance.GetCurrentState().GetComponent<GameStateNormal>().SetSelectedWorker(bot.GetComponent<Worker>(), true, false);
                return true;
            });
            s.Add(el =>
            {
                var ed = TeachWorkerScriptEdit.Instance;
                bool ok = ed != null && ed.m_CurrentTarget && ed.m_CurrentTarget.m_UniqueID == botUid;
                if (!ok && el < 5f) return false;
                if (!ok) { TestBot.Assert("client: bot selected in script editor", false); return true; }
                ed.OnClickTeach();     // the Record button
                return true;
            });
            s.Add(el =>
            {
                bool ok = GameStateManager.Instance.GetActualState() == GameStateManager.State.TeachWorker;
                if (!ok && el < 5f) return false;
                TestBot.Assert("client: in teach mode", ok);
                BotState = null; var n = new JSONObject(); n["a"] = "test_botstate"; n["w"] = botUid; Coop.I.Client.Host.Send(Msg.Input, n.ToString());
                return true;
            });
            s.Add(el =>
            {
                if (BotState == null && el < 10f) return false;
                TestBot.Assert("host: bot is learning and joiner's player is recording", BotState != null && BotState["learning"].AsBool && BotState["teaching"].AsBool,
                    BotState == null ? "no reply" : BotState.ToString());
                // teach: pick up the nearest stick
                var stick = Nearest(ObjectType.Stick, 25);
                TestBot.Assert("client: found a stick to teach with", stick != null);
                if (stick) Click(Tile(stick), stick, AFO.AT.Primary);
                return true;
            });
            s.Add(el =>
            {
                var ed = TeachWorkerScriptEdit.Instance;
                int count = ed != null && ed.m_Instructions != null ? HighInstruction.GetInstructionCount(ed.m_Instructions.m_List) : 0;
                if (count < 1 && el < 40f) return false;
                TestBot.Assert("client: recorded actions became instructions in the editor", count >= 1, "instructions " + count + ", relayed " + Teach.Received);
                Plugin.Log("[test] editor instructions " + count);
                ed.OnClickGo();         // the Play button
                return true;
            });
            s.Add(el =>
            {
                if (el < 3f) return false;
                if (UnityEngine.Time.realtimeSinceStartup > nextBotQuery)
                {
                    nextBotQuery = UnityEngine.Time.realtimeSinceStartup + 2f; BotState = null;
                    var n = new JSONObject(); n["a"] = "test_botstate"; n["w"] = botUid; Coop.I.Client.Host.Send(Msg.Input, n.ToString());
                }
                bool ok = BotState != null && BotState["n"].AsInt >= 1 && (BotState["running"].AsBool || BotState["started"].AsBool || BotState["carry"].AsInt > 0) && !BotState["learning"].AsBool;
                if (ok) lastGood = BotState;
                if (!ok && lastGood == null && el < 30f) return false;
                if (lastGood != null) { ok = true; BotState = lastGood; }
                TestBot.Assert("host: bot got the joiner's script and runs it", ok, BotState == null ? "no reply" : BotState.ToString());
                return true;
            });
            s.Add(el =>
            {
                // Leave the editor; the joiner's copy of the bot then catches up with the host's (script included).
                // (The taught stick is in the joiner's hands, so the bot's run itself ends with a question mark;
                // the host-side check above covers that it ran.)
                if (el < 0.5f) { GameStateManager.Instance.GetCurrentState().GetComponent<GameStateNormal>()?.ClearSelectedWorkers(); return false; }
                var b = Obj(botUid);
                int n = b ? b.GetComponent<Worker>().m_WorkerInterpreter.m_HighInstructions.m_List.Count : 0;
                bool ok = n >= 1 && Teach.EditingUid() != botUid;
                if (!ok && el < 25f) return false;
                TestBot.Assert("client: bot's taught script visible on the joiner after closing the editor", ok, "instructions " + n);
                return true;
            });
            // 9. locks, rename, inventory transfers
            s.Add(el => { AskBot(); return true; });
            s.Add(el =>
            {
                if (BotState == null && el < 10f) return false;
                // the bot is no longer open on the joiner, so no lock; now the host "opens" it
                TestBot.Assert("host: no lock once the joiner closed the editor", BotState != null && BotState["locked"].Value == "", BotState?.ToString());
                var n = new JSONObject(); n["a"] = "test_hostlock"; n["w"] = botUid; Coop.I.Client.Host.Send(Msg.Input, n.ToString());
                return true;
            });
            s.Add(el =>
            {
                string who = Locks.LockedByOther(botUid);
                if (who == null && el < 10f) return false;
                TestBot.Assert("client: sees 'Being programmed by' the host", who == "Host tester", who ?? "none");
                var bot = Obj(botUid).GetComponent<Worker>();
                GameStateManager.Instance.GetCurrentState().GetComponent<GameStateNormal>().SetSelectedWorker(bot, true, false);
                return true;
            });
            s.Add(el =>
            {
                var ed = TeachWorkerScriptEdit.Instance;
                if ((ed == null || !ed.m_CurrentTarget) && el < 5f) return false;
                ed.OnClickTeach();
                return true;
            });
            s.Add(el =>
            {
                if (el < 2f) return false;
                bool blocked = GameStateManager.Instance.GetActualState() != GameStateManager.State.TeachWorker;
                TestBot.Assert("client: can't record a bot the host is programming", blocked, GameStateManager.Instance.GetActualState().ToString());
                if (!blocked) TeachWorkerScriptEdit.Instance.OnClickBack(false);
                var n = new JSONObject(); n["a"] = "test_hostlock"; n["w"] = -1; Coop.I.Client.Host.Send(Msg.Input, n.ToString());
                return true;
            });
            s.Add(el =>
            {
                // joiner records the bot: host must see the lock and be blocked itself
                if (Locks.LockedByOther(botUid) != null && el < 10f) return false;
                var ed = TeachWorkerScriptEdit.Instance;
                if (ed != null && ed.m_CurrentTarget && ed.m_CurrentTarget.m_UniqueID == botUid) ed.OnClickTeach();
                return true;
            });
            s.Add(el =>
            {
                if (el < 2f) return false;
                if (UnityEngine.Time.realtimeSinceStartup > nextBotQuery) { nextBotQuery = UnityEngine.Time.realtimeSinceStartup + 2f; AskBot(); }
                bool ok = BotState != null && BotState["locked"].Value != "" && BotState["hostblocked"].AsBool;
                if (!ok && el < 15f) return false;
                TestBot.Assert("host: sees the joiner's lock and can't record that bot", ok, BotState?.ToString());
                TeachWorkerScriptEdit.Instance.OnClickBack(false);
                return true;
            });
            // rename
            s.Add(el =>
            {
                if (el < 1.5f) return false;
                var w = Obj(botUid).GetComponent<Worker>();
                w.SetWorkerName("Coopy");
                BotState = null; nextBotQuery = 0;
                return true;
            });
            s.Add(el =>
            {
                if (UnityEngine.Time.realtimeSinceStartup > nextBotQuery) { nextBotQuery = UnityEngine.Time.realtimeSinceStartup + 2f; AskBot(); }
                bool ok = BotState != null && BotState["name"].Value == "Coopy";
                if (!ok && el < 15f) return false;
                TestBot.Assert("host: bot renamed by the joiner", ok, BotState?["name"].Value);
                return true;
            });
            // inventory: drag what's in my hands onto the bot's hands, then back (bot stops and empties its hands first)
            s.Add(el => { HostReq("test_botdrop", n => n["w"] = botUid); return true; });
            s.Add(el => { var b = Obj(botUid); return (b && b.GetComponent<Worker>().m_FarmerCarry.m_CarryObject.Count == 0 && el > 2f) || el > 15f; });
            s.Add(el =>
            {
                var me = Me(); var bot = Obj(botUid).GetComponent<Worker>();
                carriedUid = me.m_FarmerCarry.m_CarryObject.Count > 0 && me.m_FarmerCarry.m_CarryObject[0] ? me.m_FarmerCarry.m_CarryObject[0].m_UniqueID : -1;
                if (carriedUid < 0) { TestBot.Assert("client: has something in hands for the inventory test", false); return true; }
                GameStateManager.Instance.PushState(GameStateManager.State.Inventory);
                GameStateManager.Instance.GetCurrentState().GetComponent<GameStateInventory>().SetInfo(me, bot);
                return true;
            });
            s.Add(el => el > 1f && DragHands(true));
            s.Add(el =>
            {
                if (UnityEngine.Time.realtimeSinceStartup > nextBotQuery) { nextBotQuery = UnityEngine.Time.realtimeSinceStartup + 2f; AskBot(); }
                var b = Obj(carriedUid);
                bool ok = BotState != null && BotState["carry"].AsInt > 0 && !Carrying(Me(), carriedUid);
                if (!ok && el < 20f) return false;
                TestBot.Assert("client: dragged item from my hands to the bot (host side)", ok, BotState?.ToString());
                return true;
            });
            s.Add(el => el > 2f && (DragHands(false) || el > 30f));
            s.Add(el =>
            {
                bool ok = Carrying(Me(), carriedUid);
                var hud = HudManager.Instance.m_InventoryBar;
                bool hudOk = hud && hud.m_Object && hud.m_Object.m_UniqueID == Sync.MyPlayerUID;
                if ((!ok || !hudOk) && el < 20f) return false;
                TestBot.Assert("client: dragged it back from the bot to my hands", ok);
                TestBot.Assert("client: HUD inventory bar shows my player", hudOk, "hud " + (hud && hud.m_Object ? hud.m_Object.m_UniqueID : -1) + " me " + Sync.MyPlayerUID + " carry " + Me().m_FarmerCarry.m_CarryObject.Count);
                GameStateManager.Instance.PopState();
                return true;
            });
            // 10. whole-world consistency after things settle
            s.Add(el => el > 25f);   // covers at least two anti-entropy rounds
            // 10. building menus: engage a workbench, pick a recipe in its menu (on the joiner's screen)
            s.Add(el => { HostReq("test_workbench"); return true; });
            s.Add(el =>
            {
                var wb = Obj(WbUid);
                if (!wb && el < 15f) return false;
                TestBot.Assert("client: host-built workbench visible", wb != null);
                if (wb) Click(wb.GetComponent<Building>().GetAccessPosition(), wb, AFO.AT.Primary);
                return true;
            });
            s.Add(el =>
            {
                bool open = GameStateManager.Instance.GetActualState() == GameStateManager.State.BuildingSelect;
                if (!open && el < 40f) return false;
                TestBot.Assert("client: workbench menu opened on the joiner's screen", open, "opened " + Menus.Opened);
                if (!open) return true;
                var conv = Obj(WbUid).GetComponent<Converter>();
                var res = conv.GetUnlockedResults();
                if ((res == null || res.Count == 0) && el < 12f) return false;   // unlock may still be on its way
                if (res == null || res.Count == 0)
                {
                    var why = new List<string>();
                    foreach (var r in conv.m_Results) { var t = r[0].m_Type; why.Add(t + (QuestManager.Instance.m_ObjectsLocked.ContainsKey(t) ? ":locked" : "") + (CeremonyManager.Instance.IsObjectTypeInCeremonyQueue(t) ? ":ceremony" : "")); }
                    TestBot.Assert("client: workbench has a recipe to pick", false, string.Join(" ", why.ToArray()));
                    GameStateManager.Instance.PopState(); return true;
                }
                pickedRecipe = ObjectTypeList.Instance.GetSaveNameFromIdentifier(res[0].m_Type);
                // exactly what ConverterSelect does when a recipe button is clicked
                var me = conv.m_Engager;
                me.SendAction(new ActionInfo(ActionType.SetValue, me.GetComponent<TileCoordObject>().m_TileCoord, conv, Converter.m_ResultsToCreateName, pickedRecipe));
                me.SendAction(new ActionInfo(ActionType.DisengageObject, me.GetComponent<TileCoordObject>().m_TileCoord, conv));
                GameStateManager.Instance.PopState();
                Plugin.Log("[test] picked recipe " + pickedRecipe);
                return true;
            });
            s.Add(el =>
            {
                if (UnityEngine.Time.realtimeSinceStartup > nextBotQuery) { nextBotQuery = UnityEngine.Time.realtimeSinceStartup + 2f; ConvState = null; HostReq("test_convstate", n => n["w"] = WbUid); }
                bool ok = ConvState != null && ConvState["result"].Value == pickedRecipe && !ConvState["engaged"].AsBool && ConvState["hoststate"].Value != "BuildingSelect";
                if (!ok && el < 20f) return false;
                TestBot.Assert("host: workbench makes the joiner's recipe, joiner disengaged, host screen untouched", ok, ConvState?.ToString());
                return true;
            });
            // 11. world state after join: host completes research/quests and changes the time of day
            s.Add(el => { WorldReply = null; HostReq("test_world"); return true; });
            s.Add(el =>
            {
                if (WorldReply == null && el < 15f) return false;
                if (WorldReply == null) { TestBot.Assert("client: world test reply", false); return true; }
                int c = QuestManager.Instance.m_CompletedQuests.Count, l = QuestManager.Instance.m_ObjectsLocked.Count;
                float tod = DayNightManager.Instance.GetTime();
                bool ok = c == WorldReply["completed"].AsInt && l == WorldReply["locked"].AsInt && Mathf.Abs(tod - WorldReply["tod"].AsFloat) < 0.05f;
                if (!ok && el < 20f) return false;
                TestBot.Assert("client: quests/unlocks and time of day follow the host after join", ok,
                    "client " + c + "/" + l + "/" + tod.ToString("F2") + " host " + WorldReply["completed"].AsInt + "/" + WorldReply["locked"].AsInt + "/" + WorldReply["tod"].AsFloat.ToString("F2"));
                return true;
            });
            // 12. animations reached the joiner (chopping, idle...)
            s.Add(el => { TestBot.Assert("client: host-side animations played on the joiner", Anim.Played > 0, "played " + Anim.Played); return true; });
            s.Add(el => { Chk = null; var n = new JSONObject(); n["a"] = "chk"; Coop.I.Client.Host.Send(Msg.Input, n.ToString()); return true; });
            s.Add(el =>
            {
                if (Chk == null && el < 15f) return false;
                Checksum(out int c, out long sum);
                bool ok = Chk != null && Chk["count"].AsInt == c && Chk["sum"].Value == sum.ToString();
                // Animals/timers can change on the host inside one 200 ms scan window; ask again a few times.
                if (Chk != null && !ok && chkTries < 4)
                {
                    chkTries++; Plugin.Log("[test] checksum differs, retry " + chkTries);
                    Chk = null; var rq = new JSONObject(); rq["a"] = "chk"; Coop.I.Client.Host.Send(Msg.Input, rq.ToString());
                    return false;
                }
                if (Chk != null && !ok)
                {
                    var mine = Hashes(); var theirs = new Dictionary<int, int>();
                    var arr = Chk["u"].AsArray;
                    for (int i = 0; i + 1 < arr.Count; i += 2) theirs[arr[i].AsInt] = arr[i + 1].AsInt;
                    int shown = 0;
                    foreach (var kv in mine)
                        if (!theirs.ContainsKey(kv.Key) && shown++ < 15) Plugin.Log("[test] only on client: " + kv.Key + " " + Obj(kv.Key).m_TypeIdentifier + " " + Sync.SaveObject(Obj(kv.Key), out _));
                    foreach (var kv in theirs)
                        if (!mine.ContainsKey(kv.Key) && shown++ < 30) Plugin.Log("[test] only on host: " + kv.Key);
                        else if (mine.TryGetValue(kv.Key, out int h) && h != kv.Value && shown++ < 45) Plugin.Log("[test] differs: " + kv.Key + " " + Sync.SaveObject(Obj(kv.Key), out _));
                }
                TestBot.Assert("client: world checksum matches host", ok, Chk == null ? "no reply" : "host " + Chk["count"].AsInt + "/" + Chk["sum"].Value + " client " + c + "/" + sum);
                Plugin.Log("[test] stats menus " + Menus.Opened + "/" + Menus.Sent + " world " + WorldState.Applied + " anims " + Anim.Played);
                Plugin.Log("[test] stats inplace " + Sync.StatInPlace + " recreated " + Sync.StatRecreated + " created " + Sync.StatCreated + " removed " + Sync.StatRemoved + " batches " + Sync.StatBatches + " resync " + Sync.StatResync + " errors " + Sync.StatErrors + " digests " + Sync.StatDigests + " digestfixes " + Sync.StatDigestFixes + " swept " + Sync.StatSwept);
                foreach (var kv in Sync.RecreatedByType) Plugin.Log("[test]   recreated " + kv.Key + " x" + kv.Value);
                return true;
            });
            // 13. host leaves: the joiner gets a message and goes back to the main menu
            s.Add(el => { HostReq("test_hostquit"); return true; });
            s.Add(el =>
            {
                bool ok = Coop.Role == Role.Off && TestBot.AtMainMenu() && Session.LastDisconnect != null;
                if (!ok && el < 40f) return false;
                TestBot.Assert("client: host left -> joiner back at the main menu with a message", ok, Session.LastDisconnect ?? "no message");
                return true;
            });
        }
    }
}
