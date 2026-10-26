// AutonautsCoop - Shared.cs - world state both sides can edit: the script library (bot server) and planning areas.
// Also: game speed follows the host (joiners can't change it), and bot-server actions on bots go to the host.
// Version 0.12.25 - 2026-10-25 - Created with Claude
//
// The script library and planning areas are plain manager data, not objects, and their editors change them with
// direct calls and field writes. Instead of forwarding every small edit, the joiner watches the whole section while
// its editor is open and sends the full section when it changes; the host replaces its copy and broadcasts it as a
// normal world section. Only one player changes a section at a time (SectionLocks): the first to change it holds
// it for a few seconds after their last change; a change anyone else makes meanwhile is undone with a message.
using System;
using System.Collections.Generic;
using HarmonyLib;
using SimpleJSON;
using UnityEngine;

namespace AutonautsCoop
{
    public class SharedSection
    {
        public string Name;
        public Func<JSONNode> Save;          // current local state
        public Action<JSONNode> Replace;     // replace local state with this
        public Func<bool> Editing;           // local editor open: watch for changes
        public Func<bool> Busy;              // local editor holds references: wait before replacing
        public string Label;                 // for messages ("the script library")
        bool requested; JSONNode lastHost;
        /// Host: combine a joiner's copy with ours by entry (scripts, folders) instead of replacing the section.
        /// (cur, base the joiner edited from, joiner's copy, conflicts out) -> result. Sections with a merge don't lock.
        public Func<JSONNode, JSONNode, JSONNode, List<string>, JSONNode> Merge;

        // joiner
        int baseHash, pushedHash; float pushedAt; bool wasEditing; JSONNode deferred;
        // host
        JSONNode queued;
        public int Pushes, Replaces;         // stats/tests

        /// Parse round trip first: a section built locally and the same section received over the wire print the same.
        static int H(JSONNode n) => n == null ? 0 : JSON.Parse(n.ToString()).ToString().GetHashCode();
        JSONNode held;   // host data that arrived while our own edit was in flight

        public void ClientReset() { baseHash = H(Safe()); pushedHash = 0; deferred = null; held = null; wasEditing = false; lastHost = Safe(); requested = false; seenHash = baseHash; wasOpen = false; }

        /// Both sides: take the section's lock while editing, give it back when done; if someone else holds it,
        /// say so and close the editor (or put the section back to the host's copy when the editor can't close).
        // Locking follows changes, not open editors: several players can look at the library at once. Whoever
        // changes it first holds it until HoldSeconds after their last change (or until their editor closes); a
        // change anyone else makes meanwhile is undone with a message (their editor stays open).
        public bool ChangeBased = true;      // false: Editing() itself means changing (bot groups: "touched lately")
        const float HoldSeconds = 5f;
        int seenHash; float lastChangeAt = -100f; bool wasOpen; JSONNode baseline;
        /// Remember the current state as not-a-local-change (after taking someone else's copy).
        void Rebase() { var n = Safe(); seenHash = H(n); baseline = n; }

        public void LockTick()
        {
            if (Merge != null) return;   // merged by entry: no lock needed
            bool open = Editing();
            float now = Time.realtimeSinceStartup;
            bool mine = SectionLocks.Mine(Name), other = SectionLocks.HeldByOther(Name);
            if (!ChangeBased) { if (open) lastChangeAt = now; }
            else if (open)
            {
                int h = H(Safe());
                if (!wasOpen) seenHash = h;                                    // just opened: nothing changed yet
                else if (h != seenHash) { seenHash = h; lastChangeAt = now; }
            }
            if (!other && !requested && !mine) baseline = null;                  // free: nothing to protect
            if (other && baseline == null) Rebase();                            // someone else started: this is the state to keep
            wasOpen = open;
            bool changing = now - lastChangeAt < HoldSeconds && (open || !ChangeBased);
            if (changing && other)
            {
                // our change while someone else holds it: undo it
                lastChangeAt = -100f;
                Menu.Toast(SectionLocks.Who(Name) + " is editing " + Label + ": your change was undone. Try again when they're done.");
                var back = Coop.Role == Role.Client ? lastHost : baseline;
                if (back != null && H(Safe()) != H(back))
                {
                    if (Busy()) { if (Coop.Role == Role.Client) deferred = back; }
                    else { Sync.Applying = true; try { Replace(back); if (Coop.Role == Role.Client) baseHash = H(Safe()); } finally { Sync.Applying = false; } }
                }
                seenHash = H(Safe());
                return;
            }
            if (changing && !mine && !requested) { requested = true; SectionLocks.Request(Name, true); }
            if (!changing && (mine || requested))
            {
                if (Coop.Role == Role.Client) Flush();   // last edits first
                requested = false; SectionLocks.Request(Name, false);
            }
        }

