// AutonautsCoop - Sync.cs - world replication (host -> clients) and remote input (clients -> host)
// Version 0.1.0 - 2026-10-20 - Created with Claude
//
// Host: scans every savable object a few times a second, sends the ones whose save JSON changed
// ("up"), the UIDs that vanished ("rm"), transforms of things that moved ("mv"), tile type changes
// ("tl") and plots that were uncovered ("pv").
// Client: simulation frozen (normal time off). Applies the batches. Existing objects are updated in
// place with Savable.Load; if a re-save doesn't match what the host sent, the object is recreated.
using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using SimpleJSON;
using UnityEngine;

namespace AutonautsCoop
{
    public static class Sync
    {
        // ---------------- shared ----------------
        public static int HostPlayerUID = -1;       // host: the host's own FarmerPlayer
        public static int MyPlayerUID = -1;         // client: my FarmerPlayer (spawned by host)
        public static readonly Dictionary<Peer, int> PeerPlayers = new Dictionary<Peer, int>();
        public static bool Applying;                // client: true while applying host data

        // stats (for logs and tests)
        public static int StatInPlace, StatRecreated, StatCreated, StatRemoved, StatBatches;
        public static readonly Dictionary<string, int> RecreatedByType = new Dictionary<string, int>();

        public static Dictionary<BaseClass, int> Savables()
        {
            return CollectionManager.Instance.GetCollection("Savable");
        }

        public static string SaveObject(BaseClass b, out string saveName)
        {
            var n = new JSONObject();
            saveName = ObjectTypeList.Instance.GetSaveNameFromIdentifier(b.m_TypeIdentifier);
            n["ID"] = saveName;
            b.GetComponent<Savable>().Save(n);
            return n.ToString();
        }

        // Fields that tick every frame on the host (state timers) or that Load resets (animal AI state).
        // They are left out of change detection and verification; the client sim is frozen anyway.
        static readonly System.Text.RegularExpressions.Regex VolatileAll = new System.Text.RegularExpressions.Regex(",\"STT\":-?[0-9.eE+-]+");
        static readonly System.Text.RegularExpressions.Regex VolatileAnimal = new System.Text.RegularExpressions.Regex(",\"(S|ST)\":-?[0-9.eE+-]+");
        static readonly System.Text.RegularExpressions.Regex Rotation = new System.Text.RegularExpressions.Regex(",\"Rotation\":-?[0-9]+");
        static bool IsPath(ObjectType t) => t == ObjectType.SandPath || t == ObjectType.StonePath || t == ObjectType.RoadCrude || t == ObjectType.RoadGood;
        // A held item's own tile (inside its holder's save) lags behind the holder on one side or the other; and a
        // modded item saves "Used":0 on one machine but not the other. Neither matters.
        static readonly System.Text.RegularExpressions.Regex ChildPos = new System.Text.RegularExpressions.Regex("(\\{\"ID\":\"[^\"]*\",\"UID\":-?\\d+),\"TX\":-?\\d+,\"TY\":-?\\d+");
        static readonly System.Text.RegularExpressions.Regex UsedZero = new System.Text.RegularExpressions.Regex(",\"Used\":0(?=[,}])");
        static readonly System.Text.RegularExpressions.Regex Instruction = new System.Text.RegularExpressions.Regex(",\"Instruction\":-?\\d+");
        /// Drop the object's own ,"TX":n,"TY":n (the first one: its own comes before its children's).
        static string StripOwnTile(string json)
        {
            int i = json.IndexOf(",\"TX\":", StringComparison.Ordinal); if (i < 0) return json;
            int j = json.IndexOf(",\"TY\":", i + 6, StringComparison.Ordinal); if (j < 0 || j > i + 20) return json;
            int k = j + 6; while (k < json.Length && (char.IsDigit(json[k]) || json[k] == '-')) k++;
            return json.Remove(i, k - i);
        }
        public static string Stable(BaseClass b, string json)
        {
            // (each pass only when its text is there: this runs for every object looked at, and the passes made garbage)
            int second = json.IndexOf("{\"ID\":", 1, StringComparison.Ordinal);
            if (second > 0)
            {
                int n = 0;
                json = ChildPos.Replace(json, m => n++ == 0 ? m.Value : m.Groups[1].Value);   // keep the object's own tile
            }
            if (json.IndexOf("\"Used\":0", StringComparison.Ordinal) >= 0) json = UsedZero.Replace(json, "");
            if (json.IndexOf(",\"STT\":", StringComparison.Ordinal) >= 0) json = VolatileAll.Replace(json, "");
            if (b is Farmer || b is Animal || b is Vehicle) json = StripOwnTile(json);   // movers: position goes in "mv"
            if (b is Worker && json.IndexOf("\"Instruction\":", StringComparison.Ordinal) >= 0) json = Instruction.Replace(json, "");   // current line goes in "ip"
            if (b is Animal) json = VolatileAnimal.Replace(json, "");
            else if (b is Floor2D || IsPath(b.m_TypeIdentifier)) json = Rotation.Replace(json, "");   // paths/flooring pick their look from neighbours on each machine
            return json;
        }
        // the host's JSON for one update is normalized once, not at every comparison
        static string memoIn, memoOut; static BaseClass memoB;
        static string StableW(BaseClass b, string json)
        {
            if (ReferenceEquals(json, memoIn) && memoB == b) return memoOut;
            memoIn = json; memoB = b; memoOut = Stable(b, json); return memoOut;
        }
        /// Joiner: what each object was last made to match (uid -> object, normalized hash). An update with the same
        /// hash is skipped without saving our copy to compare. Dropped when the digest says our copy is off.
        static readonly Dictionary<int, KeyValuePair<BaseClass, int>> applied = new Dictionary<int, KeyValuePair<BaseClass, int>>();
        // Checking every in-place update (save our copy, normalize, compare) was a big part of the joiner's garbage.
        // A kind of update that has matched 20 times in a row is checked one time in 10; the digest re-saves what was
        // touched every 10 s anyway, and anything it flags (mustVerify) is always checked when it comes back.
        static readonly Dictionary<string, int> verifiedRun = new Dictionary<string, int>();
        static readonly HashSet<int> mustVerify = new HashSet<int>();
        public static int StatVerifySkipped;
        static bool TrustInPlace(string kind, int uid)
        {
            if (mustVerify.Contains(uid)) return false;
            verifiedRun.TryGetValue(kind, out int run);
            if (run < 20) return false;
            verifiedRun[kind] = run + 1;
            return run % 10 != 0;
        }
        static void Verified(string kind, int uid, bool ok)
        {
            mustVerify.Remove(uid);
            if (!ok) { verifiedRun[kind] = 0; return; }
            verifiedRun.TryGetValue(kind, out int run); if (run < 20) verifiedRun[kind] = run + 1;
        }
        static void Applied(BaseClass b, int uid, string wantStable) { applied[uid] = new KeyValuePair<BaseClass, int>(b, wantStable.GetHashCode()); }
        public static int StableHash(BaseClass b) => Stable(b, SaveObject(b, out _)).GetHashCode();

        public struct Entry { public BaseClass B; public string Json; public int Hash; }
        /// Savable objects that are not children inside another object's save (e.g. a bees nest inside its
        /// tree). Children travel with their parent; listing them separately makes the two copies fight.
        static readonly HashSet<int> badSave = new HashSet<int>();
        public static List<Entry> TopLevel()
        {
            var list = new List<Entry>();
            var nested = new HashSet<int>();
            var tmp = new List<int>();
            foreach (var kv in Savables())
            {
                var b = kv.Key;
                if (b == null || b.m_UniqueID == -1 || !b.GetComponent<Savable>().GetIsSavable()) continue;
                string js;
                try { js = SaveObject(b, out _); }
                catch (Exception e)
                {
                    // a half-built object must not stop the whole scan (or every later scan)
                    if (badSave.Add(b.m_UniqueID)) Plugin.Log("skipping unsaveable " + b.m_TypeIdentifier + " " + b.m_UniqueID + ": " + e.Message);
                    continue;
                }
                int first = js.IndexOf("\"UID\"", StringComparison.Ordinal);
                if (first >= 0 && js.IndexOf("\"UID\"", first + 5, StringComparison.Ordinal) >= 0)
                {
                    tmp.Clear(); CollectUIDs(js, b.m_UniqueID, tmp);
                    foreach (var u in tmp) nested.Add(u);
                }
                list.Add(new Entry { B = b, Json = js, Hash = Stable(b, js).GetHashCode() });
            }
            if (nested.Count > 0) list.RemoveAll(e => nested.Contains(e.B.m_UniqueID));
            return list;
        }

        // ---------------- host ----------------
        static readonly Dictionary<int, int> sentHash = new Dictionary<int, int>();
        static readonly List<string> upText = new List<string>();
        static readonly Dictionary<int, Vector3> sentPos = new Dictionary<int, Vector3>();
        static readonly List<int[]> tileQ = new List<int[]>();
        static readonly List<int> plotQ = new List<int>();
        static float nextScan, nextMove, nextDigest;
        public static float DigestEvery = 10f;
        public static float ScanEvery = 0.2f, MoveEvery = 0.1f;
        public static double LastScanMs;

        public static void OnHostStart()
        {
            Persist.Ensure();
            var players = CollectionManager.Instance.GetPlayers();
            // A world saved during co-op holds the joiners' characters too; the save says which one is ours.
            HostPlayerUID = Persist.HostUid >= 0 && ObjectTypeList.Instance.GetObjectFromUniqueID(Persist.HostUid, false) ? Persist.HostUid
                          : players.Count > 0 ? players[0].m_UniqueID : -1;
            Persist.HostUid = HostPlayerUID;
            sentHash.Clear(); sentPos.Clear(); Scan.Reset();
            foreach (var pl in players) if (pl) Plugin.Log("  player uid " + pl.m_UniqueID + " type " + pl.GetType().Name + " active " + pl.gameObject.activeInHierarchy + " tile " + pl.GetComponent<TileCoordObject>().m_TileCoord.x + "," + pl.GetComponent<TileCoordObject>().m_TileCoord.y + " name " + pl.name);
            Plugin.Log("host player uid " + HostPlayerUID + " tile " + (players.Count > 0 ? players[0].GetComponent<TileCoordObject>().m_TileCoord.x + "," + players[0].GetComponent<TileCoordObject>().m_TileCoord.y : "-")
                + " pos " + (players.Count > 0 ? players[0].transform.position.ToString() : "-") + " map " + TileManager.Instance.m_TilesWide + "x" + TileManager.Instance.m_TilesHigh
                + " players " + players.Count + " state " + GameStateManager.Instance.GetActualState());
        }

