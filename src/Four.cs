// AutonautsCoop - Four.cs - four-player test (COOP_FOUR=1): a host and three game joiners on one PC (test kit,
// mode=four). New world, or the host loads a save (COOP_FOUR_SAVE=<name>). Joiners: COOP_TEST=client1|client2|client3.
// Version 0.12.37 - 2026-10-26 - Created with Claude
//
// What it checks, with every joiner a real game:
//   - three joiners joining within seconds of each other (overlapping snapshots), all with the same name
//   - everyone sees everyone (4 players), walks, and sees the other joiners walk
//   - each joiner places a blueprint; everyone sees all three
//   - each joiner's world matches the host's (twice: after the edits, and after a rejoin)
//   - a watch with all four in the world: frame rates, joiner rebuilds (a rebuild storm fails the test)
//   - joiner 3 drops and rejoins: same character back; joiners 1 and 2 see it leave and come back, untouched
//   - the host ends the session: every joiner gets "Host has ended the lobby."
using System;
using System.Collections.Generic;
using SimpleJSON;
using UnityEngine;

namespace AutonautsCoop
{
    public static class Four
    {
        public static bool On = Environment.GetEnvironmentVariable("COOP_FOUR") == "1";
        static readonly string SaveName = Environment.GetEnvironmentVariable("COOP_FOUR_SAVE") ?? "";
        static bool OnSave => SaveName != "";
        const int Joiners = 3;
        static float WatchSeconds = float.TryParse(Environment.GetEnvironmentVariable("COOP_FOUR_WATCH"), out var v) ? v : (OnSave ? 120f : 60f);
        /// 1, 2 or 3 (from COOP_TEST=clientN)
        static int Index => TestBot.Mode != null && TestBot.Mode.StartsWith("client") && int.TryParse(TestBot.Mode.Substring(6), out int i) ? i : 1;
        static string Me => "joiner " + Index;

        // ---------------- frame stats ----------------
        static readonly List<float> frames = new List<float>();
        static void Frame() { if (!Diag.InTestWork()) frames.Add(Time.unscaledDeltaTime); }
        static string Fps()
        {
            if (frames.Count == 0) return "-";
            float sum = 0, worst = 0; foreach (var f in frames) { sum += f; if (f > worst) worst = f; }
            var sorted = new List<float>(frames); sorted.Sort();
            return (frames.Count / sum).ToString("F0") + " fps avg, 95% of frames under " + (sorted[(int)(sorted.Count * 0.95f)] * 1000).ToString("F0") + " ms, worst " + (worst * 1000).ToString("F0") + " ms";
        }
        static void Info(string what, string detail) { TestBot.Assert("info: " + what, true, detail); }

        // ---------------- host ----------------
        static readonly Dictionary<int, HashSet<Peer>> reached = new Dictionary<int, HashSet<Peer>>();
        static int done; static float hostStart; static double scanSum, scanMax; static int scanN;
        public static bool HostOp(string a, JSONNode n, Peer from)
        {
            if (a == "f4b")
            {
                int k = n["k"].AsInt;
                if (!reached.TryGetValue(k, out var set)) reached[k] = set = new HashSet<Peer>();
                set.Add(from);
                var r = new JSONObject(); r["tr"] = "f4b"; r["k"] = k; r["n"] = set.Count;
                from.Send(Msg.Info, r.ToString());
                return true;
            }
            if (a == "f4done") { done++; Plugin.Log("[test] four: " + from.Name + " done (" + done + ")"); return true; }
            return false;
        }

        static List<string> PeerNames()
        {
            var names = new List<string>();
            lock (Coop.I.Host.Peers) foreach (var p in Coop.I.Host.Peers) if (p.Alive && p.Name != "?") names.Add(p.Name);
            names.Sort();
            return names;
        }

