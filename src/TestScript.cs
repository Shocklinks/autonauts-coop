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

        public static int WardrobeUid = -1; public static JSONNode UnlockReply;
        public static int WbUid = -1; static string pickedRecipe; public static JSONNode ConvState, WorldReply;
        static void HostReq(string a, Action<JSONObject> args = null) { var n = new JSONObject(); n["a"] = a; args?.Invoke(n); Coop.I.Client.Host.Send(Msg.Input, n.ToString()); }
        static int axeUid = -1, treeUid = -1, logsBefore, bpUid = -1, dragCount, botUid = -1;
        public static JSONNode BotState; static JSONNode lastGood; static float nextBotQuery; static int carriedUid = -1; static float nextDragLog;
        public static int askStep = -1;
        /// Ask the host for the bot's state. Replies to asks from earlier steps are dropped (Coop.cs), so a slow
        /// link can't hand a step an old answer, and a new ask doesn't throw away an answer still on its way.
        static void AskBot() { if (askStep != TestBot.StepIndex) { BotState = null; askStep = TestBot.StepIndex; } var n = new JSONObject(); n["a"] = "test_botstate"; n["w"] = botUid; n["step"] = TestBot.StepIndex; Coop.I.Client.Host.Send(Msg.Input, n.ToString()); }
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
        public static TileCoord EmptyTile(TileCoord c)
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

        /// Joiner: my world as it was when the host's checksum reply arrived (deltas keep landing after that, so
        /// comparing a frame later raced the host's busy bots).
        public static Dictionary<int, int> ChkMine;
        public static void OnChkReply(JSONNode chk) { ChkMine = Hashes(); Chk = chk; }
        public static Dictionary<int, int> HashesAtChk() => ChkMine ?? Hashes();
        public static void ChecksumAtChk(out int count, out long sum)
        {
            if (ChkMine == null) { Checksum(out count, out sum); return; }
            count = ChkMine.Count; sum = 0; foreach (var v in ChkMine.Values) sum += v;
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
            if (TestBot.Transport == "tcp")
                s.Add(el => { TestBot.Assert("host: refused a joiner with a different mod version", VersionCheck.Rejected >= 1, "refused " + VersionCheck.Rejected); return true; });
            s.Add(el =>
            {
                if (SaveReloadPhase > 0) { SaveReloadTick(); return false; }
                return el > 1400f || (!AwaitingRejoin() && ClientGone()) || HostQuitRequested;
            });
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

        static readonly List<Worker> wanderers = new List<Worker>(); static readonly List<TileCoord> wanderHome = new List<TileCoord>();
        static float nextWander; static readonly System.Random wanderRng = new System.Random(11);
        /// Host: keep the test bots walking between spots near where they started.
        public static void HostWanderTick()
        {
            if (wanderers.Count == 0 || UnityEngine.Time.realtimeSinceStartup < nextWander) return;
            nextWander = UnityEngine.Time.realtimeSinceStartup + 1.5f;
            for (int i = 0; i < wanderers.Count; i++)
            {
                var w = wanderers[i];
                if (!w || w.m_State != Farmer.State.None) continue;
                var h = wanderHome[i];
                var t = Sync.FreeTileNear(new TileCoord(h.x + wanderRng.Next(-3, 4), h.y + wanderRng.Next(-3, 4)), true);
                w.RequestGoTo(t);
            }
        }

        public static bool HostTestOp(string a, JSONNode n, Peer from)
        {
            switch (a)
            {
                case "test_wander":
                {
                    // test fixture: bots with energy that keep walking around the joiner (like a busy base)
                    Sync.PeerPlayers.TryGetValue(from, out int pu);
                    var pl = ObjectTypeList.Instance.GetObjectFromUniqueID(pu, false);
                    var c = pl.GetComponent<TileCoordObject>().m_TileCoord;
                    var u = new JSONArray(); var rnd = new System.Random(5);
                    for (int i = 0; i < n["n"].AsInt; i++)
                    {
                        var at = Sync.FreeTileNear(new TileCoord(c.x + rnd.Next(-5, 6), c.y + rnd.Next(-5, 6)), true);
                        var w = ObjectTypeList.Instance.CreateObjectFromIdentifier(ObjectType.Worker, at.ToWorldPositionTileCentered(), Quaternion.identity).GetComponent<Worker>();
                        w.SetFrame(ObjectType.WorkerFrameMk0); w.SetDrive(ObjectType.WorkerDriveMk0); w.SetHead(ObjectType.WorkerHeadMk0);
                        if (w.m_Energy <= 0f) w.AddEnergy(w.m_DriveInfo.m_Energy > 0 ? w.m_DriveInfo.m_Energy : 100f);
                        wanderers.Add(w); wanderHome.Add(at); u.Add(w.m_UniqueID);
                    }
                    var r = new JSONObject(); r["tr"] = "wander"; r["u"] = u; from.Send(Msg.Info, r.ToString());
                    Plugin.Log("[test] " + wanderers.Count + " wandering bots, energy " + (wanderers.Count > 0 ? wanderers[0].m_Energy.ToString("F0") : "-"));
                    return true;
                }
                case "test_wanderstop":
                {
                    foreach (var w in wanderers) if (w) w.StopUsing();
                    wanderers.Clear(); wanderHome.Clear();
                    return true;
                }
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
                case "test_unlockbuilding":
                {
                    ObjectType pick = ObjectTypeList.m_Total;
                    foreach (var kv in QuestManager.Instance.m_BuildingsLocked) { pick = kv.Key; break; }
                    if (pick != ObjectTypeList.m_Total) QuestManager.Instance.UnlockObject(pick);
                    var st = new JSONObject(); st["type"] = ObjectTypeList.Instance.GetSaveNameFromIdentifier(pick); st["count"] = QuestManager.Instance.m_BuildingsLocked.Count;
                    var r = new JSONObject(); r["unlock"] = st; from.Send(Msg.Info, r.ToString());
                    Plugin.Log("[test] unlocked building " + pick);
                    return true;
                }
                case "test_unlock":
                {
                    var t = ObjectTypeList.Instance.GetIdentifierFromSaveName(n["type"], false);
                    if (t != ObjectTypeList.m_Total) QuestManager.Instance.UnlockObject(t);
                    Plugin.Log("[test] unlocked " + t);
                    return true;
                }
                case "test_wardrobe":
                {
                    Sync.PeerPlayers.TryGetValue(from, out int pu);
                    var pt = ObjectTypeList.Instance.GetObjectFromUniqueID(pu, false).GetComponent<TileCoordObject>().m_TileCoord;
                    Building b = null; TileCoord t = pt;
                    foreach (var off in new[] { new TileCoord(-3, 3), new TileCoord(3, 3), new TileCoord(-3, -3), new TileCoord(3, -3), new TileCoord(0, 5), new TileCoord(5, 0) })
                    {
                        t = Sync.FreeTileNear(new TileCoord(pt.x + off.x, pt.y + off.y));
                        b = BuildingManager.Instance.AddBuilding(t, ObjectType.Wardrobe, 0, false, null, true);
                        if (b) break;
                    }
                    if (b)
                    {
                        var hat = ObjectTypeList.Instance.CreateObjectFromIdentifier(ObjectType.HatChullo, b.transform.position, Quaternion.identity).GetComponent<Holdable>();
                        b.GetComponent<Wardrobe>().AttemptAdd(hat);
                    }
                    var r = new JSONObject(); r["wardrobe"] = b ? b.m_UniqueID : -1; from.Send(Msg.Info, r.ToString());
                    Plugin.Log("[test] built wardrobe " + (b ? b.m_UniqueID : -1));
                    return true;
                }
                case "test_build":
                {
                    Sync.PeerPlayers.TryGetValue(from, out int pu);
                    var pt = ObjectTypeList.Instance.GetObjectFromUniqueID(pu, false).GetComponent<TileCoordObject>().m_TileCoord;
                    var type = ObjectTypeList.Instance.GetIdentifierFromSaveName(n["t"], false);
                    Building b = null;
                    foreach (var off in new[] { new TileCoord(4, 4), new TileCoord(-4, 4), new TileCoord(4, -4), new TileCoord(-4, -4), new TileCoord(0, 7), new TileCoord(7, 0), new TileCoord(-7, 0), new TileCoord(0, -7) })
                    {
                        var t = Sync.FreeTileNear(new TileCoord(pt.x + off.x, pt.y + off.y));
                        b = BuildingManager.Instance.AddBuilding(t, type, 0, false, null, true);
                        if (b) break;
                    }
                    if (b && b.GetComponent<SpacePort>() && OffworldMissionsManager.Instance.m_RegularMissionsAvailable.Count == 0)
                        OffworldMissionsManager.Instance.SetupRegularMissions();
                    Plugin.Log("[test] built " + (string)n["t"] + " " + (b ? b.m_UniqueID : -1));
                    TestReply(from, "built", r => r["b"] = b ? b.m_UniqueID : -1);
                    return true;
                }
                case "test_speed":
                    TimeManager.Instance.SetTimeScale(n["fast"].AsBool ? TimeManager.m_FastTime : 1f);
                    Plugin.Log("[test] host speed x" + TimeManager.Instance.m_TimeScale);
                    return true;
                case "test_pause":
                    if (n["on"].AsBool) TimeManager.Instance.PauseAll(true); else TimeManager.Instance.UnPauseAll(true);
                    return true;
                case "test_hostscript":
                {
                    var hl = new HighInstructionList();
                    WorkerScriptManager.Instance.AddScript("HostScript", hl, ObjectType.WorkerHeadMk0, -1, -1, Color.white);
                    return true;
                }
                case "test_hostplan":
                {
                    Sync.PeerPlayers.TryGetValue(from, out int pu);
                    var pt = ObjectTypeList.Instance.GetObjectFromUniqueID(pu, false).GetComponent<TileCoordObject>().m_TileCoord;
                    var a2 = PlanningManager.Instance.NewArea(new TileCoord(pt.x - 6, pt.y - 6), new TileCoord(pt.x - 3, pt.y - 3));
                    a2.SetName("HostPlan");
                    return true;
                }
                case "test_q":
                {
                    Sync.PeerPlayers.TryGetValue(from, out int pu);
                    var fp = ObjectTypeList.Instance.GetObjectFromUniqueID(pu, false)?.GetComponent<FarmerPlayer>();
                    var o = ObjectTypeList.Instance.GetObjectFromUniqueID(n["w"].AsInt, false);
                    TestReply(from, (string)n["q"], r =>
                    {
                        r["engaged"] = fp && fp.m_EngagedObject;
                        switch ((string)n["q"])
                        {
                            case "speed": r["s"] = TimeManager.Instance.m_TimeScale; break;
                            case "look": r["y"] = o ? o.transform.eulerAngles.y : -999f; break;
                            case "lib":
                            {
                                var names = new JSONArray(); foreach (var sc in WorkerScriptManager.Instance.m_Scripts) names.Add(sc.m_Name + "#" + sc.m_UID);
                                r["names"] = names; r["folders"] = WorkerScriptManager.Instance.m_Folders.Count;
                                var w = o ? o.GetComponent<Worker>() : null;
                                r["link"] = w ? w.m_WorkerInterpreter.m_LinkedScriptUID : -2;
                                break;
                            }
                            case "cat":
                            {
                                var c = o ? o.GetComponent<Catapult>() : null;
                                if (c) { var t = c.GetTargetTile(); r["x"] = t.x; r["y"] = t.y; }
                                break;
                            }
                            case "sp":
                            {
                                var om = OffworldMissionsManager.Instance;
                                r["sel"] = om.m_SelectedMission != null ? om.m_SelectedMission.m_ID : -1;
                                var ids = new JSONArray(); foreach (var m in om.m_RegularMissionsAvailable) ids.Add(m != null ? m.m_ID : -1); r["ids"] = ids;
                                break;
                            }
                            case "grp":
                            {
                                var names = new JSONArray();
                                foreach (var g in WorkerGroupManager.Instance.m_Groups) names.Add(g.m_Name + "#" + g.m_ID + ":" + string.Join(",", g.m_WorkerUIDs.ConvertAll(x => x.ToString()).ToArray()));
                                r["names"] = names; r["lastcmd"] = Groups.LastCmd;
                                break;
                            }
                            case "pk":
                            {
                                r["parked"] = Persist.ParkedCount; r["restored"] = Persist.Restored; r["relocated"] = Persist.Relocated; r["players"] = playersAtPark;
                                bool left = false, joined = false; foreach (var t in Menu.Toasts) { if (t.EndsWith("left the game")) left = true; if (t.EndsWith("joined the game")) joined = true; }
                                r["left"] = left; r["joined"] = joined;
                                break;
                            }
                            case "plan":
                            {
                                var names = new JSONArray();
                                foreach (var ar in Traverse.Create(PlanningManager.Instance).Field("m_PlanningArea").GetValue<List<PlanningArea>>()) names.Add(ar.m_Name);
                                r["names"] = names;
                                break;
                            }
                        }
                    });
                    return true;
                }
                case "test_blockspot":
                {
                    expectRejoinUntil = UnityEngine.Time.realtimeSinceStartup + 150f;
                    Persist.OnParkedAt = t =>
                    {
                        Persist.OnParkedAt = null;
                        playersAtPark = 0; foreach (var kv in Sync.Savables()) if (kv.Key && kv.Key.m_TypeIdentifier == ObjectType.FarmerPlayer) playersAtPark++;
                        // the host builds right where the joiner stood
                        var b = BuildingManager.Instance.AddBuilding(t, ObjectType.LogWall, 0, false, null, true);
                        Plugin.Log("[test] host built on the joiner's spot " + t.x + "," + t.y + ": " + (b ? b.m_UniqueID : -1));
                    };
                    return true;
                }
                case "test_expectrejoin":
                    expectRejoinUntil = UnityEngine.Time.realtimeSinceStartup + 150f;
                    return true;
                case "test_hostgroup":
                {
                    TabWorkers.Instance.NewGroup(null);
                    var g = WorkerGroupManager.Instance.m_Groups[WorkerGroupManager.Instance.m_Groups.Count - 1];
                    g.m_Name = "HostGroup";
                    return true;
                }
                case "test_bugreport":
                    BugReport.LastPath = null; BugReport.Begin();
                    TestReply(from, "bug", r => { r["path"] = BugReport.LastPath ?? ""; r["marks"] = Diag.Marks; });
                    return true;
                case "test_hostquit":
                    HostQuitRequested = true;
                    return true;
                case "test_savereload":
                    SaveReloadPhase = 1;
                    return true;
            }
            return false;
        }
        public static bool HostQuitRequested;
        public static JSONNode Reply;   // client: last generic test reply ("tr" = kind)
        static void TestReply(Peer to, string kind, Action<JSONObject> fill)
        {
            var r = new JSONObject(); r["tr"] = kind; fill(r); to.Send(Msg.Info, r.ToString());
        }
        static bool Got(string kind) => Reply != null && (string)Reply["tr"] == kind;
        static void Ask(string q, int w = -1) { Reply = null; HostReq("test_q", n => { n["q"] = q; n["w"] = w; }); }
        static float nextAsk;
        /// ask every 1.5 s until ok(reply) or timeout; returns null while still waiting
        static bool? Poll(float el, string q, int w, Func<JSONNode, bool> ok, float timeout = 20f)
        {
            if (Got(q) && ok(Reply)) return true;
            if (el > timeout) return false;
            if (UnityEngine.Time.realtimeSinceStartup > nextAsk) { nextAsk = UnityEngine.Time.realtimeSinceStartup + 1.5f; Ask(q, w); }
            return null;
        }
        static int builtUid = -1, newScriptUid = -1, foldersBefore = -1; static TileCoord catTarget; static int missionPick = -1, missionDrop = -1;
        // ---- bot selection test (8b) ----
        public static readonly List<int> wanderUids = new List<int>();
        static int selTrial, selHits, selFrame; static Worker selTarget, selClicked; static float selT;
        static int SelTrials = 6;
        public static int SelHits => selHits;
        public static void SelBegin(List<int> uids, int trials) { wanderUids.Clear(); wanderUids.AddRange(uids); SelTrials = trials; selTrial = 0; selHits = 0; selTarget = null; selT = 0; }
        public static Vector3 BotScreen(Worker w) => CameraManager.Instance.m_Camera.WorldToScreenPoint(w.transform.position + Vector3.up * 1.0f);
        public static bool OnScreen(Vector3 p) => p.z > 0 && p.x > 40 && p.y > 40 && p.x < Screen.width - 40 && p.y < Screen.height - 40;
        /// Not hidden behind another bot or a tree (a player clicks the one they can see).
        static bool Visible(Worker w)
        {
            var st = GameStateManager.Instance.GetCurrentState();
            var go = st ? st.TestCollisionOffset(out _, true, false, true, false, true, BotScreen(w)) : null;   // the layers the whistle screen tests
            return go && go.GetComponentInParent<Worker>() == w;
        }
        static Worker PickOnScreenBot(Worker not = null)
        {
            var list = new List<Worker>();
            foreach (var u in wanderUids) { var b = Obj(u); var w = b ? b.GetComponent<Worker>() : null; if (w && w != not && OnScreen(BotScreen(w)) && Visible(w)) list.Add(w); }
            if (list.Count == 0) return null;
            return list[(selTrial * 5 + 3) % list.Count];
        }
        /// Hover each bot in turn like a player tracking it with the mouse; the game must put it under the cursor.
        public static bool SelectTrials(float el)
        {
            var sel = GameStateSelectWorker.Instance;
            if (!sel || GameStateManager.Instance.GetActualState() != GameStateManager.State.SelectWorker)
            {
                if (el < 5f) return false;
                TestBot.Assert("client: whistle-select screen open", false, GameStateManager.Instance.GetActualState().ToString());
                Diag.FakeMouse = null; Diag.FakeTrack = null; return true;
            }
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (selTarget == null && el > 170f && selTrial >= Math.Min(3, wanderUids.Count))   // slow machine: judge on the tries done (step limit 240 s)
            {
                TestBot.Assert("client: hovering a busy bot picks it (" + selTrial + " tries in time)", selHits >= selTrial - 1, selHits + "/" + selTrial);
                return true;
            }
            if (selTarget == null)
            {
                if (selTrial >= SelTrials)
                {
                    TestBot.Assert("client: hovering a busy bot picks it (" + SelTrials + " tries)", selHits >= SelTrials - 1, selHits + "/" + SelTrials);
                    return true;
                }
                selTarget = PickOnScreenBot(Traverse.Create(sel).Field("m_CurrentTarget").GetValue<Worker>()); selT = now; selFrame = UnityEngine.Time.frameCount;   // a different bot each time
                if (!selTarget && selTrial >= Math.Min(3, wanderUids.Count))
                {
                    TestBot.Assert("client: hovering a busy bot picks it (" + selTrial + " tries, ran out of other bots on screen)", selHits >= Math.Max(1, selTrial - 1), selHits + "/" + selTrial);
                    return true;
                }
                if (!selTarget)
                {
                    if (UnityEngine.Time.realtimeSinceStartup > nextDragLog && wanderUids.Count > 0)
                    {
                        nextDragLog = UnityEngine.Time.realtimeSinceStartup + 3f;
                        var b0 = Obj(wanderUids[0]); var cam = CameraManager.Instance.m_Camera;
                        Plugin.Log("[test] no bot on screen: bot " + (b0 ? b0.transform.position.ToString() + " screen " + BotScreen(b0.GetComponent<Worker>()) : "missing") + " cam " + cam.transform.position + " " + cam.name + " " + cam.pixelWidth + "x" + cam.pixelHeight + " Screen " + Screen.width + "x" + Screen.height + " me " + Me().transform.position);
                    }
                    if (el > 20f) { TestBot.Assert("client: bots on screen for the selection test", false); Diag.FakeMouse = null; Diag.FakeTrack = null; return true; }
                    return false;
                }
            }
            if (!selTarget) { selTarget = null; selTrial++; return false; }   // rebuilt or gone mid-try
            Diag.FakeMouse = BotScreen(selTarget); Diag.FakeTrack = selTarget.transform;
            var cur = Traverse.Create(sel).Field("m_CurrentTarget").GetValue<Worker>();
            // the screen needs the bot under the cursor for 10 frames in a row; give it 30 (this test machine draws ~2 fps)
            if (cur == selTarget || (now - selT > 2.5f && UnityEngine.Time.frameCount - selFrame > 30))
            {
                bool hit = cur == selTarget;
                if (hit) selHits++;
                var tr = Traverse.Create(sel);
                var bsl = tr.Field("m_BotSelectList").GetValue<BotSelectList>();
                var plot = PlotManager.Instance.GetPlotAtTile(selTarget.m_TileCoord);
                int atTile = plot ? plot.GetObjectsTypeAtTile(ObjectType.Worker, selTarget.m_TileCoord).Count : -1;
                Plugin.Log("[test] select try " + selTrial + ": " + (hit ? "picked " : "MISSED ") + Diag.BotInfo(selTarget) + " in " + (now - selT).ToString("F2") + " s / " + (UnityEngine.Time.frameCount - selFrame) + " frames; cursor on " + Diag.Obj(cur)
                    + " [sel state " + tr.Field("m_State").GetValue() + " next " + Diag.Obj(tr.Field("m_NextTarget").GetValue<Worker>()) + " timer " + tr.Field("m_NextTargetTimer").GetValue() + " list " + (bsl && bsl.gameObject.activeSelf) + " bots on its tile " + atTile + " found " + tr.Field("m_WorkersFound").GetValue() + "]"
                    + " | " + Diag.Probe(Diag.Mouse));
                selTarget = null; selTrial++;
            }
            return false;
        }

        static ObjectType paletteType = ObjectTypeList.m_Total; static bool coverShown; static float lookYaw;
        static int groupId = -1, copyPlayer = -1, copyObjs = -1, autosBefore = -1, relocAtLeave = -1; static TileCoord undoTile, leaveTile, reloadTile; static float leaveAt;
        static WorkerGroup G(int id) => WorkerGroupManager.Instance.GetGroupFromID(id);
        static bool HasName(JSONNode arr, string name) { foreach (JSONNode x in arr.AsArray) if (((string)x).StartsWith(name)) return true; return false; }

        // ---- host: save the co-op world, stop, load it again, host again (what a player does the next day) ----
        public static int SaveReloadPhase; static float phaseT, reloadedAt = -1; static int hostBefore, relocBefore;
        const string ReloadSave = "coop_test_reload";
        static void SaveReloadTick()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (SaveReloadPhase == 1)
            {
                hostBefore = Sync.HostPlayerUID;
                bool saved = SaveLoadManager.Instance.Save(ReloadSave);
                TestBot.Assert("host: saved the co-op world", saved);
                TestBot.Assert("host: save records the joiner's seat", Persist.Seats.Count >= 1, Persist.Seats.Count + " seat(s)");
                relocBefore = Persist.Relocated;
                Session.StopHost("test: reload");
                SessionManager.Instance.m_LoadFileName = ReloadSave;
                SessionManager.Instance.LoadLevel(true, "Main");
                SaveReloadPhase = 2; phaseT = now;
                Plugin.Log("[test] saved and reloading " + ReloadSave);
            }
            else if (SaveReloadPhase == 2)
            {
                if (now - phaseT < 5f || !Coop.InGame() || GameStateManager.Instance.GetActualState() != GameStateManager.State.Normal) { SkipLoadCeremony(); return; }
                if (now - phaseT < 12f) return;
                int players = 0; foreach (var kv in Sync.Savables()) if (kv.Key && kv.Key.m_TypeIdentifier == ObjectType.FarmerPlayer) players++;
                TestBot.Assert("host: reloaded world keeps the joiner's character stored, not standing in the world", players == 1 && Persist.Parked.Count == 1, players + " FarmerPlayers, " + Persist.Parked.Count + " stored");
                TestBot.Assert("host: after reload the host controls its own character", Me().m_UniqueID == hostBefore, "player0 " + Me().m_UniqueID + " was " + hostBefore);
                Coop.I.StartHost(TestBot.Port);
                TestBot.Assert("host: hosting again after reload, same host player", Sync.HostPlayerUID == hostBefore, Sync.HostPlayerUID + " vs " + hostBefore);
                SaveReloadPhase = 0; reloadedAt = now;
            }
        }
        static void SkipLoadCeremony() { }
        static float expectRejoinUntil; static int playersAtPark = -1;
        static bool AwaitingRejoin() => SaveReloadPhase > 0 || (((reloadedAt > 0 && UnityEngine.Time.realtimeSinceStartup - reloadedAt < 240f) || UnityEngine.Time.realtimeSinceStartup < expectRejoinUntil) && !JoinedPeer());
        static bool JoinedPeer() { lock (Coop.I.Host.Peers) return Coop.I.Host.Peers.Exists(p => p.Alive && p.Name != "?"); }

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
        static bool versionTried; static float vT;
        static int seatUid = -1; static List<int> seatStuff = new List<int>(); static float rejoinTry;
        static void ResendHello()
        {
            if (UnityEngine.Time.realtimeSinceStartup - lastHello < 3f) return;
            lastHello = UnityEngine.Time.realtimeSinceStartup;
            var hello = Coop.MakeHello(); hello["name"] = "player";
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
                if (!versionTried)
                {
                    // first knock with a wrong version: the host must refuse it and say why
                    VersionCheck.FakeVersion = "0.0.1-test";
                    try { Coop.I.StartClient(TestBot.Ip, TestBot.Port); versionTried = true; vT = el; }
                    catch (System.Exception e) { Plugin.Log("connect retry: " + e.Message); }
                    return false;
                }
                if (VersionCheck.FakeVersion != null)
                {
                    bool refused = Coop.Role == Role.Off && Session.LastDisconnect != null && Session.LastDisconnect.Contains("Version mismatch");
                    if (!refused && el - vT < 20f) return false;
                    TestBot.Assert("client: wrong mod version refused with a reason", refused, Session.LastDisconnect ?? "no message");
                    VersionCheck.FakeVersion = null; Session.LastDisconnect = null;
                    if (Coop.Role != Role.Off) Session.ClientLost("test cleanup");
                    vT = el; return false;
                }
                if (el - vT < 3f) return false;
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
            // the editor's "stop the bot first" cover must go away again (it counts down with game time, frozen on joiners)
            s.Add(el =>
            {
                var ed = TeachWorkerScriptEdit.Instance;
                if ((ed == null || !ed.m_CurrentTarget) && el < 5f) return false;
                if (ed == null) return true;
                if (el < 0.5f) return false;
                if (!coverShown) { AccessTools.Method(typeof(TeachWorkerScriptEdit), "DoNonEditable").Invoke(ed, null); coverShown = true; return false; }
                var ne = Traverse.Create(ed).Field("m_NonEditable").GetValue<GameObject>();
                bool hidden = !ne || !ne.activeSelf;
                if (UnityEngine.Time.realtimeSinceStartup > nextDragLog) { nextDragLog = UnityEngine.Time.realtimeSinceStartup + 1f; Plugin.Log("[test] cover timer " + Traverse.Create(ed).Field("m_NonEditableTimer").GetValue<float>().ToString("F2") + " active " + (ne && ne.activeSelf) + " editor active " + ed.isActiveAndEnabled + " state " + GameStateManager.Instance.GetActualState()); }
                if (!hidden && el < 8f) return false;
                TestBot.Assert("client: script editor's 'not editable' cover clears by itself", hidden);
                TestBot.Assert("client: camera/UI run on real frame time", RealTime.LastUiDelta > 0f, RealTime.LastUiDelta.ToString("F3"));
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
                AskBot();
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
                    nextBotQuery = UnityEngine.Time.realtimeSinceStartup + 2f;
                    AskBot();
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
            // 8b. pick moving bots with the mouse (whistle, hover, click), the way a player does, among a crowd of busy bots
            s.Add(el => { Reply = null; HostReq("test_wander", n => n["n"] = 14); return true; });
            s.Add(el =>
            {
                if ((Reply == null || (string)Reply["tr"] != "wander") && el < 20f) return false;
                wanderUids.Clear();
                if (Reply != null && (string)Reply["tr"] == "wander") foreach (JSONNode u in Reply["u"].AsArray) wanderUids.Add(u.AsInt);
                TestBot.Assert("client: host made busy bots for the selection test", wanderUids.Count > 0, wanderUids.Count + " bots");
                return true;
            });
            s.Add(el =>
            {
                int here = 0; foreach (var u in wanderUids) if (Obj(u)) here++;
                if ((here < wanderUids.Count || el < 6f) && el < 30f) return false;   // let them get going
                int moving = 0; foreach (var u in wanderUids) { var b = Obj(u); if (b && Sync.IsMovingOnClient(u)) moving++; }
                Plugin.Log("[test] select test: " + here + " bots here, " + moving + " moving");
                // look at them (earlier tests left the camera elsewhere)
                var sum = Vector3.zero; int cnt = 0; foreach (var u in wanderUids) { var b = Obj(u); if (b) { sum += b.transform.position; cnt++; } }
                if (cnt > 0) CameraManager.Instance.Focus(sum / cnt);
                GameStateManager.Instance.SetState(GameStateManager.State.SelectWorker);   // what the whistle key does
                selTrial = 0; selHits = 0; selTarget = null; selT = 0;
                return true;
            });
            s.Add(el => el > 1f);
            s.Add(el => SelectTrials(el));
            s.Add(el =>
            {
                // click on the bot under the cursor: it must be selected (not "walk over to it")
                var sel = GameStateSelectWorker.Instance;
                if (!sel) { TestBot.Assert("client: whistle screen still open for the click", false); Diag.FakeMouse = null; Diag.FakeTrack = null; return true; }
                var cur = Traverse.Create(sel).Field("m_CurrentTarget").GetValue<Worker>();
                if (!cur)
                {
                    var w = PickOnScreenBot();
                    if (w) { Diag.FakeMouse = BotScreen(w); Diag.FakeTrack = w.transform; }
                    if (el < 60f) return false;
                    TestBot.Assert("client: a bot under the cursor to click", false); Diag.FakeMouse = null; Diag.FakeTrack = null; return true;
                }
                selClicked = cur;
                sel.SelectSingleBot(cur, cur.m_TileCoord);   // what the click does
                return true;
            });
            s.Add(el =>
            {
                var gn = GameStateManager.Instance.GetCurrentState()?.GetComponent<GameStateNormal>();
                var picked = gn ? gn.GetSelectedWorker() : null;
                bool ok = selClicked && picked == selClicked && GameStateManager.Instance.GetActualState() == GameStateManager.State.Normal;
                if (!ok && el < 3f) return false;
                var ed = TeachWorkerScriptEdit.Instance;
                TestBot.Assert("client: clicking a busy bot selects it and opens its program", ok && ed && ed.m_CurrentTarget == selClicked,
                    "clicked " + Diag.Obj(selClicked) + " selected " + Diag.Obj(picked) + " editor " + (ed ? Diag.Obj(ed.m_CurrentTarget) : "-") + " screen " + GameStateManager.Instance.GetActualState());
                Diag.FakeMouse = null; Diag.FakeTrack = null;
                gn?.ClearSelectedWorkers();
                if (GameStateManager.Instance.GetActualState() != GameStateManager.State.Normal) GameStateManager.Instance.SetState(GameStateManager.State.Normal);
                HostReq("test_wanderstop");
                return true;
            });
            s.Add(el => el > 3f);
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
                ConvState = null;
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
                if (UnityEngine.Time.realtimeSinceStartup > nextBotQuery) { nextBotQuery = UnityEngine.Time.realtimeSinceStartup + 2f; HostReq("test_convstate", n => n["w"] = WbUid); }
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
            // 12. building unlocks after join: host unlocks a building, the joiner's build palette data follows
            s.Add(el => { UnlockReply = null; HostReq("test_unlockbuilding"); return true; });
            s.Add(el =>
            {
                if (UnlockReply == null && el < 10f) return false;
                if (UnlockReply == null) { TestBot.Assert("client: unlock reply", false); return true; }
                var t = ObjectTypeList.Instance.GetIdentifierFromSaveName(UnlockReply["type"], false);
                bool ok = t != ObjectTypeList.m_Total && !QuestManager.Instance.m_BuildingsLocked.ContainsKey(t) && QuestManager.Instance.m_BuildingsLocked.Count == UnlockReply["count"].AsInt;
                if (!ok && el < 15f) return false;
                TestBot.Assert("client: building unlocked on the host shows as unlocked for the joiner", ok, (string)UnlockReply["type"] + " client locked " + QuestManager.Instance.m_BuildingsLocked.Count + " host " + UnlockReply["count"].AsInt);
                return true;
            });
            // 12b. an unlock arriving while the joiner has edit mode open shows up without reopening it
            s.Add(el => { GameStateManager.Instance.SetState(GameStateManager.State.Edit); return true; });
            s.Add(el =>
            {
                if (el < 1.5f) return false;
                var pal = GameStateEdit.Instance ? GameStateEdit.Instance.m_BuildingPalette : null;
                var infos = pal ? Traverse.Create(pal).Field("m_CategoryInfo").GetValue<List<BuildingCategoryInfo>>() : null;
                paletteType = ObjectTypeList.m_Total;
                if (infos != null) foreach (var info in infos) { for (int j = 0; j < info.m_Buildings.Count && paletteType == ObjectTypeList.m_Total; j++) if (info.m_Locked[j]) paletteType = info.m_Buildings[j]; }
                if (paletteType == ObjectTypeList.m_Total) { TestBot.Assert("client: a locked building in the palette", false); return true; }
                HostReq("test_unlock", n => n["type"] = ObjectTypeList.Instance.GetSaveNameFromIdentifier(paletteType));
                return true;
            });
            s.Add(el =>
            {
                var pal = GameStateEdit.Instance ? GameStateEdit.Instance.m_BuildingPalette : null;
                bool ok = false;
                var infos = pal ? Traverse.Create(pal).Field("m_CategoryInfo").GetValue<List<BuildingCategoryInfo>>() : null;
                if (infos != null) foreach (var info in infos) { int j = info.m_Buildings.IndexOf(paletteType); if (j >= 0 && !info.m_Locked[j]) ok = true; }
                if (!ok && el < 15f) return false;
                TestBot.Assert("client: unlock shows in the open build palette (no reopen)", ok && GameStateManager.Instance.GetActualState() == GameStateManager.State.Edit, paletteType + " refreshes " + WorldState.PaletteRefreshes);
                GameStateManager.Instance.SetState(GameStateManager.State.Normal);
                return true;
            });
            // 13. wardrobe: engage it (screen opens on the joiner, not the host), put on a hat from it, close
            s.Add(el => { WardrobeUid = -1; HostReq("test_wardrobe"); return true; });
            s.Add(el =>
            {
                var wr = Obj(WardrobeUid);
                if (!wr && el < 15f) return false;
                TestBot.Assert("client: host-built wardrobe visible", wr != null);
                if (wr) Click(wr.GetComponent<Building>().GetAccessPosition(), wr, AFO.AT.Primary);
                return true;
            });
            s.Add(el =>
            {
                var gs = GameStateManager.Instance.GetCurrentState();
                var inv = gs ? gs.GetComponent<GameStateInventory>() : null;
                bool ok = inv && inv.m_SecondBar && inv.m_SecondBar.m_Object && inv.m_SecondBar.m_Object.m_UniqueID == WardrobeUid;
                if (!ok && el < 40f) return false;
                TestBot.Assert("client: wardrobe screen opened on the joiner", ok);
                return true;
            });
            s.Add(el =>
            {
                if (el < 1f) return false;
                var inv = GameStateManager.Instance.GetCurrentState().GetComponent<GameStateInventory>();
                if (inv == null) return true;
                InventorySlot src = null;
                foreach (var g in inv.m_SecondBar.m_WardrobeSlots.m_Buttons) { var sl = g.GetComponent<InventorySlot>(); if (sl && sl.m_Object) { src = sl; break; } }
                var panel = Traverse.Create(inv).Field("m_Panel").GetValue<InventoryBar>();
                var slots = panel ? Traverse.Create(panel).Field("m_ClothingSlots").GetValue<InventorySlot[]>() : null;
                InventorySlot dst = null;
                if (slots != null) foreach (var sl in slots) if (sl && src && sl.CanAcceptObject(src)) { dst = sl; break; }
                if (!src || !dst) { if (el < 15f) return false; TestBot.Assert("client: hat in wardrobe and a free clothing slot", false, "src " + (src != null) + " dst " + (dst != null)); return true; }
                inv.m_SecondBar.DragStart(src);
                var drag = GameStateManager.Instance.GetCurrentState().GetComponent<GameStateDragInventorySlot>();
                if (drag == null) return false;
                drag.SetDragTarget(dst);
                inv.m_SecondBar.DragEnd(dst);
                Plugin.Log("[test] dragged " + src.m_Object.m_TypeIdentifier + " from wardrobe to " + dst.m_Type);
                return true;
            });
            s.Add(el =>
            {
                var me = Obj(Sync.MyPlayerUID);
                bool ok = me && Sync.SaveObject(me, out _).Contains("HatChullo");
                if (!ok && el < 20f) return false;
                TestBot.Assert("client: wearing the hat from the wardrobe (via host)", ok);
                var gs2 = GameStateManager.Instance.GetCurrentState();
                var inv = gs2 ? gs2.GetComponent<GameStateInventory>() : null;
                ConvState = null; if (inv) inv.Close();
                return true;
            });
            s.Add(el =>
            {
                if (UnityEngine.Time.realtimeSinceStartup > nextBotQuery) { nextBotQuery = UnityEngine.Time.realtimeSinceStartup + 2f; HostReq("test_convstate", n => n["w"] = WardrobeUid); }
                bool ok = ConvState != null && !ConvState["engaged"].AsBool && ConvState["hoststate"].Value == "Normal";
                if (!ok && el < 20f) return false;
                TestBot.Assert("host: joiner left the wardrobe, host screen untouched", ok, ConvState?.ToString());
                return true;
            });
            // 14. animations reached the joiner (chopping, idle...)
            s.Add(el => { TestBot.Assert("client: host-side animations played on the joiner", Anim.Played > 0, "played " + Anim.Played); return true; });
            s.Add(el => { Chk = null; var n = new JSONObject(); n["a"] = "chk"; Coop.I.Client.Host.Send(Msg.Input, n.ToString()); return true; });
            s.Add(el =>
            {
                if (Chk == null && el < 15f) return false;
                ChecksumAtChk(out int c, out long sum);
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
                    var mine = HashesAtChk(); var theirs = new Dictionary<int, int>();
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
                Plugin.Log("[test] stats inplace " + Sync.StatInPlace + " (farmers with held items " + Sync.StatNestedInPlace + ") recreated " + Sync.StatRecreated + " created " + Sync.StatCreated + " removed " + Sync.StatRemoved + " batches " + Sync.StatBatches + " resync " + Sync.StatResync + " errors " + Sync.StatErrors + " digests " + Sync.StatDigests + " digestfixes " + Sync.StatDigestFixes + " swept " + Sync.StatSwept);
                foreach (var kv in Sync.RecreatedByType) Plugin.Log("[test]   recreated " + kv.Key + " x" + kv.Value);
                return true;
            });
            // 15b. where I look: my idle character faces my cursor; the host turns it the same way (not to the host's cursor)
            s.Add(el =>
            {
                if (Me().m_State != Farmer.State.None) return el > 10f;
                return el > 1f;
            });
            s.Add(el =>
            {
                lookYaw = Me().transform.eulerAngles.y;   // wherever my cursor made me look
                var r = Poll(el, "look", Sync.MyPlayerUID, x => Mathf.Abs(Mathf.DeltaAngle(x["y"].AsFloat, lookYaw)) < 6f, 12f);
                if (r == null) return false;
                TestBot.Assert("host: joiner's character faces where the joiner looks", r == true, (Reply != null ? Reply["y"].Value : "?") + " want " + lookYaw.ToString("F0"));
                return true;
            });
            // 16. game speed: the host decides, the joiner follows and can't change it
            s.Add(el => { HostReq("test_speed", n => n["fast"] = true); return true; });
            s.Add(el =>
            {
                bool ok = TimeManager.Instance.GetIsFastTime();
                if (!ok && el < 10f) return false;
                TestBot.Assert("client: game speed follows the host (fast)", ok, "x" + TimeManager.Instance.m_TimeScale);
                TimeManager.Instance.ToggleFastTime();     // joiner tries to slow it down
                TimeManager.Instance.SetTimeScale(1f);
                TestBot.Assert("client: joiner can't change the game speed locally", TimeManager.Instance.GetIsFastTime(), "x" + TimeManager.Instance.m_TimeScale);
                return true;
            });
            s.Add(el =>
            {
                var r = Poll(el, "speed", -1, x => x["s"].AsFloat > 1.5f, 8f);
                if (r == null) return false;
                TestBot.Assert("host: still fast after the joiner tried to change it", r == true, Reply?.ToString());
                HostReq("test_speed", n => n["fast"] = false); return true;
            });
            s.Add(el =>
            {
                bool ok = !TimeManager.Instance.GetIsFastTime() && Mathf.Abs(TimeManager.Instance.m_TimeScale - 1f) < 0.01f;
                if (!ok && el < 10f) return false;
                TestBot.Assert("client: back to normal speed with the host", ok, "x" + TimeManager.Instance.m_TimeScale);
                HostReq("test_pause", n => n["on"] = true); return true;
            });
            s.Add(el =>
            {
                if (!Speed.HostPaused && el < 10f) return false;
                TestBot.Assert("client: sees that the host paused", Speed.HostPaused);
                HostReq("test_pause", n => n["on"] = false); return true;
            });
            s.Add(el =>
            {
                if (Speed.HostPaused && el < 10f) return false;
                TestBot.Assert("client: sees the host unpause", !Speed.HostPaused);
                return true;
            });
            // 17. bot server: open it by engaging, upload the bot's script (links it), rename it, add a folder
            s.Add(el => { builtUid = -1; Reply = null; HostReq("test_build", n => n["t"] = "BotServer"); return true; });
            s.Add(el =>
            {
                if (Got("built") && builtUid < 0) builtUid = Reply["b"].AsInt;
                var b = Obj(builtUid);
                if (!b && el < 15f) return false;
                TestBot.Assert("client: host-built bot server visible", b != null);
                if (b) Click(b.GetComponent<Building>().GetAccessPosition(), b, AFO.AT.Primary);
                return true;
            });
            s.Add(el =>
            {
                var sel = Library.Open();
                if (!sel && el < 40f) return false;
                TestBot.Assert("client: bot server screen opened on the joiner", sel != null);
                if (!sel) return true;
                foldersBefore = WorkerScriptManager.Instance.m_Folders.Count;
                var bot = Obj(botUid)?.GetComponent<Worker>();
                if (!bot) { TestBot.Assert("client: bot for the bot server test", false); return true; }
                sel.SetTarget(bot);
                int before = WorkerScriptManager.Instance.m_Scripts.Count;
                sel.OnUploadClicked(null);                // exactly what the Upload button does
                var added = WorkerScriptManager.Instance.m_Scripts.Count > before ? WorkerScriptManager.Instance.m_Scripts[WorkerScriptManager.Instance.m_Scripts.Count - 1] : null;
                if (added != null) { added.m_Name = "CoopTestScript"; newScriptUid = added.m_UID; }   // what the name field writes
                sel.OnNewFolderClicked(null);
                Plugin.Log("[test] uploaded script " + newScriptUid + ", folders " + foldersBefore + " -> " + WorkerScriptManager.Instance.m_Folders.Count);
                return true;
            });
            s.Add(el => { if (el < 1.5f) return false; GameStateManager.Instance.PopState(); return true; });
            s.Add(el =>
            {
                var r = Poll(el, "lib", botUid, x => HasName(x["names"], "CoopTestScript#" + newScriptUid) && x["folders"].AsInt == foldersBefore + 1 && x["link"].AsInt == newScriptUid && !x["engaged"].AsBool);
                if (r == null) return false;
                TestBot.Assert("host: joiner's uploaded script, rename, new folder and link reached the host", r == true, Reply?.ToString());
                HostReq("test_hostscript"); return true;
            });
            s.Add(el =>
            {
                bool ok = false; foreach (var sc in WorkerScriptManager.Instance.m_Scripts) if (sc.m_Name == "HostScript") ok = true;
                if (!ok && el < 10f) return false;
                TestBot.Assert("client: script the host stored shows in the joiner's library", ok);
                return true;
            });
            // delete the joiner's script from the bot server (no building needed: the bot database hotkey path)
            s.Add(el =>
            {
                var bot = Obj(botUid)?.GetComponent<Worker>();
                if (BotServer.m_FirstBotServer == null || !bot) { TestBot.Assert("client: bot server for delete", false); return true; }
                GameStateManager.Instance.SetState(GameStateManager.State.Normal);
                GameStateManager.Instance.StartSelectBuilding(BotServer.m_FirstBotServer, ObjectType.BotServer);
                GameStateManager.Instance.GetCurrentState().GetComponent<GameStateBuildingSelect>().m_Menu.GetComponent<BotServerSelectLayout>().SetTarget(bot);
                return true;
            });
            s.Add(el =>
            {
                var sel = Library.Open();
                if (!sel && el < 10f) return false;
                if (!sel) { TestBot.Assert("client: bot database opened", false); return true; }
                if (el < 1f) return false;
                StoredScript sc = null; foreach (var x in WorkerScriptManager.Instance.m_Scripts) if (x.m_UID == newScriptUid) sc = x;
                var row = sc != null ? sel.GetStoredFromObject(sc) : null;
                if (row) sel.Delete(row);
                Plugin.Log("[test] deleted script " + newScriptUid + " row " + (row != null));
                return true;
            });
            s.Add(el => { if (el < 1.5f) return false; GameStateManager.Instance.PopState(); return true; });
            s.Add(el =>
            {
                var r = Poll(el, "lib", botUid, x => !HasName(x["names"], "CoopTestScript#") && x["link"].AsInt == -1);
                if (r == null) return false;
                TestBot.Assert("host: joiner deleted the script and the bot was unlinked", r == true, Reply?.ToString());
                return true;
            });
            // 18. catapult: engage, aim, confirm -> the host's catapult gets the target
            s.Add(el => { builtUid = -1; Reply = null; HostReq("test_build", n => n["t"] = "Catapult"); return true; });
            s.Add(el =>
            {
                if (Got("built") && builtUid < 0) builtUid = Reply["b"].AsInt;
                var b = Obj(builtUid);
                if (!b && el < 15f) return false;
                TestBot.Assert("client: host-built catapult visible", b != null);
                if (b) Click(b.GetComponent<Building>().GetAccessPosition(), b, AFO.AT.Primary);
                return true;
            });
            s.Add(el =>
            {
                bool open = GameStateManager.Instance.GetActualState() == GameStateManager.State.SetTargetTile;
                if (!open && el < 40f) return false;
                TestBot.Assert("client: catapult aiming screen opened on the joiner", open, "state " + GameStateManager.Instance.GetActualState());
                if (!open) return true;
                var c = Obj(builtUid).GetComponent<Catapult>();
                var cur = c.GetTargetTile(); bool found = false;
                for (int r = 3; r < 12 && !found; r++)
                    foreach (var d in new[] { new TileCoord(r, 0), new TileCoord(-r, 0), new TileCoord(0, r), new TileCoord(0, -r) })
                    {
                        var t = new TileCoord(c.m_TileCoord.x + d.x, c.m_TileCoord.y + d.y);
                        if (t != cur && TileManager.Instance.GetTile(t) != null && c.GetIsTileAcceptable(t)) { catTarget = t; found = true; break; }
                    }
                if (!found) { TestBot.Assert("client: found a catapult target", false); return true; }
                c.SetTargetTile(catTarget);                                       // what hovering does
                GameStateManager.Instance.GetCurrentState().GetComponent<GameStateSetTargetTile>().Close(false);   // what the click does
                Plugin.Log("[test] catapult aimed at " + catTarget.x + "," + catTarget.y);
                return true;
            });
            s.Add(el =>
            {
                var r = Poll(el, "cat", builtUid, x => x["x"].AsInt == catTarget.x && x["y"].AsInt == catTarget.y && !x["engaged"].AsBool);
                if (r == null) return false;
                TestBot.Assert("host: catapult target set by the joiner, joiner let go", r == true, Reply?.ToString());
                return true;
            });
            // 19. spaceport: engage, accept a mission, decline another
            s.Add(el => { builtUid = -1; Reply = null; HostReq("test_build", n => n["t"] = "SpacePort"); return true; });
            s.Add(el =>
            {
                if (Got("built") && builtUid < 0) builtUid = Reply["b"].AsInt;
                var b = Obj(builtUid);
                if (!b && el < 15f) return false;
                TestBot.Assert("client: host-built spaceport visible", b != null);
                if (b) Click(b.GetComponent<Building>().GetAccessPosition(), b, AFO.AT.Primary);
                return true;
            });
            s.Add(el =>
            {
                bool open = GameStateManager.Instance.GetActualState() == GameStateManager.State.SetSpacePort;
                var om = OffworldMissionsManager.Instance;
                if ((!open || om.m_RegularMissionsAvailable.Count < 2) && el < 40f) return false;
                TestBot.Assert("client: spaceport screen opened on the joiner", open, "state " + GameStateManager.Instance.GetActualState() + " missions " + om.m_RegularMissionsAvailable.Count);
                if (!open || om.m_RegularMissionsAvailable.Count < 2) return true;
                missionPick = om.m_RegularMissionsAvailable[0].m_ID; missionDrop = om.m_RegularMissionsAvailable[1].m_ID;
                om.DiscardRegularMission(om.m_RegularMissionsAvailable[1]);     // Decline -> Confirm
                om.SelectMission(om.m_RegularMissionsAvailable[0]);              // Accept
                GameStateManager.Instance.PopState();
                return true;
            });
            s.Add(el =>
            {
                var r = Poll(el, "sp", builtUid, x =>
                {
                    bool dropped = true; foreach (JSONNode i in x["ids"].AsArray) if (i.AsInt == missionDrop) dropped = false;
                    return x["sel"].AsInt == missionPick && dropped && !x["engaged"].AsBool;
                });
                if (r == null) return false;
                TestBot.Assert("host: joiner accepted one mission and declined another", r == true, Reply?.ToString() + " pick " + missionPick + " drop " + missionDrop);
                return true;
            });
            s.Add(el =>
            {
                var om = OffworldMissionsManager.Instance;
                bool ok = om.m_SelectedMission != null && om.m_SelectedMission.m_ID == missionPick;
                if (!ok && el < 10f) return false;
                TestBot.Assert("client: accepted mission shows on the joiner", ok);
                return true;
            });
            // 20. planning mode: the joiner draws and names an area, the host sees it; the host's area shows for the joiner
            s.Add(el =>
            {
                GameStateManager.Instance.SetState(GameStateManager.State.Edit);
                GameStateManager.Instance.PushState(GameStateManager.State.Planning);
                return true;
            });
            s.Add(el =>
            {
                bool inPlan = PlanAreas.InPlanning();
                if (!inPlan && el < 5f) return false;
                TestBot.Assert("client: in planning mode", inPlan, "state " + GameStateManager.Instance.GetActualState());
                var t = Tile(Me());
                var a = PlanningManager.Instance.NewArea(new TileCoord(t.x + 2, t.y + 2), new TileCoord(t.x + 5, t.y + 4));   // drag release
                a.SetName("CoopPlan");                                                                                  // name field
                return true;
            });
            s.Add(el => { if (el < 1f) return false; GameStateManager.Instance.SetState(GameStateManager.State.Normal); return true; });
            s.Add(el =>
            {
                var r = Poll(el, "plan", -1, x => HasName(x["names"], "CoopPlan"));
                if (r == null) return false;
                TestBot.Assert("host: joiner's planning area reached the host", r == true, Reply?.ToString());
                HostReq("test_hostplan"); return true;
            });
            s.Add(el =>
            {
                bool ok = false;
                foreach (var ar in Traverse.Create(PlanningManager.Instance).Field("m_PlanningArea").GetValue<List<PlanningArea>>()) if (ar.m_Name == "HostPlan") ok = true;
                if (!ok && el < 10f) return false;
                TestBot.Assert("client: host's planning area shows for the joiner", ok, PlanAreas.Count() + " areas");
                return true;
            });
            // 21. undo in edit mode: place a blueprint, undo it; delete one, undo brings it back
            s.Add(el => { GameStateManager.Instance.SetState(GameStateManager.State.Edit); return true; });
            s.Add(el =>
            {
                if (el < 1f) return false;
                undoTile = EmptyTile(Walkable(Tile(Me()), 3, -3));
                BuildingManager.Instance.AddBuilding(undoTile, ObjectType.Workbench, 0, false, null, false, false, false);   // what a click does
                return true;
            });
            s.Add(el =>
            {
                bool ok = BuildingAt(undoTile) != null && Build.UndoAvailable;
                if (!ok && el < 10f) return false;
                TestBot.Assert("client: placed blueprint, undo offered", ok, "building " + (BuildingAt(undoTile) != null) + " undo " + Build.UndoAvailable);
                GameStateEdit.Instance.Undo();
                return true;
            });
            s.Add(el =>
            {
                bool ok = BuildingAt(undoTile) == null;
                if (!ok && el < 10f) return false;
                TestBot.Assert("client: undo removed my blueprint (via host)", ok);
                BuildingManager.Instance.AddBuilding(undoTile, ObjectType.Workbench, 0, false, null, false, false, false);
                return true;
            });
            s.Add(el =>
            {
                var b = BuildingAt(undoTile);
                if (!b && el < 10f) return false;
                if (b) BuildingManager.Instance.DestroyBuilding(b, true);
                return true;
            });
            s.Add(el =>
            {
                if (BuildingAt(undoTile) != null && el < 10f) return false;
                if (el < 1f) return false;
                GameStateEdit.Instance.Undo();
                return true;
            });
            s.Add(el =>
            {
                bool ok = BuildingAt(undoTile) != null;
                if (!ok && el < 10f) return false;
                TestBot.Assert("client: undo brought back the deleted blueprint", ok, "undos " + Build.UndoSent);
                GameStateManager.Instance.SetState(GameStateManager.State.Normal);
                return true;
            });
            // 22. bot groups: make a group, rename/recolour it, call its bots, see the host's group, delete it
            s.Add(el =>
            {
                var bot = Obj(botUid)?.GetComponent<Worker>();
                if (!bot) { TestBot.Assert("client: bot for groups", false); return true; }
                int before = WorkerGroupManager.Instance.m_Groups.Count;
                TabWorkers.Instance.NewGroup(new List<Worker> { bot });                 // Group button
                var g = WorkerGroupManager.Instance.m_Groups.Count > before ? WorkerGroupManager.Instance.m_Groups[WorkerGroupManager.Instance.m_Groups.Count - 1] : null;
                if (g != null) { g.m_Name = "CoopGroup"; g.SetColour(new Color(0.9f, 0.2f, 0.2f)); groupId = g.m_ID; }   // name field, palette
                Plugin.Log("[test] made group " + groupId);
                return true;
            });
            s.Add(el =>
            {
                var r = Poll(el, "grp", -1, x => HasName(x["names"], "CoopGroup#" + groupId + ":" + botUid));
                if (r == null) return false;
                TestBot.Assert("host: joiner's new group (name, bot) reached the host", r == true, Reply?.ToString());
                var eg = Resources.FindObjectsOfTypeAll<EditGroup>();
                if (eg.Length > 0 && G(groupId) != null) AccessTools.Method(typeof(EditGroup), "CallAllChildren").Invoke(eg[0], new object[] { G(groupId) });   // Call button
                return true;
            });
            s.Add(el =>
            {
                var r = Poll(el, "grp", -1, x => (string)x["lastcmd"] == "call", 10f);
                if (r == null) return false;
                TestBot.Assert("host: group Call from the joiner ran on the host", r == true, Reply?.ToString());
                HostReq("test_hostgroup"); return true;
            });
            s.Add(el =>
            {
                bool ok = false; foreach (var g in WorkerGroupManager.Instance.m_Groups) if (g.m_Name == "HostGroup") ok = true;
                if (!ok && el < 10f) return false;
                TestBot.Assert("client: host's new group shows for the joiner", ok);
                var mine = G(groupId);
                if (mine != null) TabWorkers.Instance.RemoveGroup(mine);
                return true;
            });
            s.Add(el =>
            {
                var r = Poll(el, "grp", -1, x => !HasName(x["names"], "CoopGroup#") && HasName(x["names"], "HostGroup"));
                if (r == null) return false;
                TestBot.Assert("host: joiner deleted their group, host's group kept", r == true, Reply?.ToString());
                return true;
            });
            // 23. bug report: one zip with my logs and the host's
            s.Add(el => { BugReport.LastPath = null; BugReport.Begin(); return true; });
            s.Add(el =>
            {
                if (BugReport.LastPath == null && el < 15f) return false;
                bool ok = BugReport.LastPath != null && System.IO.File.Exists(BugReport.LastPath);
                string body = ok ? System.Text.Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(BugReport.LastPath)) : "";
                ok = ok && body.Contains("info.txt") && body.Contains("coop.log") && body.Contains("host-coop.log") && body.Contains("joined the game") && !body.Contains("the host did not answer");
                TestBot.Assert("client: bug report zip has my logs and the host's", ok, BugReport.LastPath ?? "none");
                // diagnostics: both sides' logs in the zip, with my actions, what the host did with them, and lag
                bool diag = body.Contains("coop_diag.log") && body.Contains("host-coop_diag.log") && body.Contains("J send {\"a\":\"go\"") && body.Contains("H   go ") && body.Contains("stats 10s");
                TestBot.Assert("client: diagnostics logs (mine and the host's) are in the bug report", diag);
                var h = Coop.I.Client.Host;
                TestBot.Assert("client: round-trip time measured", h.Rtt > 0f && h.Rtt < 30f, (h.Rtt * 1000).ToString("F0") + " ms" + (NetSim.On ? " (simulated " + NetSim.LagMs + "+" + NetSim.JitterMs + " ms each way)" : ""));
                if (NetSim.On) TestBot.Assert("client: simulated lag shows in the round trip", h.Rtt * 1000f >= NetSim.LagMs * 2 * 0.9f, (h.Rtt * 1000).ToString("F0") + " ms");
                Diag.Mark("test");
                HostReq("test_bugreport"); Reply = null;
                return true;
            });
            s.Add(el =>
            {
                if (!Got("bug") && el < 10f) return false;
                TestBot.Assert("host: bug report written on the host", Got("bug") && ((string)Reply["path"]).EndsWith(".zip"), Reply?.ToString());
                TestBot.Assert("host: the joiner's F7 mark reached the host's diagnostics log", Got("bug") && Reply["marks"].AsInt >= 1, Reply?.ToString());
                return true;
            });
            // 24. joiner saves: autosave is skipped, Save As writes my own copy
            s.Add(el =>
            {
                autosBefore = MyFile.GetAllSaveNames(true, false).Count;
                AccessTools.Method(typeof(SaveLoadManager), "DoAutosave").Invoke(SaveLoadManager.Instance, null);
                int after = MyFile.GetAllSaveNames(true, false).Count;
                TestBot.Assert("client: joiner autosave is skipped", after == autosBefore && JoinerSaves.AutosavesSkipped >= 1, autosBefore + " -> " + after);
                bool saved = SaveLoadManager.Instance.Save("coop_joiner_copy");
                copyPlayer = Sync.MyPlayerUID; copyObjs = Coop.ObjectCount();
                TestBot.Assert("client: joiner can Save As a copy of the world", saved && SaveLoadManager.Instance.DoesFileExist("coop_joiner_copy"));
                return true;
            });
            // 15. host saves, reloads the world and hosts again; I rejoin and get my character and pockets back
            if (TestBot.Transport == "tcp")
            {
                // leave (connection drops), the host builds where I stood; rejoin next to the host with everything
                s.Add(el =>
                {
                    seatUid = Sync.MyPlayerUID; seatStuff.Clear();
                    Sync.CollectUIDs(Sync.SaveObject(Me(), out _), seatUid, seatStuff); seatStuff.Sort();
                    leaveTile = Tile(Me());
                    HostReq("test_blockspot");
                    Plugin.Log("[test] leaving: me " + seatUid + " at " + leaveTile.x + "," + leaveTile.y + " holding [" + string.Join(",", seatStuff.ConvertAll(x => x.ToString()).ToArray()) + "]");
                    return true;
                });
                s.Add(el => { if (el < 1.5f) return false; Coop.I.Client.Host.Close(); return true; });
                s.Add(el =>
                {
                    bool ok = Coop.Role == Role.Off && TestBot.AtMainMenu();
                    if (!ok && el < 30f) return false;
                    TestBot.Assert("client: lost connection -> menu with \"" + Session.LostMsg + "\"", ok && Session.LastDisconnect == Session.LostMsg, Session.LastDisconnect ?? "");
                    return true;
                });
                s.Add(el =>
                {
                    if (el < 6f || UnityEngine.Time.realtimeSinceStartup < rejoinTry) return false;
                    rejoinTry = UnityEngine.Time.realtimeSinceStartup + 3f;
                    try { Session.LastDisconnect = null; Coop.I.StartClient(TestBot.Ip, TestBot.Port); return true; }
                    catch (System.Exception e) { Plugin.Log("rejoin retry: " + e.Message); return false; }
                });
                s.Add(el =>
                {
                    if (Coop.Role == Role.Off) { TestBot.Assert("client: rejoined after leaving", false, Session.LastDisconnect ?? "dropped"); return true; }
                    return Coop.I.ClientLoaded && Obj(Sync.MyPlayerUID) && el > 3f;
                });
                s.Add(el =>
                {
                    var r = Poll(el, "pk", -1, x => x["parked"].AsInt >= 1 && x["restored"].AsInt >= 1, 10f);
                    if (r == null) return false;
                    var now = new List<int>(); Sync.CollectUIDs(Sync.SaveObject(Me(), out _), Sync.MyPlayerUID, now); now.Sort();
                    var host = Obj(hostUid) ?? CollectionManager.Instance.GetPlayers().Find(p => p && p.m_UniqueID != Sync.MyPlayerUID);
                    var at = Tile(Me());
                    TestBot.Assert("host: joiner's character left the world while away", Reply != null && Reply["players"].AsInt == 1, Reply?.ToString());
                    TestBot.Assert("host: 'left the game' and 'joined the game' notifications", Reply != null && Reply["left"].AsBool && Reply["joined"].AsBool);
                    TestBot.Assert("client: same character back after leaving", Sync.MyPlayerUID == seatUid);
                    TestBot.Assert("client: hands/pockets/upgrades/clothes back after leaving", string.Join(",", now.ConvertAll(x => x.ToString()).ToArray()) == string.Join(",", seatStuff.ConvertAll(x => x.ToString()).ToArray()), "now [" + string.Join(",", now.ConvertAll(x => x.ToString()).ToArray()) + "]");
                    TestBot.Assert("client: spot was built on -> back next to the host", Reply != null && Reply["relocated"].AsInt >= 1 && host && Dist(at, Tile(host)) <= 3 && (at.x != leaveTile.x || at.y != leaveTile.y),
                        "at " + at.x + "," + at.y + " left " + leaveTile.x + "," + leaveTile.y + " host " + (host ? Tile(host).x + "," + Tile(host).y : "?"));
                    relocAtLeave = Reply != null ? Reply["relocated"].AsInt : -1;
                    return true;
                });

                s.Add(el =>
                {
                    seatUid = Sync.MyPlayerUID; seatStuff.Clear();
                    Sync.CollectUIDs(Sync.SaveObject(Me(), out _), seatUid, seatStuff); seatStuff.Sort();
                    reloadTile = Tile(Me());
                    Plugin.Log("[test] before reload: me " + seatUid + " holding [" + string.Join(",", seatStuff.ConvertAll(x => x.ToString()).ToArray()) + "]");
                    HostReq("test_savereload"); return true;
                });
                s.Add(el =>
                {
                    bool ok = Coop.Role == Role.Off && TestBot.AtMainMenu();
                    if (!ok && el < 60f) return false;
                    TestBot.Assert("client: host reload sent me to the menu", ok, Session.LastDisconnect ?? "");
                    return true;
                });
                s.Add(el =>
                {
                    if (el < 5f || UnityEngine.Time.realtimeSinceStartup < rejoinTry) return false;
                    rejoinTry = UnityEngine.Time.realtimeSinceStartup + 3f;
                    try { Session.LastDisconnect = null; Coop.I.StartClient(TestBot.Ip, TestBot.Port); return true; }
                    catch (System.Exception e) { Plugin.Log("rejoin retry: " + e.Message); return false; }
                });
                s.Add(el =>
                {
                    if (Coop.Role == Role.Off) { TestBot.Assert("client: rejoined after the host reloaded", false, Session.LastDisconnect ?? "dropped"); return true; }
                    return Coop.I.ClientLoaded && Obj(Sync.MyPlayerUID) && el > 3f;
                });
                s.Add(el =>
                {
                    var now = new List<int>();
                    Sync.CollectUIDs(Sync.SaveObject(Me(), out _), Sync.MyPlayerUID, now); now.Sort();
                    int players = CountType(ObjectType.FarmerPlayer);
                    TestBot.Assert("client: rejoined after the host reloaded", Coop.I.ClientLoaded);
                    TestBot.Assert("client: got my old character back after reload", Sync.MyPlayerUID == seatUid && Me().m_UniqueID == seatUid, "now " + Sync.MyPlayerUID + " was " + seatUid);
                    TestBot.Assert("client: my pockets/hands survived the reload", string.Join(",", now.ConvertAll(x => x.ToString()).ToArray()) == string.Join(",", seatStuff.ConvertAll(x => x.ToString()).ToArray()),
                        "now [" + string.Join(",", now.ConvertAll(x => x.ToString()).ToArray()) + "]");
                    TestBot.Assert("client: no duplicate character after reload", players == 2, players + " FarmerPlayers");
                    var at = Tile(Me());
                    TestBot.Assert("client: back where I stood before the reload (spot was free)", at.x == reloadTile.x && at.y == reloadTile.y, "at " + at.x + "," + at.y + " was " + reloadTile.x + "," + reloadTile.y);
                    return true;
                });
            }
            // 13. host leaves: the joiner gets a message and goes back to the main menu
            s.Add(el => { HostReq("test_hostquit"); return true; });
            s.Add(el =>
            {
                bool ok = Coop.Role == Role.Off && TestBot.AtMainMenu() && Session.LastDisconnect != null;
                if (!ok && el < 40f) return false;
                TestBot.Assert("client: host left -> joiner back at the main menu with \"" + Session.EndedMsg + "\"", ok && Session.LastDisconnect == Session.EndedMsg, Session.LastDisconnect ?? "no message");
                TestBot.Assert("client: old lobby forgotten, so I can host next", SteamNet.Lobby == Steamworks.CSteamID.Nil && SteamNet.HostId == 0);
                return true;
            });
            // 25. load my own saved copy in single player: I play my character, nothing local leaked into it
            s.Add(el =>
            {
                if (el < 3f) return false;
                Session.LastDisconnect = null;
                SessionManager.Instance.m_LoadFileName = "coop_joiner_copy";
                SessionManager.Instance.LoadLevel(true, "Main");
                return true;
            });
            s.Add(el =>
            {
                bool ok = Coop.InGame() && GameStateManager.Instance.GetActualState() == GameStateManager.State.Normal && el > 8f;
                if (!ok && el < 90f) return false;
                int local = 0; foreach (var kv in Sync.Savables()) if (kv.Key && kv.Key.m_UniqueID >= Sync.LocalUidBase) local++;
                int objs = Coop.ObjectCount();
                TestBot.Assert("client: my saved copy loads in single player", ok);
                TestBot.Assert("client: in my copy I play my own character", ok && Me().m_UniqueID == copyPlayer, "player0 " + (ok ? Me().m_UniqueID : -1) + " want " + copyPlayer);
                TestBot.Assert("client: copy has the world and no joiner-local objects", ok && local == 0 && System.Math.Abs(objs - copyObjs) <= copyObjs / 50 + 5, "objects " + objs + " saved " + copyObjs + " local " + local);
                return true;
            });
        }
    }
}

