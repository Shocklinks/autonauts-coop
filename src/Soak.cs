// AutonautsCoop - Soak.cs - test on the player's own save (test kit, COOP_SOAK=1 COOP_SAVE=<name>)
// Version 0.12.1 - 2026-10-21 - Created with Claude
//
// The host loads a copy of a real save and hosts it; a joiner joins and plays for a few minutes:
// join time, object counts, walking, picking bots with the whistle, opening a bot's program, a long watch with
// world checksums, frame rates and traffic on both sides. Nothing is saved.
using System;
using System.Collections.Generic;
using SimpleJSON;
using UnityEngine;

namespace AutonautsCoop
{
    public static class Soak
    {
        public static bool On = Environment.GetEnvironmentVariable("COOP_SOAK") == "1";
        static readonly string SaveName = Environment.GetEnvironmentVariable("COOP_SAVE") ?? "";
        static float WatchSeconds = float.TryParse(Environment.GetEnvironmentVariable("COOP_SOAK_SECONDS"), out var v) ? v : 180f;

        static FarmerPlayer Me() { var o = ObjectTypeList.Instance.GetObjectFromUniqueID(Coop.Role == Role.Client ? Sync.MyPlayerUID : Sync.HostPlayerUID, false); return o ? o.GetComponent<FarmerPlayer>() : CollectionManager.Instance.GetPlayers()[0].GetComponent<FarmerPlayer>(); }
        static TileCoord Tile(BaseClass b) => b.GetComponent<TileCoordObject>().m_TileCoord;
        static void Info(string what, string detail) { TestBot.Assert("info: " + what, true, detail); }
        static void Req(string a, Action<JSONObject> f = null) { var n = new JSONObject(); n["a"] = a; f?.Invoke(n); Coop.I.Client.Host.Send(Msg.Input, n.ToString()); }

        // frame stats over a window
        static readonly List<float> frames = new List<float>();
        static void Frame() { frames.Add(Time.unscaledDeltaTime); }
        static int gc0; static long alloc0; static float aloneStart;
        /// Collections since the mark, the worst frame one landed in, and the garbage rate (all code, not just the mod).
        static string GcLine()
        {
            float secs = Mathf.Max(1f, Time.realtimeSinceStartup - aloneStart);
            string r = "; GC " + (Diag.GcTotal - gc0) + "x in " + secs.ToString("F0") + " s, worst GC frame " + (Diag.GcWorstEver * 1000).ToString("F0") + " ms, garbage "
                + ((Diag.AllocTotal - alloc0) / 1048576.0 / secs).ToString("F1") + " MB/s";
            gc0 = Diag.GcTotal; Diag.GcWorstEver = 0; alloc0 = Diag.AllocTotal; aloneStart = Time.realtimeSinceStartup;
            return r;
        }
        static string Fps()
        {
            if (frames.Count == 0) return "-";
            float sum = 0, worst = 0; foreach (var f in frames) { sum += f; if (f > worst) worst = f; }
            var sorted = new List<float>(frames); sorted.Sort();
            float p95 = sorted[(int)(sorted.Count * 0.95f)];
            return (frames.Count / sum).ToString("F0") + " fps avg, 95% of frames under " + (p95 * 1000).ToString("F0") + " ms, worst " + (worst * 1000).ToString("F0") + " ms";
        }

        // ---------------- host ----------------
        static bool reported; static float loadStart, hostStart; static int done; static double scanSum, scanMax; static int scanN;
        public static bool HostOp(string a, JSONNode n, Peer from)
        {
            if (a == "soak_done") { done++; Plugin.Log("[test] soak: joiner done"); return true; }
            if (a == "soak_hoststats")
            {
                var r = new JSONObject(); r["tr"] = "hoststats"; r["fps"] = Fps(); r["scan"] = (scanN > 0 ? scanSum / scanN : 0).ToString("F1") + " ms avg, " + scanMax.ToString("F0") + " ms max";
                from.Send(Msg.Info, r.ToString());
                return true;
            }
            return false;
        }