        public static void BuildHost(List<TestBot.IEnumeratorStep> s)
        {
            s.Add(el => TestBot.AtMainMenu() && el > 8f);
            if (OnSave) Soak.AddLoadSave(s, SaveName);
            else s.Add(el => { TestBot.StartNewGame(); return true; });
            s.Add(el => Coop.InGame() && GameStateManager.Instance.GetActualState() == GameStateManager.State.Normal && el > (OnSave ? 5f : 10f));
            s.Add(el =>
            {
                Info("world", (OnSave ? "save \"" + SaveName + "\"" : "new world") + ": " + Coop.ObjectCount() + " objects");
                Coop.I.StartHost(TestBot.Port); hostStart = Time.realtimeSinceStartup; frames.Clear();
                return true;
            });
            s.Add(el =>
            {
                Frame();
                var names = PeerNames();
                if (names.Count < Joiners && el < (OnSave ? 900f : 400f)) return false;
                TestBot.Assert("host: three joiners connected", names.Count == Joiners, names.Count + " connected");
                TestBot.Assert("host: three joiners with the same name get different names", names.Count == Joiners && new HashSet<string>(names).Count == Joiners, string.Join(" | ", names.ToArray()));
                Info("host: all joined", "after " + (Time.realtimeSinceStartup - hostStart).ToString("F0") + " s");
                return true;
            });
            s.Add(el =>
            {
                Frame();
                if (Sync.LastScanMs > 0) { scanSum += Sync.LastScanMs; scanN++; if (Sync.LastScanMs > scanMax) scanMax = Sync.LastScanMs; }
                return done >= Joiners || el > 1500f;
            });
            s.Add(el =>
            {
                TestBot.Assert("host: all three joiners finished", done >= Joiners, done + " of " + Joiners);
                Info("host with three joiners", Fps());
                Info("host scan", (scanN > 0 ? scanSum / scanN : 0).ToString("F1") + " ms avg, " + scanMax.ToString("F0") + " ms max; sent " + (Peer.BytesSent / 1048576f).ToString("F1") + " MB");
                return true;
            });
            s.Add(el => { if (el < 2f) return false; Session.StopHost("test: done"); return true; });
        }

        // ---------------- joiners ----------------
        static int myUid = -1, baseBps; static float nextAsk, connectTry, connectAt; static int chkTries;
        static readonly Dictionary<int, TileCoord> otherStart = new Dictionary<int, TileCoord>();
        static List<int> myStuff = new List<int>();
        static int rebuilt0; static float watchStart;
        static BaseClass Obj(int uid) => uid < 0 ? null : ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false);
        static TileCoord Tile(BaseClass b) => b.GetComponent<TileCoordObject>().m_TileCoord;
        static FarmerPlayer MeObj() { var o = Obj(Sync.MyPlayerUID); return o ? o.GetComponent<FarmerPlayer>() : null; }
        static void Req(string a, Action<JSONObject> f = null) { var n = new JSONObject(); n["a"] = a; f?.Invoke(n); Coop.I.Client.Host.Send(Msg.Input, n.ToString()); }
        static int Players() { int c = 0; foreach (var kv in Sync.Savables()) if (kv.Key && kv.Key.m_TypeIdentifier == ObjectType.FarmerPlayer && kv.Key.GetComponent<Savable>().GetIsSavable()) c++; return c; }
        /// Everyone else's character (the host's included: a joiner can't tell which one is the host's).
        static List<int> OtherPlayers()
        {
            var l = new List<int>();
            foreach (var kv in Sync.Savables())
            {
                var b = kv.Key;
                if (b && b.m_TypeIdentifier == ObjectType.FarmerPlayer && b.GetComponent<Savable>().GetIsSavable() && b.m_UniqueID != Sync.MyPlayerUID) l.Add(b.m_UniqueID);
            }
            return l;
        }
        static int Blueprints() { int n = 0; foreach (var kv in Sync.Savables()) if (kv.Key && kv.Key.m_TypeIdentifier == ObjectType.ConverterFoundation) n++; return n; }
        static int Rebuilt() { int n = 0; foreach (var kv in Sync.RecreatedByType) n += kv.Value; return n; }

        /// Wait until every joiner present reaches this point (joiner 3 is away during its rejoin: not counted then).
        static void Barrier(List<TestBot.IEnumeratorStep> s, int stage)
        {
            s.Add(el =>
            {
                var r = TestScript.Reply;
                if (r != null && (string)r["tr"] == "f4b" && r["k"].AsInt == stage && r["n"].AsInt >= Joiners) return true;
                if (Time.realtimeSinceStartup > nextAsk) { nextAsk = Time.realtimeSinceStartup + 1f; Req("f4b", n => n["k"] = stage); }
                if (el > (OnSave ? 600f : 240f)) { TestBot.Assert(Me + ": everyone reached point " + stage, false); return true; }
                return false;
            });
        }

