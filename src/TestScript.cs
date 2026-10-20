// AutonautsCoop - TestScript.cs - the QA scenario steps (driven through the real input path)
// Version 0.1.0 - 2026-10-20 - Created with Claude
using System.Collections.Generic;
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
        static TileCoord Walkable(TileCoord from, int dx, int dy)
        {
            for (int r = 0; r < 6; r++)
            {
                var t = new TileCoord(from.x + dx + r, from.y + dy);
                var tile = TileManager.Instance.GetTile(t);
                if (tile != null && !global::Tile.GetIsSolid(tile.m_TileType) && PlotManager.Instance.GetPlotAtTile(t).GetObjectTypeAtTile(ObjectType.Rock, t) == null) return t;
            }
            return Sync.FreeTileNear(new TileCoord(from.x + dx, from.y + dy));
        }

        static int axeUid = -1, treeUid = -1, logsBefore;
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
            s.Add(el => { Coop.I.StartHost(TestBot.Port); return true; });
            s.Add(el => { lock (Coop.I.Host.Peers) return Coop.I.Host.Peers.Exists(p => p.Name != "?") && el > 1f; });
            s.Add(el => { TestBot.Assert("host: client joined", true); return true; });
            s.Add(el => HostActRequested);
            s.Add(el => { HostAct(); return true; });
            s.Add(el => { if (el > 6f) { Plugin.Log("[test] host now at " + Tile(Me()).x + "," + Tile(Me()).y + " state " + Me().m_State); return true; } return false; });
            s.Add(el => el > 230f || ClientGone());
            s.Add(el =>
            {
                Plugin.Log("[test] host scan ms " + Sync.LastScanMs.ToString("F1"));
                return true;
            });
        }

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
        public static JSONNode TileTest; public static JSONNode Chk;

        public static void BuildClient(List<TestBot.IEnumeratorStep> s)
        {
            s.Add(el => TestBot.AtMainMenu() && el > 8f);
            s.Add(el =>
            {
                if (el < 2f) return false;
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
            // 7. whole-world consistency after things settle
            s.Add(el => el > 25f);   // covers at least two anti-entropy rounds
            s.Add(el => { Chk = null; var n = new JSONObject(); n["a"] = "chk"; Coop.I.Client.Host.Send(Msg.Input, n.ToString()); return true; });
            s.Add(el =>
            {
                if (Chk == null && el < 10f) return false;
                Checksum(out int c, out long sum);
                bool ok = Chk != null && Chk["count"].AsInt == c && Chk["sum"].Value == sum.ToString();
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
                Plugin.Log("[test] stats inplace " + Sync.StatInPlace + " recreated " + Sync.StatRecreated + " created " + Sync.StatCreated + " removed " + Sync.StatRemoved + " batches " + Sync.StatBatches + " resync " + Sync.StatResync + " errors " + Sync.StatErrors + " digests " + Sync.StatDigests + " digestfixes " + Sync.StatDigestFixes);
                foreach (var kv in Sync.RecreatedByType) Plugin.Log("[test]   recreated " + kv.Key + " x" + kv.Value);
                return true;
            });
        }
    }
}