        public static void BuildHost(List<TestBot.IEnumeratorStep> s)
        {
            s.Add(el => TestBot.AtMainMenu() && el > 8f);
            s.Add(el =>
            {
                SaveLoadManager.m_MiniMap = false; SaveLoadManager.m_SpecificFile = false;
                TestBot.Assert("host: the save to test exists (" + SaveName + ")", SaveFileWorldData.Exists(MyFile.GetBasePath(false) + SaveName), MyFile.GetBasePath(false) + SaveName);
                // the main menu's Load screen, picking the save (quick load): the game's own load path
                int bad = SaveLoadManager.Instance.CheckValidFile(SaveName);
                if (bad != 0) { TestBot.Assert("host: the game accepts the save", false, "CheckValidFile " + bad); return true; }
                // the game sets up its recipe tables for the save's game mode when it enters the world; loading right after
                // boot (no world played yet) can reach the buildings first, so set them up now
                var sf = new SaveFile(); sf.LoadPreview(SaveName);
                GameOptionsManager.Instance.m_Options.m_GameMode = sf.m_Summary.m_GameOptions.m_GameMode;
                VariableManager.Instance.ReInit();
                GameStateManager.Instance.PushState(GameStateManager.State.Load);
                var gl = GameStateManager.Instance.GetCurrentState().GetComponent<GameStateLoad>();
                gl.Init(false);
                gl.SaveFilePreviewSelected(SaveName, true);
                loadStart = Time.realtimeSinceStartup;
                return true;
            });
            s.Add(el => Coop.InGame() && GameStateManager.Instance.GetActualState() == GameStateManager.State.Normal && el > 5f);
            s.Add(el =>
            {
                int bots = 0, buildings = 0;
                foreach (var kv in Sync.Savables()) { if (!kv.Key) continue; if (kv.Key is Worker) bots++; else if (kv.Key is Building) buildings++; }
                Info("your save", "\"" + SaveName + "\": map " + TileManager.Instance.m_TilesWide + "x" + TileManager.Instance.m_TilesHigh + ", " + Coop.ObjectCount() + " objects, " + bots + " bots, "
                    + buildings + " buildings; loaded in " + (Time.realtimeSinceStartup - loadStart).ToString("F0") + " s");
                frames.Clear();
                return true;
            });
            s.Add(el => { if (el < 0.1f) { gc0 = Diag.GcTotal; Diag.GcWorstEver = 0; alloc0 = Diag.AllocTotal; aloneStart = Time.realtimeSinceStartup; } Frame(); return el > 40f; });
            s.Add(el => { float secs = Time.realtimeSinceStartup - aloneStart; Info("host alone", Fps() + GcLine()); var gg = AllocProf.TakeReport(Mathf.Max(1f, secs)); if (gg != null) Info("host alone", gg); frames.Clear(); Coop.I.StartHost(TestBot.Port); hostStart = Time.realtimeSinceStartup; return true; });
            s.Add(el =>
            {
                Frame();
                if (Sync.LastScanMs > 0) { scanSum += Sync.LastScanMs; scanN++; if (Sync.LastScanMs > scanMax) scanMax = Sync.LastScanMs; }
                return done >= 1 || el > 1500f;
            });
            s.Add(el =>
            {
                if (reported) return el > 2f;   // report once, then give the joiner a moment
                reported = true;
                Info("host with a joiner", Fps() + GcLine());
                Info("host scan", (scanN > 0 ? scanSum / scanN : 0).ToString("F1") + " ms avg, " + scanMax.ToString("F0") + " ms max; sent " + (Peer.BytesSent / 1048576f).ToString("F1") + " MB");
                return false;
            });
            s.Add(el => { Session.StopHost("test: done"); return true; });
        }

        // ---------------- joiner ----------------
        static float connectAt, rejoinTry; static int chkTries; static TileCoord start; static int editBot = -1;
        static bool coverShown;