        /// Joiner: the host refused our copy (someone else holds the lock): take the host's.
        public void ClientRevert(JSONNode n)
        {
            pushedHash = 0; held = null; lastHost = n;
            if (Busy()) { deferred = n; return; }
            Sync.Applying = true; try { Replace(n); Replaces++; baseHash = H(Safe()); seenHash = baseHash; } finally { Sync.Applying = false; }
        }

        JSONNode Safe() { try { return Save(); } catch (Exception e) { Plugin.Log("shared " + Name + " save: " + e.Message); return null; } }

        /// Joiner: send our copy now if it differs from what the host last gave us.
        public void Flush()
        {
            if (SectionLocks.HeldByOther(Name)) return;   // someone else is editing: the host would refuse it
            // (not yet granted is fine: the host takes a change while nobody holds the section, e.g. a script the
            // joiner uploads and links to a bot in the same moment it opened the library)
            var n = Safe(); int h = H(n);
            if (n == null || h == baseHash || h == pushedHash) return;
            var m = new JSONObject(); m["a"] = "shared"; m["n"] = Name; m["s"] = n;
            if (Merge != null && lastHost != null) m["b"] = lastHost;   // what we started from: the host takes only what we changed
            Coop.I.Client.Host.Send(Msg.Input, m.ToString());
            pushedHash = h; pushedAt = Time.realtimeSinceStartup; Pushes++;
            // the host has our changes now: the next push (before its copy comes back) is measured from this one,
            // or a script we added and then renamed would look new again (and get a second UID)
            if (Merge != null) lastHost = n;
            Plugin.Log("shared " + Name + " sent to host");
        }

        public void ClientTick()
        {
            bool ed = Editing();
            if (ed || wasEditing) Flush();
            if (held != null && (pushedHash == 0 || Time.realtimeSinceStartup - pushedAt >= 4f))
            {
                // our edit was never echoed back as-is: take the host's latest
                var h = held; held = null; pushedHash = 0;
                Sync.Applying = true;
                try { ClientIncoming(h); } finally { Sync.Applying = false; }
            }
            wasEditing = ed;
            if (deferred != null && !Busy())
            {
                var d = deferred; deferred = null;
                Sync.Applying = true;
                try { ClientIncoming(d); } finally { Sync.Applying = false; }
            }
        }

        /// Joiner: the host's section arrived.
        public void ClientIncoming(JSONNode n)
        {
            lastHost = n;
            int h = H(n);
            if (pushedHash != 0)
            {
                if (h == pushedHash) { pushedHash = 0; baseHash = h; held = null; return; }   // our own edit, echoed
                if (Time.realtimeSinceStartup - pushedAt < 4f) { held = n; return; }   // maybe older than our edit: wait
                pushedHash = 0;
            }
            if (Editing()) Flush();                                               // unsent local edit wins
            if (pushedHash != 0) return;
            if (Busy()) { deferred = n; return; }
            Replace(n); Replaces++;
            baseHash = H(Safe()); seenHash = baseHash;
        }

