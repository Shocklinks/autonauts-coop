// AutonautsCoop - Diag.cs - diagnostics log for real sessions (BepInEx/coop_diag.log, included in the F8 bug report)
// Version 0.12.1 - 2026-10-21 - Created with Claude
//
// What it records, on both sides:
//   - every mouse click in the world: what is under the cursor, the tile, the bot nearest to where the cursor hits,
//     whether the UI swallowed the click
//   - every action a joiner sends, and on the host what became of it (done, target not found, held while busy, refused)
//   - screen changes (normal, select worker, teach, building menus...), script editor target, toasts the player saw
//   - bot selection (whistle): which bot the cursor is on, which one got selected
//   - every 10 s: frame rate, round-trip time, traffic, sync apply time, objects rebuilt
//   - F7: "something went wrong here" marker, written on this side and the host's
// It is on by default (cheap: a few lines per click plus one summary every 10 s). Config [Debug] Diagnostics.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using SimpleJSON;
using UnityEngine;

namespace AutonautsCoop
{
    public static class Diag
    {
        public static bool On = Environment.GetEnvironmentVariable("COOP_DIAG") != "0";
        public static string LogPath;
        public static KeyCode MarkKey = KeyCode.F7;
        public static int Lines, Clicks, Marks, SelectHovers, Inputs;
        public static string LastLine = "";
        static readonly object fileLock = new object();

        public static void Init()
        {
            LogPath = Path.Combine(Paths.BepInExRootPath, "coop_diag.log");
            try
            {
                var cfg = Plugin.Instance.Config;
                if (!cfg.Bind("Debug", "Diagnostics", true, "Write BepInEx/coop_diag.log (clicks, actions, screens, lag). Goes into the F8 bug report.").Value) On = false;
                MarkKey = cfg.Bind("Debug", "MarkKey", KeyCode.F7, "Press when something goes wrong: marks the moment in both players' diagnostics logs").Value;
                if (File.Exists(LogPath)) { var old = LogPath + ".old"; if (File.Exists(old)) File.Delete(old); File.Move(LogPath, old); }
                File.WriteAllText(LogPath, "");
            }
            catch (Exception e) { Plugin.Log("diag init: " + e.Message); }
            Log("garbage collector: GC_FREE_SPACE_DIVISOR=" + (Environment.GetEnvironmentVariable("GC_FREE_SPACE_DIVISOR") ?? "(default)"));
            Log("AutonautsCoop " + Plugin.Version + " diagnostics" + (On ? "" : " OFF") + "  " + SystemInfo.operatingSystem + "  " + SystemInfo.processorType + "  RAM " + SystemInfo.systemMemorySize + " MB  GPU " + SystemInfo.graphicsDeviceName);
            if (NetSim.LagMs > 0 || NetSim.JitterMs > 0) Log("SIMULATED LAG: " + NetSim.LagMs + " ms + up to " + NetSim.JitterMs + " ms jitter, each way");
        }

        static string Who() => Coop.Role == Role.Host ? "H" : Coop.Role == Role.Client ? "J" : "-";

        public static void Log(string s)
        {
            if (!On || LogPath == null) return;
            Lines++; LastLine = s;
            var line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + Who() + " " + s + "\n";
            lock (fileLock)
            {
                try
                {
                    var fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 32L * 1024 * 1024) { var old = LogPath + ".old"; if (File.Exists(old)) File.Delete(old); File.Move(LogPath, old); }
                    File.AppendAllText(LogPath, line);
                }
                catch { }
            }
        }

        public static string Obj(BaseClass b) => b ? b.m_TypeIdentifier + "#" + b.m_UniqueID : "none";
        static string T(TileCoord t) => t.x + "," + t.y;

        // ---------------- joiner/host: actions over the wire ----------------
        static readonly HashSet<string> quiet = new HashSet<string> { "look", "need", "chk", "mb", "hostlog", "lock" };

