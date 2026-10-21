// AutonautsCoop - Persist.cs - joiner characters: stored when they leave, restored when they come back, saved with the world
// Version 0.10.0 - 2026-10-21 - Created with Claude
using System;
using System.Collections.Generic;
using HarmonyLib;
using SimpleJSON;

namespace AutonautsCoop
{
    /// The host's save already holds every FarmerPlayer. What it lacks is who they belong to:
    /// which one is the host, and which joiner owns each of the others. That goes in an extra
    /// "AutonautsCoop" node at the save's root (the game ignores unknown keys).
    public static class Persist
    {
        /// joiner key (steam:ID or name:NAME) -> their FarmerPlayer uid, for the world currently loaded
        public static readonly Dictionary<string, int> Seats = new Dictionary<string, int>();
        public static int HostUid = -1;
        /// joiner key -> their character's full save JSON (position, hands, pockets, upgrades, clothes) while away
        public static readonly Dictionary<string, JSONNode> Parked = new Dictionary<string, JSONNode>();
        public static int ParkedCount, Restored, Relocated;   // stats/tests
        public static Action<TileCoord> OnParkedAt;            // tests: host builds on the empty spot
        static object world;   // TileManager the seats belong to

        public static string Key(Peer p)
        {
            var w = p as WirePeer;
            if (w != null && p.Describe().StartsWith("steam")) return "steam:" + w.Remote;
            return "name:" + p.Name;
        }

        /// Seats from another world are meaningless: drop them when the world changes.
        public static void Ensure()
        {
            var tm = TileManager.Instance;
            if (tm == null || ReferenceEquals(world, tm)) return;
            if (world != null && (Seats.Count > 0 || HostUid >= 0)) Plugin.Log("new world: forgetting " + Seats.Count + " seat(s)");
            world = tm; Seats.Clear(); Parked.Clear(); HostUid = -1;
        }

        public static void Write(JSONNode root)
        {
            Ensure();
            if (Coop.Role == Role.Client)
            {
                // A joiner's own copy of the world: loading it later, they play their own character.
                var c = new JSONObject(); c["host"] = Sync.MyPlayerUID; c["seats"] = new JSONObject();
                root["AutonautsCoop"] = c;
                return;
            }
            int host = Coop.Role == Role.Host ? Sync.HostPlayerUID : HostUid;
            if (host < 0 && Seats.Count == 0) return;
            var n = new JSONObject();
            n["host"] = host;
            var seats = new JSONObject();
            foreach (var kv in Seats) seats[kv.Key] = kv.Value;
            n["seats"] = seats;
            var parked = new JSONObject();
            foreach (var kv in Parked) parked[kv.Key] = kv.Value;
            n["parked"] = parked;
            root["AutonautsCoop"] = n;
        }

        public static void Read(JSONNode root)
        {
            world = TileManager.Instance; Seats.Clear(); Parked.Clear(); HostUid = -1;
            if (Coop.Role == Role.Client) return;   // the joiner loads the host's snapshot; not ours
            var n = root?["AutonautsCoop"];
            if (n == null || n.IsNull) return;
            HostUid = n["host"].AsInt;
            foreach (KeyValuePair<string, JSONNode> kv in n["seats"].AsObject) Seats[kv.Key] = kv.Value.AsInt;
            var pk = n["parked"];
            if (pk != null && !pk.IsNull) foreach (KeyValuePair<string, JSONNode> kv in pk.AsObject) Parked[kv.Key] = kv.Value;
            Plugin.Log("save has co-op data: host player " + HostUid + ", " + Seats.Count + " joiner seat(s), " + Parked.Count + " stored character(s)");
        }

        // ---------------- leaving / coming back (host) ----------------

        /// Take a joiner's character out of the world and keep everything about it for later.
        public static void Park(string key, BaseClass fpObj)
        {
            if (!fpObj || string.IsNullOrEmpty(key)) return;
            var fp = fpObj.GetComponent<FarmerPlayer>();
            try { if (fp && fp.m_Teaching) fp.SetIsTeaching(false); } catch { }
            try { if (fp && fp.m_EngagedObject) fp.SendAction(new ActionInfo(ActionType.DisengageObject, fp.m_TileCoord)); } catch { }
            string js = Sync.SaveObject(fpObj, out _);
            var node = JSON.Parse(js);
            var nested = new List<int>(); Sync.CollectUIDs(js, fpObj.m_UniqueID, nested);
            var tile = fpObj.GetComponent<TileCoordObject>().m_TileCoord;
            Sync.Destroy(fpObj);
            foreach (var u in nested) { var c = ObjectTypeList.Instance.GetObjectFromUniqueID(u, false); if (c) Sync.Destroy(c); }
            // the players list keeps destroyed entries; drop them so "player 0" stays valid
            try { CollectionManager.Instance.GetPlayers().RemoveAll(x => x == null || x.m_UniqueID == -1 || x.m_UniqueID == node["UID"].AsInt); } catch { }
            Parked[key] = node; Seats[key] = node["UID"].AsInt;
            ParkedCount++;
            Plugin.Log("stored " + key + "'s character " + node["UID"].AsInt + " (" + nested.Count + " carried/worn) at " + tile.x + "," + tile.y);
            OnParkedAt?.Invoke(tile);
        }