        /// Host: a joiner sent its copy.
        public void HostIncoming(JSONNode n, JSONNode basis = null, Peer from = null)
        {
            if (Merge != null && basis != null && !basis.IsNull)
            {
                var conflicts = new List<string>();
                JSONNode merged;
                try { merged = Merge(queued ?? Safe(), basis, n, conflicts); }   // on top of one still waiting
                catch (Exception e) { Plugin.Log("shared " + Name + " merge failed: " + e.Message); merged = n; }
                n = merged;
                if (conflicts.Count > 0 && from != null)
                {
                    Plugin.Log("shared " + Name + " from " + from.Name + ": kept the other change to " + string.Join(", ", conflicts.ToArray()));
                    var r = new JSONObject(); r["note"] = "Someone else changed " + string.Join(", ", conflicts.ToArray()) + " first: your change to it wasn't kept";
                    from.Send(Msg.Info, System.Text.Encoding.UTF8.GetBytes(r.ToString()));
                }
            }
            if (Busy()) { queued = n; Plugin.Log("shared " + Name + " from joiner queued (host editing)"); return; }
            Replace(n); Replaces++; Rebase();
            Plugin.Log("shared " + Name + (Merge != null && basis != null ? " merged from joiner" : " replaced from joiner"));
        }

        public void HostTick()
        {
            if (queued != null && !Busy()) { var q = queued; queued = null; Replace(q); Replaces++; Rebase(); Plugin.Log("shared " + Name + " replaced from joiner (queued)"); }
        }
    }

    public static class Shared
    {
        public static readonly SharedSection Scripts = new SharedSection
        {
            Name = "Scripts", Label = "the script library", Merge = Library.Merge,
            Save = () => { var n = new JSONObject(); WorkerScriptManager.Instance.Save(n); return n; },
            Replace = Library.Replace,
            Editing = () => Library.Open() != null,
            Busy = () => { var s = Library.Open(); return s != null && s.GetIsDragging(); },
        };

        public static readonly SharedSection Planning = new SharedSection
        {
            Name = "Planning", Label = "the planning areas",
            Save = PlanAreas.Save,
            Replace = PlanAreas.Replace,
            Editing = PlanAreas.InPlanning,
            Busy = PlanAreas.InPlanning,
        };

        public static readonly SharedSection WorkerGroups = new SharedSection
        {
            Name = "WorkerGroups", Label = "the bot groups", ChangeBased = false,
            Save = Groups.Save,
            Replace = Groups.Replace,
            Editing = Groups.Editing,
            Busy = Groups.Busy,
        };

        public static readonly SharedSection[] All = { Scripts, Planning, WorkerGroups };
        public static SharedSection Get(string name) { foreach (var s in All) if (s.Name == name) return s; return null; }

        static float next, nextHost;
        public static void ClientTick()
        {
            if (Coop.Role != Role.Client || !Coop.I.ClientLoaded || Time.realtimeSinceStartup < next) return;
            next = Time.realtimeSinceStartup + 0.25f;
            foreach (var s in All) { try { s.ClientTick(); s.LockTick(); } catch (Exception e) { Plugin.Log("shared " + s.Name + ": " + e.Message); } }
            Speed.ClientTick();
        }

        public static void HostTick()
        {
            bool tick = Time.realtimeSinceStartup >= nextHost; if (tick) nextHost = Time.realtimeSinceStartup + 0.25f;
            foreach (var s in All) { try { s.HostTick(); if (tick && Coop.I.Host.AnyRecipient()) s.LockTick(); } catch (Exception e) { Plugin.Log("shared " + s.Name + ": " + e.Message); } }
        }

        public static void OnClientLoaded() { foreach (var s in All) s.ClientReset(); }