        /// Joiner: an action left for the host.
        public static void Out(string json)
        {
            if (!On) return;
            try
            {
                var n = JSON.Parse(json); string a = n["a"];
                if (a == null || quiet.Contains(a) || a.StartsWith("test_")) return;
                Inputs++;
                Log("send " + Trim(json));
            }
            catch { }
        }

        /// Host: an action arrived from a joiner.
        public static void In(Peer from, JSONNode n)
        {
            if (!On) return;
            string a = n["a"];
            if (a == null || quiet.Contains(a) || a.StartsWith("test_")) return;
            Inputs++;
            Log("from " + (from != null ? from.Name : "?") + ": " + Trim(n.ToString()));
        }

        static string Trim(string s) => s.Length > 300 ? s.Substring(0, 300) + "..." : s;

        // ---------------- clicks ----------------
        public static Vector3? FakeMouse;   // tests: where the "mouse" is (the game's own raycasts use it too)
        public static Transform FakeTrack;  // tests: keep the "mouse" on this object, wherever the camera is this moment
        public static bool Faked => FakeTrack || FakeMouse.HasValue;
        public static Vector3 Mouse =>
            FakeTrack && CameraManager.Instance ? CameraManager.Instance.m_Camera.WorldToScreenPoint(FakeTrack.position + Vector3.up) : FakeMouse ?? Input.mousePosition;

        /// What the cursor is on, as the game would see it, plus the bot nearest to the hit point.
        public static string Probe(Vector3 screen)
        {
            var sb = new StringBuilder();
            var gsm = GameStateManager.Instance;
            var st = gsm ? gsm.GetCurrentState() : null;
            sb.Append("screen ").Append(gsm ? gsm.GetActualState().ToString() : "-");
            sb.Append(" at ").Append((int)screen.x).Append(",").Append((int)screen.y).Append(" of ").Append(Screen.width).Append("x").Append(Screen.height);
            try
            {
                var im = CustomStandaloneInputModule.Instance;
                if (im && (im.IsUIInFocus() || im.IsHover())) sb.Append(" UI-HOVER");
            }
            catch { }
            if (!st || CameraManager.Instance == null) return sb.ToString();
            // everything a click could hit: tiles, buildings, bots, small things, stopped bots
            var go = st.TestCollisionOffset(out var cp, true, true, true, true, true, screen);
            if (!go) { sb.Append(" hit nothing"); return sb.ToString(); }
            var tc = new TileCoord(cp);
            sb.Append(" hit ").Append(go.name).Append(" layer ").Append(LayerName(go.layer)).Append(" tile ").Append(T(tc));
            var root = go.GetComponentInParent<BaseClass>();
            if (root && !go.GetComponent<PlotRoot>()) sb.Append(" obj ").Append(Obj(root));
            var tile = TileManager.Instance ? TileManager.Instance.GetTile(tc) : null;
            if (tile != null)
            {
                if (tile.m_Farmer) sb.Append(" tileFarmer ").Append(Obj(tile.m_Farmer));
                if (tile.m_Building) sb.Append(" building ").Append(Obj(tile.m_Building));
            }
            var w = NearestWorker(cp, Tile.m_Size * 2f, out float d);
            if (w) sb.Append(" | nearest bot ").Append(BotInfo(w)).Append(" ").Append((d / Tile.m_Size).ToString("F1")).Append(" tiles from hit");
            return sb.ToString();
        }

        static string LayerName(int l) => l >= 0 && l < (int)Layers.Total ? ((Layers)l).ToString() : l.ToString();

        public static string BotInfo(Worker w)
        {
            var vis = new TileCoord(w.transform.position);
            string s = Obj(w) + " tile " + T(w.m_TileCoord);
            if (vis.x != w.m_TileCoord.x || vis.y != w.m_TileCoord.y) s += " (drawn at " + T(vis) + ")";
            s += " energy " + w.m_Energy.ToString("F0") + " state " + w.m_State + (w.GetIsListening() ? " listening" : "") + (w.m_BeingHeld ? " held" : "");
            string lk = Locks.Table.TryGetValue(w.m_UniqueID, out string who) ? who : null;
            if (lk != null) s += " locked by " + lk;
            if (Sync.RecreatedUid.TryGetValue(w.m_UniqueID, out int rc)) s += " rebuilt " + rc + "x";
            return s;
        }

