// AutonautsCoop - WorldState.cs - world-wide state after join (quests/research/unlocks, resources, time of day,
// script library, bot groups, offworld missions), leaving cleanly when the host goes away, and joiner animations.
// Version 0.6.0 - 2026-10-20 - Created with Claude
using System;
using System.Collections.Generic;
using HarmonyLib;
using SimpleJSON;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AutonautsCoop
{
    public static class WorldState
    {
        // Sections of the save that live outside objects. Each is sent when its JSON changes.
        static readonly string[] Sections = { "Quest", "Locked", "Resources", "DayNight", "Scripts", "WorkerGroups", "OffworldMissions", "Wardrobe", "Planning", "Time" };
        static readonly Dictionary<string, int> sent = new Dictionary<string, int>();
        static float next;
        public static float Every = 1f;
        public static int Applied;   // client stats
        static JSONNode lastLocked;

        /// If the joiner has the build menu open, update it so new unlocks show up right away (the palette caches
        /// each building's locked flag when edit mode opens). Also flag newly unlocked buildings as "new".
        public static void RefreshPalette(ICollection<ObjectType> newlyUnlocked)
        {
            try
            {
                if (newlyUnlocked != null) foreach (var t in newlyUnlocked) { try { BuildingPalette.AddNewUnlocked(t); } catch { } }
                var ed = GameStateEdit.Instance;
                var pal = ed ? ed.m_BuildingPalette : null;
                if (pal == null) return;
                var tp = Traverse.Create(pal);
                var infos = tp.Field("m_CategoryInfo").GetValue<List<BuildingCategoryInfo>>();
                if (infos == null) return;
                bool changed = false;
                foreach (var info in infos)
                    for (int j = 0; j < info.m_Buildings.Count; j++)
                    {
                        bool locked = QuestManager.Instance.m_BuildingsLocked.ContainsKey(info.m_Buildings[j]);
                        if (info.m_Locked[j] != locked) { info.m_Locked[j] = locked; changed = true; }
                    }
                if (!changed) return;
                var cats = tp.Field("m_CategoryButtons").GetValue<List<BuildingCategoryButton>>();
                if (cats != null) foreach (var c in cats) if (c) UnityEngine.Object.Destroy(c.gameObject);
                pal.CreateCategoryButtons();
                tp.Method("DeleteBuildingButtons").GetValue();
                tp.Method("CreateBuildingButtons").GetValue();
                PaletteRefreshes++;
                Plugin.Log("build palette refreshed for new unlocks");
            }
            catch (Exception e) { Plugin.Log("palette refresh: " + e.Message); }
        }
        public static int PaletteRefreshes;
        static HashSet<ObjectType> prevLocked;

        static JSONNode SaveSection(string s)
        {
            var n = new JSONObject();
            switch (s)
            {
                case "Quest": QuestManager.Instance.Save(n); break;
                case "Locked":
                {
                    // The host's unlock set, after quests *and* the host's badges (badge unlocks are per player and
                    // not in the world save). Joiners build from the host's palette.
                    var arr = new JSONArray();
                    foreach (var kv in QuestManager.Instance.m_ObjectsLocked) { var e = new JSONArray(); e.Add(ObjectTypeList.Instance.GetSaveNameFromIdentifier(kv.Key)); e.Add(kv.Value); arr.Add(e); }
                    n["L"] = arr;
                    var barr = new JSONArray();   // buildings have their own lock table (the build palette uses it)
                    foreach (var kv in QuestManager.Instance.m_BuildingsLocked) { var e = new JSONArray(); e.Add(ObjectTypeList.Instance.GetSaveNameFromIdentifier(kv.Key)); e.Add(kv.Value); barr.Add(e); }
                    n["B"] = barr;
                    break;
                }
                case "Resources": ResourceManager.Instance.Save(n); break;
                case "DayNight": DayNightManager.Instance.Save(n); break;
                case "Scripts": WorkerScriptManager.Instance.Save(n); break;
                case "WorkerGroups": WorkerGroupManager.Instance.Save(n); break;
                case "OffworldMissions": OffworldMissionsManager.Instance.Save(n); break;
                case "Wardrobe": WardrobeManager.Instance.Save(n); break;   // all wardrobes share one store
                case "Planning": return PlanAreas.Save();
                case "Time": return Speed.Save();
            }
            return n;
        }

        public static void ResetSent() { sent.Clear(); next = 0; prevLocked = null; due.Clear(); same.Clear(); }

        // One section per frame, each on its own timer: saving them all at once (quests are big in a late game) made a
        // stutter every second. A section that is slow to save is checked less often (at most every 6 s).
        static readonly Dictionary<string, float> due = new Dictionary<string, float>();
        static readonly Dictionary<string, int> same = new Dictionary<string, int>();
        static readonly HashSet<string> Backoff = new HashSet<string> { "Quest", "Locked", "Scripts", "WorkerGroups", "OffworldMissions", "Wardrobe" };
        static int nextSection;
        public static void HostTick(bool force = false)
        {
            float now = Time.realtimeSinceStartup;
            JSONObject w = null;   // (made only when something is sent: this runs every frame)
            for (int i = 0; i < Sections.Length; i++)
            {
                string s = Sections[(nextSection + i) % Sections.Length];
                if (!force && due.TryGetValue(s, out float d) && now < d) continue;
                double t0 = Prof.Now;
                JSONNode node;
                try { node = SaveSection(s); } catch (Exception e) { Plugin.Log("world " + s + " save: " + e.Message); due[s] = now + Every; continue; }
                int h = node.ToString().GetHashCode();
                double ms = Prof.Now - t0; Prof.Add("section " + s, ms);
                bool changed = !(sent.TryGetValue(s, out int old) && old == h);
                if (changed) { sent[s] = h; if (w == null) w = new JSONObject(); w[s] = node; same[s] = 0; }
                else if (Backoff.Contains(s)) same[s] = (same.TryGetValue(s, out int k) ? k : 0) + 1;
                // rarely changing sections that stayed the same are looked at less often (up to every 4 s): saving
                // the quest list and the script library every second was most of the sections' garbage
                float wait = Every;
                if (same.TryGetValue(s, out int st) && st > 0) wait = Mathf.Min(Every * (1 << Mathf.Min(st, 3)), 4f);
                due[s] = now + Mathf.Clamp((float)ms * 0.15f, wait, 6f);
                if (!force) { nextSection = (nextSection + i + 1) % Sections.Length; break; }
            }
            if (w == null || w.Count == 0) return;
            var m = new JSONObject(); m["world"] = w;
            var keys = new List<string>(); foreach (var s in Sections) if (w[s] != null && !w[s].IsNull) keys.Add(s);
            if (TestBot.Active) Plugin.Log("world sent: " + string.Join(",", keys.ToArray()));
            Coop.I.Host.Broadcast(Msg.Delta, Gz.Pack(m.ToString()));
        }

        public static void ClientApply(JSONNode w)
        {
            if (TestBot.Active) Plugin.Log("world received");
            Sync.Applying = true;
            try
            {
                if (w["Locked"] != null && !w["Locked"].IsNull) lastLocked = w["Locked"];
                foreach (var s in Sections)
                {
                    var n = w[s];
                    // QuestManager.Load recomputes locks; put the host's set back even when only quests changed
                    if (s == "Locked" && (n == null || n.IsNull) && w["Quest"] != null && !w["Quest"].IsNull) n = lastLocked;
                    if (n == null || n.IsNull) continue;
                    try
                    {
                        switch (s)
                        {
                            case "Quest": QuestManager.Instance.Load(n); break;
                            case "Locked":
                            {
                                var d = QuestManager.Instance.m_ObjectsLocked; d.Clear();
                                foreach (JSONNode e in n["L"].AsArray)
                                {
                                    var t = ObjectTypeList.Instance.GetIdentifierFromSaveName(e[0], false);
                                    if (t != ObjectTypeList.m_Total && !d.ContainsKey(t)) d.Add(t, e[1].AsInt);
                                }
                                if (n["B"] != null && !n["B"].IsNull)
                                {
                                    var bd = QuestManager.Instance.m_BuildingsLocked; bd.Clear();
                                    foreach (JSONNode e in n["B"].AsArray)
                                    {
                                        var t = ObjectTypeList.Instance.GetIdentifierFromSaveName(e[0], false);
                                        if (t != ObjectTypeList.m_Total && !bd.ContainsKey(t)) bd.Add(t, e[1].AsInt);
                                    }
                                }
                                // newly unlocked = locked at the last apply, not now (a Quest load in between resets the table)
                                var opened = new List<ObjectType>();
                                if (prevLocked != null) foreach (var t in prevLocked) if (!QuestManager.Instance.m_BuildingsLocked.ContainsKey(t)) opened.Add(t);
                                prevLocked = new HashSet<ObjectType>(QuestManager.Instance.m_BuildingsLocked.Keys);
                                RefreshPalette(opened);
                                break;
                            }
                            case "Resources": ResourceManager.Instance.Load(n); break;
                            case "DayNight": DayNightManager.Instance.Load(n); break;
                            case "Scripts": Shared.Scripts.ClientIncoming(n); break;   // joiners edit it too
                            case "Planning": Shared.Planning.ClientIncoming(n); break;
                            case "Time": Speed.ClientApply(n); break;
                            case "WorkerGroups": Shared.WorkerGroups.ClientIncoming(n); break;
                            case "OffworldMissions": OffworldMissionsManager.Instance.Load(n); break;
                            case "Wardrobe":
                            {
                                // Load appends and creates the clothes; drop ours first, then load like the game's loader
                                var list = Traverse.Create(WardrobeManager.Instance).Field("m_Objects").GetValue<List<Holdable>>();
                                foreach (var h in new List<Holdable>(list)) if (h) Sync.Destroy(h);
                                list.Clear();
                                foreach (JSONNode o in n["Wardrobe"].AsArray)
                                {
                                    var clash = ObjectTypeList.Instance.GetObjectFromUniqueID(o["UID"].AsInt, false);
                                    if (clash) Sync.Destroy(clash);
                                }
                                ObjectTypeList.m_Loading = true;
                                try { WardrobeManager.Instance.Load(n); } finally { ObjectTypeList.m_Loading = false; }
                                break;
                            }
                        }
                        Applied++;
                    }
                    catch (Exception e) { Plugin.Log("world " + s + " load: " + e.Message); }
                }
                if ((w["Quest"] != null && !w["Quest"].IsNull) || (w["Locked"] != null && !w["Locked"].IsNull))
                {
                    // same refreshes the game's loader does after a load
                    try { TabManager.Instance?.PostLoad(); } catch { }
                    try { FolkManager.Instance?.UpdateFolkTiers(); } catch { }
                    try { ModeButton.Get(ModeButton.Type.Evolution)?.UpdateFolkLevel(); } catch { }
                }
            }
            finally { Sync.Applying = false; }
        }
    }

    // ======================= leaving =======================
    public static class Session
    {
        public static string LastDisconnect;
        public static float LastDisconnectAt;

        /// Joiner: host gone (closed, crashed, timed out, or went back to its menu). Back to the main menu.
        public const string EndedMsg = "Host has ended the lobby.";
        public const string LostMsg = "Host has ended the lobby (connection lost).";

        public static void ClientLost(string why, bool verbatim = false)
        {
            Plugin.Log("lost host: " + why);
            // Closed before the world arrived: an older host (protocol 1) drops newer joiners without a word.
            if (!verbatim && Coop.PendingWorld == null && !Coop.I.ClientLoaded)
                why = "The host closed the connection before sending the world. It may run a different AutonautsCoop version (you have " + Plugin.Version + ").";
            else if (!verbatim) why = LostMsg;
            LastDisconnect = why;
            LastDisconnectAt = Time.realtimeSinceStartup;
            ResetAll();
            if (SceneManager.GetActiveScene().name != "MainMenu")
            {
                try { GameStateManager.Instance?.SetState(GameStateManager.State.SceneChange); } catch { }
                SessionManager.Instance.LoadLevel(false, "MainMenu");
            }
        }

        public static void StopHost(string why)
        {
            Plugin.Log("stop hosting: " + why);
            // Joiners' characters leave the world with them (stored, and saved with the world).
            try { Persist.ParkAll(); } catch (Exception e) { Plugin.Log("park on stop: " + e); }
            try
            {
                var n = new JSONObject(); n["end"] = EndedMsg;
                Coop.I.Host?.Broadcast(Msg.Info, System.Text.Encoding.UTF8.GetBytes(n.ToString()));
            }
            catch { }
            try { Coop.I.Host?.Stop(); } catch { }
            ResetAll();
        }

        /// Host: tell everyone (and show it here) that a player came or went.
        public static void Notify(string text, Peer except = null)
        {
            Menu.Toast(text);
            if (Coop.Role != Role.Host || Coop.I.Host == null) return;
            var n = new JSONObject(); n["note"] = text;
            var d = System.Text.Encoding.UTF8.GetBytes(n.ToString());
            lock (Coop.I.Host.Peers) foreach (var p in Coop.I.Host.Peers) if (p != except && p.Alive && p.Name != "?") p.Send(Msg.Info, d);
        }

        static void ResetAll()
        {
            try { SteamNet.Shutdown(); } catch { }
            Coop.I.Client?.Host?.Close();
            Coop.Role = Role.Off;
            Coop.PendingWorld = null;
            Coop.I.ClientLoaded = false;
            Coop.I.Host = null; Coop.I.Client = null;
            Sync.MyPlayerUID = -1;
            Sync.PeerPlayers.Clear();
            Locks.Table.Clear();
            WorldState.ResetSent();
        }

        static float graceUntil;
        public static void Tick()
        {
            if (Coop.Role == Role.Client)
            {
                var h = Coop.I.Client?.Host;
                if (h != null && !h.Alive && !NetSim.Pending(Coop.I.Client.Inbox)) { ClientLost("connection closed"); return; }
                // joiner quit to the main menu on its own
                if (Coop.I.ClientLoaded && SceneManager.GetActiveScene().name == "MainMenu") { ResetAll(); Plugin.Log("left the session"); }
            }
            else if (Coop.Role == Role.Host)
            {
                // host went back to the main menu (or loaded another world): end the session
                string scene = SceneManager.GetActiveScene().name;
                if (scene != "Main") { if (Time.realtimeSinceStartup > graceUntil) StopHost("host left the world (" + scene + ")"); }
                else graceUntil = Time.realtimeSinceStartup + 2f;
            }
        }
    }

    // ======================= joiner animations =======================
    // The joiner's simulation is frozen, so nothing plays animations. The host forwards the animation each
    // farmer starts (Farmer.StartAnimation: chop, dig, idle...) and the joiner plays it; walking is a procedural
    // hop in the game (FarmerStateMove), reproduced from the position stream.
    public static class Anim
    {
        static readonly List<int[]> q = new List<int[]>();
        static readonly List<string> names = new List<string>();
        public static int Played;

        public static void HostRecord(Farmer f, string name)
        {
            if (Coop.Role != Role.Host || !f || f.m_UniqueID <= 0) return;
            q.Add(new[] { f.m_UniqueID }); names.Add(name);
            if (q.Count > 512) { q.RemoveAt(0); names.RemoveAt(0); }
        }

        public static JSONArray HostTake()
        {
            if (q.Count == 0) return null;
            var arr = new JSONArray();
            for (int i = 0; i < q.Count; i++) { var e = new JSONArray(); e.Add(q[i][0]); e.Add(names[i]); arr.Add(e); }
            q.Clear(); names.Clear();
            return arr;
        }

        public static void ClientPlay(JSONArray arr)
        {
            foreach (JSONNode e in arr)
            {
                var o = ObjectTypeList.Instance.GetObjectFromUniqueID(e[0].AsInt, false);
                var f = o ? o.GetComponent<Farmer>() : null;
                if (!f || !f.m_Animator) continue;
                try { f.m_Animator.speed = 1f; f.StartAnimation(e[1]); Played++; } catch { }
            }
        }

        // procedural walk hop, as FarmerStateMove does: +0.5 for 5 of every 8 frames (vibrating drives: +0.1, 2 of 4)
        public static float Hop(BaseClass b, float t)
        {
            var w = b as Worker;
            if (w && WorkerDrive.GetIsTypeVibraty(w.m_Drive)) return ((int)(t * 60f) % 4 < 2) ? 0.1f : 0f;
            if (b is Farmer) return ((int)(t * 60f) % 8 < 5) ? 0.5f : 0f;
            if (b is Animal) return ((int)(t * 60f) % 8 < 5) ? 0.3f : 0f;
            return 0f;
        }
    }

    [HarmonyPatch(typeof(Farmer), nameof(Farmer.StartAnimation))]
    static class P_AnimRecord { static void Postfix(Farmer __instance, string Name) { Anim.HostRecord(__instance, Name); } }

    // Worker.Update sets its animator speed to 0 while normal time is off (always, on the joiner).
    [HarmonyPatch(typeof(Worker), "Update")]
    static class P_WorkerAnimSpeed
    {
        static void Postfix(Worker __instance) { if (Coop.Role == Role.Client && Coop.I.ClientLoaded && __instance.m_Animator) __instance.m_Animator.speed = 1f; }
    }

    // Tile-map animators (water etc.) are disabled with normal time; keep them running on the joiner.
    [HarmonyPatch(typeof(TimeManager), "UpdateAnimators")]
    static class P_TileAnims
    {
        static bool Prefix(List<Animator> ___m_Animations)
        {
            if (Coop.Role != Role.Client || !Coop.I.ClientLoaded) return true;
            foreach (var a in ___m_Animations) if (a) a.enabled = true;
            return false;
        }
    }
}