        public static bool HostApply(string a, JSONNode n, FarmerPlayer fp, Peer from)
        {
            if (a == "slock") { SectionLocks.HostSet("peer" + from.Id, from.Name, n["n"], n["on"].AsBool); return true; }
            if (a == "shared")
            {
                var s = Get(n["n"]);
                if (s == null || n["s"] == null || n["s"].IsNull) return true;
                if (s.Merge == null && !SectionLocks.HostAllowed("peer" + from.Id, s.Name))
                {
                    // someone else holds it: keep ours and give the sender our copy back
                    Plugin.Log("shared " + s.Name + " from " + from.Name + " refused: " + SectionLocks.Who(s.Name) + " is editing it");
                    var r = new JSONObject(); r["srevert"] = s.Name; r["s"] = s.Save();
                    r["note"] = SectionLocks.Who(s.Name) + " is editing " + s.Label + ": your change wasn't kept";
                    from.Send(Msg.Info, System.Text.Encoding.UTF8.GetBytes(r.ToString()));
                    return true;
                }
                try { s.HostIncoming(n["s"], n["b"], from); } catch (Exception e) { Plugin.Log("shared " + (string)n["n"] + " apply: " + e); }
                return true;
            }
            if (Groups.HostApply(a, n, fp)) return true;
            return Library.HostApply(a, n, fp);
        }
    }

    // ======================= script library =======================
    public static class Library
    {
        /// Host: take from a joiner's copy only the scripts and folders it changed since the copy it started from
        /// (base); everything else stays as we have it (others may have changed it meanwhile). The same entry changed
        /// by both: ours (applied first) stays and the name goes in conflicts. A new entry whose UID someone else used
        /// meanwhile gets a fresh UID.
        public static JSONNode Merge(JSONNode cur, JSONNode basis, JSONNode mine, List<string> conflicts)
        {
            var result = JSON.Parse(cur.ToString());
            int maxUid = Math.Max(cur["UIDCounter"].AsInt, mine["UIDCounter"].AsInt);
            foreach (var arrName in new[] { "ScriptArray", "FolderArray" })
            {
                var c = Index(result[arrName]); var b = Index(basis[arrName]); var m = Index(mine[arrName]);
                foreach (var kv in c) maxUid = Math.Max(maxUid, kv.Key + 1);
                foreach (var kv in m) maxUid = Math.Max(maxUid, kv.Key + 1);
                var outList = new List<JSONNode>(); var order = new List<int>();
                foreach (JSONNode e in result[arrName].AsArray) order.Add(e["UID"].AsInt);
                var keep = new Dictionary<int, JSONNode>(c);
                var keys = new HashSet<int>(b.Keys); keys.UnionWith(m.Keys);
                foreach (var uid in keys)
                {
                    b.TryGetValue(uid, out var bv); m.TryGetValue(uid, out var mv); c.TryGetValue(uid, out var cv);
                    string bs = bv?.ToString(), ms = mv?.ToString(), cs = cv?.ToString();
                    if (ms == bs) continue;                       // the joiner didn't touch it
                    if (ms == cs) continue;                       // already so (an earlier push of the same change)
                    if (bv == null)                               // new on the joiner
                    {
                        if (cv != null) { mv["UID"] = maxUid++; }  // UID taken meanwhile: a fresh one
                        keep[mv["UID"].AsInt] = mv; order.Add(mv["UID"].AsInt);
                        continue;
                    }
                    if (cs != bs) { conflicts.Add("'" + (string)(cv ?? bv)["Name"] + "'"); continue; }   // changed here too
                    if (mv == null) keep.Remove(uid); else keep[uid] = mv;   // deleted / changed by the joiner
                }
                var arr = new JSONArray();
                foreach (var uid in order) if (keep.TryGetValue(uid, out var e)) { arr.Add(e); keep.Remove(uid); }
                result[arrName] = arr;
            }
            if (mine["LinkNames"].ToString() != basis["LinkNames"].ToString()) result["LinkNames"] = mine["LinkNames"];
            result["UIDCounter"] = maxUid;
            return result;
        }
        static Dictionary<int, JSONNode> Index(JSONNode arr)
        {
            var d = new Dictionary<int, JSONNode>();
            if (arr != null && !arr.IsNull) foreach (JSONNode e in arr.AsArray) d[e["UID"].AsInt] = e;
            return d;
        }

