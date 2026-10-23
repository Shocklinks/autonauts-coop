// AutonautsCoop - Multi.cs - three-player test (COOP_MULTI=1): host + a game joiner + test/protobot.py (a second
// joiner speaking the protocol), both named "player" like two friends joining by IP without Steam. They must get
// different names, see each other act, and the bot's leave/rejoin must not touch the game joiner.
// Version 0.11.0 - 2026-10-21 - Created with Claude
using System;
using System.Collections.Generic;
using SimpleJSON;
using UnityEngine;

namespace AutonautsCoop
{
    public static class Multi
    {
        public static bool On = Environment.GetEnvironmentVariable("COOP_MULTI") == "1";
        static bool Second => TestBot.Mode == "client2";

        // ---------------- host: barrier + end ----------------
        static readonly Dictionary<int, HashSet<Peer>> reached = new Dictionary<int, HashSet<Peer>>();
        static int done;
        public static bool HostOp(string a, JSONNode n, Peer from)
        {
            if (a == "mb")
            {
                int k = n["k"].AsInt;
                if (!reached.TryGetValue(k, out var set)) reached[k] = set = new HashSet<Peer>();
                set.Add(from);
                var r = new JSONObject(); r["tr"] = "mb"; r["k"] = k; r["n"] = set.Count;
                from.Send(Msg.Info, r.ToString());
                return true;
            }
            if (a == "multi_done") { done++; Plugin.Log("[test] multi: joiner done (" + done + ")"); return true; }
            return false;
        }

        public static void BuildHost(List<TestBot.IEnumeratorStep> s)
        {
            s.Add(el => TestBot.AtMainMenu() && el > 8f);
            s.Add(el => { TestBot.StartNewGame(); return true; });
            s.Add(el => Coop.InGame() && GameStateManager.Instance.GetActualState() == GameStateManager.State.Normal && el > 10f);
            s.Add(el => GameStateManager.Instance.GetActualState() == GameStateManager.State.Normal && el > 5f);
            s.Add(el => { Coop.I.StartHost(TestBot.Port); return true; });
            s.Add(el =>
            {
                var names = new List<string>();
                lock (Coop.I.Host.Peers) foreach (var p in Coop.I.Host.Peers) if (p.Alive && p.Name != "?") names.Add(p.Name);
                if (names.Count < 2 && el < 400f) return false;
                names.Sort();
                TestBot.Assert("host: two joiners with the same name get different names", names.Count == 2 && names[0] != names[1], string.Join(" | ", names.ToArray()));
                return true;
            });
            s.Add(el => done >= 2 || el > 1200f);
            s.Add(el => { if (el < 2f) return false; Session.StopHost("test: done"); return true; });
        }

        // ---------------- joiners ----------------
        static int myUid = -1, other = -1, k; static TileCoord otherStart, bpTile; static float nextAsk, rejoinTry; static int chkTries;
        static List<int> myStuff = new List<int>();
        static BaseClass Obj(int uid) => uid < 0 ? null : ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false);
        static TileCoord Tile(BaseClass b) => b.GetComponent<TileCoordObject>().m_TileCoord;
        static FarmerPlayer Me() => CollectionManager.Instance.GetPlayers()[0].GetComponent<FarmerPlayer>();
        static void Req(string a, Action<JSONObject> f = null) { var n = new JSONObject(); n["a"] = a; f?.Invoke(n); Coop.I.Client.Host.Send(Msg.Input, n.ToString()); }
        static int Players() { int c = 0; foreach (var kv in Sync.Savables()) if (kv.Key && kv.Key.m_TypeIdentifier == ObjectType.FarmerPlayer && kv.Key.GetComponent<Savable>().GetIsSavable()) c++; return c; }
        static int OtherJoiner()
        {
            foreach (var kv in Sync.Savables())
            {
                var b = kv.Key;
                if (b && b.m_TypeIdentifier == ObjectType.FarmerPlayer && b.m_UniqueID != Sync.MyPlayerUID && b.m_UniqueID != hostUid) return b.m_UniqueID;
            }
            return -1;
        }
        static int hostUid = -1;

        /// Wait for both joiners to reach the same point.
        static void Barrier(List<TestBot.IEnumeratorStep> s, int stage)
        {
            s.Add(el =>
            {
                bool ok = TestScript.Reply != null && (string)TestScript.Reply["tr"] == "mb" && TestScript.Reply["k"].AsInt == stage && TestScript.Reply["n"].AsInt >= 2;
                if (ok) return true;
                if (Time.realtimeSinceStartup > nextAsk) { nextAsk = Time.realtimeSinceStartup + 1f; Req("mb", n => n["k"] = stage); }
                if (el > 200f) { TestBot.Assert("joiner: barrier " + stage, false); return true; }
                return false;
            });
        }

        static bool Connect(float el)
        {
            if (el < 3f || Time.realtimeSinceStartup < rejoinTry) return false;
            rejoinTry = Time.realtimeSinceStartup + 3f;
            try { Session.LastDisconnect = null; Coop.I.StartClient(TestBot.Ip, TestBot.Port); return true; }
            catch (Exception e) { Plugin.Log("connect retry: " + e.Message); return false; }
        }

