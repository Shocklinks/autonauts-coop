// AutonautsCoop - BigBase.cs - stress test (COOP_BIG=1): a large free-play map with a dense late-game style base,
// a joiner joins, the base keeps changing for a while, then both sides must still match.
// Version 0.10.0 - 2026-10-21 - Created with Claude
using System;
using System.Collections.Generic;
using SimpleJSON;
using UnityEngine;

namespace AutonautsCoop
{
    public static class BigBase
    {
        public static int Bots = Env("COOP_BIG_BOTS", 400), Buildings = Env("COOP_BIG_BUILDINGS", 800), Items = Env("COOP_BIG_ITEMS", 4000);
        public static float ChurnSeconds = 90f;
        static int Env(string k, int d) => int.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;

        static FarmerPlayer Me() => CollectionManager.Instance.GetPlayers()[0].GetComponent<FarmerPlayer>();
        static TileCoord Tile(BaseClass b) => b.GetComponent<TileCoordObject>().m_TileCoord;
        static readonly List<Worker> bots = new List<Worker>();
        static readonly List<BaseClass> items = new List<BaseClass>();
        static readonly List<double> scans = new List<double>();
        static readonly List<float> frames = new List<float>();
        static long bytes0; static float churnStart, nextChurn, nextFlip; static bool towardJoiner;
        static System.Random rng = new System.Random(7);
        public static bool ChurnDone, Stopped; static double baseFps;

        static void Info(string what, string detail) { TestBot.Assert("info: " + what, true, detail); }