        public static BotServerSelect Open()
        {
            if (!GameStateManager.Instance || GameStateManager.Instance.GetActualState() != GameStateManager.State.BuildingSelect) return null;
            return UnityEngine.Object.FindObjectOfType<BotServerSelect>();
        }

        /// Replace the whole library, then put back the links bots and data storages hold (they live on the bots).
        public static void Replace(JSONNode n)
        {
            var wsm = WorkerScriptManager.Instance;
            wsm.m_Scripts.Clear(); wsm.m_Folders.Clear(); wsm.m_Stored.Clear();
            wsm.Load(n); wsm.PostLoad();
            foreach (var kv in Sync.Savables())
            {
                var b = kv.Key; if (!b) continue;
                var w = b.GetComponent<Worker>();
                if (w && w.m_WorkerInterpreter != null && w.m_WorkerInterpreter.m_LinkedScriptUID != -1)
                {
                    int uid = w.m_WorkerInterpreter.m_LinkedScriptUID;
                    if (wsm.GetScriptInstructions(uid, false) != null) wsm.LinkBotToScript(uid, w);
                    else { w.m_WorkerInterpreter.m_LinkedScriptUID = -1; Traverse.Create(w.m_WorkerInterpreter).Field("m_LinkedHighInstructions").SetValue(null); }
                }
                var ds = b.GetComponent<DataStorageCrude>();
                if (ds && ds.m_LinkedScriptUID != -1)
                {
                    if (wsm.GetScriptInstructions(ds.m_LinkedScriptUID, false) != null) wsm.LinkDataStorageToScript(ds.m_LinkedScriptUID, ds);
                    else ds.m_LinkedScriptUID = -1;
                }
            }
            Refresh();
        }

        /// Rebuild an open bot server list so it shows (and edits) the new entries.
        static void Refresh()
        {
            var sel = Open();
            if (!sel) return;
            try
            {
                sel.ClearSelection();
                var list = Traverse.Create(sel).Field("m_Scripts").GetValue<List<BotServerStorageBase>>();
                if (list != null) { foreach (var e in list) if (e) UnityEngine.Object.Destroy(e.gameObject); list.Clear(); }
                AccessTools.Method(typeof(BotServerSelect), "CreateScripts").Invoke(sel, new object[] { WorkerScriptManager.Instance.m_Stored, null });
                sel.UpdatePositions(); sel.UpdateVisibility();
                AccessTools.Method(typeof(BotServerSelect), "UpdateLinkButtons").Invoke(sel, null);
            }
            catch (Exception e) { Plugin.Log("bot server refresh: " + e.Message); }
        }

        // ---- joiner: bot-server actions that change bots or data storages (the library itself syncs as a section) ----
        public static bool Forward => Build.Intercept && !Sync.Applying && Open() != null;

        public static void Send(string a, BaseClass target, Action<JSONObject> args = null)
        {
            Shared.Scripts.Flush();   // the host must know a script before a bot links to it
            var n = new JSONObject(); n["a"] = a; n["w"] = target ? target.m_UniqueID : -1;
            args?.Invoke(n);
            Coop.I.Client.Host.Send(Msg.Input, n.ToString());
            Sent++;
        }
        public static int Sent;

        public static JSONObject SaveList(List<HighInstruction> list)
        {
            var hl = new HighInstructionList(); hl.m_List.AddRange(list);
            var s = new JSONObject(); hl.Save(s); return s;
        }