        public static void OnPeerJoined(Peer p)
        {
            // Spawn a FarmerPlayer for this peer next to the host's player.
            Persist.Ensure();
            if (!PeerPlayers.TryGetValue(p, out int uid) && Persist.HasParked(p))
            {
                var host0 = ObjectTypeList.Instance.GetObjectFromUniqueID(HostPlayerUID, false);
                var back = Persist.Unpark(p, host0 ? host0.GetComponent<TileCoordObject>().m_TileCoord : new TileCoord(0, 0));
                if (back) { uid = back.m_UniqueID; PeerPlayers[p] = uid; Persist.Seats[Persist.Key(p)] = uid; Plugin.Log("peer " + p.Id + " (" + p.Name + ") back in FarmerPlayer " + uid); }
            }
            if (!PeerPlayers.TryGetValue(p, out uid) && (Persist.Seats.TryGetValue(Persist.Key(p), out int seat) || Persist.Seats.TryGetValue("name:" + p.Name, out seat))
                && ObjectTypeList.Instance.GetObjectFromUniqueID(seat, false) && !PeerPlayers.ContainsValue(seat))
            {
                // Same player rejoining this session: give them their character (and pockets) back.
                uid = seat; PeerPlayers[p] = uid;
                Persist.Seats.Remove("name:" + p.Name); Persist.Seats[Persist.Key(p)] = uid;
                Plugin.Log("peer " + p.Id + " (" + p.Name + ") back in FarmerPlayer " + uid);
            }
            if (!PeerPlayers.ContainsKey(p))
            {
                var host = ObjectTypeList.Instance.GetObjectFromUniqueID(HostPlayerUID, false);
                var tc = host ? host.GetComponent<TileCoordObject>().m_TileCoord : new TileCoord(0, 0);
                var spawn = FreeTileNear(tc);
                var fp = ObjectTypeList.Instance.CreateObjectFromIdentifier(ObjectType.FarmerPlayer, spawn.ToWorldPositionTileCentered(), Quaternion.identity);
                uid = fp.m_UniqueID;
                PeerPlayers[p] = uid;
                Persist.Seats[Persist.Key(p)] = uid;
                Plugin.Log("spawned FarmerPlayer uid " + uid + " for peer " + p.Id);
            }
            Session.Notify(p.Name + " joined the game", p);
            // The snapshot was captured before the spawn, so force a full resend of what changed.
            var info = new JSONObject();
            info["you"] = uid;
            info["key"] = "peer" + p.Id;
            p.Send(Msg.Info, info.ToString());
            // Make sure the new player goes out in the next batch.
            sentHash.Remove(uid); Scan.Force(uid);
            nextScan = 0;
        }

        /// Called right before a snapshot: flush pending changes to existing peers, then mark the current
        /// state as sent (the new peer gets it in the snapshot).
        public static void FlushBeforeSnapshot()
        {
            Scan.Full();
            nextScan = 0; nextMove = 0;
            HostTick();
            foreach (var kv in Scan.TopLevel()) sentHash[kv.Key] = kv.Value.Hash;
        }