        // ---------------- host ----------------
        public static void BuildHost(List<TestBot.IEnumeratorStep> s)
        {
            s.Add(el => TestBot.AtMainMenu() && el > 8f);
            s.Add(el =>
            {
                // the main menu's background world forces the 84x96 mini map until it finishes; a real new game doesn't
                SaveLoadManager.m_MiniMap = false; SaveLoadManager.m_SpecificFile = false;
                var o = GameOptionsManager.Instance.m_NewOptions;
                o.m_GameMode = GameOptions.GameMode.ModeCampaign;
                o.SetMapSize(GameOptions.GameSize.Small);   // two medium maps (3 GB each) exceed this sandbox memory
                o.NewMapSeed();
                o.m_TutorialEnabled = false; o.m_RecordingEnabled = false;
                GameOptionsManager.Instance.LoadNewOptions();
                var mo = GameOptionsManager.Instance.m_Options;
                mo.m_MapSeed = o.m_MapSeed;
                mo.m_PlayerPosition = new TileCoord(mo.m_MapWidth / 2 + Plot.m_PlotTilesWide / 2, mo.m_MapHeight / 2 + Plot.m_PlotTilesHigh / 2);   // stale from the default size otherwise
                SessionManager.Instance.LoadLevel(false, "Main");
                return true;
            });
            s.Add(el => Coop.InGame() && GameStateManager.Instance.GetActualState() == GameStateManager.State.Normal && el > 15f);
            s.Add(el =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int natural = Coop.ObjectCount();
                Generate();
                Info("big world", "map " + TileManager.Instance.m_TilesWide + "x" + TileManager.Instance.m_TilesHigh + ", natural objects " + natural + ", after base " + Coop.ObjectCount()
                    + " (" + bots.Count + " bots, " + items.Count + " items), built in " + sw.ElapsedMilliseconds + " ms");
                // reveal the plots around the base like a late game would
                foreach (var p in PlotManager.Instance.m_Plots) if (p && !p.m_Visible && Vector2.Distance(new Vector2(p.m_PlotX, p.m_PlotY), PlotCenter()) < 6) p.SetVisible(true, true);
                return true;
            });
            s.Add(el => GameStateManager.Instance.GetActualState() == GameStateManager.State.Normal && el > 5f);
            s.Add(el =>
            {
                // baseline: the same world with nobody connected (this sandbox is slow; compare against this)
                if (el < 0.5f) { frames.Clear(); return false; }
                frames.Add(Time.unscaledDeltaTime);
                if (el < 30f) return false;
                double sum = 0; foreach (var f in frames) sum += f;
                baseFps = frames.Count / Math.Max(0.001, sum);
                Info("host baseline, nobody connected", "fps " + baseFps.ToString("F2"));
                return true;
            });
            s.Add(el => { Coop.I.StartHost(TestBot.Port); return true; });
            s.Add(el => { lock (Coop.I.Host.Peers) return Coop.I.Host.Peers.Exists(p => p.Name != "?") && el > 1f; });
            s.Add(el => el > 5f);   // the joiner loads
            s.Add(el =>
            {
                // churn: bots walk between the two players, items appear/disappear, tiles change
                if (churnStart == 0) { churnStart = Time.realtimeSinceStartup; bytes0 = Peer.BytesSent; scans.Clear(); frames.Clear(); }
                float now = Time.realtimeSinceStartup;
                scans.Add(Sync.LastScanMs); frames.Add(Time.unscaledDeltaTime);
                if (now > nextFlip)
                {
                    nextFlip = now + 12f; towardJoiner = !towardJoiner;
                    FarmerPlayer target = Me();
                    if (towardJoiner) foreach (var kv in Sync.PeerPlayers) { var o = ObjectTypeList.Instance.GetObjectFromUniqueID(kv.Value, false); if (o) target = o.GetComponent<FarmerPlayer>(); }
                    int n = 0; foreach (var w in bots) if (w && n++ < 120) w.GoToPlayer(target);
                }
                if (now > nextChurn)
                {
                    nextChurn = now + 1f;
                    for (int i = 0; i < 10 && items.Count > 0; i++) { int k = rng.Next(items.Count); if (items[k]) items[k].StopUsing(); items.RemoveAt(k); }
                    var c = Tile(Me());
                    for (int i = 0; i < 10; i++) Spawn(i % 2 == 0 ? ObjectType.Log : ObjectType.Rock, Sync.FreeTileNear(Clamp(new TileCoord(c.x + rng.Next(-25, 25), c.y + rng.Next(-25, 25)))));
                    for (int i = 0; i < 4; i++)
                    {
                        var t = Sync.FreeTileNear(Clamp(new TileCoord(c.x + rng.Next(-30, 30), c.y + rng.Next(-30, 30))));
                        var cur = TileManager.Instance.GetTile(t).m_TileType;
                        if (cur == global::Tile.TileType.Empty || cur == global::Tile.TileType.Soil) TileManager.Instance.SetTileType(t, cur == global::Tile.TileType.Empty ? global::Tile.TileType.Soil : global::Tile.TileType.Empty);
                    }
                }
                if (now - churnStart < ChurnSeconds) return false;
                ChurnDone = true;
                double secs = now - churnStart;
                scans.Sort(); frames.Sort();
                double avg = 0; foreach (var x in scans) avg += x; avg /= Math.Max(1, scans.Count);
                double fps = 0; foreach (var f in frames) fps += f; fps = frames.Count / Math.Max(0.001, fps);
                Info("host during churn", "scan avg " + avg.ToString("F1") + " ms, p95 " + P(scans, 0.95).ToString("F1") + " ms, max " + P(scans, 1).ToString("F1") + " ms, fps avg " + fps.ToString("F2") + " (baseline " + baseFps.ToString("F2") + ")"
                    + " (worst frame " + (P(frames, 1) * 1000).ToString("F0") + " ms), rescanned/scan " + Scan.LastRescanned + ", upload " + ((Peer.BytesSent - bytes0) / secs / 1024).ToString("F1") + " KB/s, objects " + Coop.ObjectCount());
                Plugin.Log("[test] big: churn done");
                return true;
            });
            s.Add(el => el > 600f || Stopped || !JoinedPeer());
        }

        static bool JoinedPeer() { lock (Coop.I.Host.Peers) return Coop.I.Host.Peers.Exists(p => p.Alive && p.Name != "?"); }
        static double P<T>(List<T> sorted, double q) where T : IConvertible
        {
            if (sorted.Count == 0) return 0;
            int i = (int)Math.Min(sorted.Count - 1, Math.Floor(q * (sorted.Count - 1)));
            return Convert.ToDouble(sorted[i]);
        }

