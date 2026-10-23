// AutonautsCoop - Scan.cs - incremental change detection (big bases)
// Version 0.12.2 - 2026-10-22 - Created with Claude
//
// Serializing every object five times a second costs ~0.035 ms per object; a late-game base with 8,000+ objects
// spent ~280 ms per scan. Instead, keep each object's last JSON/hash and only re-serialize what may have changed:
//   - new objects, objects asked for (Force), and anything that changed recently (hot for 3 s)
//   - things that move by themselves (players, bots, animals, vehicles) every scan
//   - anything whose position moved
//   - plus a slice of everything else, so a full round takes about 5 s (catches e.g. storage contents)
// Children inside another object's save (carried items, bees nests, a blueprint's building) are tracked from the
// parent's JSON and never sent on their own.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace AutonautsCoop
{
    public static class Scan
    {
        public class C { public BaseClass B; public int Hash; public string Json; public List<int> Nested; public float HotUntil; public Vector3 Pos; public long Fp; public float LastFull; public int Act; }

        public static readonly Dictionary<int, C> Cache = new Dictionary<int, C>();
        static readonly Dictionary<int, int> parentOf = new Dictionary<int, int>();   // child uid -> parent uid
        static readonly HashSet<int> force = new HashSet<int>();
        static readonly HashSet<int> present = new HashSet<int>();
        static readonly HashSet<int> done = new HashSet<int>();
        static readonly HashSet<int> badSave = new HashSet<int>();
        static List<int> ring = new List<int>(); static int ringIdx;
        public static int LastRescanned;
        public static float RingSeconds = 20f;   // (things that stay put; what bots and players touch is looked at right away)
        public static double RingBudgetMs = 4.0;

        public static void Reset() { Cache.Clear(); parentOf.Clear(); force.Clear(); ring.Clear(); ringIdx = 0; }
        public static void Force(int uid) { force.Add(uid); }
        public static bool IsNested(int uid) => parentOf.ContainsKey(uid);

        static bool Moves(BaseClass b) => b is Farmer || b is Animal || b is Vehicle;
        static readonly HashSet<int> touchNext = new HashSet<int>();
        public static int StatTouched;
        static void Touch(Farmer f)
        {
            void T(BaseClass o) { if (o && o.m_UniqueID > 0) { touchNext.Add(o.m_UniqueID); StatTouched++; } }
            T(f.m_GoToTargetObject); T(f.m_EngagedObject);
            var tm = TileManager.Instance; var tc = f.m_TileCoord;
            if (tm == null) return;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var t = tm.GetTile(new TileCoord(tc.x + dx, tc.y + dy));
                    if (t != null) { T(t.m_Building); T(t.m_BuildingFootprint); }
                }
        }
        public static float MoverFullEvery = 2f;
        public static int StatMoverSaves, StatMoverSkips;

        /// What changes when a mover does something worth re-saving: tile, facing, state, hands, energy, engagement.
        static long Fingerprint(BaseClass b)
        {
            var t = b.GetComponent<TileCoordObject>();
            long h = t ? (t.m_TileCoord.x * 73856093L) ^ (t.m_TileCoord.y * 19349663L) : 0;
            h = h * 31 + (int)(b.transform.eulerAngles.y / 45f);
            if (b is Farmer f)
            {
                h = h * 31 + (int)f.m_State;
                h = h * 31 + (f.m_FarmerCarry != null && f.m_FarmerCarry.m_CarryObject != null ? f.m_FarmerCarry.m_CarryObject.Count * 1000 + (int)f.m_FarmerCarry.GetTopObjectType() : 0);
                h = h * 31 + (int)(f.m_Energy / 5f);
                h = h * 31 + (f.m_EngagedObject ? f.m_EngagedObject.m_UniqueID : 0);
            }
            // a bot's program variables (counters, flags): they change while it stands still
            if (b is Worker w && w.m_WorkerInterpreter != null)
            {
                var gv = w.m_WorkerInterpreter.m_GlobalVariables;
                if (gv != null) for (int i = 0; i < gv.Length; i++) if (gv[i] != null) h = h * 31 + gv[i].GetHashCode();
                var cs = w.m_WorkerInterpreter.GetCurrentScript();
                if (cs != null && cs.m_LocalVariables != null) for (int i = 0; i < cs.m_LocalVariables.Length; i++) if (cs.m_LocalVariables[i] != null) h = h * 31 + cs.m_LocalVariables[i].GetHashCode();
            }
            return h;
        }

        static void Rescan(BaseClass b, float now)
        {
            int uid = b.m_UniqueID;
            done.Add(uid);
            string js;
            try { js = Sync.SaveObject(b, out _); }
            catch (Exception e)
            {
                if (badSave.Add(uid)) Plugin.Log("skipping unsaveable " + b.m_TypeIdentifier + " " + uid + ": " + e.Message);
                return;
            }
            if (!Cache.TryGetValue(uid, out var c) || c.B != b) { c = new C { B = b }; Cache[uid] = c; }
            int h = Sync.Stable(b, js).GetHashCode();
            if (c.Json != null && c.Hash != h) c.HotUntil = now + 3f;
            c.Hash = h; c.Json = js; c.Pos = b.transform.position; c.LastFull = now;
            LastRescanned++;
            // children named inside this save
            List<int> kids = null;
            int first = js.IndexOf("\"UID\"", StringComparison.Ordinal);
            if (first >= 0 && js.IndexOf("\"UID\"", first + 5, StringComparison.Ordinal) >= 0) { kids = new List<int>(); Sync.CollectUIDs(js, uid, kids); }
            int nid = js.IndexOf("\"NewID\":", StringComparison.Ordinal);
            if (nid >= 0)
            {
                int i = nid + 8, j = i; while (j < js.Length && (char.IsDigit(js[j]) || js[j] == '-')) j++;
                if (int.TryParse(js.Substring(i, j - i), out int v) && v > 0) { if (kids == null) kids = new List<int>(); kids.Add(v); }
            }
            if (c.Nested != null)
                foreach (var old in c.Nested)
                    if ((kids == null || !kids.Contains(old)) && parentOf.TryGetValue(old, out int p) && p == uid) { parentOf.Remove(old); force.Add(old); }   // let go: maybe top-level now
            if (kids != null) foreach (var k in kids) parentOf[k] = uid;
            c.Nested = kids;
        }

        /// Re-serialize everything (snapshot time, joiner load).
        public static void Full()
        {
            float now = Time.realtimeSinceStartup;
            done.Clear();
            foreach (var kv in Sync.Savables())
            {
                var b = kv.Key;
                if (b == null || b.m_UniqueID == -1 || !b.GetComponent<Savable>().GetIsSavable()) continue;
                Rescan(b, now);
            }
            DropMissing(true);
            force.Clear();
        }

        /// One scan. host=true: also re-check things that move and things that moved.
        public static void Step(bool host, int ringMin = 150)
        {
            float now = Time.realtimeSinceStartup;
            if (touchNext.Count > 0) { foreach (var u in touchNext) force.Add(u); touchNext.Clear(); }
            done.Clear(); present.Clear(); LastRescanned = 0;
            foreach (var kv in Sync.Savables())
            {
                var b = kv.Key;
                if (b == null || b.m_UniqueID == -1 || !b.GetComponent<Savable>().GetIsSavable()) continue;
                int uid = b.m_UniqueID;
                present.Add(uid);
                if (!Cache.TryGetValue(uid, out var c) || c.B != b || force.Contains(uid)) { Rescan(b, now); continue; }
                if (!host || parentOf.ContainsKey(uid)) continue;   // carried/contained: travels with its parent
                if (Moves(b))
                {
                    // Serializing every bot, animal and vehicle five times a second was most of the host's frame time
                    // on a big base (a bot's save holds its whole program). Their movement goes out separately ("mv");
                    // re-save one only when something a cheap look can see changed, or once a second regardless.
                    long fp = Fingerprint(b);
                    if (fp != c.Fp || now - c.LastFull > MoverFullEvery + (uid % 10) * 0.1f) { Rescan(b, now); c.Fp = fp; c.LastFull = now; StatMoverSaves++; }
                    else StatMoverSkips++;
                    // it picked something up or put something down: what it worked with changed too (storage, a
                    // machine's ingredients). Look at those next scan instead of waiting for the slow round.
                    if (b is Farmer f)
                    {
                        int act = (int)f.m_State * 100000 + (f.m_FarmerCarry != null && f.m_FarmerCarry.m_CarryObject != null ? f.m_FarmerCarry.m_CarryObject.Count * 1000 + (int)f.m_FarmerCarry.GetTopObjectType() % 1000 : 0);
                        if (act != c.Act) { c.Act = act; Touch(f); }
                    }
                    continue;
                }
                if (now < c.HotUntil || (b.transform.position - c.Pos).sqrMagnitude > 0.0001f) Rescan(b, now);
            }
            // a slice of everything else
            if (ringIdx >= ring.Count) { ring = new List<int>(Cache.Keys); ringIdx = 0; }
            int slice = Math.Max(ringMin, (int)(ring.Count * Sync.ScanEvery / RingSeconds) + 1);
            double rs = Prof.Now;
            for (int n = 0; n < slice && ringIdx < ring.Count; ringIdx++)
            {
                if (host && n >= 40 && (n & 15) == 0 && Prof.Now - rs > RingBudgetMs) break;   // spread the round; the rest goes next scan
                int uid = ring[ringIdx];
                if (done.Contains(uid) || !present.Contains(uid) || parentOf.ContainsKey(uid) || !Cache.TryGetValue(uid, out var c) || !c.B) continue;
                Rescan(c.B, now); n++;
            }
            DropMissing(false);
            force.Clear();
        }

        static readonly List<int> drop = new List<int>();
        static void DropMissing(bool full)
        {
            drop.Clear();
            foreach (var kv in Cache)
            {
                var b = kv.Value.B;
                bool gone = !b || b.m_UniqueID != kv.Key || (full ? !done.Contains(kv.Key) : !present.Contains(kv.Key));
                if (gone) drop.Add(kv.Key);
            }
            foreach (var uid in drop)
            {
                if (Cache.TryGetValue(uid, out var c) && c.Nested != null)
                    foreach (var k in c.Nested) if (parentOf.TryGetValue(k, out int p) && p == uid) { parentOf.Remove(k); force.Add(k); }
                Cache.Remove(uid);
                parentOf.Remove(uid);
            }
        }

        /// Top-level objects as cached (no serialization).
        public static IEnumerable<KeyValuePair<int, C>> TopLevel()
        {
            foreach (var kv in Cache) if (!parentOf.ContainsKey(kv.Key) && kv.Value.B) yield return kv;
        }
    }
}