        public static bool HostApply(string a, JSONNode n, FarmerPlayer fp)
        {
            if (!a.StartsWith("wi_") && !a.StartsWith("ds_")) return false;
            var o = ObjectTypeList.Instance.GetObjectFromUniqueID(n["w"].AsInt, false);
            var w = o ? o.GetComponent<Worker>() : null;
            var ds = o ? o.GetComponent<DataStorageCrude>() : null;
            var wsm = WorkerScriptManager.Instance;
            int s = n["s"].AsInt;
            switch (a)
            {
                case "wi_link":
                    if (!w || wsm.GetScriptInstructions(s, false) == null) { Plugin.Log("wi_link refused: bot or script " + s + " missing"); return true; }
                    w.m_WorkerInterpreter.LinkHighInstructions(s);
                    break;
                case "wi_unlink":
                    if (!w) return true;
                    w.m_WorkerInterpreter.UnlinkHighInstructions();
                    break;
                case "wi_set":
                case "wi_add":
                {
                    if (!w) return true;
                    var hl = new HighInstructionList(); hl.Load(n["l"]); hl.PostLoad();
                    if (a == "wi_set") w.m_WorkerInterpreter.SetHighInstructions(hl.m_List, n["start"].AsBool);
                    else w.m_WorkerInterpreter.AddHighInstructions(hl.m_List, n["start"].AsBool);
                    break;
                }
                case "ds_copy":
                    if (!ds || wsm.GetScriptInstructions(s, false) == null) return true;
                    ds.Copy(s, n["linked"].AsBool);
                    break;
                case "ds_add":
                    if (!ds || wsm.GetScriptInstructions(s, false) == null) return true;
                    ds.Add(s);
                    break;
                default: return true;
            }
            Plugin.Log("joiner bot server " + a + " on " + (o ? o.m_TypeIdentifier + " " + o.m_UniqueID : "?") + " script " + s);
            return true;
        }
    }

    [HarmonyPatch(typeof(WorkerInterpreter), nameof(WorkerInterpreter.LinkHighInstructions))]
    static class P_LibLink
    {
        static void Prefix(WorkerInterpreter __instance, int LinkedScriptUID)
        {
            if (!Library.Forward) return;
            var w = Traverse.Create(__instance).Field("m_Worker").GetValue<Worker>();
            Library.Send("wi_link", w, n => n["s"] = LinkedScriptUID);
        }
    }

    [HarmonyPatch(typeof(WorkerInterpreter), nameof(WorkerInterpreter.UnlinkHighInstructions))]
    static class P_LibUnlink
    {
        static void Prefix(WorkerInterpreter __instance)
        {
            // LinkHighInstructions unlinks first; that one is covered by the link message
            if (!Library.Forward || __instance.m_LinkedScriptUID == -1 || P_LibGuard.InLink) return;
            var w = Traverse.Create(__instance).Field("m_Worker").GetValue<Worker>();
            Library.Send("wi_unlink", w);
        }
    }

    [HarmonyPatch(typeof(WorkerInterpreter), nameof(WorkerInterpreter.LinkHighInstructions))]
    static class P_LibGuard
    {
        public static bool InLink;
        static void Prefix() { InLink = true; }
        static void Finalizer() { InLink = false; }
    }

    [HarmonyPatch(typeof(WorkerInterpreter), nameof(WorkerInterpreter.SetHighInstructions))]
    static class P_LibSet
    {
        static void Prefix(WorkerInterpreter __instance, List<HighInstruction> NewInstructions, bool Start)
        {
            if (!Library.Forward) return;
            var w = Traverse.Create(__instance).Field("m_Worker").GetValue<Worker>();
            Library.Send("wi_set", w, n => { n["l"] = Library.SaveList(NewInstructions); n["start"] = Start; });
        }
    }

    [HarmonyPatch(typeof(WorkerInterpreter), nameof(WorkerInterpreter.AddHighInstructions))]
    static class P_LibAdd
    {
        static void Prefix(WorkerInterpreter __instance, List<HighInstruction> NewInstructions, bool Start)
        {
            if (!Library.Forward) return;
            var w = Traverse.Create(__instance).Field("m_Worker").GetValue<Worker>();
            Library.Send("wi_add", w, n => { n["l"] = Library.SaveList(NewInstructions); n["start"] = Start; });
        }
    }