        static Vector2 PlotCenter()
        {
            var t = Tile(Me());
            return new Vector2(t.x / Plot.m_PlotTilesWide, t.y / Plot.m_PlotTilesHigh);
        }

        static TileCoord Clamp(TileCoord t) => new TileCoord(Mathf.Clamp(t.x, 1, TileManager.Instance.m_TilesWide - 2), Mathf.Clamp(t.y, 1, TileManager.Instance.m_TilesHigh - 2));
        static BaseClass Spawn(ObjectType t, TileCoord at)
        {
            at = Clamp(at);
            var b = ObjectTypeList.Instance.CreateObjectFromIdentifier(t, at.ToWorldPositionTileCentered(), Quaternion.identity);
            if (b) items.Add(b);
            return b;
        }

        static void Generate()
        {
            var c = Tile(Me());
            var types = new[] { ObjectType.StorageGeneric, ObjectType.Workbench, ObjectType.ChoppingBlock, ObjectType.StoragePalette, ObjectType.StorageSeedlings, ObjectType.LogWall, ObjectType.FencePicket, ObjectType.StonePath, ObjectType.SandPath, ObjectType.BrickWall };
            int built = 0, tries = 0;
            for (int r = 4; built < Buildings && r < 80 && tries < Buildings * 20; r++)
                for (int dx = -r; dx <= r && built < Buildings; dx += 2)
                    foreach (var dy in new[] { -r, r })
                    {
                        tries++;
                        var t = new TileCoord(c.x + dx, c.y + dy);
                        var tile = TileManager.Instance.GetTile(t);
                        if (tile == null || tile.m_Building || tile.m_Floor || global::Tile.GetIsSolid(tile.m_TileType)) continue;
                        var b = BuildingManager.Instance.AddBuilding(t, types[built % types.Length], 0, false, null, true);
                        if (b) built++;
                    }
            for (int i = 0; i < Bots; i++)
            {
                var at = Sync.FreeTileNear(Clamp(new TileCoord(c.x + rng.Next(-20, 20), c.y + rng.Next(-20, 20))));
                var w = ObjectTypeList.Instance.CreateObjectFromIdentifier(ObjectType.Worker, at.ToWorldPositionTileCentered(), Quaternion.identity).GetComponent<Worker>();
                w.SetFrame(ObjectType.WorkerFrameMk0); w.SetDrive(ObjectType.WorkerDriveMk0); w.SetHead(ObjectType.WorkerHeadMk0);
                bots.Add(w);
            }
            var kinds = new[] { ObjectType.Log, ObjectType.Rock, ObjectType.Stick, ObjectType.Plank, ObjectType.StoneBlock };
            for (int i = 0; i < Items; i++)
                Spawn(kinds[i % kinds.Length], new TileCoord(c.x + rng.Next(-40, 40), c.y + rng.Next(-40, 40)));
            Plugin.Log("[test] big: built " + built + " buildings, " + bots.Count + " bots, " + items.Count + " items");
        }

        public static bool HostOp(string a, JSONNode n, Peer from)
        {
            if (a == "big_state") { var r = new JSONObject(); r["tr"] = "big"; r["done"] = ChurnDone; from.Send(Msg.Info, r.ToString()); return true; }
            if (a == "big_stop") { Stopped = true; return true; }
            return false;
        }