        /// Host stops: everyone connected leaves the world now (so a save afterwards stores them).
        public static void ParkAll()
        {
            if (Coop.Role != Role.Host) return;
            foreach (var kv in new List<KeyValuePair<Peer, int>>(Sync.PeerPlayers))
                Park(Key(kv.Key), ObjectTypeList.Instance.GetObjectFromUniqueID(kv.Value, false));
            Sync.PeerPlayers.Clear();
        }

        /// A world saved while joiners were connected still has their characters standing in it: store them.
        public static void ParkLoaded()
        {
            if (Coop.Role == Role.Client) return;
            foreach (var kv in new List<KeyValuePair<string, int>>(Seats))
            {
                if (Parked.ContainsKey(kv.Key) || kv.Value == HostUid) continue;
                var o = ObjectTypeList.Instance.GetObjectFromUniqueID(kv.Value, false);
                if (o && o.GetComponent<FarmerPlayer>()) Park(kv.Key, o);
            }
        }

        /// Something was built (or the ground changed) where the joiner stood: they can't stand there now.
        public static bool Blocked(TileCoord t)
        {
            var tile = TileManager.Instance.GetTile(t);
            if (tile == null) return true;
            if (Tile.GetIsSolid(tile.m_TileType)) return true;
            if (tile.m_TileType == Tile.TileType.WaterDeep || tile.m_TileType == Tile.TileType.SeaWaterDeep) return true;
            if (tile.m_Building && !(tile.m_Building is Floor)) return true;
            return false;
        }

        public static bool HasParked(Peer p) => Parked.ContainsKey(Key(p)) || Parked.ContainsKey("name:" + p.Name);

        /// Bring a returning joiner's character back. Where they stood, unless something was built there since:
        /// then next to the host.
        public static BaseClass Unpark(Peer p, TileCoord hostTile)
        {
            string key = Parked.ContainsKey(Key(p)) ? Key(p) : "name:" + p.Name;
            if (!Parked.TryGetValue(key, out var node)) return null;
            Parked.Remove(key); Seats.Remove(key);
            var t = new TileCoord(node["TX"].AsInt, node["TY"].AsInt);
            if (Blocked(t))
            {
                var near = Sync.FreeTileNear(hostTile);
                Plugin.Log("spot " + t.x + "," + t.y + " is taken now; " + p.Name + " comes back next to the host at " + near.x + "," + near.y);
                node["TX"] = near.x; node["TY"] = near.y; Relocated++;
            }
            BaseClass b;
            ObjectTypeList.m_Loading = true;   // keep the stored UIDs (character and everything it carries)
            try { b = Sync.Create(node); }
            finally { ObjectTypeList.m_Loading = false; }
            if (b)
            {
                var tc = new TileCoord(node["TX"].AsInt, node["TY"].AsInt);
                b.transform.position = tc.ToWorldPositionTileCentered();
                Restored++;
            }
            Plugin.Log("restored " + key + "'s character " + (b ? b.m_UniqueID : -1));
            return b;
        }
    }

    [HarmonyPatch(typeof(SaveJSON), nameof(SaveJSON.Capture))]
    static class P_SaveSeats { static void Postfix(JSONNode rootNode) { try { Persist.Write(rootNode); } catch (Exception e) { Plugin.Log("save seats: " + e.Message); } } }

    [HarmonyPatch(typeof(LoadJSON), "InitialSetup")]
    static class P_LoadSeats { static void Prefix() { try { Persist.Read(LoadJSON.m_RootNode); } catch (Exception e) { Plugin.Log("load seats: " + e.Message); } } }

    [HarmonyPatch(typeof(LoadJSON), "EndLoad")]
    static class P_LoadPark { static void Postfix() { try { Persist.ParkLoaded(); } catch (Exception e) { Plugin.Log("park loaded: " + e); } } }
}