    [HarmonyPatch(typeof(DataStorageCrude), nameof(DataStorageCrude.Copy), new[] { typeof(int), typeof(bool) })]
    static class P_LibDsCopy
    {
        static void Prefix(DataStorageCrude __instance, int LinkedScriptUID, bool MakeLinked)
        {
            if (!Library.Forward) return;
            Library.Send("ds_copy", __instance, n => { n["s"] = LinkedScriptUID; n["linked"] = MakeLinked; });
        }
    }

    [HarmonyPatch(typeof(DataStorageCrude), nameof(DataStorageCrude.Add), new[] { typeof(int) })]
    static class P_LibDsAdd
    {
        static void Prefix(DataStorageCrude __instance, int LinkedScriptUID)
        {
            if (!Library.Forward) return;
            Library.Send("ds_add", __instance, n => n["s"] = LinkedScriptUID);
        }
    }

    // ======================= planning areas =======================
    public static class PlanAreas
    {
        public static bool InPlanning() => GameStateManager.Instance && GameStateManager.Instance.GetActualState() == GameStateManager.State.Planning;

        /// Only the areas are shared; show/hide toggles stay per player.
        public static JSONNode Save()
        {
            var n = new JSONObject(); PlanningManager.Instance.Save(n);
            var o = new JSONObject(); o["Areas"] = n["Areas"]; return o;
        }

        public static void Replace(JSONNode n)
        {
            var pm = PlanningManager.Instance;
            var list = Traverse.Create(pm).Field("m_PlanningArea").GetValue<List<PlanningArea>>();
            foreach (var a in new List<PlanningArea>(list)) pm.DeleteArea(a);
            var full = new JSONObject();
            full["Areas"] = n["Areas"];
            full["ShowAreas"] = pm.m_ShowAreas; full["ShowNames"] = pm.m_ShowNames; full["ShowDimensions"] = pm.m_ShowDimensions;
            pm.Load(full);
        }

        public static int Count() => Traverse.Create(PlanningManager.Instance).Field("m_PlanningArea").GetValue<List<PlanningArea>>().Count;
    }

    // ======================= game speed =======================
    /// The host decides the game speed; joiners follow it and can't change it.
    public static class Speed
    {
        public static bool HostPaused;
        static float lastToast;

        public static JSONNode Save()
        {
            var n = new JSONObject();
            n["S"] = TimeManager.Instance.m_TimeScale;
            n["P"] = TimeManager.Instance.m_PauseScreenEnabled;
            return n;
        }

        public static void ClientApply(JSONNode n)
        {
            float s = n["S"].AsFloat;
            HostPaused = n["P"].AsBool;
            if (Mathf.Abs(TimeManager.Instance.m_TimeScale - s) < 0.001f) return;
            TimeManager.Instance.SetTimeScale(s);
            bool fast = TimeManager.Instance.GetIsFastTime();
            var hen = CollectionManager.Instance.GetCollection("StoneHenge");
            if (hen != null) foreach (var kv in hen) try { kv.Key.GetComponent<StoneHenge>().SetActive(fast); } catch { }
            try { var fb = HudManager.Instance.m_FastTimeButton; if (fb) AccessTools.Method(typeof(FastButton), "UpdateImage").Invoke(fb, null); } catch { }
            Plugin.Log("game speed from host: x" + s);
        }

        public static bool Blocked()
        {
            if (Coop.Role != Role.Client || Sync.Applying) return false;
            if (Time.realtimeSinceStartup - lastToast > 2f) { lastToast = Time.realtimeSinceStartup; Menu.Toast("Only the host can change the game speed"); }
            return true;
        }

        public static void ClientTick() { }
    }

    [HarmonyPatch(typeof(TimeManager), nameof(TimeManager.ToggleFastTime))]
    static class P_SpeedToggle { static bool Prefix() => !Speed.Blocked(); }

