// AutonautsCoop - VersionCheck.cs - refuse joiners on a different mod or game version, with a reason
// Version 0.8.0 - 2026-10-20 - Created with Claude
using System;
using System.Collections.Generic;
using SimpleJSON;
using UnityEngine;

namespace AutonautsCoop
{
    public static class VersionCheck
    {
        /// Test only: the joiner claims this version instead of its own.
        public static string FakeVersion;
        public static int Rejected;
        static readonly HashSet<Peer> refused = new HashSet<Peer>();
        public static bool IsRefused(Peer p) => refused.Contains(p);
        static readonly List<KeyValuePair<Peer, float>> closing = new List<KeyValuePair<Peer, float>>();

        /// null = ok, otherwise the reason shown to the joiner.
        public static string Check(JSONNode hello)
        {
            string theirs = hello["version"];
            if (hello["protocol"].AsInt != Coop.Protocol || string.IsNullOrEmpty(theirs))
                return "The host runs AutonautsCoop " + Plugin.Version + "; your version is older. Install " + Plugin.Version + " to join.";
            if (theirs != Plugin.Version)
                return "Version mismatch: the host runs AutonautsCoop " + Plugin.Version + ", you have " + theirs + ". Both players need the same version.";
            string game = hello["game"];
            if (!string.IsNullOrEmpty(game) && game != SaveLoadManager.GetVersion())
                return "Game version mismatch: the host runs Autonauts " + SaveLoadManager.GetVersion() + ", you have " + game + ".";
            return null;
        }

        public static void Reject(Peer p, string name, string why)
        {
            Rejected++;
            Plugin.Log("refused " + (name ?? "?") + " (" + p.Describe() + "): " + why);
            Menu.Toast((string.IsNullOrEmpty(name) ? "A player" : name) + " couldn't join: different mod version");
            var n = new JSONObject(); n["reject"] = why;
            try { p.Send(Msg.Info, n.ToString()); } catch { }
            refused.Add(p);   // stays nameless, so it never counts as joined or gets world data
            // close a moment later so the reason gets out first
            closing.Add(new KeyValuePair<Peer, float>(p, Time.realtimeSinceStartup + 2f));
        }

        public static void Tick()
        {
            for (int i = closing.Count - 1; i >= 0; i--)
                if (Time.realtimeSinceStartup > closing[i].Value) { try { closing[i].Key.Close(); } catch { } refused.Remove(closing[i].Key); closing.RemoveAt(i); }
        }
    }
}