        /// Nearest in-bounds, walkable, dry, unbuilt tile near a position (the host may stand at the map edge).
        /// includeCenter: the position itself counts. Tiles with nothing standing on them are preferred.
        public static TileCoord FreeTileNear(TileCoord c, bool includeCenter = false)
        {
            TileCoord? fallback = null;
            for (int r = includeCenter ? 0 : 1; r < 10; r++)
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) != r) continue;
                        var t = new TileCoord(c.x + dx, c.y + dy);
                        var tile = TileManager.Instance.GetTile(t);
                        if (tile == null || Tile.GetIsSolid(tile.m_TileType) || tile.m_Building || tile.m_Floor) continue;
                        if (tile.m_TileType.ToString().Contains("Water")) continue;
                        var objs = PlotManager.Instance.GetPlotAtTile(t).GetObjectsTypeAtTile(t);
                        if (objs == null || objs.Count == 0) return t;
                        if (fallback == null) fallback = t;
                    }
            return fallback ?? c;
        }

        public static void HostTick()
        {
            if (Coop.I.Host == null || !Coop.InGame()) return;
            RetryPendingInput();
            Teach.HostTick();
            foreach (var pp in new List<Peer>(PeerPlayers.Keys))
                if (!pp.Alive)
                {
                    Locks.HostPeerGone(pp);
                    SectionLocks.HostPeerGone(pp);
                    var fpo = ObjectTypeList.Instance.GetObjectFromUniqueID(PeerPlayers[pp], false);
                    PeerPlayers.Remove(pp);
                    try { Persist.Park(Persist.Key(pp), fpo); } catch (Exception e) { Plugin.Log("park: " + e); }
                    Plugin.Log("peer " + pp.Id + " (" + pp.Name + ") left; their character is stored until they rejoin");
                    Session.Notify(pp.Name + " left the game");
                }
            bool anyPeer;
            lock (Coop.I.Host.Peers) anyPeer = Coop.I.Host.Peers.Exists(x => x.Alive && x.Name != "?");
            if (!anyPeer) return;
            float now = Time.realtimeSinceStartup;
            JSONObject msg = null;

            double t0 = Prof.Now;
            if (now >= nextScan)
            {
                nextScan = now + ScanEvery;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long ms0 = Prof.Mem;
                msg = new JSONObject();
                var rm = new JSONArray();
                var seen = scanSeen; seen.Clear();   // reused: a fresh set of every uid each scan was ~2.5 MB/s of garbage
                Scan.Step(true);
                Prof.Add("scan", sw.Elapsed.TotalMilliseconds); Prof.Alloc("scan", ms0); ms0 = Prof.Mem;
                // changed objects go out as their saved JSON text (no parse/re-serialize)
                upText.Clear();
                foreach (var kv in Scan.TopLevel())
                {
                    seen.Add(kv.Key);
                    if (!sentHash.TryGetValue(kv.Key, out int old) || old != kv.Value.Hash) { sentHash[kv.Key] = kv.Value.Hash; upText.Add(kv.Value.Json); }
                }
                var gone = new List<int>();
                foreach (var uid in sentHash.Keys) if (!seen.Contains(uid)) gone.Add(uid);
                foreach (var uid in gone) { sentHash.Remove(uid); sentPos.Remove(uid); rm.Add(uid); }
                if (rm.Count > 0) msg["rm"] = rm;
                if (tileQ.Count > 0)
                {
                    var tl = new JSONArray();
                    foreach (var t in tileQ) { var a = new JSONArray(); a.Add(t[0]); a.Add(t[1]); a.Add(t[2]); tl.Add(a); }
                    msg["tl"] = tl; tileQ.Clear();
                }
                if (plotQ.Count > 0)
                {
                    var pv = new JSONArray(); foreach (var i in plotQ) pv.Add(i);
                    msg["pv"] = pv; plotQ.Clear();
                }
                LastScanMs = sw.Elapsed.TotalMilliseconds;
                Prof.Add("changes", LastScanMs); Prof.Alloc("diff", ms0);
            }
            double t1 = Prof.Now; long mm = Prof.Mem;

            if (now >= nextMove)
            {
                nextMove = now + MoveEvery;
                var mv = new JSONArray();
                foreach (var kv in Savables())
                {
                    var b = kv.Key;
                    if (b == null || b.m_UniqueID == -1 || !(b is Farmer || b is Animal || b is Vehicle)) continue;
                    var pos = b.transform.position;
                    // strip the walking hop (FarmerStateMove adds it on top of the path position); joiners add their own
                    var f = b as Farmer;
                    if (f && f.m_State == Farmer.State.Moving) pos.y -= Anim.Hop(b, f.m_StateTimer);
                    if (sentPos.TryGetValue(b.m_UniqueID, out var old) && (old - pos).sqrMagnitude < 0.0004f) continue;
                    sentPos[b.m_UniqueID] = pos;
                    var a = new JSONArray();
                    a.Add(b.m_UniqueID); a.Add(pos.x); a.Add(pos.y); a.Add(pos.z); a.Add(b.transform.eulerAngles.y);
                    mv.Add(a);
                }
                if (mv.Count > 0) { if (msg == null) msg = new JSONObject(); msg["mv"] = mv; }
                // bots' current program line (what the program editor highlights): small, so it goes every time it changes
                var ip = new JSONArray();
                var workers = CollectionManager.Instance.GetCollection("Worker");
                if (workers != null) foreach (var kv in workers)
                {
                    var w = kv.Key as Worker;
                    if (!w || w.m_UniqueID <= 0 || w.m_WorkerInterpreter == null) continue;
                    var cs = w.m_WorkerInterpreter.GetCurrentScript();
                    int line = cs != null ? cs.m_CurrentInstruction : -1;
                    if (sentIp.TryGetValue(w.m_UniqueID, out int was) && was == line) continue;
                    sentIp[w.m_UniqueID] = line;
                    ip.Add(w.m_UniqueID); ip.Add(line);
                }
                if (ip.Count > 0) { if (msg == null) msg = new JSONObject(); msg["ip"] = ip; }
                var an = Anim.HostTake();
                if (an != null) { if (msg == null) msg = new JSONObject(); msg["an"] = an; }
            }

            double t2 = Prof.Now; Prof.Add("movement", t2 - t1); Prof.Alloc("movement", mm); mm = Prof.Mem;
            if (((msg != null && msg.Count > 0) || upText.Count > 0) && !Coop.I.Host.AnyRecipient()) upText.Clear();   // nobody to tell (snapshot time)
            else if ((msg != null && msg.Count > 0) || upText.Count > 0)
            {
                string body = msg != null ? msg.ToString() : "{}";
                if (upText.Count > 0) body = "{\"up\":[" + string.Join(",", upText.ToArray()) + "]" + (body.Length > 2 ? "," + body.Substring(1) : "}");
                upText.Clear();
                Coop.I.Host.Broadcast(Msg.Delta, Gz.Pack(body));
            }
            double t3 = Prof.Now; Prof.Add("pack+send", t3 - t2); Prof.Alloc("pack+send", mm); mm = Prof.Mem;
            WorldState.HostTick();
            double t4 = Prof.Now; Prof.Add("world sections", t4 - t3); Prof.Alloc("sections", mm); mm = Prof.Mem;

            if (now >= nextDigest)
            {
                var swd = System.Diagnostics.Stopwatch.StartNew();
                // Anti-entropy: the list of (uid, hash) the clients should have. They ask for what's off.
                nextDigest = now + DigestEvery;
                var sb = new StringBuilder("{\"ae\":[", sentHash.Count * 16); bool first = true;
                foreach (var kv in sentHash) { if (!first) sb.Append(','); first = false; sb.Append(kv.Key).Append(',').Append(kv.Value); }
                sb.Append("]}");
                Coop.I.Host.Broadcast(Msg.Delta, Gz.Pack(sb.ToString()));
                Prof.Add("digest", swd.Elapsed.TotalMilliseconds); Prof.Alloc("digest", mm);
            }
            Prof.Add("host sync total", Prof.Now - t0);
        }

        class PendingGo { public string Json; public Peer From; public float Until; }
        static readonly Dictionary<int, PendingGo> pendingGo = new Dictionary<int, PendingGo>();
        static bool Busy(Farmer f) => f.m_State != Farmer.State.None && f.m_State != Farmer.State.Moving && f.m_State != Farmer.State.RequestMove && !f.CheckCanAbortAction();

        static void RetryPendingInput()
        {
            if (pendingGo.Count == 0) return;
            foreach (var uid in new List<int>(pendingGo.Keys))
            {
                var p = pendingGo[uid];
                var o = ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false);
                if (!o || Time.realtimeSinceStartup > p.Until) { pendingGo.Remove(uid); Diag.Log("  go: held click dropped (still busy after 3 s)"); continue; }
                if (!Busy(o.GetComponent<Farmer>())) { pendingGo.Remove(uid); HostApplyInput(p.From, p.Json); }
            }
        }

        public static void HostRecordTile(TileCoord c, Tile.TileType t)
        {
            if (Coop.Role == Role.Host) tileQ.Add(new[] { c.x, c.y, (int)t });
        }
        public static void HostRecordPlot(Plot p)
        {
            if (Coop.Role == Role.Host && p.m_Visible) plotQ.Add(p.m_PlotY * PlotManager.Instance.m_PlotsWide + p.m_PlotX);
        }

        static int resendLogged;
        public static void HostApplyInput(Peer from, string json)
        {
            var n0 = JSON.Parse(json);
            Diag.In(from, n0);
            if ((string)n0["a"] == "mark") { Diag.HostMark(from); return; }
            if ((string)n0["a"] == "chkobj")   // test: the world check found these different; log my side to compare
            {
                foreach (JSONNode x in n0["u"].AsArray)
                {
                    var b = ObjectTypeList.Instance.GetObjectFromUniqueID(x.AsInt, false);
                    if (!b) { Plugin.Log("[test] stable host " + x.AsInt + ": (none)"); continue; }
                    string js = SaveObject(b, out _);
                    Plugin.Log("[test] stable host " + x.AsInt + ": " + Stable(b, js));
                    Scan.Cache.TryGetValue(x.AsInt, out var sc);
                    Plugin.Log("[test] host record " + x.AsInt + ": fresh " + Stable(b, js).GetHashCode() + ", scan " + (sc != null ? sc.Hash.ToString() + " (looked " + (Time.realtimeSinceStartup - sc.LastFull).ToString("F0") + " s ago)" : "none")
                        + ", sent " + (sentHash.TryGetValue(x.AsInt, out int sh) ? sh.ToString() : "none") + ", inside another: " + Scan.IsNested(x.AsInt));
                }
                return;
            }
            if ((string)n0["a"] == "chk" || (string)n0["a"] == "chknf")
            {
                // send everything that changed first ("chknf": don't: how far behind is the joiner in normal play?)
                if ((string)n0["a"] == "chk") { Scan.Full(); nextScan = 0; HostTick(); }
                TestScript.Checksum(out int c, out long sum);
                var r = new JSONObject(); var o = new JSONObject(); o["count"] = c; o["sum"] = sum.ToString(); r["chk"] = o;
                var u = new JSONArray(); foreach (var kv in TestScript.Hashes()) { u.Add(kv.Key); u.Add(kv.Value); } o["u"] = u;
                from.Send(Msg.Info, r.ToString());
                return;
            }
            if ((string)n0["a"] == "hostlog") { BugReport.HostSendLog(from); return; }
            if ((string)n0["a"] == "need")
            {
                // A client lost track of these objects; resend them in the next scan.
                foreach (JSONNode u in n0["u"].AsArray)
                {
                    var nb = ObjectTypeList.Instance.GetObjectFromUniqueID(u.AsInt, false);
                    if (TestBot.Active || resendLogged++ < 50) Plugin.Log("resend requested: " + u.AsInt + " " + (nb ? nb.m_TypeIdentifier.ToString() : "gone"));
                    sentHash.Remove(u.AsInt); Scan.Force(u.AsInt);
                }
                nextScan = 0;
                return;
            }
            if ((string)n0["a"] == "test_hostact") { if (TestBot.Active) TestScript.HostActRequested = true; return; }
            if ((string)n0["a"] == "test_givebot" && TestBot.Active && PeerPlayers.TryGetValue(from, out int pb))
            {
                var pl = ObjectTypeList.Instance.GetObjectFromUniqueID(pb, false);
                var at = FreeTileNear(pl.GetComponent<TileCoordObject>().m_TileCoord);
                var w = ObjectTypeList.Instance.CreateObjectFromIdentifier(ObjectType.Worker, at.ToWorldPositionTileCentered(), Quaternion.identity).GetComponent<Worker>();
                w.SetFrame(ObjectType.WorkerFrameMk0); w.SetDrive(ObjectType.WorkerDriveMk0); w.SetHead(ObjectType.WorkerHeadMk0);
                Plugin.Log("[test] spawned bot uid " + w.m_UniqueID + " at " + at.x + "," + at.y);
                return;
            }
            if ((string)n0["a"] == "test_botstate" && TestBot.Active)
            {
                var o = ObjectTypeList.Instance.GetObjectFromUniqueID(n0["w"].AsInt, false);
                var w = o ? o.GetComponent<Worker>() : null;
                var r = new JSONObject(); var st = new JSONObject();
                if (w)
                {
                    st["n"] = w.m_WorkerInterpreter.m_HighInstructions.m_List.Count;
                    st["running"] = w.m_WorkerInterpreter.GetCurrentScript() != null;
                    st["started"] = Teach.LastStarted == w.m_UniqueID;
                    st["name"] = w.GetWorkerName();
                    st["locked"] = Locks.Table.TryGetValue(w.m_UniqueID, out string lk) ? lk : "";
                    st["hostblocked"] = Locks.LockedByOther(w.m_UniqueID) != null;
                    st["learning"] = w.m_Learning;
                    st["carry"] = w.m_FarmerCarry.m_CarryObject.Count;
                    st["x"] = w.m_TileCoord.x; st["y"] = w.m_TileCoord.y;
                    PeerPlayers.TryGetValue(from, out int fu);
                    var fpo = ObjectTypeList.Instance.GetObjectFromUniqueID(fu, false);
                    st["teaching"] = fpo && fpo.GetComponent<FarmerPlayer>().m_Teaching;
                }
                st["step"] = n0["step"]; r["botstate"] = st; from.Send(Msg.Info, r.ToString());
                return;
            }
            if (TestBot.Active && Soak.HostOp((string)n0["a"], n0, from)) return;
            if (TestBot.Active && BigBase.HostOp((string)n0["a"], n0, from)) return;
            if (TestBot.Active && Multi.HostOp((string)n0["a"], n0, from)) return;
            if (TestBot.Active && TestScript.HostTestOp((string)n0["a"], n0, from)) return;
            if ((string)n0["a"] == "test_hostlock" && TestBot.Active)
            {
                // test fixture: the host "opens" the bot in its editor (or closes it)
                Locks.HostSet(Locks.HostKey, "Host tester", n0["w"].AsInt);
                return;
            }
            if ((string)n0["a"] == "test_giveaxe" && TestBot.Active && PeerPlayers.TryGetValue(from, out int pu))
            {
                // test fixture only: drop a stone axe next to that player
                var pl = ObjectTypeList.Instance.GetObjectFromUniqueID(pu, false);
                var at = FreeTileNear(pl.GetComponent<TileCoordObject>().m_TileCoord);
                var axe = ObjectTypeList.Instance.CreateObjectFromIdentifier(ObjectType.ToolAxeStone, at.ToWorldPositionTileCentered(), Quaternion.identity);
                Plugin.Log("[test] spawned axe uid " + axe.m_UniqueID + " at " + at.x + "," + at.y);
                return;
            }
            if ((string)n0["a"] == "lock") { Locks.HostSet("peer" + from.Id, from.Name, n0["w"].AsInt); return; }
            if ((string)n0["a"] == "bot_script" || (string)n0["a"] == "learn_start" || (string)n0["a"] == "teach_on" || ((string)n0["a"]).StartsWith("wi_"))
                if (!Locks.HostAllowed("peer" + from.Id, n0["w"].AsInt)) { Plugin.Log((string)n0["a"] + " refused: bot locked by " + Locks.Table[n0["w"].AsInt]); Diag.Log("  refused: bot " + n0["w"].AsInt + " is being programmed by " + Locks.Table[n0["w"].AsInt]); return; }
            if (Build.HostApply((string)n0["a"], n0, from)) return;
            if (!PeerPlayers.TryGetValue(from, out int uid)) { Diag.Log("  ignored: " + from.Name + " has no character yet"); return; }
            var fpObj = ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false);
            if (!fpObj) { Diag.Log("  ignored: " + from.Name + "'s character " + uid + " is missing"); return; }
            var fp = fpObj.GetComponent<FarmerPlayer>();
            var n = JSON.Parse(json);
            if (Teach.HostApply((string)n["a"], n, fp)) return;
            if (Inv.HostApply((string)n["a"], n, fp, from)) return;
            if (Menus.HostApply((string)n["a"], n, fp)) return;
            if (MenuActApply.HostApply((string)n["a"], n, fp)) return;
            if (KeyApply.HostApply((string)n["a"], n, fp)) return;
            if (Shared.HostApply((string)n["a"], n, fp, from)) return;
            if (Look.HostApply((string)n["a"], n, fp)) return;
            switch ((string)n["a"])
            {
                case "go":
                    var dest = new TileCoord(n["x"].AsInt, n["y"].AsInt);
                    int t = n["t"].AsInt;
                    // Plot UIDs are assigned locally on each machine, so plots are sent as "the plot at dest".
                    BaseClass target = t >= 0 ? ObjectTypeList.Instance.GetObjectFromUniqueID(t, false) : t == -2 ? PlotManager.Instance.GetPlotAtTile(dest) : null;
                    if (t >= 0 && !target) { Plugin.Log("input target " + t + " not found on host"); Diag.Log("  go: target #" + t + " NOT FOUND on host (joiner's copy is stale)"); break; }
                    // Mid-action (picking up, chopping...) the game ignores clicks it can't abort. The joiner can't
                    // see that animation reliably, so hold the latest click and retry for a moment.
                    if (Busy(fp)) { if (!pendingGo.ContainsKey(uid)) Diag.Log("  go: " + from.Name + " is busy (" + fp.m_State + "), holding the click up to 3 s"); pendingGo[uid] = new PendingGo { Json = json, From = from, Until = Time.realtimeSinceStartup + 3f }; break; }
                    pendingGo.Remove(uid);
                    fp.GoToAndAction(dest, target ? target.GetComponent<Actionable>() : null, (AFO.AT)n["k"].AsInt);
                    if (Diag.On)
                    {
                        string ti = target is Worker tw ? Diag.BotInfo(tw) : Diag.Obj(target);
                        Diag.Log("  go " + dest.x + "," + dest.y + " k" + n["k"].AsInt + " target " + ti + " -> " + from.Name + " state " + fp.m_State + (fp.m_EngagedObject ? " engaged " + Diag.Obj(fp.m_EngagedObject) : ""));
                    }
                    if (TestBot.Active)
                    {
                        var tl = TileManager.Instance.GetTile(dest);
                        Plugin.Log("[test] input go " + dest.x + "," + dest.y + " k" + n["k"].AsInt + " target " + (target ? target.m_TypeIdentifier.ToString() : "none")
                            + " tile " + (tl != null ? tl.m_TileType.ToString() : "-") + " -> state " + fp.m_State + " carrying " + fp.m_FarmerCarry.m_CarryObject.Count);
                    }
                    break;
            }
        }

        // ---------------- client ----------------
        static readonly List<byte[]> early = new List<byte[]>();
        static readonly Dictionary<int, Vector3> targetPos = new Dictionary<int, Vector3>();
        static readonly Dictionary<int, float> targetRot = new Dictionary<int, float>();
        static readonly Dictionary<int, Vector3> basePos = new Dictionary<int, Vector3>();
        static readonly Dictionary<int, float> movingUntil = new Dictionary<int, float>();
        public static bool IsMovingOnClient(int uid) => movingUntil.TryGetValue(uid, out float t) && Time.realtimeSinceStartup < t + 1f;

        public static void QueueEarlyDelta(byte[] d) { early.Add(d); }

        public static readonly HashSet<int> HostKnown = new HashSet<int>();
        public const int LocalUidBase = 1000000000;

        public static void OnClientLoaded()
        {
            recheckAt = Time.realtimeSinceStartup + 5f;
            TimeManager.Instance.EnableNormalTime(false);
            foreach (var kv in Savables()) if (kv.Key && kv.Key.m_UniqueID > 0) HostKnown.Add(kv.Key.m_UniqueID);
            // Objects the joiner creates locally (edit-mode cursor/previews, effects) get UIDs from a range the
            // host never reaches, so they can't collide with objects the host creates later.
            // (never go back: local UIDs already handed out must not be reused)
            if (Traverse.Create(typeof(ObjectTypeList)).Field("m_UniqueIDCounter").GetValue<int>() < LocalUidBase) ObjectTypeList.Instance.SetUniqueIDCounter(LocalUidBase);
            Shared.OnClientLoaded();
            Scan.Reset(); Scan.Full();
            foreach (var d in early) Guard("early delta", () => ClientApplyDelta(Gz.Unpack(d)));
            early.Clear();
        }

        static readonly HashSet<int> scanSeen = new HashSet<int>();
        static readonly Dictionary<int, int> sentIp = new Dictionary<int, int>();
        public static double StatApplyMs, StatApplyMaxMs;
        public static void ClientApplyDelta(string json)
        {
            var swApply = System.Diagnostics.Stopwatch.StartNew();
            try { ClientApplyDeltaInner(json); }
            finally { double ms = swApply.Elapsed.TotalMilliseconds; StatApplyMs += ms; if (ms > StatApplyMaxMs) StatApplyMaxMs = ms; }
        }

        static bool destroyedLastBatch;
        /// Objects Unity destroyed together with their parent (a bot's load, a building's contents) never leave the
        /// UID registry or their plot's lists. Their UIDs then look taken: the host's copy gets a new UID ("UID
        /// mismatch") or loading throws "same key". Clear such leftovers (they read as null one frame after Destroy).
        public static int StatPurged;
        public static void PurgeDead()
        {
            if (Coop.Role != Role.Client) return;
            // collections ("Worker", "Savable", ...): the whistle screen walks the Worker one and stopped at a dead entry
            var cols = Traverse.Create(typeof(CollectionManager)).Field("m_Objects").GetValue<System.Collections.IDictionary>();
            if (cols != null)
                foreach (var col in cols.Values)
                {
                    var d = Traverse.Create(col).Field("m_ObjectsDictionary").GetValue<Dictionary<BaseClass, int>>();
                    if (d == null) continue;
                    List<BaseClass> deadKeys = null;
                    foreach (var k in d.Keys) if (!k) (deadKeys ?? (deadKeys = new List<BaseClass>())).Add(k);
                    if (deadKeys != null) { foreach (var k in deadKeys) d.Remove(k); StatPurged += deadKeys.Count; }
                }
            if (uidList == null) uidList = Traverse.Create(typeof(ObjectTypeList)).Field("m_UniqueIDList").GetValue<Dictionary<int, BaseClass>>();
            List<int> dead = null;
            if (uidList != null) foreach (var kv in uidList) if (!kv.Value) (dead ?? (dead = new List<int>())).Add(kv.Key);
            if (dead != null)
            {
                var ids = Traverse.Create(typeof(ObjectTypeList)).Field("m_UniqueIDs").GetValue<Dictionary<BaseClass, int>>();
                foreach (var u in dead) { var v = uidList[u]; uidList.Remove(u); if (ids != null && (object)v != null) ids.Remove(v); }
                StatPurged += dead.Count;
            }
            var pm = PlotManager.Instance;
            if (pm && pm.m_Plots != null)
                foreach (var p in pm.m_Plots)
                {
                    if (!p || p.m_Objects == null) continue;
                    List<int> gone = null;
                    foreach (var kv in p.m_Objects) if (!kv.Value) (gone ?? (gone = new List<int>())).Add(kv.Key);
                    if (gone == null) continue;
                    foreach (var u in gone) p.m_Objects.Remove(u);
                    if (p.m_ObjectDictionary != null) foreach (var l in p.m_ObjectDictionary.Values) l.RemoveAll(o => !o);
                    StatPurged += gone.Count;
                }
            FixPlayers();
        }

        /// Drop dead entries from the player list and make sure every live player (mine first) is in it.
        public static void FixPlayers()
        {
            var players = Traverse.Create(typeof(CollectionManager)).Field("m_Players").GetValue<List<BaseClass>>();
            var me = MyPlayerUID >= 0 ? ObjectTypeList.Instance.GetObjectFromUniqueID(MyPlayerUID, false) : null;
            if (players != null && me && me is FarmerPlayer && !players.Contains(me)) { players.RemoveAll(b => !b); players.Insert(0, me); }
            if (players != null && Savables() != null)
            {
                players.RemoveAll(b => !b);
                // a rebuilt player's new object must be in the list (input, HUD and teaching use player 0)
                foreach (var kv in Savables())
                    if (kv.Key is FarmerPlayer fp && fp.GetComponent<Savable>().GetIsSavable() && !players.Contains(fp)) { if (fp.m_UniqueID == MyPlayerUID) players.Insert(0, fp); else players.Add(fp); }
            }
        }

        static void ClientApplyDeltaInner(string json)
        {
            StatBatches++;
            if (destroyedLastBatch) { destroyedLastBatch = false; PurgeDead(); }
            var ups = SplitUp(json, out string rest);
            var n = JSON.Parse(ups != null ? rest : json);
            Applying = true;
            // Same mode the game's own loader uses: new objects keep the UID from the JSON and register in Load.
            ObjectTypeList.m_Loading = true;
            try
            {
                var wo = n["world"];
                if (wo != null && !wo.IsNull) { WorldState.ClientApply(wo); return; }
                var anim = n["an"];
                if (anim != null && !anim.IsNull) Anim.ClientPlay(anim.AsArray);
                var ae = n["ae"];
                if (ae != null && !ae.IsNull) { ApplyDigest(ae.AsArray); return; }
                var rm = n["rm"];
                if (rm != null) foreach (JSONNode u in rm.AsArray) Guard("rm", () => Remove(u.AsInt));
                var up = n["up"];
                if (ups != null && ups.Count > 0)
                {
                    foreach (var raw in ups) Guard("up", () => UpsertRaw(raw));
                    // Recreating a holder can destroy objects that were applied earlier in this batch.
                    foreach (var raw in ups)
                        if (!ObjectTypeList.Instance.GetObjectFromUniqueID(RawUid(raw), false))
                            Guard("re-up", () => { StatCreated++; Create(JSON.Parse(raw)); });
                }
                if (ups != null && ups.Count > 0 || up != null)
                {
                    if (up != null)
                    {
                        foreach (JSONNode o in up.AsArray) Guard("up " + o["ID"], () => Upsert(o));
                        foreach (JSONNode o in up.AsArray)
                            if (!ObjectTypeList.Instance.GetObjectFromUniqueID(o["UID"].AsInt, false))
                                Guard("re-up " + o["ID"], () => { StatCreated++; Create(o); });
                    }
                    // Children we cleared that the parent didn't rebuild (they live on their own on the host): ask again.
                    var need = new JSONArray();
                    foreach (var c in touchedNested) if (!ObjectTypeList.Instance.GetObjectFromUniqueID(c, false)) need.Add(c);
                    touchedNested.Clear();
                    if (need.Count > 0) { var r = new JSONObject(); r["a"] = "need"; r["u"] = need; Coop.I.Client.Host.Send(Msg.Input, r.ToString()); StatResync += need.Count; }
                }
                var tl = n["tl"];
                if (tl != null) foreach (JSONNode t in tl.AsArray)
                    TileManager.Instance.SetTileType(new TileCoord(t[0].AsInt, t[1].AsInt), (Tile.TileType)t[2].AsInt);
                var pv = n["pv"];
                if (pv != null) foreach (JSONNode i in pv.AsArray)
                {
                    var plot = PlotManager.Instance.m_Plots[i.AsInt];
                    if (!plot.m_Visible) plot.SetVisible(true, true);
                }
                var ipl = n["ip"];
                if (ipl != null) { var ia = ipl.AsArray; for (int i = 0; i + 1 < ia.Count; i += 2) SetLine(ia[i].AsInt, ia[i + 1].AsInt); }
                var mv = n["mv"];
                if (mv != null) foreach (JSONNode a in mv.AsArray)
                {
                    int uid = a[0].AsInt;
                    var np = new Vector3(a[1].AsFloat, a[2].AsFloat, a[3].AsFloat);
                    if (targetPos.TryGetValue(uid, out var prev) && (prev - np).sqrMagnitude > 0.001f) movingUntil[uid] = Time.realtimeSinceStartup + 0.25f;
                    targetPos[uid] = np;
                    targetRot[uid] = a[4].AsFloat;
                    Smooth.Add(uid, np, a[4].AsFloat);
                }
            }
            finally { ObjectTypeList.m_Loading = false; Applying = false; }
        }

        static void SetLine(int uid, int line)
        {
            var w = ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false) as Worker;
            if (!w || w.m_WorkerInterpreter == null || line < 0) return;
            var cs = w.m_WorkerInterpreter.GetCurrentScript();
            if (cs != null) cs.m_CurrentInstruction = line;
        }

        public static int StatSkippedParse;
        /// The host sends changed objects as {"up":[<saved text>,...], ...}. Take each object's text as sent: no parse of
        /// the whole batch and no re-printing each object to compare (both were garbage), and an object we already
        /// match is skipped before it is parsed at all.
        static List<string> SplitUp(string json, out string rest)
        {
            rest = null;
            const string head = "{\"up\":[";
            if (!json.StartsWith(head, StringComparison.Ordinal)) return null;
            var list = new List<string>();
            int i = head.Length, depth = 0, start = -1; bool str = false;
            for (; i < json.Length; i++)
            {
                char ch = json[i];
                if (str) { if (ch == '\\') i++; else if (ch == '"') str = false; continue; }
                if (ch == '"') { str = true; continue; }
                if (ch == '{' || ch == '[') { if (depth == 0 && ch == '{') start = i; depth++; }
                else if (ch == '}' || ch == ']')
                {
                    if (depth == 0) break;   // the end of "up"
                    depth--;
                    if (depth == 0 && start >= 0) { list.Add(json.Substring(start, i - start + 1)); start = -1; }
                }
            }
            if (i >= json.Length) return null;
            rest = i + 1 < json.Length && json[i + 1] == ',' ? "{" + json.Substring(i + 2) : "{}";
            return list;
        }
        static int RawUid(string raw)
        {
            int i = raw.IndexOf("\"UID\":", StringComparison.Ordinal); if (i < 0) return 0;
            i += 6; int v = 0; bool neg = false; if (i < raw.Length && raw[i] == '-') { neg = true; i++; }
            while (i < raw.Length && char.IsDigit(raw[i])) { v = v * 10 + (raw[i] - '0'); i++; }
            return neg ? -v : v;
        }
        static void UpsertRaw(string raw)
        {
            int uid = RawUid(raw);
            var b = uid > 0 ? ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false) : null;
            if (b && uid != Teach.EditingUid() && applied.TryGetValue(uid, out var ap) && ap.Key == b && ap.Value == StableW(b, raw).GetHashCode()
                && !(b.transform.parent && b.transform.parent.GetComponentInParent<BaseClass>()))
            { StatSame++; StatSameCached++; StatSkippedParse++; return; }
            Upsert(JSON.Parse(raw), raw);
        }

        public static int StatErrors, StatResync;
        static readonly HashSet<int> touchedNested = new HashSet<int>();
        static void Guard(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { StatErrors++; if (StatErrors < 50) Plugin.Log("apply " + what + " failed: " + e); }
        }

        public static int StatDigests, StatDigestFixes;
        static readonly Dictionary<int, float> recentApply = new Dictionary<int, float>();
        public static void MarkRecent(int uid) { if (uid > 0) recentApply[uid] = Time.realtimeSinceStartup; }
        static void ApplyDigest(JSONArray arr)
        {
            StatDigests++;
            var theirs = new Dictionary<int, int>();
            for (int i = 0; i + 1 < arr.Count; i += 2) theirs[arr[i].AsInt] = arr[i + 1].AsInt;
            var need = new JSONArray(); var extra = new List<BaseClass>();
            // the client only changes through applies (its sim is frozen): re-check what was touched plus a slice
            Scan.Step(false, 300);
            foreach (var kv in Scan.TopLevel())
            {
                var b = kv.Value.B;
                if (b.m_UniqueID == Teach.EditingUid()) { theirs.Remove(b.m_UniqueID); continue; }
                if (!theirs.TryGetValue(b.m_UniqueID, out int h)) { extra.Add(b); continue; }
                // just changed: the digest may predate it (and rebuilding now would pull it out from under open screens)
                if (kv.Value.Hash != h && !(recentApply.TryGetValue(b.m_UniqueID, out float at) && Time.realtimeSinceStartup - at < 3f)) { need.Add(b.m_UniqueID); applied.Remove(b.m_UniqueID); mustVerify.Add(b.m_UniqueID); }
                theirs.Remove(b.m_UniqueID);
            }
            foreach (var uid in theirs.Keys) need.Add(uid);   // host has, we don't
            foreach (var b in extra)
            {
                Plugin.Log("digest: removing extra " + b.m_UniqueID + " " + b.m_TypeIdentifier); Destroy(b);
            }
            if (need.Count > 0)
            {
                Plugin.Log("digest: requesting " + need.Count + " objects");
                var r = new JSONObject(); r["a"] = "need"; r["u"] = need; Coop.I.Client.Host.Send(Msg.Input, r.ToString());
            }
            StatDigestFixes += need.Count + extra.Count;
        }

        /// Remove an object on the client. Buildings must also leave the tile map (Building.StopUsing doesn't),
        /// or tiles keep pointing at a destroyed building.
        public static void Destroy(BaseClass b)
        {
            if (!b) return;
            var bld = b.GetComponent<Building>();
            if (bld)
            {
                try { MapManager.Instance.RemoveBuilding(bld); BuildingManager.Instance.RefreshBuilding(bld); }
                catch (Exception e) { Plugin.Log("map remove failed for " + b.m_UniqueID + ": " + e.Message); }
            }
            b.StopUsing();
            destroyedLastBatch = true;
        }

        static void Remove(int uid)
        {
            var b = ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false);
            if (b) { Destroy(b); StatRemoved++; }
            targetPos.Remove(uid); targetRot.Remove(uid);
        }

        static void Upsert(JSONNode o, string raw = null)
        {
            int uid = o["UID"].AsInt;
            Scan.Force(uid);   // re-check it at the next digest
            recentApply[uid] = Time.realtimeSinceStartup;
            if (uid == Teach.EditingUid()) { frozenSkipped = uid; return; }   // the editor UI holds this bot
            var b = ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false);
            bool reselect = Coop.Role == Role.Client && Teach.IsSelected(b);
            try { UpsertInner(o, uid, b, raw); }
            finally { if (reselect && ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false) != b) Teach.Reselect(uid); }
        }

        public static int StatSame, StatKeptEngaged;
        /// A farmer's save names what it is engaged with ("Engaged"), but Load only notes the id; the link is made when
        /// a whole world loads. Reloading a joiner's character in place while it used a workbench dropped the link, the
        /// check failed, and the character was rebuilt (closing the joiner's building screen mid-use).
        /// What a farmer holds (hands, pockets, upgrades, clothes): its held objects sit under it. Found directly
        /// instead of saving the whole bot (program and all) just to read the UIDs out of the text.
        static List<int> HeldUids(BaseClass b)
        {
            var l = new List<int>();
            foreach (var h in b.GetComponentsInChildren<Holdable>(true)) if (h && h != b && h.m_UniqueID > 0) l.Add(h.m_UniqueID);
            return l;
        }
        static void KeepEngaged(BaseClass b, JSONNode o, BaseClass before)
        {
            var f = b as Farmer;
            if (!f) return;
            int want = o["Engaged"] != null && !o["Engaged"].IsNull ? o["Engaged"].AsInt : -1;
            if (want <= 0) { if (f.m_EngagedObject) f.m_EngagedObject = null; return; }   // done with it on the host (Load doesn't clear it)
            if (f.m_EngagedObject) return;
            var e = before && before.m_UniqueID == want ? before : ObjectTypeList.Instance.GetObjectFromUniqueID(want, false);
            var a = e ? e.GetComponent<Actionable>() : null;
            if (a) { f.m_EngagedObject = a; StatKeptEngaged++; }
        }
        static void UpsertInner(JSONNode o, int uid, BaseClass b, string raw = null)
        {
            string want = raw ?? o.ToString();
            // Already exactly what the host has (e.g. everything the host re-sends right after a join): nothing to do.
            // Rebuilding those (blueprints with their building-to-be, bots carrying things) cost ~700 rebuilds and a
            // second-long stall on a big base, and briefly took the joiner's own character away.
            if (b && applied.TryGetValue(uid, out var ap) && ap.Key == b && ap.Value == StableW(b, want).GetHashCode()
                && !(b.transform.parent && b.transform.parent.GetComponentInParent<BaseClass>())) { StatSame++; StatSameCached++; return; }
            if (b && (bool)b.GetComponent<Savable>() && b.m_TypeIdentifier == ObjectTypeList.Instance.GetIdentifierFromSaveName(o["ID"], false)
                && !(b.transform.parent && b.transform.parent.GetComponentInParent<BaseClass>())
                && Stable(b, SaveObject(b, out _)) == StableW(b, want)) { StatSame++; Applied(b, uid, StableW(b, want)); return; }
            var nested = new List<int>();
            CollectUIDs(want, uid, nested);
            // a blueprint's building-to-be is created by its Load and only named by "NewID"
            if (o["NewID"] != null && !o["NewID"].IsNull && o["NewID"].AsInt > 0) nested.Add(o["NewID"].AsInt);
            if (nested.Count > 0 && b is Housing hs && b.m_TypeIdentifier == ObjectTypeList.Instance.GetIdentifierFromSaveName(o["ID"], false) && HousingInPlace(hs, o, want, uid)) return;
            if (nested.Count > 0 && b is Building bi && !(b is Housing) && !(b is ConverterFoundation) && !bi.m_ParentBuilding
                && b.m_TypeIdentifier == ObjectTypeList.Instance.GetIdentifierFromSaveName(o["ID"], false) && BuildingInPlace(bi, o, want, uid, nested)) return;
            if (nested.Count > 0)
            {
                // Children (carried items, nests, contents) are rebuilt by the parent's Load; clear them first.
                foreach (var c in nested) { var cb = ObjectTypeList.Instance.GetObjectFromUniqueID(c, false); if (cb && cb != b) Destroy(cb); touchedNested.Add(c); }
                // Players and bots pick things up and put them down all the time. Rebuilding them each time made them
                // hard to click and dropped the selection/editor on the joiner; reload the same object instead.
                if (b && b is Farmer && b.m_TypeIdentifier == ObjectTypeList.Instance.GetIdentifierFromSaveName(o["ID"], false))
                {
                    try
                    {
                        // what our copy holds that the host's no longer does (used up, put down): remove it too
                        var hadN = HeldUids(b);
                        foreach (var c in hadN) { if (nested.Contains(c)) continue; var cb = ObjectTypeList.Instance.GetObjectFromUniqueID(c, false); if (!cb || cb == b) continue; var hold = cb.transform.parent ? cb.transform.parent.GetComponentInParent<Farmer>() : null; if (hold && hold != b) continue; Destroy(cb); }
                        ClearHeld(b.GetComponent<Farmer>());
                        foreach (var c in nested) FreeUid(c);   // what it now holds keeps the host's UID (stale entries made Load pick new ones)
                        ResetRuntime(b);
                        if (ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false) == b) ObjectTypeList.Instance.RemoveActionable(b);
                        var engKeep = (b as Farmer) ? ((Farmer)b).m_EngagedObject : null;
                        b.GetComponent<Savable>().Load(o);
                        KeepEngaged(b, o, engKeep);
                        b.GetComponent<Savable>().PostLoad();
                        ApplyStored(b, o);
                        string kindN = "nested " + (string)o["ID"];
                        if (b.m_UniqueID == uid && TrustInPlace(kindN, uid)) { StatInPlace++; StatNestedInPlace++; StatVerifySkipped++; Applied(b, uid, StableW(b, want)); return; }
                        string got = Stable(b, SaveObject(b, out _));
                        bool okN = b.m_UniqueID == uid && got == StableW(b, want); Verified(kindN, uid, okN);
                        if (okN) { StatInPlace++; Applied(b, uid, StableW(b, want)); StatNestedInPlace++; return; }
                        LogDiff("nested " + (string)o["ID"], want, got);
                    }
                    catch (Exception e) { Plugin.Log("in-place nested load failed for " + o["ID"] + ": " + e.Message); }
                    foreach (var c in nested) { var cb = ObjectTypeList.Instance.GetObjectFromUniqueID(c, false); if (cb && cb != b) Destroy(cb); }
                }
                if (b) { Destroy(b); StatRecreated++; Count("nested:" + (string)o["ID"], uid); }
                else StatCreated++;
                Create(o);
                return;
            }
            if (b && b.transform.parent && b.transform.parent.GetComponentInParent<BaseClass>())
            {
                // Host has this as a free-standing object, but our copy is still a child of something
                // (e.g. a bees nest that fell out of its tree). Detach by rebuilding it on its own.
                var holder = b.transform.parent.GetComponentInParent<BaseClass>();
                Plugin.Log("detaching " + o["ID"] + " " + uid + " from " + holder.m_TypeIdentifier + " " + holder.m_UniqueID);
                var tree = holder.GetComponent<MyTree>();
                if (tree && tree.m_BeesNest == b) tree.m_BeesNest = null;
                Destroy(b); StatRecreated++; Count("detach:" + (string)o["ID"], uid);
                Create(o);
                return;
            }
            if (b)
            {
                var wantType = ObjectTypeList.Instance.GetIdentifierFromSaveName(o["ID"], false);
                var bld = b.GetComponent<Building>();
                bool moved = bld && (bld.m_TileCoord.x != o["TX"].AsInt || bld.m_TileCoord.y != o["TY"].AsInt || bld.m_Rotation != o["Rotation"].AsInt);
                if (b.m_TypeIdentifier == wantType && !moved)
                {
                    try
                    {
                        if (b is Farmer)
                        {
                            // it put something down on the host: drop what our copy still holds before reloading
                            var had = HeldUids(b);
                            // only what our copy still really holds: the same UID may already be in someone else's hands (applied first)
                            foreach (var c in had) { var cb = ObjectTypeList.Instance.GetObjectFromUniqueID(c, false); if (!cb || cb == b) continue; var hold = cb.transform.parent ? cb.transform.parent.GetComponentInParent<Farmer>() : null; if (hold && hold != b) continue; Destroy(cb); }
                            ClearHeld(b.GetComponent<Farmer>());
                        }
                        else if (b is Converter cvx && cvx.m_Ingredients != null && cvx.m_Ingredients.Count > 0)
                        {
                            // emptied on the host (used up): Load only adds what the host lists
                            foreach (var h in cvx.m_Ingredients) if (h) Destroy(h);
                            cvx.m_Ingredients.Clear();
                        }
                        ResetRuntime(b);
                        // Savable.Load re-registers the UID (ChangeActionable); unregister first or it allocates a new one.
                        if (ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false) == b) ObjectTypeList.Instance.RemoveActionable(b);
                        var engKeep = (b as Farmer) ? ((Farmer)b).m_EngagedObject : null;
                        b.GetComponent<Savable>().Load(o);
                        KeepEngaged(b, o, engKeep);
                        b.GetComponent<Savable>().PostLoad();
                        // plants (and storage) apply part of what they load only on Refresh, as after the game's own load
                        if (!(b is Farmer)) b.GetComponent<Actionable>().SendAction(new ActionInfo(ActionType.Refresh, default(TileCoord)));
                        ApplyStored(b, o);
                        string kindM = "in-place " + (string)o["ID"];
                        if (b.m_UniqueID == uid && TrustInPlace(kindM, uid)) { StatInPlace++; StatVerifySkipped++; Applied(b, uid, StableW(b, want)); return; }
                        string got = Stable(b, SaveObject(b, out _));
                        bool okM = b.m_UniqueID == uid && got == StableW(b, want); Verified(kindM, uid, okM);
                        if (okM) { StatInPlace++; Applied(b, uid, StableW(b, want)); return; }
                        LogDiff((string)o["ID"], want, got);
                    }
                    catch (Exception e) { Plugin.Log("in-place load failed for " + o["ID"] + ": " + e.Message); }
                }
                Count((string)o["ID"], uid);
                if (b) Destroy(b);
                StatRecreated++;
            }
            else StatCreated++;
            Create(o);
        }

        public static int StatNestedInPlace, StatHousingInPlace;

        /// Settlement huts save their folk inside them, and folk change all the time (energy, timers), so every hut
        /// changed every scan and was rebuilt with its folk. Update the hut and each of its folk in place instead,
        /// as long as the same folk live there.
        public static int StatBuildingInPlace, StatSameCached;
        /// Buildings with contents (storage stacks, machines holding ingredients) were destroyed and rebuilt on every
        /// change: a stack's count went up by one, a bench got an ingredient. ~380 rebuilds in 3 minutes on a big base,
        /// each a pile of new objects (and the old ones' garbage). Reload the same building instead: its levels in
        /// place, its ingredients replaced. Anything else nested (unknown contents) still takes the rebuild.
        static bool BuildingInPlace(Building bld, JSONNode o, string want, int uid, List<int> nested)
        {
            try
            {
                var lv = o["Levels"]; var lvArr = lv != null && !lv.IsNull ? lv.AsArray : null;
                var ing = o["IngredientsItems"]; var ingArr = ing != null && !ing.IsNull ? ing.AsArray : null;
                var known = new HashSet<int>();
                void Add(JSONNode e) { int u = e["UID"].AsInt; if (u > 0) known.Add(u); var l = new List<int>(); CollectUIDs(e.ToString(), u, l); foreach (var x in l) known.Add(x); }
                if (lvArr != null) foreach (JSONNode e in lvArr) Add(e);
                if (ingArr != null) foreach (JSONNode e in ingArr) Add(e);
                foreach (var n in nested) if (!known.Contains(n)) return false;   // something else inside
                // the levels must be the same buildings, in the same order
                int nl = lvArr != null ? lvArr.Count : 0;
                int have = bld.m_Levels != null ? bld.m_Levels.Count : 0;
                if (nl != have) return false;
                for (int i = 0; i < nl; i++)
                {
                    var l = bld.m_Levels[i];
                    if (!l || l.m_UniqueID != lvArr[i]["UID"].AsInt) return false;
                    var lnest = new List<int>(); CollectUIDs(lvArr[i].ToString(), l.m_UniqueID, lnest);
                    if (lnest.Count > 0) return false;
                }
                var cv = bld as Converter;
                if (ingArr != null && ingArr.Count > 0 && !cv) return false;
                if (cv && cv.m_Ingredients != null)
                {
                    foreach (var h in cv.m_Ingredients) if (h) Destroy(h);
                    cv.m_Ingredients.Clear();
                }
                if (ingArr != null) foreach (JSONNode e in ingArr) { int u = e["UID"].AsInt; if (u > 0) FreeUid(u); }
                if (ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false) == bld) ObjectTypeList.Instance.RemoveActionable(bld);
                bld.GetComponent<Savable>().Load(o);
                bld.GetComponent<Savable>().PostLoad();
                for (int i = 0; i < nl; i++)
                {
                    var l = bld.m_Levels[i];
                    if (ObjectTypeList.Instance.GetObjectFromUniqueID(l.m_UniqueID, false) == l) ObjectTypeList.Instance.RemoveActionable(l);
                    l.GetComponent<Savable>().Load(lvArr[i]);
                    l.GetComponent<Savable>().PostLoad();
                }
                ApplyStored(bld, o);
                string kindB = "building " + (string)o["ID"];
                if (bld.m_UniqueID == uid && TrustInPlace(kindB, uid)) { StatInPlace++; StatBuildingInPlace++; StatVerifySkipped++; Applied(bld, uid, StableW(bld, want)); return true; }
                string got = Stable(bld, SaveObject(bld, out _));
                bool okB = bld.m_UniqueID == uid && got == StableW(bld, want); Verified(kindB, uid, okB);
                if (okB) { StatInPlace++; Applied(bld, uid, StableW(bld, want)); StatBuildingInPlace++; return true; }
                LogDiff("building " + (string)o["ID"], want, got);
            }
            catch (Exception e) { Plugin.Log("in-place building load failed for " + o["ID"] + ": " + e.Message); }
            return false;
        }

        static bool HousingInPlace(Housing h, JSONNode o, string want, int uid)
        {
            try
            {
                var arr = o["Folks"].AsArray;
                int n = arr != null ? arr.Count : 0;
                if (h.m_Folks == null || h.m_Folks.Count != n) return false;
                var byUid = new Dictionary<int, Folk>();
                foreach (var f in h.m_Folks) { if (!f) return false; byUid[f.m_UniqueID] = f; }
                for (int i = 0; i < n; i++) if (!byUid.ContainsKey(arr[i]["UID"].AsInt)) return false;
                // the hut itself, without re-creating its folk
                var shell = JSON.Parse(want); shell["Folks"] = new JSONArray();
                if (ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false) == h) ObjectTypeList.Instance.RemoveActionable(h);
                h.GetComponent<Savable>().Load(shell);
                h.GetComponent<Savable>().PostLoad();
                for (int i = 0; i < n; i++)
                {
                    var f = byUid[arr[i]["UID"].AsInt];
                    if (ObjectTypeList.Instance.GetObjectFromUniqueID(f.m_UniqueID, false) == f) ObjectTypeList.Instance.RemoveActionable(f);
                    f.GetComponent<Savable>().Load(arr[i]);
                }
                string got = Stable(h, SaveObject(h, out _));
                if (h.m_UniqueID == uid && got == StableW(h, want)) { StatInPlace++; Applied(h, uid, StableW(h, want)); StatHousingInPlace++; return true; }
                LogDiff("housing " + (string)o["ID"], want, got);
            }
            catch (Exception e) { Plugin.Log("in-place housing load failed for " + o["ID"] + ": " + e.Message); }
            return false;
        }

        /// Empty a farmer's hands, pockets, upgrades and clothes lists (their objects were destroyed just before)
        /// so its Load can fill them from the host's JSON without duplicates.
        /// Not (or no longer) held by f: destroyed, or now in someone else's hands (another update, or the joiner's
        /// own screen, moved it first).
        static bool NotHeldBy(Holdable x, Farmer f)
        {
            if (!x || x.m_UniqueID == -1) return true;
            var hold = x.transform.parent ? x.transform.parent.GetComponentInParent<Farmer>() : null;   // (upgrades like a backpack hold items too: only another farmer counts)
            return hold && hold != f;
        }
        static void ClearHeld(Farmer f)
        {
            if (!f) return;
            try { f.m_FarmerCarry.m_CarryObject.RemoveAll(x => NotHeldBy(x, f)); } catch { }
            foreach (var comp in new Component[] { f.m_FarmerInventory, f.GetComponent<FarmerUpgrades>(), f.GetComponent<FarmerClothes>() })
            {
                if (!comp) continue;
                foreach (var fld in comp.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    if (fld.FieldType == typeof(List<Holdable>))
                    {
                        var l = fld.GetValue(comp) as List<Holdable>;
                        l?.RemoveAll(x => NotHeldBy(x, f));
                    }
                    else if (fld.FieldType == typeof(Holdable[]))
                    {
                        var a = fld.GetValue(comp) as Holdable[];
                        if (a != null) for (int i = 0; i < a.Length; i++) if (a[i] != null && NotHeldBy(a[i], f)) a[i] = null;
                    }
                }
            }
        }

        /// Load only adds a bot's running script, fail-jump stack and instructions; clear them so a reload replaces them.
        /// A storage's count is loaded into a temporary and only made real by a Refresh, which the game skips for a
        /// storage that sits in another building (and Refresh isn't sent at all for an in-place update). Those kept the
        /// old count on the joiner, failed the in-place check and were rebuilt over and over. Take the host's count.
        static readonly HarmonyLib.AccessTools.FieldRef<Storage, int> tempStored = HarmonyLib.AccessTools.FieldRefAccess<Storage, int>("m_TempStored");
        static void ApplyStored(BaseClass b, JSONNode o)
        {
            void One(Storage st, JSONNode n)
            {
                if (!st || n == null || n["Stored"] == null || n["Stored"].IsNull) return;
                int want = n["Stored"].AsInt;
                // the loaded count waits in m_TempStored for a Refresh; left there, a later Refresh put the old
                // count back (a storage drifting below the host's by one or two). Always clear it.
                tempStored(st) = 0;
                if (st.m_Stored == want) return;
                Diag.Log("stored " + st.m_UniqueID + ": " + st.m_Stored + " -> " + want);
                st.m_Stored = want;
                st.UpdateStored();
            }
            try
            {
                One(b.GetComponent<Storage>(), o);
                var lv = o["Levels"];
                if (lv != null && !lv.IsNull)
                    foreach (JSONNode l in lv.AsArray)
                    {
                        var lb = ObjectTypeList.Instance.GetObjectFromUniqueID(l["UID"].AsInt, false);
                        if (lb) One(lb.GetComponent<Storage>(), l);
                    }
            }
            catch (Exception e) { Plugin.Log("apply stored: " + e.Message); }
        }

        static void ResetRuntime(BaseClass b)
        {
            var w = b as Worker;
            if (!w || w.m_WorkerInterpreter == null) return;
            try
            {
                var t = Traverse.Create(w.m_WorkerInterpreter);
                t.Field("m_CurrentScript").SetValue(null);
                t.Field("m_FailJumpStack").GetValue<System.Collections.IList>()?.Clear();
                w.m_WorkerInterpreter.m_HighInstructions.m_List.Clear();
            }
            catch (Exception e) { Plugin.Log("reset bot runtime: " + e.Message); }
        }

        static readonly HashSet<string> diffLogged = new HashSet<string>();
        static void LogDiff(string type, string want, string got)
        {
            int i = 0; while (i < want.Length && i < got.Length && want[i] == got[i]) i++;
            // one log line per object type and differing field (the key just before the first difference)
            int q = want.LastIndexOf("\":", Math.Min(i, want.Length - 1), StringComparison.Ordinal); int q0 = q > 0 ? want.LastIndexOf('"', q - 1) : -1;
            string field = q0 >= 0 ? want.Substring(q0 + 1, q - q0 - 1) : "?";
            if (!diffLogged.Add(type + "." + field)) return;
            Diag.Log("in-place update of " + type + " didn't match (field " + field + "): rebuilt instead");
            int from = Math.Max(0, i - 60);
            Plugin.Log("in-place mismatch " + type + " at " + i + "\n  want: " + want.Substring(from, Math.Min(200, want.Length - from)) + "\n  got:  " + got.Substring(from, Math.Min(200, got.Length - from)));
        }

        public static int StatUidFixes, StatStaleUids;
        static Dictionary<int, BaseClass> uidList;
        /// Drop a registry entry for this UID if it belongs to nothing live (or we were told to clear it).
        static void FreeUid(int uid)
        {
            if (uidList == null) uidList = Traverse.Create(typeof(ObjectTypeList)).Field("m_UniqueIDList").GetValue<Dictionary<int, BaseClass>>();
            if (uidList == null || !uidList.TryGetValue(uid, out var held)) return;
            if (held && held.gameObject.activeInHierarchy && held.m_UniqueID == uid) { Destroy(held); if (!uidList.ContainsKey(uid)) return; }
            uidList.Remove(uid);
            var ids = Traverse.Create(typeof(ObjectTypeList)).Field("m_UniqueIDs").GetValue<Dictionary<BaseClass, int>>();
            if (ids != null && (object)held != null) ids.Remove(held);
            StatStaleUids++;
            var pm = PlotManager.Instance;
            if (pm && pm.m_Plots != null)
                foreach (var p in pm.m_Plots)
                    if (p && p.m_Objects != null && p.m_Objects.TryGetValue(uid, out var po))
                    {
                        p.m_Objects.Remove(uid);
                        if (p.m_ObjectDictionary != null && (object)po != null) foreach (var l in p.m_ObjectDictionary.Values) l.Remove(po);
                    }
            if (StatStaleUids <= 50) Plugin.Log("freed stale UID " + uid + " (held by " + ((object)held == null ? "nothing" : held ? held.m_TypeIdentifier + (held.gameObject.activeInHierarchy ? "" : ", inactive") : "a destroyed object") + ")");
        }

        static void CleanupUid(int uid)
        {
            try
            {
                var half = ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false);
                if (half) { try { half.StopUsing(); } catch { } }
                var list = Traverse.Create(typeof(ObjectTypeList)).Field("m_UniqueIDList").GetValue<Dictionary<int, BaseClass>>();
                if (list != null && list.TryGetValue(uid, out var still))
                {
                    list.Remove(uid);
                    var ids = Traverse.Create(typeof(ObjectTypeList)).Field("m_UniqueIDs").GetValue<Dictionary<BaseClass, int>>();
                    if (still && ids != null) ids.Remove(still);
                }
                Plugin.Log("cleaned up half-created object " + uid);
            }
            catch (Exception e) { Plugin.Log("cleanup " + uid + ": " + e.Message); }
        }

        static void Count(string k, int uid = -1)
        {
            RecreatedByType[k] = RecreatedByType.TryGetValue(k, out int c) ? c + 1 : 1;
            RecreatedByTypeWindow[k] = RecreatedByTypeWindow.TryGetValue(k, out int w) ? w + 1 : 1;
            if (uid >= 0) { RecreatedUid[uid] = RecreatedUid.TryGetValue(uid, out int u) ? u + 1 : 1; if (Diag.On) Diag.Log("rebuilt " + k + "#" + uid + " (could not update in place)"); }
        }
        public static readonly Dictionary<string, int> RecreatedByTypeWindow = new Dictionary<string, int>();   // diagnostics: last 10 s
        public static readonly Dictionary<int, int> RecreatedUid = new Dictionary<int, int>();

        // Only saved objects count: they start {"ID":"<type>","UID":n. A bot's program also names objects by "UID"
        // (the workbench or storage an instruction uses); taking those as the bot's children made every bot update
        // destroy the buildings its program uses (hundreds missing after a join on a big base, endless re-requests).
        static readonly System.Text.RegularExpressions.Regex UidRx = new System.Text.RegularExpressions.Regex("\\{\\s*\"ID\"\\s*:\\s*\"[^\"]*\"\\s*,\\s*\"UID\"\\s*:\\s*(-?\\d+)");
        /// All UIDs mentioned in the object's JSON except its own (one occurrence of the root UID is skipped).
        public static void CollectUIDs(string json, int rootUid, List<int> into)
        {
            bool skipped = false;
            foreach (System.Text.RegularExpressions.Match m in UidRx.Matches(json))
            {
                int u = int.Parse(m.Groups[1].Value);
                if (u == rootUid && !skipped) { skipped = true; continue; }
                if (u > 0) into.Add(u);
            }
        }

        public static BaseClass Create(JSONNode o)
        {
            int uid = o["UID"].AsInt;
            var type = ObjectTypeList.Instance.GetIdentifierFromSaveName(o["ID"], false);
            if (type == ObjectTypeList.m_Total) { Plugin.Log("unknown type " + o["ID"]); return null; }
            // A client-local object may hold this UID (spawn effects etc). Host wins.
            var clash = ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false);
            if (clash) Destroy(clash);
            FreeUid(uid);   // also entries left by objects Unity destroyed with their parent (never unregistered)
            // the same for what it holds (a bot carrying an item our copy still has lying on the ground)
            var kids = new List<int>(); CollectUIDs(o.ToString(), uid, kids);
            foreach (var k in kids)
            {
                var kb = ObjectTypeList.Instance.GetObjectFromUniqueID(k, false);
                if (kb) Destroy(kb);
                FreeUid(k);
            }
            HostKnown.Add(uid);
            // Players and bots create HUD indicators under HudManager's indicator root in PostCreate. Some screens
            // (inventory, teaching) hide that root, so the indicator's Awake hasn't run and PostCreate throws.
            // Briefly make the root active while creating.
            var reactivate = new List<GameObject>();
            var root = HudManager.Instance ? HudManager.Instance.m_IndicatorsRootTransform : null;
            for (var t = root; t != null; t = t.parent) if (!t.gameObject.activeSelf) { t.gameObject.SetActive(true); reactivate.Add(t.gameObject); }
            BaseClass b = null;
            try { b = ObjectTypeList.Instance.CreateObjectFromSaveName(o, Vector3.zero, Quaternion.identity); }
            catch
            {
                // Don't leave a half-built object holding the UID (every later create would fail with "same key").
                CleanupUid(uid);
                throw;
            }
            finally { foreach (var g in reactivate) g.SetActive(false); }
            if (!b) return null;
            b.GetComponent<Savable>().Load(o);
            var bld = b.GetComponent<Building>();
            if (bld) bld.PostLoad(o);
            b.GetComponent<Savable>().PostLoad();
            b.GetComponent<Actionable>().SendAction(new ActionInfo(ActionType.Refresh, default(TileCoord)));
            ApplyStored(b, o);
            if (b.m_UniqueID != uid)
            {
                // the UID was still taken: move the new object onto the host's UID so later updates find it
                Plugin.Log("UID mismatch on create: wanted " + uid + " got " + b.m_UniqueID + "; moving it");
                ObjectTypeList.Instance.RemoveActionable(b);
                FreeUid(uid);
                ObjectTypeList.Instance.ChangeActionable(b, uid);
                StatUidFixes++;
            }
            if (b is FarmerPlayer) FixPlayers();
            if (uid == MyPlayerUID) Teach.AttachToMyPlayer();
            if (Coop.Role == Role.Client && b.GetComponent<Worker>()) Groups.Relink(b.GetComponent<Worker>());
            return b;
        }

        static int frozenSkipped = -1, lastEditing = -1;
        static float nextSweep;
        public static int StatSwept;
        // Some of the world settles a moment after the join load (storage stacks re-counting their levels); our record
        // of it was taken before, so the digest saw no difference and nothing was repaired. Look at everything again.
        static float recheckAt;
        public static void ClientFrame()
        {
            if (!Coop.I.ClientLoaded) return;
            if (recheckAt > 0 && Time.realtimeSinceStartup > recheckAt) { recheckAt = 0; Scan.Full(); Plugin.Log("joiner: re-checked the world after loading"); }
            int editing = Teach.EditingUid();
            if (editing != lastEditing)
            {
                // editor closed (or switched bot): fetch the bot's current state now instead of waiting for the digest
                if (lastEditing >= 0 && lastEditing == frozenSkipped)
                {
                    var r = new JSONObject(); var u = new JSONArray(); u.Add(lastEditing); r["a"] = "need"; r["u"] = u;
                    Coop.I.Client.Host.Send(Msg.Input, r.ToString());
                    frozenSkipped = -1;
                }
                lastEditing = editing;
            }
            if (Time.realtimeSinceStartup >= nextSweep)
            {
                // Side effects of applying host state (e.g. a felled tree dropping logs and seeds) can spawn
                // savable objects locally. They carry local-range UIDs; the host's real ones arrive by sync.
                nextSweep = Time.realtimeSinceStartup + 0.5f;
                List<BaseClass> local = null;
                foreach (var kv in Savables())
                {
                    var b = kv.Key;
                    if (b && b.m_UniqueID >= LocalUidBase && b.GetComponent<Savable>().GetIsSavable()) (local ?? (local = new List<BaseClass>())).Add(b);
                }
                if (local != null) foreach (var b in local) { StatSwept++; Destroy(b); }
            }
            float k = Mathf.Clamp01(Time.unscaledDeltaTime * 12f);
            var drop = new List<int>();
            foreach (var kv in targetPos)
            {
                var b = ObjectTypeList.Instance.GetObjectFromUniqueID(kv.Key, false);
                if (!b) { drop.Add(kv.Key); continue; }
                var t = b.transform;
                // play movement back a fixed moment behind the host, between the two samples around that time:
                // steady motion even when packets arrive in bursts (internet / Steam relay)
                if (!Smooth.Sample(kv.Key, out var bp, out float ry, out bool walking)) { bp = kv.Value; ry = targetRot.TryGetValue(kv.Key, out var r0) ? r0 : t.eulerAngles.y; walking = false; }
                basePos[kv.Key] = bp;
                if (SelectHold.Hold(b, bp)) continue;   // whistle screen open: bots stand still (Feel.cs)
                t.position = walking ? bp + Vector3.up * Anim.Hop(b, Time.realtimeSinceStartup) : bp;
                // its tile follows (bots' saved tile isn't re-sent on every step any more)
                var tco = b as TileCoordObject;
                if (tco) { var tc = new TileCoord(bp); if (tco.m_TileCoord != tc) tco.SetTilePosition(tc); }
                // my own idle character faces my cursor (local); everyone else faces where the host says
                var f = b as Farmer;
                bool mineIdle = kv.Key == MyPlayerUID && f && f.m_State == Farmer.State.None;
                if (!mineIdle) t.rotation = Quaternion.Slerp(t.rotation, Quaternion.Euler(0, ry, 0), Mathf.Clamp01(Time.unscaledDeltaTime * 20f));
            }
            foreach (var d in drop) { targetPos.Remove(d); targetRot.Remove(d); basePos.Remove(d); movingUntil.Remove(d); Smooth.Forget(d); }
        }

        public static void ClientSendGo(TileCoord dest, Actionable target, AFO.AT type)
        {
            var n = new JSONObject();
            n["a"] = "go"; n["x"] = dest.x; n["y"] = dest.y;
            n["t"] = !target ? -1 : target.GetComponent<Plot>() ? -2 : target.m_UniqueID; n["k"] = (int)type;
            Coop.I.Client.Host.Send(Msg.Input, n.ToString());
        }
    }

    // ---------- patches ----------

    [HarmonyPatch(typeof(TileManager), nameof(TileManager.SetTileType))]
    static class P_TileRecord
    {
        static void Postfix(TileCoord Position, Tile.TileType NewTileType) { Sync.HostRecordTile(Position, NewTileType); }
    }

    [HarmonyPatch(typeof(Plot), nameof(Plot.SetVisible))]
    static class P_PlotRecord
    {
        static void Postfix(Plot __instance) { Sync.HostRecordPlot(__instance); }
    }

    // Client: keep simulation frozen; the host runs the world.
    [HarmonyPatch(typeof(TimeManager), nameof(TimeManager.EnableNormalTime))]
    static class P_ClientFreeze
    {
        static void Prefix(ref bool Enabled) { if (Coop.Role == Role.Client && Coop.I.ClientLoaded) Enabled = false; }
    }

    // Client: my player's clicks go to the host instead of moving the local copy.
    [HarmonyPatch(typeof(FarmerPlayer), nameof(FarmerPlayer.GoToAndAction))]
    static class P_ClientInput
    {
        static bool Prefix(FarmerPlayer __instance, TileCoord Destination, Actionable TargetObject, AFO.AT NewInputType)
        {
            if (Coop.Role != Role.Client || !Coop.I.ClientLoaded) return true;
            if (__instance.m_UniqueID == Sync.MyPlayerUID) Sync.ClientSendGo(Destination, TargetObject, NewInputType);
            return false;
        }
    }

    // Each machine sees its own FarmerPlayer as player 0 (input, UI and teaching use index 0).
    [HarmonyPatch(typeof(CollectionManager), nameof(CollectionManager.GetPlayers))]
    static class P_PlayerOrder
    {
        static void Postfix(List<BaseClass> __result)
        {
            if (Coop.Role == Role.Off) Persist.Ensure();
            if (Coop.Role == Role.Client && __result != null && (__result.Count == 0 || !__result[0])) Sync.FixPlayers();   // a rebuilt player's old entry
            int mine = Coop.Role == Role.Client ? Sync.MyPlayerUID : Coop.Role == Role.Host ? Sync.HostPlayerUID : Persist.HostUid;
            if (mine < 0 || __result == null || __result.Count < 2) return;
            if (__result[0] && __result[0].m_UniqueID == mine) return;
            int i = __result.FindIndex(b => b && b.m_UniqueID == mine);
            if (i > 0) { var b = __result[i]; __result.RemoveAt(i); __result.Insert(0, b); }
        }
    }
}

