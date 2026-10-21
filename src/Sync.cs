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
        static readonly Dictionary<string, int> seats = new Dictionary<string, int>();   // player name -> FarmerPlayer (host, this session)
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
        public static string Stable(BaseClass b, string json)
        {
            json = VolatileAll.Replace(json, "");
            if (b is Animal) json = VolatileAnimal.Replace(json, "");
            return json;
        }
        public static int StableHash(BaseClass b) => Stable(b, SaveObject(b, out _)).GetHashCode();

        public struct Entry { public BaseClass B; public string Json; public int Hash; }
        /// Savable objects that are not children inside another object's save (e.g. a bees nest inside its
        /// tree). Children travel with their parent; listing them separately makes the two copies fight.
        public static List<Entry> TopLevel()
        {
            var list = new List<Entry>();
            var nested = new HashSet<int>();
            var tmp = new List<int>();
            foreach (var kv in Savables())
            {
                var b = kv.Key;
                if (b == null || b.m_UniqueID == -1 || !b.GetComponent<Savable>().GetIsSavable()) continue;
                string js = SaveObject(b, out _);
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
        static readonly Dictionary<int, Vector3> sentPos = new Dictionary<int, Vector3>();
        static readonly List<int[]> tileQ = new List<int[]>();
        static readonly List<int> plotQ = new List<int>();
        static float nextScan, nextMove, nextDigest;
        public static float DigestEvery = 10f;
        public static float ScanEvery = 0.2f, MoveEvery = 0.1f;
        public static double LastScanMs;

        public static void OnHostStart()
        {
            var players = CollectionManager.Instance.GetPlayers();
            HostPlayerUID = players.Count > 0 ? players[0].m_UniqueID : -1;
            sentHash.Clear(); sentPos.Clear();
            foreach (var pl in players) Plugin.Log("  player uid " + pl.m_UniqueID + " type " + pl.GetType().Name + " active " + pl.gameObject.activeInHierarchy + " tile " + pl.GetComponent<TileCoordObject>().m_TileCoord.x + "," + pl.GetComponent<TileCoordObject>().m_TileCoord.y + " name " + pl.name);
            Plugin.Log("host player uid " + HostPlayerUID + " tile " + (players.Count > 0 ? players[0].GetComponent<TileCoordObject>().m_TileCoord.x + "," + players[0].GetComponent<TileCoordObject>().m_TileCoord.y : "-")
                + " pos " + (players.Count > 0 ? players[0].transform.position.ToString() : "-") + " map " + TileManager.Instance.m_TilesWide + "x" + TileManager.Instance.m_TilesHigh
                + " players " + players.Count + " state " + GameStateManager.Instance.GetActualState());
        }

        public static void OnPeerJoined(Peer p)
        {
            // Spawn a FarmerPlayer for this peer next to the host's player.
            if (!PeerPlayers.TryGetValue(p, out int uid) && seats.TryGetValue(p.Name, out int seat)
                && ObjectTypeList.Instance.GetObjectFromUniqueID(seat, false) && !PeerPlayers.ContainsValue(seat))
            {
                // Same player rejoining this session: give them their character (and pockets) back.
                uid = seat; PeerPlayers[p] = uid;
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
                seats[p.Name] = uid;
                Plugin.Log("spawned FarmerPlayer uid " + uid + " for peer " + p.Id);
            }
            // The snapshot was captured before the spawn, so force a full resend of what changed.
            var info = new JSONObject();
            info["you"] = uid;
            info["key"] = "peer" + p.Id;
            p.Send(Msg.Info, info.ToString());
            // Make sure the new player goes out in the next batch.
            sentHash.Remove(uid);
            nextScan = 0;
        }

        /// Called right before a snapshot: flush pending changes to existing peers, then mark the current
        /// state as sent (the new peer gets it in the snapshot).
        public static void FlushBeforeSnapshot()
        {
            nextScan = 0; nextMove = 0;
            HostTick();
            foreach (var e in TopLevel()) sentHash[e.B.m_UniqueID] = e.Hash;
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
                    var fpo = ObjectTypeList.Instance.GetObjectFromUniqueID(PeerPlayers[pp], false);
                    if (fpo && fpo.GetComponent<FarmerPlayer>().m_Teaching) fpo.GetComponent<FarmerPlayer>().SetIsTeaching(false);
                    PeerPlayers.Remove(pp);
                    Plugin.Log("peer " + pp.Id + " (" + pp.Name + ") left; their character waits for them to rejoin");
                }
            bool anyPeer;
            lock (Coop.I.Host.Peers) anyPeer = Coop.I.Host.Peers.Exists(x => x.Alive && x.Name != "?");
            if (!anyPeer) return;
            float now = Time.realtimeSinceStartup;
            JSONObject msg = null;

            if (now >= nextScan)
            {
                nextScan = now + ScanEvery;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                msg = new JSONObject();
                var up = new JSONArray(); var rm = new JSONArray();
                var seen = new HashSet<int>();
                foreach (var e in TopLevel())
                {
                    seen.Add(e.B.m_UniqueID);
                    if (!sentHash.TryGetValue(e.B.m_UniqueID, out int old) || old != e.Hash)
                    {
                        sentHash[e.B.m_UniqueID] = e.Hash;
                        up.Add(JSON.Parse(e.Json));
                    }
                }
                var gone = new List<int>();
                foreach (var uid in sentHash.Keys) if (!seen.Contains(uid)) gone.Add(uid);
                foreach (var uid in gone) { sentHash.Remove(uid); sentPos.Remove(uid); rm.Add(uid); }
                if (up.Count > 0) msg["up"] = up;
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
            }

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
                var an = Anim.HostTake();
                if (an != null) { if (msg == null) msg = new JSONObject(); msg["an"] = an; }
            }

            if (msg != null && msg.Count > 0)
                Coop.I.Host.Broadcast(Msg.Delta, Gz.Pack(msg.ToString()));
            WorldState.HostTick();

            if (now >= nextDigest)
            {
                // Anti-entropy: the list of (uid, hash) the clients should have. They ask for what's off.
                nextDigest = now + DigestEvery;
                var d = new JSONObject(); var u = new JSONArray();
                foreach (var kv in sentHash) { u.Add(kv.Key); u.Add(kv.Value); }
                d["ae"] = u;
                Coop.I.Host.Broadcast(Msg.Delta, Gz.Pack(d.ToString()));
            }
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
                if (!o || Time.realtimeSinceStartup > p.Until) { pendingGo.Remove(uid); continue; }
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

        public static void HostApplyInput(Peer from, string json)
        {
            var n0 = JSON.Parse(json);
            if ((string)n0["a"] == "chk")
            {
                TestScript.Checksum(out int c, out long sum);
                var r = new JSONObject(); var o = new JSONObject(); o["count"] = c; o["sum"] = sum.ToString(); r["chk"] = o;
                var u = new JSONArray(); foreach (var kv in TestScript.Hashes()) { u.Add(kv.Key); u.Add(kv.Value); } o["u"] = u;
                from.Send(Msg.Info, r.ToString());
                return;
            }
            if ((string)n0["a"] == "need")
            {
                // A client lost track of these objects; resend them in the next scan.
                foreach (JSONNode u in n0["u"].AsArray)
                {
                    var nb = ObjectTypeList.Instance.GetObjectFromUniqueID(u.AsInt, false);
                    Plugin.Log("resend requested: " + u.AsInt + " " + (nb ? nb.m_TypeIdentifier.ToString() : "gone"));
                    sentHash.Remove(u.AsInt);
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
                r["botstate"] = st; from.Send(Msg.Info, r.ToString());
                return;
            }
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
            if ((string)n0["a"] == "bot_script" || (string)n0["a"] == "learn_start" || (string)n0["a"] == "teach_on")
                if (!Locks.HostAllowed("peer" + from.Id, n0["w"].AsInt)) { Plugin.Log((string)n0["a"] + " refused: bot locked by " + Locks.Table[n0["w"].AsInt]); return; }
            if (Build.HostApply((string)n0["a"], n0)) return;
            if (!PeerPlayers.TryGetValue(from, out int uid)) return;
            var fpObj = ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false);
            if (!fpObj) return;
            var fp = fpObj.GetComponent<FarmerPlayer>();
            var n = JSON.Parse(json);
            if (Teach.HostApply((string)n["a"], n, fp)) return;
            if (Inv.HostApply((string)n["a"], n, fp, from)) return;
            if (Menus.HostApply((string)n["a"], n, fp)) return;
            if (MenuActApply.HostApply((string)n["a"], n, fp)) return;
            if (KeyApply.HostApply((string)n["a"], n, fp)) return;
            switch ((string)n["a"])
            {
                case "go":
                    var dest = new TileCoord(n["x"].AsInt, n["y"].AsInt);
                    int t = n["t"].AsInt;
                    // Plot UIDs are assigned locally on each machine, so plots are sent as "the plot at dest".
                    BaseClass target = t >= 0 ? ObjectTypeList.Instance.GetObjectFromUniqueID(t, false) : t == -2 ? PlotManager.Instance.GetPlotAtTile(dest) : null;
                    if (t >= 0 && !target) { Plugin.Log("input target " + t + " not found on host"); break; }
                    // Mid-action (picking up, chopping...) the game ignores clicks it can't abort. The joiner can't
                    // see that animation reliably, so hold the latest click and retry for a moment.
                    if (Busy(fp)) { pendingGo[uid] = new PendingGo { Json = json, From = from, Until = Time.realtimeSinceStartup + 3f }; break; }
                    pendingGo.Remove(uid);
                    fp.GoToAndAction(dest, target ? target.GetComponent<Actionable>() : null, (AFO.AT)n["k"].AsInt);
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

        public static void QueueEarlyDelta(byte[] d) { early.Add(d); }

        public static readonly HashSet<int> HostKnown = new HashSet<int>();
        public const int LocalUidBase = 1000000000;

        public static void OnClientLoaded()
        {
            TimeManager.Instance.EnableNormalTime(false);
            foreach (var kv in Savables()) if (kv.Key && kv.Key.m_UniqueID > 0) HostKnown.Add(kv.Key.m_UniqueID);
            // Objects the joiner creates locally (edit-mode cursor/previews, effects) get UIDs from a range the
            // host never reaches, so they can't collide with objects the host creates later.
            ObjectTypeList.Instance.SetUniqueIDCounter(LocalUidBase);
            foreach (var d in early) ClientApplyDelta(Gz.Unpack(d));
            early.Clear();
        }

        public static void ClientApplyDelta(string json)
        {
            StatBatches++;
            var n = JSON.Parse(json);
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
                if (up != null)
                {
                    foreach (JSONNode o in up.AsArray) Guard("up " + o["ID"], () => Upsert(o));
                    // Recreating a holder can destroy objects that were applied earlier in this batch.
                    foreach (JSONNode o in up.AsArray)
                        if (!ObjectTypeList.Instance.GetObjectFromUniqueID(o["UID"].AsInt, false))
                            Guard("re-up " + o["ID"], () => { StatCreated++; Create(o); });
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
                var mv = n["mv"];
                if (mv != null) foreach (JSONNode a in mv.AsArray)
                {
                    int uid = a[0].AsInt;
                    var np = new Vector3(a[1].AsFloat, a[2].AsFloat, a[3].AsFloat);
                    if (targetPos.TryGetValue(uid, out var prev) && (prev - np).sqrMagnitude > 0.001f) movingUntil[uid] = Time.realtimeSinceStartup + 0.25f;
                    targetPos[uid] = np;
                    targetRot[uid] = a[4].AsFloat;
                }
            }
            finally { ObjectTypeList.m_Loading = false; Applying = false; }
        }

        public static int StatErrors, StatResync;
        static readonly HashSet<int> touchedNested = new HashSet<int>();
        static void Guard(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { StatErrors++; if (StatErrors < 50) Plugin.Log("apply " + what + " failed: " + e); }
        }

        public static int StatDigests, StatDigestFixes;
        static void ApplyDigest(JSONArray arr)
        {
            StatDigests++;
            var theirs = new Dictionary<int, int>();
            for (int i = 0; i + 1 < arr.Count; i += 2) theirs[arr[i].AsInt] = arr[i + 1].AsInt;
            var need = new JSONArray(); var extra = new List<BaseClass>();
            foreach (var e in TopLevel())
            {
                var b = e.B;
                if (b.m_UniqueID == Teach.EditingUid()) { theirs.Remove(b.m_UniqueID); continue; }
                if (!theirs.TryGetValue(b.m_UniqueID, out int h)) { extra.Add(b); continue; }
                if (e.Hash != h) need.Add(b.m_UniqueID);
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
        }

        static void Remove(int uid)
        {
            var b = ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false);
            if (b) { Destroy(b); StatRemoved++; }
            targetPos.Remove(uid); targetRot.Remove(uid);
        }

        static void Upsert(JSONNode o)
        {
            int uid = o["UID"].AsInt;
            if (uid == Teach.EditingUid()) { frozenSkipped = uid; return; }   // the editor UI holds this bot
            var b = ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false);
            bool reselect = Coop.Role == Role.Client && Teach.IsSelected(b);
            try { UpsertInner(o, uid, b); }
            finally { if (reselect && ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false) != b) Teach.Reselect(uid); }
        }

        static void UpsertInner(JSONNode o, int uid, BaseClass b)
        {
            string want = o.ToString();
            var nested = new List<int>();
            CollectUIDs(want, uid, nested);
            if (nested.Count > 0)
            {
                // Children (carried items, nests, contents) are rebuilt by the parent's Load; clear them first.
                foreach (var c in nested) { var cb = ObjectTypeList.Instance.GetObjectFromUniqueID(c, false); if (cb && cb != b) Destroy(cb); touchedNested.Add(c); }
                if (b) { Destroy(b); StatRecreated++; Count("nested:" + (string)o["ID"]); }
                else StatCreated++;
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
                        // Savable.Load re-registers the UID (ChangeActionable); unregister first or it allocates a new one.
                        if (ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false) == b) ObjectTypeList.Instance.RemoveActionable(b);
                        b.GetComponent<Savable>().Load(o);
                        b.GetComponent<Savable>().PostLoad();
                        string got = Stable(b, SaveObject(b, out _));
                        if (b.m_UniqueID == uid && got == Stable(b, want)) { StatInPlace++; return; }
                        LogDiff((string)o["ID"], want, got);
                    }
                    catch (Exception e) { Plugin.Log("in-place load failed for " + o["ID"] + ": " + e.Message); }
                }
                Count((string)o["ID"]);
                if (b) Destroy(b);
                StatRecreated++;
            }
            else StatCreated++;
            Create(o);
        }

        static readonly HashSet<string> diffLogged = new HashSet<string>();
        static void LogDiff(string type, string want, string got)
        {
            if (!diffLogged.Add(type)) return;
            int i = 0; while (i < want.Length && i < got.Length && want[i] == got[i]) i++;
            int from = Math.Max(0, i - 60);
            Plugin.Log("in-place mismatch " + type + " at " + i + "\n  want: " + want.Substring(from, Math.Min(200, want.Length - from)) + "\n  got:  " + got.Substring(from, Math.Min(200, got.Length - from)));
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

        static void Count(string k) { RecreatedByType[k] = RecreatedByType.TryGetValue(k, out int c) ? c + 1 : 1; }

        static readonly System.Text.RegularExpressions.Regex UidRx = new System.Text.RegularExpressions.Regex("\"UID\"\\s*:\\s*(-?\\d+)");
        /// All UIDs mentioned in the object's JSON except its own (one occurrence of the root UID is skipped).
        static void CollectUIDs(string json, int rootUid, List<int> into)
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
            if (b.m_UniqueID != uid) Plugin.Log("UID mismatch on create: wanted " + uid + " got " + b.m_UniqueID);
            if (uid == MyPlayerUID) Teach.AttachToMyPlayer();
            return b;
        }

        static int frozenSkipped = -1, lastEditing = -1;
        static float nextSweep;
        public static int StatSwept;
        public static void ClientFrame()
        {
            if (!Coop.I.ClientLoaded) return;
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
                if (!basePos.TryGetValue(kv.Key, out var bp)) bp = t.position;
                bp = (bp - kv.Value).sqrMagnitude > 25f ? kv.Value : Vector3.Lerp(bp, kv.Value, k);
                basePos[kv.Key] = bp;
                bool walking = movingUntil.TryGetValue(kv.Key, out float mu) && Time.realtimeSinceStartup < mu;
                t.position = walking ? bp + Vector3.up * Anim.Hop(b, Time.realtimeSinceStartup) : bp;
                if (targetRot.TryGetValue(kv.Key, out float ry))
                    t.rotation = Quaternion.Slerp(t.rotation, Quaternion.Euler(0, ry, 0), k);
            }
            foreach (var d in drop) { targetPos.Remove(d); targetRot.Remove(d); basePos.Remove(d); movingUntil.Remove(d); }
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
            int mine = Coop.Role == Role.Client ? Sync.MyPlayerUID : Coop.Role == Role.Host ? Sync.HostPlayerUID : -1;
            if (mine < 0 || __result == null || __result.Count < 2) return;
            if (__result[0] && __result[0].m_UniqueID == mine) return;
            int i = __result.FindIndex(b => b && b.m_UniqueID == mine);
            if (i > 0) { var b = __result[i]; __result.RemoveAt(i); __result.Insert(0, b); }
        }
    }
}
