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
        public class C { public BaseClass B; public int Hash; public string Json; public List<int> Nested; public float HotUntil; public Vector3 Pos; public long Fp, Fp2; public float LastFull; public int Act; }

        public static readonly Dictionary<int, C> Cache = new Dictionary<int, C>();
        static readonly Dictionary<int, int> parentOf = new Dictionary<int, int>();   // child uid -> parent uid
        static readonly HashSet<int> force = new HashSet<int>();
        static readonly HashSet<int> present = new HashSet<int>();
        static readonly HashSet<int> done = new HashSet<int>();
        static readonly HashSet<int> badSave = new HashSet<int>();
        static List<int> ring = new List<int>(); static int ringIdx;
        public static int LastRescanned;
        public static float FpRingSeconds = 4f;    // cheap look (fingerprint) at everything still, this often
        public static float SafetySeconds = 60f;   // full re-save regardless of the fingerprint, this often
        public static int StatRingSaves, StatRingSkips;
        public static double RingBudgetMs = 4.0;

        /// Hosting starts with a cache filled earlier (Warm): spread the periodic full re-saves over their period, or
        /// they would all fall due in the first scan.
        public static void Spread()
        {
            float now = Time.realtimeSinceStartup;
            // what's left from an earlier world (or destroyed since): forget it, and any "inside" link it made
            var dead = new List<int>();
            foreach (var kv in Cache) if (!kv.Value.B || kv.Value.B.m_UniqueID != kv.Key) dead.Add(kv.Key);
            foreach (var u in dead) Cache.Remove(u);
            var loose = new List<int>();
            foreach (var kv in parentOf) if (!Cache.TryGetValue(kv.Value, out var pc) || pc.Nested == null || !pc.Nested.Contains(kv.Key)) loose.Add(kv.Key);
            foreach (var u in loose) parentOf.Remove(u);
            force.Clear(); ring.Clear(); ringIdx = 0;
            foreach (var kv in Cache)
                if (kv.Value.B) kv.Value.LastFull = now - UnityEngine.Random.value * (Moves(kv.Value.B) ? MoverFullEvery : SafetySeconds);
        }
        public static void Reset() { Cache.Clear(); parentOf.Clear(); force.Clear(); ring.Clear(); ringIdx = 0; nextWarm = 0; warmLogged = false; }
        public static void Force(int uid) { force.Add(uid); }
        /// Host: something happened to this object (the game's SendAction: added to, taken from, engaged, held...).
        /// Look at it, or what it sits in, next scan instead of waiting for the slow round.
        public static int StatDirty;
        public static void MarkDirty(BaseClass b)
        {
            if (!b) return;
            int uid = b.m_UniqueID; if (uid <= 0) return;
            for (int i = 0; i < 8 && parentOf.TryGetValue(uid, out int p); i++) uid = p;
            if (touchNext.Add(uid)) StatDirty++;
        }
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
        public static float HotFullEvery = 1f;
        public static int StatHotSkips;
        public static float MoverFullEvery = 8f;   // full re-save regardless of the fingerprint (bots someone has open: every scan)
        public static int StatMoverSaves, StatMoverSkips;

        /// What changes when a mover does something worth re-saving: tile, facing, state, hands, energy, engagement.
        static long Fingerprint(BaseClass b)
        {
            // (no tile: where it walks goes out in the movement stream; its saved tile isn't compared, see Sync.Stable)
            long h = 17;
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

        // ---- cheap look at a still object: tile, height, facing and the fields that change in play ----
        static readonly string[] fpNames = { "m_State", "m_Stored", "m_ObjectType", "m_Stage", "m_UsageCount", "m_NumCreated",
            "m_ResultsToCreate", "m_Rotation", "m_Flipped", "m_Name", "m_ChangeTypeLocked", "m_Capacity", "m_Fuel", "m_Energy",
            "m_Water", "m_Food", "m_NewState", "m_Slow", "m_Dug", "m_Watered", "m_Fertilised" };
        static readonly string[] fpLists = { "m_Ingredients", "m_Levels", "m_Folks", "m_Used" };
        static readonly Dictionary<Type, System.Reflection.FieldInfo[]> fpFieldsOf = new Dictionary<Type, System.Reflection.FieldInfo[]>();
        static readonly Dictionary<Type, System.Reflection.FieldInfo[]> fpListsOf = new Dictionary<Type, System.Reflection.FieldInfo[]>();
        static System.Reflection.FieldInfo[] FieldsOf(Type t, string[] names, Dictionary<Type, System.Reflection.FieldInfo[]> cache, bool lists)
        {
            if (cache.TryGetValue(t, out var fs)) return fs;
            var l = new List<System.Reflection.FieldInfo>();
            foreach (var n in names)
            {
                var f = HarmonyLib.AccessTools.Field(t, n);
                if (f == null || f.IsStatic) continue;
                if (lists ? typeof(System.Collections.ICollection).IsAssignableFrom(f.FieldType) : (f.FieldType.IsPrimitive || f.FieldType.IsEnum || f.FieldType == typeof(string))) l.Add(f);
            }
            return cache[t] = l.ToArray();
        }
        static long StaticFp(BaseClass b)
        {
            long h = 17;
            var tco = b as TileCoordObject;
            if (tco) h = (tco.m_TileCoord.x * 73856093L) ^ (tco.m_TileCoord.y * 19349663L);
            var tr = b.transform;
            h = h * 31 + (int)(tr.position.y * 20f);
            h = h * 31 + (int)(tr.eulerAngles.y / 45f);
            var t = b.GetType();
            foreach (var f in FieldsOf(t, fpNames, fpFieldsOf, false)) { var v = f.GetValue(b); h = h * 31 + (v != null ? v.GetHashCode() : 0); }
            foreach (var f in FieldsOf(t, fpLists, fpListsOf, true)) { var v = f.GetValue(b) as System.Collections.ICollection; h = h * 31 + (v != null ? v.Count : -1); }
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
            else if (c.Json != null && c.Json.Length == js.Length && c.Json == js)
            {
                // unchanged: skip the normalizing pass, its strings were half the scan's garbage
                c.Pos = b.transform.position; c.LastFull = now; LastRescanned++;
                if (!Moves(b)) c.Fp2 = StaticFp(b);
                return;
            }
            int h = Sync.Stable(b, js).GetHashCode();
            if (c.Json != null && c.Hash != h) c.HotUntil = now + 3f;
            c.Hash = h; c.Json = js; c.Pos = b.transform.position; c.LastFull = now;
            if (!Moves(b)) c.Fp2 = StaticFp(b);
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
                    if (fp != c.Fp || Locks.Table.ContainsKey(uid) || now - c.LastFull > MoverFullEvery + (uid % 10) * 0.1f) { Rescan(b, now); c.Fp = fp; c.LastFull = now; StatMoverSaves++; }
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
                if ((b.transform.position - c.Pos).sqrMagnitude > 0.0001f) { Rescan(b, now); continue; }
                if (now < c.HotUntil)
                {
                    // just changed: look again soon, but re-save only when the cheap look changed or once a second
                    // (re-saving it every scan for 3 s was much of the scan's garbage)
                    if (StaticFp(b) != c.Fp2 || now - c.LastFull >= HotFullEvery) Rescan(b, now); else StatHotSkips++;
                }
            }
            // a slice of everything else
            if (ringIdx >= ring.Count) { ring = new List<int>(Cache.Keys); ringIdx = 0; }
            // host: a cheap look at everything still every few seconds, a full save only when that look changed (or
            // every SafetySeconds); joiner: full saves (it only re-checks itself for the digest)
            int slice = host ? Math.Max(ringMin, (int)(ring.Count * Sync.ScanEvery / FpRingSeconds) + 1) : Math.Max(ringMin, (int)(ring.Count * Sync.ScanEvery / 40f) + 1);
            double rs = Prof.Now;
            for (int n = 0; n < slice && ringIdx < ring.Count; ringIdx++)
            {
                if (host && n >= 40 && (n & 15) == 0 && Prof.Now - rs > RingBudgetMs) break;   // spread the round; the rest goes next scan
                int uid = ring[ringIdx];
                if (done.Contains(uid) || !present.Contains(uid) || parentOf.ContainsKey(uid) || !Cache.TryGetValue(uid, out var c) || !c.B) continue;
                n++;
                if (host && !Moves(c.B))
                {
                    long fp = StaticFp(c.B);
                    if (fp == c.Fp2 && now - c.LastFull < SafetySeconds + (uid % 20)) { StatRingSkips++; continue; }
                    StatRingSaves++;
                }
                Rescan(c.B, now);
            }
            DropMissing(false);
            force.Clear();
        }

        /// Host with nobody joined yet: fill the cache a few ms at a time, so the first joiner doesn't wait for (and
        /// the host doesn't hitch on) saving every object at once. Returns how many were still missing.
        static float nextWarm; static bool warmLogged;
        public static int Warm(double budgetMs = 2.0)
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextWarm) return -1;
            double t0 = Prof.Now; int missing = 0, saved = 0;
            foreach (var kv in Sync.Savables())
            {
                var b = kv.Key;
                if (b == null || b.m_UniqueID == -1 || (Cache.TryGetValue(b.m_UniqueID, out var c) && c.B == b)) continue;
                if (!b.GetComponent<Savable>().GetIsSavable()) continue;
                missing++;
                if (Prof.Now - t0 > budgetMs) continue;
                Rescan(b, now); saved++;
                if (Moves(b) && Cache.TryGetValue(b.m_UniqueID, out var cm)) cm.Fp = Fingerprint(b);   // (else the first scan re-saves every mover)
            }
            // done: look again now and then for things built meanwhile
            if (!warmLogged && missing == saved) { warmLogged = true; Plugin.Log("scan cache ready for joiners (" + Cache.Count + " objects)"); }
            nextWarm = now + (missing > saved ? 0f : 20f);   // (every frame until done: ~1 s on a big base)
            return missing - saved;
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

namespace AutonautsCoop
{
    // Host: an action on an object (storage added to, machine given an ingredient, item picked up) marks it for the
    // next scan, so the slow round can be slow without the joiner seeing stale contents.
    // (Actionable.SendAction itself is empty and its base calls get inlined: patch every override instead.)
    [HarmonyLib.HarmonyPatch]
    static class P_HostDirtyOnAction
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (var t in typeof(Actionable).Assembly.GetTypes())
            {
                if (!typeof(Actionable).IsAssignableFrom(t) || t.IsGenericTypeDefinition) continue;
                var m = t.GetMethod("SendAction", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly, null, new[] { typeof(ActionInfo) }, null);
                if (m != null && !m.IsAbstract && m.GetMethodBody() != null) yield return m;
            }
        }
        static void Postfix(Actionable __instance, ActionInfo Info)
        {
            if (Coop.Role != Role.Host) return;
            Scan.MarkDirty(__instance);
            if (Info != null && Info.m_Object) Scan.MarkDirty(Info.m_Object);
        }
    }
}