        // ---------------- client ----------------
        static float joinStart; static int chkTries;
        public static void BuildClient(List<TestBot.IEnumeratorStep> s)
        {
            s.Add(el => TestBot.AtMainMenu() && el > 8f);
            s.Add(el =>
            {
                if (el < 2f) return false;
                try { Coop.I.StartClient(TestBot.Ip, TestBot.Port); joinStart = Time.realtimeSinceStartup; return true; }
                catch (Exception e) { Plugin.Log("connect retry: " + e.Message); return false; }
            });
            s.Add(el => Coop.I.ClientLoaded && Sync.MyPlayerUID >= 0 && ObjectTypeList.Instance.GetObjectFromUniqueID(Sync.MyPlayerUID, false) && el > 2f);
            s.Add(el =>
            {
                float secs = Time.realtimeSinceStartup - joinStart;
                int snap = Coop.I.LastInfo != null ? Coop.I.LastInfo["objects"].AsInt : -1;
                TestBot.Assert("client: joined the big world", true, "join took " + secs.ToString("F0") + " s, objects host " + snap + " client " + Coop.ObjectCount());
                TestBot.Assert("client: join finished within 4 minutes", secs < 240f, secs.ToString("F0") + " s");
                Sync.StatApplyMs = 0; Sync.StatApplyMaxMs = 0; batches0 = Sync.StatBatches; frames.Clear();
                return true;
            });
            s.Add(el =>
            {
                frames.Add(Time.unscaledDeltaTime);
                if (Time.realtimeSinceStartup > nextAsk) { nextAsk = Time.realtimeSinceStartup + 3f; var n = new JSONObject(); n["a"] = "big_state"; Coop.I.Client.Host.Send(Msg.Input, n.ToString()); }
                bool done = TestScript.Reply != null && (string)TestScript.Reply["tr"] == "big" && TestScript.Reply["done"].AsBool;
                if (!done && el < 400f) return false;
                frames.Sort();
                double fps = 0; foreach (var f in frames) fps += f; fps = frames.Count / Math.Max(0.001, fps);
                int b = Sync.StatBatches - batches0;
                Info("joiner during churn", "batches " + b + ", apply avg " + (Sync.StatApplyMs / Math.Max(1, b)).ToString("F1") + " ms, max " + Sync.StatApplyMaxMs.ToString("F0") + " ms, fps avg " + fps.ToString("F2")
                    + " (worst frame " + (P(frames, 1) * 1000).ToString("F0") + " ms), errors " + Sync.StatErrors + ", recreated " + Sync.StatRecreated + ", digest fixes " + Sync.StatDigestFixes);
                return true;
            });
            s.Add(el => el > 8f);   // let the last changes land
            s.Add(el => { TestScript.Chk = null; var n = new JSONObject(); n["a"] = "chk"; Coop.I.Client.Host.Send(Msg.Input, n.ToString()); return true; });
            s.Add(el =>
            {
                if (TestScript.Chk == null && el < 60f) return false;
                TestScript.ChecksumAtChk(out int c, out long sum);
                bool ok = TestScript.Chk != null && TestScript.Chk["count"].AsInt == c && TestScript.Chk["sum"].Value == sum.ToString();
                if (TestScript.Chk != null && !ok && chkTries < 6)
                {
                    chkTries++; TestScript.Chk = null; var n = new JSONObject(); n["a"] = "chk"; Coop.I.Client.Host.Send(Msg.Input, n.ToString());
                    return false;
                }
                if (!ok && TestScript.Chk != null)
                {
                    var mine = TestScript.HashesAtChk(); var theirs = new Dictionary<int, int>(); var arr = TestScript.Chk["u"].AsArray;
                    for (int i = 0; i + 1 < arr.Count; i += 2) theirs[arr[i].AsInt] = arr[i + 1].AsInt;
                    int only = 0, onlyHost = 0, diff = 0;
                    foreach (var kv in mine) if (!theirs.ContainsKey(kv.Key)) only++; else if (theirs[kv.Key] != kv.Value) diff++;
                    foreach (var kv in theirs) if (!mine.ContainsKey(kv.Key)) onlyHost++;
                    Plugin.Log("[test] big checksum: only client " + only + ", only host " + onlyHost + ", differ " + diff);
                }
                TestBot.Assert("client: big world matches the host after churn", ok, TestScript.Chk == null ? "no reply" : "host " + TestScript.Chk["count"].AsInt + " client " + c);
                var st = new JSONObject(); st["a"] = "big_stop"; Coop.I.Client.Host.Send(Msg.Input, st.ToString());
                return true;
            });
        }
        static int batches0; static float nextAsk;
    }
}