        static List<Worker> BotsOnScreen()
        {
            var l = new List<Worker>();
            var col = CollectionManager.Instance.GetCollection("Worker");
            if (col != null) foreach (var kv in col)
            {
                var w = kv.Key ? kv.Key.GetComponent<Worker>() : null;
                if (w && !w.m_BeingHeld && w.gameObject.activeInHierarchy && TestScript.OnScreen(TestScript.BotScreen(w))) l.Add(w);
            }
            return l;
        }

        /// Where the most bots are (so the camera can look there).
        static Vector3? BotCrowd()
        {
            var col = CollectionManager.Instance.GetCollection("Worker");
            if (col == null || col.Count == 0) return null;
            var cells = new Dictionary<long, List<Vector3>>();
            foreach (var kv in col)
            {
                if (!kv.Key) continue; var p = kv.Key.transform.position;
                long k = ((long)Mathf.FloorToInt(p.x / 30f) << 32) ^ (uint)Mathf.FloorToInt(p.z / 30f);
                if (!cells.TryGetValue(k, out var l)) cells[k] = l = new List<Vector3>();
                l.Add(p);
            }
            List<Vector3> best = null; foreach (var c in cells.Values) if (best == null || c.Count > best.Count) best = c;
            var sum = Vector3.zero; foreach (var p in best) sum += p;
            return sum / best.Count;
        }

        /// Which objects differ from the host's (by type), and their JSON here in coop.log.
        static readonly List<int> askUids = new List<int>();
        static string Differ(JSONArray u)
        {
            if (u == null) return "?";
            var host = new Dictionary<int, int>();
            for (int i = 0; i + 1 < u.Count; i += 2) host[u[i].AsInt] = u[i + 1].AsInt;
            var mine = TestScript.HashesAtChk();
            var types = new Dictionary<string, int>(); int logged = 0;
            void Note(int uid, string what)
            {
                var b = ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false);
                string t = (b ? b.m_TypeIdentifier.ToString() : "?") + " " + what;
                types[t] = types.TryGetValue(t, out int n) ? n + 1 : 1;
                if (logged++ < 8)
                {
                    string js = b ? Sync.SaveObject(b, out _) : null;
                    Plugin.Log("[test] differs: " + uid + " " + t + (b ? " here: " + js : ""));
                    if (b) Plugin.Log("[test] stable here " + uid + ": " + Sync.Stable(b, js));
                    if (askUids.Count < 8) askUids.Add(uid);
                }
            }
            foreach (var kv in host) { if (!mine.TryGetValue(kv.Key, out int h)) Note(kv.Key, "missing"); else if (h != kv.Value) Note(kv.Key, "different"); }
            foreach (var kv in mine) if (!host.ContainsKey(kv.Key)) Note(kv.Key, "extra");
            if (askUids.Count > 0) { Req("chkobj", n => { var ua = new JSONArray(); foreach (var x in askUids) ua.Add(x); n["u"] = ua; }); askUids.Clear(); }   // host logs its side
            var parts = new List<string>(); foreach (var kv in types) parts.Add(kv.Key + " " + kv.Value);
            return string.Join(", ", parts.ToArray());
        }