namespace AutonautsCoop
{
    // The game saves floats as (int)(value * 100) and loads them as int / 100f. Loading 8152 gives 81.52f, which saves
    // back as 8151 (81.52f * 100 = 8151.9995, truncated). On the joiner that one-unit drift made every in-place update
    // of a bot with part of its energy left fail its check, so the bot was rebuilt instead (dozens a second in a busy
    // base: choppy, and bots flickered out from under the cursor). While applying host data, load a value that saves
    // back exactly.
    [HarmonyLib.HarmonyPatch(typeof(JSONUtils), nameof(JSONUtils.GetAsFloat))]
    static class P_ExactFloatRoundTrip
    {
        static void Postfix(JSONNode Node, string Name, ref float __result)
        {
            if (!Sync.Applying || Coop.Role != Role.Client) return;
            var j = Node[Name];
            if (j == null || j.IsNull) return;
            int n = j.AsInt;
            __result = (n >= 0 ? n + 0.25f : n - 0.25f) / 100f;
        }
    }
}

namespace AutonautsCoop
{
    // Settlement mode: the Transmitter makes its certificate rewards locally (in its Start, right after the world
    // loads, and again when quests complete). On the joiner they took the next UIDs, which the host hands out next
    // (the joiner's own character, new items), so host objects landed on local ones: characters parented to the
    // Transmitter, "same key" errors, endless rebuilds. On the joiner, give those rewards local UIDs.
    [HarmonyLib.HarmonyPatch(typeof(Transmitter), nameof(Transmitter.UpdateRewards))]
    static class P_ClientRewardsLocalUids
    {
        static int localNext = Sync.LocalUidBase + 500000000;   // its own block, apart from other local objects
        static HarmonyLib.Traverse Counter => HarmonyLib.Traverse.Create(typeof(ObjectTypeList)).Field("m_UniqueIDCounter");
        static void Prefix(out int __state)
        {
            __state = -1;
            if (Coop.Role != Role.Client) return;
            __state = Counter.GetValue<int>();
            Counter.SetValue(localNext);
        }
        static void Finalizer(int __state)
        {
            if (__state < 0) return;
            localNext = Counter.GetValue<int>();
            Counter.SetValue(__state);
        }
    }
}