        /// The bot drawn closest to a world point (by where it is drawn, not the tile it is registered on).
        public static Worker NearestWorker(Vector3 p, float maxDist, out float dist)
        {
            Worker best = null; dist = maxDist;
            var col = CollectionManager.Instance ? CollectionManager.Instance.GetCollection("Worker") : null;
            if (col == null) return null;
            foreach (var kv in col)
            {
                var w = kv.Key ? kv.Key.GetComponent<Worker>() : null;
                if (!w || w.m_BeingHeld || !w.gameObject.activeInHierarchy) continue;
                var q = w.transform.position; float dd = new Vector2(q.x - p.x, q.z - p.z).magnitude;
                if (dd < dist) { dist = dd; best = w; }
            }
            return best;
        }

        // ---------------- per frame ----------------
        static float nextSummary, frameSum, frameMax, gcWorst, plainWorst; static int frames, gcs, gcLast;
        static long memLast, frameAlloc; static int bip0, vs0, sp0;
        /// Running totals for the tests: collections, the worst frame one landed in, garbage made (bytes, all code).
        public static int GcTotal; public static float GcWorstEver; public static long AllocTotal;
        static int lastSelectTarget = -2; static GameStateManager.State lastState = GameStateManager.State.Total;
        static long bytesIn0, bytesOut0; static int moverSaves0, moverSkips0; static int batches0, recreated0, inplace0; static double apply0;