    [HarmonyPatch(typeof(TimeManager), nameof(TimeManager.SetTimeScale))]
    static class P_SpeedSet { static bool Prefix() => Coop.Role != Role.Client || Sync.Applying; }
}


namespace AutonautsCoop
{
    /// One editor per shared section (script library, planning areas, bot groups). The host owns the table and
    /// broadcasts it; a joiner asks for a section when its editor opens and gives it back when it closes.
    public static class SectionLocks
    {
        static readonly Dictionary<string, string> who = new Dictionary<string, string>();     // section -> player name
        static readonly Dictionary<string, string> owner = new Dictionary<string, string>();   // section -> owner key
        public static string Who(string sec) => who.TryGetValue(sec, out var w) ? w : "someone";
        public static bool Mine(string sec) => owner.TryGetValue(sec, out var k) && k == Locks.MyKey;
        public static bool HeldByOther(string sec) => owner.TryGetValue(sec, out var k) && k != Locks.MyKey;

        public static void Request(string sec, bool on)
        {
            if (Coop.Role == Role.Host) { HostSet(Locks.HostKey, SteamNet.MyName() + " (host)", sec, on); return; }
            if (Coop.Role != Role.Client || Coop.I.Client?.Host == null) return;
            var n = new JSONObject(); n["a"] = "slock"; n["n"] = sec; n["on"] = on;
            Coop.I.Client.Host.Send(Msg.Input, n.ToString());
        }

        // ---- host ----
        public static void HostSet(string key, string name, string sec, bool on)
        {
            if (string.IsNullOrEmpty(sec)) return;
            owner.TryGetValue(sec, out var cur);
            if (on)
            {
                if (cur != null && cur != key) { Plugin.Log("section lock refused: " + name + " wants " + sec + ", held by " + who[sec]); Broadcast(); return; }
                if (cur == key) return;
                owner[sec] = key; who[sec] = name;
                if (key != Locks.HostKey) { var s = Shared.Get(sec); if (s != null && s.Editing()) Menu.Toast(name + " is editing " + s.Label); }   // (only if we're looking at it too)
            }
            else
            {
                if (cur != key) return;
                owner.Remove(sec); who.Remove(sec);
            }
            Diag.Log("section " + sec + (on ? " locked by " : " released by ") + name);
            Broadcast();
        }
        public static bool HostAllowed(string key, string sec) => !owner.TryGetValue(sec, out var k) || k == key;
        public static void HostPeerGone(Peer p)
        {
            string key = "peer" + p.Id; var gone = new List<string>();
            foreach (var kv in owner) if (kv.Value == key) gone.Add(kv.Key);
            foreach (var g in gone) HostSet(key, p.Name, g, false);
        }
        static void Broadcast()
        {
            if (Coop.I.Host == null) return;
            var n = new JSONObject(); var arr = new JSONArray();
            foreach (var kv in owner) { var e = new JSONArray(); e.Add(kv.Key); e.Add(who[kv.Key]); e.Add(kv.Value); arr.Add(e); }
            n["slocks"] = arr;
            Coop.I.Host.Broadcast(Msg.Info, System.Text.Encoding.UTF8.GetBytes(n.ToString()));
        }

        // ---- joiner ----
        public static void ClientReceive(JSONArray arr)
        {
            var before = new Dictionary<string, string>(owner);
            owner.Clear(); who.Clear();
            foreach (JSONNode e in arr) { owner[e[0]] = e[2]; who[e[0]] = e[1]; }
            foreach (var kv in owner)
                if (kv.Value != Locks.MyKey && (!before.TryGetValue(kv.Key, out var b) || b != kv.Value))
                { var s = Shared.Get(kv.Key); if (s != null && s.Editing()) Menu.Toast(who[kv.Key] + " is editing " + s.Label); }
        }
        public static void Reset() { owner.Clear(); who.Clear(); }
    }
}