        public static void BuildClient(List<TestBot.IEnumeratorStep> s)
        {
            s.Add(el => TestBot.AtMainMenu() && el > 8f);
            s.Add(el =>
            {
                if (el < 5f || Time.realtimeSinceStartup < rejoinTry) return false;   // the host is still loading the save
                rejoinTry = Time.realtimeSinceStartup + 5f;
                try { Coop.I.StartClient(TestBot.Ip, TestBot.Port); connectAt = Time.realtimeSinceStartup; return true; }
                catch (Exception e) { Plugin.Log("connect retry: " + e.Message); return el > 900f; }
            });
            s.Add(el => (Coop.I.ClientLoaded && Coop.InGame() && GameStateManager.Instance.GetActualState() == GameStateManager.State.Normal && el > 3f) || el > 600f);
            s.Add(el =>
            {
                TestBot.Assert("client: joined your save", Coop.I.ClientLoaded, "in " + (Time.realtimeSinceStartup - connectAt).ToString("F0") + " s");
                int host = Coop.I.LastInfo != null ? Coop.I.LastInfo["objects"].AsInt : -1, mine = Coop.ObjectCount();
                Info("join", "took " + (Time.realtimeSinceStartup - connectAt).ToString("F0") + " s; host " + host + " objects, here " + mine);
                start = Tile(Me());
                frames.Clear();
                return true;
            });
            // walk
            s.Add(el =>
            {
                var t = Sync.FreeTileNear(new TileCoord(start.x + 3, start.y + 2), true);
                Me().GoToAndAction(t, PlotManager.Instance.GetPlotAtTile(t).GetComponent<Actionable>(), AFO.AT.Primary);
                return true;
            });
            s.Add(el =>
            {
                Frame();
                var now = Tile(Me()); bool moved = now.x != start.x || now.y != start.y;
                if (!moved && el < 20f) return false;
                TestBot.Assert("client: my character walks", moved, "at " + now.x + "," + now.y);
                return true;
            });
            // look at the busiest bots and pick them with the whistle
            s.Add(el =>
            {
                Frame();
                var c = BotCrowd();
                if (c == null) { Info("bot selection", "no bots in this save; skipped"); return true; }
                if (el < 0.1f) { CameraManager.Instance.Focus(c.Value); return false; }
                if (BotsOnScreen().Count < 3 && el < 3f) { CameraManager.Instance.Focus(c.Value); return false; }
                var on = BotsOnScreen();
                if (on.Count == 0) { Info("bot selection", "couldn't get bots on screen; skipped"); return true; }
                var uids = new List<int>(); foreach (var w in on) uids.Add(w.m_UniqueID);
                TestScript.SelBegin(uids, Math.Min(12, Math.Max(4, on.Count)));
                Plugin.Log("[test] soak select: " + on.Count + " bots on screen");
                GameStateManager.Instance.SetState(GameStateManager.State.SelectWorker);
                return true;
            });
            s.Add(el => { Frame(); if (TestScript.wanderUids.Count == 0) return true; return TestScript.SelectTrials(el); });
            s.Add(el =>
            {
                Diag.FakeMouse = null; Diag.FakeTrack = null;
                if (GameStateManager.Instance.GetActualState() != GameStateManager.State.Normal) GameStateManager.Instance.SetState(GameStateManager.State.Normal);
                return el > 1f;
            });
            // open a bot's program: the editor must become usable
            s.Add(el =>
            {
                var on = BotsOnScreen();
                if (on.Count == 0) { Info("bot program", "no bot on screen; skipped"); return true; }
                editBot = on[0].m_UniqueID;
                GameStateManager.Instance.GetCurrentState().GetComponent<GameStateNormal>()?.SetSelectedWorker(on[0], true, false);
                coverShown = false;
                return true;
            });
            s.Add(el =>
            {
                Frame();
                if (editBot < 0) return true;
                var ed = TeachWorkerScriptEdit.Instance;
                bool open = ed && ed.m_CurrentTarget && ed.m_CurrentTarget.m_UniqueID == editBot;
                if (!open && el < 5f) return false;
                TestBot.Assert("client: a bot's program opens", open);
                if (!open) return true;
                if (!coverShown) { HarmonyLib.AccessTools.Method(typeof(TeachWorkerScriptEdit), "DoNonEditable").Invoke(ed, null); coverShown = true; }
                return true;
            });
            s.Add(el =>
            {
                if (editBot < 0) return true;
                var ed = TeachWorkerScriptEdit.Instance;
                var ne = ed ? HarmonyLib.Traverse.Create(ed).Field("m_NonEditable").GetValue<GameObject>() : null;
                bool clear = !ne || !ne.activeSelf;
                if (!clear && el < 8f) return false;
                TestBot.Assert("client: the program editor's 'not editable' cover clears", clear);
                GameStateManager.Instance.GetCurrentState().GetComponent<GameStateNormal>()?.ClearSelectedWorkers();
                CameraManager.Instance.Focus(Me().transform.position);
                frames.Clear(); GcLine();
                return true;
            });
            // watch the world for a while, then compare it with the host's
            s.Add(el => { Frame(); return el > WatchSeconds; });
            // how far behind is the joiner in normal play (no flush first): report only
            s.Add(el => { TestScript.Chk = null; Req("chknf"); return true; });
            s.Add(el =>
            {
                if (TestScript.Chk == null && el < 30f) return false;
                if (TestScript.Chk == null) { Info("joiner behind the host", "no reply"); return true; }
                var host = new Dictionary<int, int>(); var u = TestScript.Chk["u"].AsArray;
                for (int i = 0; i + 1 < u.Count; i += 2) host[u[i].AsInt] = u[i + 1].AsInt;
                var mine = TestScript.HashesAtChk(); var types = new Dictionary<string, int>(); int diff = 0;
                foreach (var kv in host)
                {
                    if (mine.TryGetValue(kv.Key, out int h) && h == kv.Value) continue;
                    diff++;
                    var b = ObjectTypeList.Instance.GetObjectFromUniqueID(kv.Key, false);
                    string t = b ? b.m_TypeIdentifier.ToString() : "missing";
                    types[t] = types.TryGetValue(t, out int c) ? c + 1 : 1;
                }
                var parts = new List<string>(); foreach (var kv in types) parts.Add(kv.Key + " " + kv.Value);
                parts.Sort((x, y) => int.Parse(y.Substring(y.LastIndexOf(' ') + 1)).CompareTo(int.Parse(x.Substring(x.LastIndexOf(' ') + 1))));
                if (parts.Count > 8) parts.RemoveRange(8, parts.Count - 8);
                Info("joiner behind the host", diff + " of " + host.Count + " objects differ at a random moment (no flush)" + (diff > 0 ? ": " + string.Join(", ", parts.ToArray()) : ""));
                return true;
            });
            s.Add(el => { Info("joiner while watching", Fps() + GcLine()); Info("joiner sync", "rebuilt " + Sync.StatRecreated + ", updated in place " + Sync.StatInPlace + ", received " + (Peer.BytesRecv / 1048576f).ToString("F1") + " MB, apply max " + Sync.StatApplyMaxMs.ToString("F0") + " ms, round trip " + (Coop.I.Client.Host.Rtt * 1000).ToString("F0") + " ms"); TestScript.Chk = null; Req("chk"); return true; });
            s.Add(el =>
            {
                if (TestScript.Chk == null && el < 60f) return false;
                TestScript.ChecksumAtChk(out int c, out long sum);
                bool ok = TestScript.Chk != null && TestScript.Chk["count"].AsInt == c && TestScript.Chk["sum"].Value == sum.ToString();
                if (TestScript.Chk != null && !ok && chkTries++ < 6) { TestScript.Chk = null; Req("chk"); return false; }
                string why = TestScript.Chk == null ? "no reply" : "host " + TestScript.Chk["count"].AsInt + " objects, me " + c;
                if (TestScript.Chk != null && !ok) why += "; differ: " + Differ(TestScript.Chk["u"].AsArray);
                TestBot.Assert("client: whole world matches the host", ok, why);
                Req("soak_hoststats");
                return true;
            });
            s.Add(el =>
            {
                var r = TestScript.Reply;
                if ((r == null || (string)r["tr"] != "hoststats") && el < 10f) return false;
                if (r != null && (string)r["tr"] == "hoststats") Info("host during the session", (string)r["fps"] + "; scan " + (string)r["scan"]);
                Req("soak_done");
                return true;
            });
            s.Add(el =>
            {
                bool ok = Coop.Role == Role.Off;
                if (!ok && el < 60f) return false;
                TestBot.Assert("client: host ended the session", ok, Session.LastDisconnect ?? "");
                return true;
            });
        }
    }
}