        public static void Tick()
        {
            float dt = Time.unscaledDeltaTime;
            AllocProf.FrameStart();
            frames++; frameSum += dt; if (dt > frameMax) frameMax = dt;
            // garbage collection pauses: a frame in which the collector ran
            int gcNow = GC.CollectionCount(0);
            long mem = Prof.Mem;
            if (gcNow != gcLast) { gcs += gcNow - gcLast; GcTotal += gcNow - gcLast; gcLast = gcNow; if (dt > gcWorst) gcWorst = dt; if (dt > GcWorstEver) GcWorstEver = dt; }
            else { if (dt > plainWorst) plainWorst = dt; if (mem > memLast) { frameAlloc += mem - memLast; AllocTotal += mem - memLast; } }
            memLast = mem;
            if (Coop.Role == Role.Off) { nextSummary = 0; return; }
            if (!On) return;
            bool inGame = Coop.InGame();
            try
            {
                if (inGame && (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)))
                {
                    Clicks++;
                    Log("click " + (Input.GetMouseButtonDown(0) ? "L" : "R") + " " + Probe(Mouse));
                }
                if (Input.GetKeyDown(MarkKey)) Mark("pressed " + MarkKey);
                if (inGame) WatchSelect();
            }
            catch (Exception e) { Log("diag error: " + e.Message); }
            float now = Time.realtimeSinceStartup;
            if (now >= nextSummary)
            {
                if (nextSummary > 0) Summary(now);
                nextSummary = now + 10f;
                frames = 0; frameSum = 0; frameMax = 0;
                bytesIn0 = Peer.BytesRecv; bytesOut0 = Peer.BytesSent; moverSaves0 = Scan.StatMoverSaves; moverSkips0 = Scan.StatMoverSkips; batches0 = Sync.StatBatches; recreated0 = Sync.StatRecreated; inplace0 = Sync.StatInPlace; apply0 = Sync.StatApplyMs;
            }
        }

        /// Bot selection (whistle): log which bot the cursor is on whenever it changes.
        static void WatchSelect()
        {
            var gsm = GameStateManager.Instance;
            var s = gsm.GetActualState();
            if (s != lastState) { lastState = s; lastSelectTarget = -2; }
            if (s != GameStateManager.State.SelectWorker) return;
            var sel = GameStateSelectWorker.Instance;
            if (!sel) return;
            var cur = Traverse.Create(sel).Field("m_CurrentTarget").GetValue<Worker>();
            int id = cur ? cur.m_UniqueID : -1;
            if (id == lastSelectTarget) return;
            lastSelectTarget = id;
            SelectHovers++;
            Log("select: cursor on " + (cur ? BotInfo(cur) : "no bot") + " | " + Probe(Mouse));
        }

        public static void Mark(string why)
        {
            Marks++;
            Log("==================== MARK (" + why + ") ====================");
            Menu.Toast("Marked in the diagnostics log");
            if (Coop.Role == Role.Client && Coop.I.Client?.Host != null && Coop.I.Client.Host.Alive)
            {
                var n = new JSONObject(); n["a"] = "mark"; n["by"] = SteamNet.MyName();
                Coop.I.Client.Host.Send(Msg.Input, n.ToString());
            }
        }

        public static void HostMark(Peer from)
        {
            Marks++;
            Log("==================== MARK from " + (from != null ? from.Name : "?") + " ====================");
            Menu.Toast((from != null ? from.Name : "A joiner") + " marked a problem (diagnostics log)");
        }

        static void Summary(float now)
        {
            float fps = frameSum > 0 ? frames / frameSum : 0;
            var sb = new StringBuilder("stats 10s: fps ").Append(fps.ToString("F0")).Append(" worst frame ").Append((frameMax * 1000).ToString("F0")).Append(" ms");
            sb.Append(" (GC ").Append(gcs).Append("x, worst GC frame ").Append((gcWorst * 1000).ToString("F0")).Append(" ms, worst other ").Append((plainWorst * 1000).ToString("F0")).Append(" ms, heap ").Append((GC.GetTotalMemory(false) / 1048576).ToString()).Append(" MB)");
            gcs = 0; gcWorst = 0; plainWorst = 0;
            sb.Append(" | garbage ").Append((frameAlloc / 10485760.0).ToString("F1")).Append(" MB/s (coop: ").Append(Prof.TakeAllocReport(10f)).Append(')');
            frameAlloc = 0;
            var gg = AllocProf.TakeReport(10f); if (gg != null) sb.Append(" | ").Append(gg);
            sb.Append(" | in ").Append(((Peer.BytesRecv - bytesIn0) / 10240f).ToString("F1")).Append(" KB/s out ").Append(((Peer.BytesSent - bytesOut0) / 10240f).ToString("F1")).Append(" KB/s");
            if (Coop.Role == Role.Client)
            {
                var h = Coop.I.Client?.Host;
                sb.Append(" | rtt ").Append(h != null && h.Rtt >= 0 ? (h.Rtt * 1000).ToString("F0") + " ms" : "?");
                int b = Sync.StatBatches - batches0;
                sb.Append(" | batches ").Append(b).Append(" apply avg ").Append(b > 0 ? ((Sync.StatApplyMs - apply0) / b).ToString("F1") : "0").Append(" ms max ").Append(Sync.StatApplyMaxMs.ToString("F0"));
                sb.Append(" | updated in place ").Append(Sync.StatInPlace - inplace0).Append(" (buildings with contents ").Append(Sync.StatBuildingInPlace - bip0).Append(", unchecked ").Append(Sync.StatVerifySkipped - vs0).Append(", same skipped unparsed ").Append(Sync.StatSkippedParse - sp0).Append(")").Append(" rebuilt ").Append(Sync.StatRecreated - recreated0); bip0 = Sync.StatBuildingInPlace; vs0 = Sync.StatVerifySkipped; sp0 = Sync.StatSkippedParse;
                Sync.StatApplyMaxMs = 0;
                if (Sync.StatRecreated - recreated0 > 0)
                {
                    var parts = new List<string>();
                    foreach (var kv in Sync.RecreatedByTypeWindow) parts.Add(kv.Key + " " + kv.Value);
                    sb.Append(" (").Append(string.Join(", ", parts.ToArray())).Append(")");
                }
                Sync.RecreatedByTypeWindow.Clear();
                sb.Append(" | objects ").Append(Coop.ObjectCount());
                if (NetSim.LagMs > 0 || NetSim.JitterMs > 0) sb.Append(" | SIMULATED LAG ").Append(NetSim.LagMs).Append("+").Append(NetSim.JitterMs).Append(" ms");
            }
            else if (Coop.Role == Role.Host && Coop.I.Host != null)
            {
                sb.Append(" | scan ").Append(Sync.LastScanMs.ToString("F1")).Append(" ms (movers re-saved ").Append(Scan.StatMoverSaves - moverSaves0).Append(", skipped ").Append(Scan.StatMoverSkips - moverSkips0).Append(", acted on ").Append(Scan.StatDirty).Append(", still: saved ").Append(Scan.StatRingSaves).Append(" looked ").Append(Scan.StatRingSkips).Append(") | objects ").Append(Coop.ObjectCount());
                Scan.StatDirty = 0; Scan.StatRingSaves = 0; Scan.StatRingSkips = 0;
                lock (Coop.I.Host.Peers)
                    foreach (var p in Coop.I.Host.Peers)
                        if (p.Alive && p.Name != "?") sb.Append(" | ").Append(p.Name).Append(" rtt ").Append(p.Rtt >= 0 ? (p.Rtt * 1000).ToString("F0") + " ms" : "?");
                var pr = Prof.TakeReport(); if (pr.Length > 0) sb.Append(" | time: ").Append(pr);
            }
            Log(sb.ToString());
        }

        // ---------------- round trip ----------------
        static float nextEcho;
        /// Both sides measure round-trip time: send our clock, the other side sends it straight back.
        public static void EchoTick()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextEcho || Coop.Role == Role.Off) return;
            nextEcho = now + 2f;
            var n = new JSONObject(); n["o"] = Coop.Role == Role.Host ? "h" : "j"; n["t"] = now;
            var d = Encoding.UTF8.GetBytes(n.ToString());
            if (Coop.Role == Role.Host && Coop.I.Host != null) Coop.I.Host.Broadcast(Msg.Echo, d);
            else if (Coop.Role == Role.Client && Coop.I.Client?.Host != null && Coop.I.ClientLoaded) Coop.I.Client.Host.Send(Msg.Echo, d);
        }

        public static void OnEcho(Packet p)
        {
            try
            {
                var n = JSON.Parse(Encoding.UTF8.GetString(p.Data));
                string mine = Coop.Role == Role.Host ? "h" : "j";
                if ((string)n["o"] != mine) { p.From.Send(Msg.Echo, p.Data); return; }
                p.From.Rtt = Time.realtimeSinceStartup - n["t"].AsFloat;
            }
            catch { }
        }
    }

    // ---------------- hooks ----------------
    [HarmonyPatch]
    static class P_DiagStates
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GameStateManager), nameof(GameStateManager.SetState));
            yield return AccessTools.Method(typeof(GameStateManager), nameof(GameStateManager.PushState));
            yield return AccessTools.Method(typeof(GameStateManager), nameof(GameStateManager.PopState));
        }
        static int depth;   // SetState calls PushState: log the outer call only
        static void Prefix(GameStateManager __instance, out GameStateManager.State __state)
        {
            depth++;
            __state = GameStateManager.State.Total;
            try { __state = __instance.GetActualState(); } catch { }
        }
        static void Finalizer(GameStateManager __instance, System.Reflection.MethodBase __originalMethod, GameStateManager.State __state)
        {
            if (--depth > 0 || !Diag.On || Coop.Role == Role.Off) return;
            GameStateManager.State now;
            try { now = __instance.GetActualState(); } catch { return; }
            if (now != __state) Diag.Log("screen " + __state + " -> " + now + " (" + __originalMethod.Name + ")");
        }
    }

    [HarmonyPatch(typeof(TeachWorkerScriptEdit), nameof(TeachWorkerScriptEdit.SetTarget))]
    static class P_DiagEditorTarget
    {
        static int last = -2;
        static void Postfix(TeachWorkerScriptEdit __instance)
        {
            if (!Diag.On || Coop.Role == Role.Off) return;
            var w = __instance.m_CurrentTarget; int id = w ? w.m_UniqueID : -1;
            if (id == last) return;
            last = id;
            Diag.Log("script editor: " + (w ? Diag.BotInfo(w) : "closed"));
        }
    }

    [HarmonyPatch(typeof(TeachWorkerScriptEdit), "DoNonEditable")]
    static class P_DiagNonEditable
    {
        static void Postfix(TeachWorkerScriptEdit __instance)
        {
            if (Diag.On && Coop.Role != Role.Off) Diag.Log("script editor: 'stop the bot first' cover shown for " + Diag.Obj(__instance.m_CurrentTarget));
        }
    }

    // Tests: the game's own mouse raycasts use the fake cursor (no OS mouse needed, and the real one isn't moved).
    // Also hands the joiner's whistle screen the bot drawn under the cursor (Feel.cs).
    [HarmonyPatch(typeof(GameStateBase), nameof(GameStateBase.TestMouseCollision))]
    static class P_FakeMouse
    {
        static bool Prefix(GameStateBase __instance, ref Vector3 CollisionPoint, bool TestTiles, bool TestBuildings, bool TestWorkers, bool TestMisc, bool TestWorkersDead, ref GameObject __result)
        {
            var w = P_JoinerPickDrawnBot.Override;
            if (w) { __result = w.gameObject; CollisionPoint = w.transform.position; return false; }   // joiner whistle screen (Feel.cs)
            if (!Diag.Faked) return true;
            __result = __instance.TestCollisionOffset(out CollisionPoint, TestTiles, TestBuildings, TestWorkers, TestMisc, TestWorkersDead, Diag.Mouse);
            return false;
        }
    }
}