        static bool Connect(float el, float after)
        {
            if (el < after || Time.realtimeSinceStartup < connectTry) return false;
            connectTry = Time.realtimeSinceStartup + 3f;
            try { Session.LastDisconnect = null; Coop.I.StartClient(TestBot.Ip, TestBot.Port); connectAt = Time.realtimeSinceStartup; return true; }
            catch (Exception e) { Plugin.Log("connect retry: " + e.Message); return false; }
        }

        static void WorldCheck(List<TestBot.IEnumeratorStep> s, string when)
        {
            s.Add(el => { chkTries = 0; TestScript.Chk = null; Req("chk"); return true; });
            s.Add(el =>
            {
                if (TestScript.Chk == null && el < (OnSave ? 60f : 20f)) return false;
                TestScript.ChecksumAtChk(out int c, out long sum);
                bool ok = TestScript.Chk != null && TestScript.Chk["count"].AsInt == c && TestScript.Chk["sum"].Value == sum.ToString();
                // busy bots on a big save keep changing between the host's reply and ours: a few tries
                if (TestScript.Chk != null && !ok && chkTries++ < (OnSave ? 8 : 5)) { TestScript.Chk = null; Req("chk"); return false; }
                TestBot.Assert(Me + ": world matches the host (" + when + ")", ok, TestScript.Chk == null ? "no reply" : "host " + TestScript.Chk["count"].AsInt + " objects, me " + c);
                return true;
            });
        }