namespace AutonautsCoop
{
    // Host: new objects (and ones that got a new UID) are looked at right away, not when the spread-out walk gets there.

}

namespace AutonautsCoop
{
}

namespace AutonautsCoop
{
    // Joiner: an old copy being destroyed must not unregister its UID from the new copy that already took it.
    [HarmonyLib.HarmonyPatch(typeof(ObjectTypeList), nameof(ObjectTypeList.RemoveActionable))]
    static class P_ClientRemoveOnlyOwn
    {
        static System.Collections.Generic.Dictionary<int, BaseClass> list;
        static bool Prefix(BaseClass NewBaseClass)
        {
            if (Coop.Role != Role.Client || (object)NewBaseClass == null) return true;
            if (list == null) list = HarmonyLib.Traverse.Create(typeof(ObjectTypeList)).Field("m_UniqueIDList").GetValue<System.Collections.Generic.Dictionary<int, BaseClass>>();
            if (list != null && list.TryGetValue(NewBaseClass.m_UniqueID, out var owner) && (object)owner != (object)NewBaseClass && owner) return false;
            return true;
        }
    }
}

namespace AutonautsCoop
{
    // Joiner: removing a building blueprint that is mid-build (an old copy being replaced, or one the host finished)
    // ran the game's "stage finished" code, which needs the blueprint's target building, already cleared by then.
    // The exception stopped the removal half way: the old blueprint stayed in the world and threw every frame
    // (~150 errors a second, each a log line with a stack trace: a big source of joiner hitches). Remove it as idle.
    [HarmonyLib.HarmonyPatch(typeof(ConverterFoundation), nameof(ConverterFoundation.StopUsing))]
    static class P_ClientFoundationStop
    {
        static void Prefix(ConverterFoundation __instance)
        {
            if (Coop.Role == Role.Client && __instance.m_State != Converter.State.Idle) __instance.m_State = Converter.State.Idle;
        }
    }