namespace AutonautsCoop
{
    /// Where the host's time goes: per named part, the worst single run and the total over the last 10 s
    /// (in the diagnostics "stats" line).
    public static class Prof
    {
        class P { public double Max, Sum; public int N; }
        static readonly Dictionary<string, P> parts = new Dictionary<string, P>();
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        public static double Now => clock.Elapsed.TotalMilliseconds;
        /// Managed heap in use. Goes up as things are allocated, drops at a collection: the difference across a
        /// piece of code (when no collection ran in between) is the garbage it made.
        public static long Mem => GC.GetTotalMemory(false);
        static readonly Dictionary<string, long> alloc = new Dictionary<string, long>();
        public static void Alloc(string name, long before)
        {
            long d = Mem - before; if (d <= 0) return;   // a collection ran in between
            alloc.TryGetValue(name, out long v); alloc[name] = v + d;
        }
        public static string TakeAllocReport(float seconds)
        {
            var list = new List<KeyValuePair<string, long>>(alloc);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new System.Text.StringBuilder();
            foreach (var kv in list)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(kv.Key).Append(' ').Append((kv.Value / 1048576.0 / seconds).ToString("F2"));
            }
            alloc.Clear();
            return sb.ToString();
        }
        public static void Add(string name, double ms)
        {
            if (!parts.TryGetValue(name, out var p)) parts[name] = p = new P();
            p.N++; p.Sum += ms; if (ms > p.Max) p.Max = ms;
        }
        public static string TakeReport()
        {
            var list = new List<KeyValuePair<string, P>>(parts);
            list.Sort((a, b) => b.Value.Max.CompareTo(a.Value.Max));
            var sb = new System.Text.StringBuilder();
            foreach (var kv in list)
            {
                if (kv.Value.Max < 1) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(kv.Key).Append(" worst ").Append(kv.Value.Max.ToString("F0")).Append(" ms (").Append(kv.Value.N).Append("x, ").Append(kv.Value.Sum.ToString("F0")).Append(" ms)");
            }
            parts.Clear();
            return sb.ToString();
        }
    }
}