        public static void BuildClient(List<TestBot.IEnumeratorStep> s)
        {
            int i = Index;
            s.Add(el => TestBot.AtMainMenu() && el > 8f);
            // all three join within a few seconds of each other: their snapshots overlap on the host
            s.Add(el => Connect(el, (OnSave ? 20f : 6f) + (i - 1) * 2f));
            s.Add(el =>
            {
                if (Coop.Role == Role.Off && el > 5f) { connectTry = 0; return Connect(99f, 0f) && false; }   // refused while the host was still loading: try again
                return (Coop.I.ClientLoaded && Obj(Sync.MyPlayerUID) && GameStateManager.Instance.GetActualState() == GameStateManager.State.Normal && el > 3f) || el > 600f;
            });
            s.Add(el =>
            {
                TestBot.Assert(Me + ": joined", Coop.I.ClientLoaded, "in " + (Time.realtimeSinceStartup - connectAt).ToString("F0") + " s");
                int host = Coop.I.LastInfo != null ? Coop.I.LastInfo["objects"].AsInt : -1;
                Info(Me + ": join", "took " + (Time.realtimeSinceStartup - connectAt).ToString("F0") + " s; host " + host + " objects, here " + Coop.ObjectCount());
                myUid = Sync.MyPlayerUID;
                return true;
            });
            // 1. everyone sees everyone
            s.Add(el =>
            {
                if (Players() < 4 && el < (OnSave ? 300f : 60f)) return false;
                TestBot.Assert(Me + ": sees the host and the two other joiners (4 players)", Players() == 4 && OtherPlayers().Count == 3, Players() + " players");
                return true;
            });
            Barrier(s, 1);
            // 2. everyone walks; each sees the other joiners walk
            s.Add(el =>
            {
                otherStart.Clear(); foreach (var u in OtherPlayers()) otherStart[u] = Tile(Obj(u));
                var me = MeObj();
                var t = Sync.FreeTileNear(new TileCoord(Tile(me).x + 2 * i - 4, Tile(me).y + 2), true);
                me.GoToAndAction(t, PlotManager.Instance.GetPlotAtTile(t).GetComponent<Actionable>(), AFO.AT.Primary);
                return true;
            });
            s.Add(el =>
            {
                int moved = 0;
                foreach (var kv in otherStart) { var o = Obj(kv.Key); if (o && (Tile(o).x != kv.Value.x || Tile(o).y != kv.Value.y)) moved++; }
                // the other two joiners walk (the host stands still)
                if (moved < 2 && el < 30f) return false;
                TestBot.Assert(Me + ": sees the other joiners walk", moved >= 2, moved + " of " + otherStart.Count + " other players moved");
                baseBps = Blueprints();
                return true;
            });
            Barrier(s, 2);
            // 3. each places a blueprint; everyone sees all three
            s.Add(el =>
            {
                var c = Tile(MeObj());
                var bp = TestScript.EmptyTile(new TileCoord(c.x - 6 - 4 * i, c.y - 8));
                BuildingManager.Instance.AddBuilding(bp, ObjectType.Workbench, 0, false, null, false, false, false);
                return true;
            });
            Barrier(s, 3);
            s.Add(el =>
            {
                int n = Blueprints() - baseBps;
                if (n < Joiners && el < 30f) return false;
                TestBot.Assert(Me + ": all three joiners' blueprints are in the world", n >= Joiners, n + " new blueprints");
                return true;
            });
            Barrier(s, 4);
            // 4. worlds match
            WorldCheck(s, "after the edits");
            Barrier(s, 5);
            // 5. watch with all four in the world
            s.Add(el =>
            {
                if (el < 0.1f) { frames.Clear(); rebuilt0 = Rebuilt(); watchStart = Time.realtimeSinceStartup; }
                Frame();
                return el > WatchSeconds;
            });
            s.Add(el =>
            {
                float secs = Time.realtimeSinceStartup - watchStart;
                int rb = Rebuilt() - rebuilt0;
                Info(Me + ": watching with four players", Fps() + "; rebuilt " + rb + " in " + secs.ToString("F0") + " s");
                // the BRAVO two-player run rebuilt 7 in 4.5 min; one every 2 s would be a storm
                TestBot.Assert(Me + ": no rebuild storm", rb < secs / 2f, rb + " rebuilt in " + secs.ToString("F0") + " s");
                return true;
            });
            Barrier(s, 6);
            // 6. joiner 3 drops and rejoins; the others see it leave and come back and keep their own characters
            if (i == Joiners)
            {
                s.Add(el =>
                {
                    myStuff.Clear(); Sync.CollectUIDs(Sync.SaveObject(MeObj(), out _), myUid, myStuff); myStuff.Sort();
                    return true;
                });
                s.Add(el => { if (el < 3f) return false; Coop.I.Client.Host.Close(); return true; });
                s.Add(el =>
                {
                    bool ok = Coop.Role == Role.Off && TestBot.AtMainMenu();
                    if (!ok && el < 60f) return false;
                    TestBot.Assert(Me + ": dropped -> main menu", ok, Session.LastDisconnect ?? "");
                    return true;
                });
                s.Add(el => Connect(el, 8f));
                s.Add(el =>
                {
                    if (Coop.Role == Role.Off) { TestBot.Assert(Me + ": rejoined", false, Session.LastDisconnect ?? "dropped"); return true; }
                    return (Coop.I.ClientLoaded && Obj(Sync.MyPlayerUID) && el > 3f) || el > 600f;
                });
                s.Add(el =>
                {
                    var now = new List<int>(); if (MeObj()) Sync.CollectUIDs(Sync.SaveObject(MeObj(), out _), Sync.MyPlayerUID, now); now.Sort();
                    TestBot.Assert(Me + ": rejoined with my own character", Sync.MyPlayerUID == myUid, "now " + Sync.MyPlayerUID + ", before " + myUid);
                    TestBot.Assert(Me + ": my things came back", string.Join(",", now.ConvertAll(x => x.ToString()).ToArray()) == string.Join(",", myStuff.ConvertAll(x => x.ToString()).ToArray()));
                    return true;
                });
            }
            else
            {
                s.Add(el => { Menu.Toasts.Clear(); return true; });
                s.Add(el =>
                {
                    bool gone = Menu.Toasts.Exists(t => t.EndsWith("left the game")) && Players() == 3;
                    if (!gone && el < 90f) return false;
                    TestBot.Assert(Me + ": joiner 3 left (character gone, notice shown)", gone, Players() + " players");
                    return true;
                });
                s.Add(el =>
                {
                    bool back = Menu.Toasts.Exists(t => t.EndsWith("joined the game")) && Players() == 4;
                    if (!back && el < (OnSave ? 600f : 180f)) return false;
                    TestBot.Assert(Me + ": joiner 3 came back (notice shown, 4 players)", back, Players() + " players");
                    TestBot.Assert(Me + ": still my own character", Sync.MyPlayerUID == myUid, "now " + Sync.MyPlayerUID + ", before " + myUid);
                    return true;
                });
            }
            Barrier(s, 7);
            WorldCheck(s, "after the rejoin");
            Barrier(s, 8);
            s.Add(el => { Req("f4done"); return true; });
            s.Add(el =>
            {
                bool ok = Coop.Role == Role.Off && Session.LastDisconnect == Session.EndedMsg;
                if (!ok && el < 120f) return false;
                TestBot.Assert(Me + ": host ended -> \"" + Session.EndedMsg + "\"", ok, Session.LastDisconnect ?? "");
                return true;
            });
        }
    }
}