    // Joiner: a blueprint without its target building (a leftover from the above in an older session) does nothing.
    [HarmonyLib.HarmonyPatch(typeof(ConverterFoundation), "UpdateUpgrading")]
    static class P_ClientFoundationNoTarget
    {
        static bool Prefix(ConverterFoundation __instance) => !(Coop.Role == Role.Client && __instance.m_NewBuilding == null);
    }
}

namespace AutonautsCoop
{
    // Joiner: placing a building (every rebuilt copy) made the game walk every bot's program and point its
    // instructions for that building at the building's current access tile, and re-send moving bots there. The host's
    // bot data already says where each bot is going; the rewrite made the joiner's bots differ from the host's (a
    // bot recorded before its storage was moved keeps the old tile on the host) and cost a pass over every bot.
    [HarmonyLib.HarmonyPatch(typeof(BuildingManager), "PatchUpWorkers")]
    static class P_ClientNoPatchUpWorkers
    {
        static bool Prefix() => Coop.Role != Role.Client;   // also outside batches (deferred placement, the join load)
    }
}

namespace AutonautsCoop
{
    // Joiner: rebuilding a storage stack re-adds its levels, and the game treats each as a player stacking storage:
    // every bot's program is pointed at the stack and recompiled (53 bots on a big base, for each rebuild). The host's
    // bot data already has the result. Run it as during loading, which skips that part.
    [HarmonyLib.HarmonyPatch(typeof(Storage), nameof(Storage.AddBuilding))]
    static class P_ClientStackQuiet
    {
        // __state: 0 = not ours, 1 = was not loading, 2 = was loading (stacks can nest, so no shared field)
        static void Prefix(out int __state)
        {
            __state = 0;
            if (Coop.Role != Role.Client || SaveLoadManager.Instance == null) return;
            __state = SaveLoadManager.Instance.m_Loading ? 2 : 1;
            SaveLoadManager.Instance.m_Loading = true;
        }
        static void Finalizer(int __state) { if (__state != 0 && SaveLoadManager.Instance != null) SaveLoadManager.Instance.m_Loading = __state == 2; }
    }
}