        public static void BuildClient(List<TestBot.IEnumeratorStep> s)
        {
            string me = Second ? "joiner B" : "joiner A";
            s.Add(el => TestBot.AtMainMenu() && el > 8f);
            s.Add(el => Connect(el + (Second ? 0 : 0)));
            s.Add(el => Coop.I.ClientLoaded && Obj(Sync.MyPlayerUID) && el > 3f);
            s.Add(el => { myUid = Sync.MyPlayerUID; var ps = CollectionManager.Instance.GetPlayers(); foreach (var p in ps) if (p && p.m_UniqueID != myUid) { hostUid = p.m_UniqueID; break; } return true; });
            Barrier(s, 1);
            // 1. both joiners see each other
            s.Add(el =>
            {
                other = OtherJoiner();
                bool ok = Players() == 3 && other >= 0;
                if (!ok && el < 30f) return false;
                TestBot.Assert(me + ": sees the host and the other joiner (3 players)", ok, Players() + " players");
                if (other >= 0) otherStart = Tile(Obj(other));
                return true;
            });
            Barrier(s, 2);
            // 2. both walk; each sees the other move
            s.Add(el =>
            {
                var t = Sync.FreeTileNear(new TileCoord(Tile(Me()).x + (Second ? -3 : 3), Tile(Me()).y + 2), true);
                Me().GoToAndAction(t, PlotManager.Instance.GetPlotAtTile(t).GetComponent<Actionable>(), AFO.AT.Primary);
                return true;
            });
            s.Add(el =>
            {
                var o = Obj(other);
                bool ok = o && (Tile(o).x != otherStart.x || Tile(o).y != otherStart.y);
                if (!ok && el < 30f) return false;
                TestBot.Assert(me + ": sees the other joiner walk", ok);
                return true;
            });
            Barrier(s, 3);
            // 3. both place a blueprint; each sees both
            s.Add(el =>
            {
                var c = Tile(Me());
                bpTile = TestScript.EmptyTile(new TileCoord(c.x - 8, c.y - 8));   // inland: the start is at the map corner
                BuildingManager.Instance.AddBuilding(bpTile, ObjectType.Workbench, 0, false, null, false, false, false);
                return true;
            });
            Barrier(s, 4);
            s.Add(el =>
            {
                int bps = 0; foreach (var kv in Sync.Savables()) if (kv.Key && kv.Key.m_TypeIdentifier == ObjectType.ConverterFoundation) bps++;
                if (bps < 2 && el < 30f) return false;
                TestBot.Assert(me + ": both joiners' blueprints are in the world", bps >= 2, bps + " blueprints");
                return true;
            });
            Barrier(s, 5);
            // 4. all three worlds match (each joiner against the host)
            s.Add(el => { TestScript.Chk = null; Req("chk"); return true; });
            s.Add(el =>
            {
                if (TestScript.Chk == null && el < 20f) return false;
                TestScript.ChecksumAtChk(out int c, out long sum);
                bool ok = TestScript.Chk != null && TestScript.Chk["count"].AsInt == c && TestScript.Chk["sum"].Value == sum.ToString();
                if (TestScript.Chk != null && !ok && chkTries++ < 5) { TestScript.Chk = null; Req("chk"); return false; }
                TestBot.Assert(me + ": world matches the host", ok, TestScript.Chk == null ? "no reply" : "host " + TestScript.Chk["count"].AsInt + " me " + c);
                return true;
            });
            Barrier(s, 6);
            // 5. joiner B (the protocol bot) drops and rejoins; A sees it leave and come back, A is untouched
            if (false)
            {
                s.Add(el =>
                {
                    myStuff.Clear(); Sync.CollectUIDs(Sync.SaveObject(Me(), out _), myUid, myStuff); myStuff.Sort();
                    Coop.I.Client.Host.Close(); return true;
                });
                s.Add(el => Coop.Role == Role.Off && TestBot.AtMainMenu());
                s.Add(Connect);
                s.Add(el => Coop.I.ClientLoaded && Obj(Sync.MyPlayerUID) && el > 3f);
                s.Add(el =>
                {
                    var now = new List<int>(); Sync.CollectUIDs(Sync.SaveObject(Me(), out _), Sync.MyPlayerUID, now); now.Sort();
                    TestBot.Assert(me + ": rejoined with my own character (not joiner A's)", Sync.MyPlayerUID == myUid && Sync.MyPlayerUID != other, "now " + Sync.MyPlayerUID + " mine " + myUid + " A " + other);
                    TestBot.Assert(me + ": my things came back", string.Join(",", now.ConvertAll(x => x.ToString()).ToArray()) == string.Join(",", myStuff.ConvertAll(x => x.ToString()).ToArray()));
                    return true;
                });
            }
            else
            {
                s.Add(el =>
                {
                    bool gone = Obj(other) == null && Menu.Toasts.Exists(t => t.EndsWith("left the game"));
                    if (!gone && el < 60f) return false;
                    TestBot.Assert(me + ": joiner B left (character gone, notice shown)", gone, Players() + " players");
                    return true;
                });
                s.Add(el =>
                {
                    bool back = Obj(other) != null && Menu.Toasts.Exists(t => t.EndsWith("joined the game"));
                    if (!back && el < 120f) return false;
                    TestBot.Assert(me + ": joiner B came back (same character, notice shown)", back);
                    TestBot.Assert(me + ": still my own character", Sync.MyPlayerUID == myUid);
                    return true;
                });
            }
            Barrier(s, 7);
            s.Add(el => { Req("multi_done"); return true; });
            s.Add(el =>
            {
                bool ok = Coop.Role == Role.Off && Session.LastDisconnect == Session.EndedMsg;
                if (!ok && el < 60f) return false;
                TestBot.Assert(me + ": host ended -> \"" + Session.EndedMsg + "\"", ok, Session.LastDisconnect ?? "");
                return true;
            });
        }
    }
}
