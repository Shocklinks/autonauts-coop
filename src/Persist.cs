// AutonautsCoop - Persist.cs - joiner characters survive save/reload (seats + host player stored in the save)
// Version 0.8.0 - 2026-10-20 - Created with Claude
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
            world = tm; Seats.Clear(); HostUid = -1;
        }

        public static void Write(JSONNode root)
        {
            Ensure();
            if (Coop.Role == Role.Client) return;
            int host = Coop.Role == Role.Host ? Sync.HostPlayerUID : HostUid;
            if (host < 0 && Seats.Count == 0) return;
            var n = new JSONObject();
            n["host"] = host;
            var seats = new JSONObject();
            foreach (var kv in Seats) seats[kv.Key] = kv.Value;
            n["seats"] = seats;
            root["AutonautsCoop"] = n;
        }

        public static void Read(JSONNode root)
        {
            world = TileManager.Instance; Seats.Clear(); HostUid = -1;
            if (Coop.Role == Role.Client) return;   // the joiner loads the host's snapshot; not ours
            var n = root?["AutonautsCoop"];
            if (n == null || n.IsNull) return;
            HostUid = n["host"].AsInt;
            foreach (KeyValuePair<string, JSONNode> kv in n["seats"].AsObject) Seats[kv.Key] = kv.Value.AsInt;
            Plugin.Log("save has co-op data: host player " + HostUid + ", " + Seats.Count + " joiner seat(s)");
        }
    }

    [HarmonyPatch(typeof(SaveJSON), nameof(SaveJSON.Capture))]
    static class P_SaveSeats { static void Postfix(JSONNode rootNode) { try { Persist.Write(rootNode); } catch (Exception e) { Plugin.Log("save seats: " + e.Message); } } }

    [HarmonyPatch(typeof(LoadJSON), "InitialSetup")]
    static class P_LoadSeats { static void Prefix() { try { Persist.Read(LoadJSON.m_RootNode); } catch (Exception e) { Plugin.Log("load seats: " + e.Message); } } }
}