namespace AutonautsCoop
{
    // Joiner: something on the joiner took items out of a storage on its own (a count drifting down by one or two
    // after the host's update). Storage counts are the host's; ignore local takes and log who tried (first few).
    [HarmonyLib.HarmonyPatch(typeof(Storage), nameof(Storage.ReleaseStored))]
    static class P_ClientNoLocalTake
    {
        static int logged;
        static bool Prefix(Storage __instance, ref int __result)
        {
            if (Coop.Role != Role.Client || !Coop.I.ClientLoaded) return true;
            // while applying host data too: the host's JSON already has the count (a bot's reload must not take again)
            if (logged++ < 5) Diag.Log("blocked a local take from storage " + (Sync.Applying ? "(while applying) " : "") + __instance.m_UniqueID + ": " + Environment.StackTrace.Replace("\n", " | ").Substring(0, Math.Min(900, Environment.StackTrace.Length)));
            __result = 0;
            return false;
        }
    }
}

namespace AutonautsCoop
{
    // Joiner: the game's own "add to storage" (capacity-checked) clamped a count the host has over capacity (a stack
    // merged past its limit: 100 in a 99 stack) back down. Counts are the host's; ignore local adds and log who
    // tried (first few). Structural merges (stacking levels, CheckCapacity: false) still run.
    [HarmonyLib.HarmonyPatch(typeof(Storage), nameof(Storage.AddToStored), new[] { typeof(ObjectType), typeof(int), typeof(Actionable), typeof(bool) })]
    static class P_ClientNoLocalAdd
    {
        static int logged;
        static bool Prefix(Storage __instance, bool CheckCapacity)
        {
            if (Coop.Role != Role.Client || !Coop.I.ClientLoaded || !CheckCapacity) return true;
            if (logged++ < 5) Diag.Log("blocked a local add to storage " + __instance.m_UniqueID + (Sync.Applying ? " (while applying)" : "") + ": " + Environment.StackTrace.Replace("\n", " | ").Substring(0, Math.Min(900, Environment.StackTrace.Length)));
            return false;
        }
    }
}

namespace AutonautsCoop
{
    // Joiner: Steam Workshop (Lua) mods run on every machine. One on a big base balanced two storages every few
    // seconds through ModStorage, so the joiner's counts fought the host's (the host runs the same mod; its results
    // arrive with the host's data). Storage writes from mods do nothing on the joiner.
    [HarmonyLib.HarmonyPatch]
    static class P_ClientNoModStorageWrites
    {
        static readonly string[] names = { "SetStorageMaxCapacity", "SetStorageQuantityStored", "SetStorageType", "AddToStorage", "AssignStorageItemType", "TransferBetweenStorages" };
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (var m in typeof(ModStorage).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly))
                if (Array.IndexOf(names, m.Name) >= 0 && (m.ReturnType == typeof(bool) || m.ReturnType == typeof(int))) yield return m;
        }
        static int logged;
        static bool Prefix(System.Reflection.MethodBase __originalMethod)
        {
            if (Coop.Role != Role.Client || !Coop.I.ClientLoaded) return true;
            if (logged++ < 3) Diag.Log("ignored a mod's storage change on the joiner: " + __originalMethod.Name);
            return false;   // (result stays default: false / 0)
        }
    }
}

namespace AutonautsCoop
{
    // Joiner: a tree's "slow growth" flag (SL) is only read back while the tree is still growing, but the host keeps
    // it after the tree has grown. Take it as the host has it (it only matters to the host's simulation).
    [HarmonyLib.HarmonyPatch(typeof(MyTree), nameof(MyTree.Load))]
    static class P_ClientTreeSlow
    {
        static readonly HarmonyLib.AccessTools.FieldRef<MyTree, bool> slow = HarmonyLib.AccessTools.FieldRefAccess<MyTree, bool>("m_Slow");
        static void Postfix(MyTree __instance, JSONNode Node)
        {
            if (Coop.Role != Role.Client || !Sync.Applying) return;
            slow(__instance) = JSONUtils.GetAsInt(Node, "SL", 0) == 1;
        }
    }
}
