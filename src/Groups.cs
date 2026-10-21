// AutonautsCoop - Groups.cs - bot groups for joiners: group edits sync both ways, group buttons run on the host
// Version 0.10.0 - 2026-10-21 - Created with Claude
//
// Groups live in WorkerGroupManager (ids + worker uid lists) with UI panels in TabWorkers. Edits are direct calls
// (TabWorkers.NewGroup/SetWorkerGroup/SetGroupGroup/RemoveGroup, WorkerGroup.SetColour, EditGroup name fields).
// The whole group list is a shared section: the joiner sends it after it touched groups, the host replaces its copy.
// Group Play/Stop/Pause/Drop/Call act on bots, so those are sent as commands and run on the host.
using System;
using System.Collections.Generic;
using HarmonyLib;
using SimpleJSON;
using UnityEngine;

namespace AutonautsCoop
{
    public static class Groups
    {
        static float touchedAt = -10f;
        public static void Touch() { if (Build.Intercept) touchedAt = Time.realtimeSinceStartup; }
        public static bool Editing() => Time.realtimeSinceStartup - touchedAt < 2f;
        public static bool Busy()
        {
            var tw = TabWorkers.Instance;
            return tw && Traverse.Create(tw).Field("m_Drag").GetValue<bool>();
        }

        public static JSONNode Save() { var n = new JSONObject(); WorkerGroupManager.Instance.Save(n); return n; }

        /// Replace all groups, keeping the bots tab's panels consistent (Load re-adds panels and members itself).
        public static void Replace(JSONNode n)
        {
            var wgm = WorkerGroupManager.Instance; var tw = TabWorkers.Instance;
            var edit = HudManager.Instance ? HudManager.Instance.m_CurrentEditGroup : null;
            int editId = edit && edit.gameObject.activeSelf && edit.m_Group != null ? edit.m_Group.m_ID : -1;
            if (edit && editId >= 0) edit.SetGroup(null);
            foreach (var kv in Sync.Savables())
            {
                var w = kv.Key ? kv.Key.GetComponent<Worker>() : null;
                if (w && w.m_Group != null) { try { tw.SetWorkerGroup(w, null); } catch { w.m_Group = null; } }
            }
            foreach (var g in new List<WorkerGroup>(wgm.m_Groups))
            {
                try { if (g.m_WorkerGroupPanel) tw.RemoveGroup(g); else wgm.DeleteGroup(g); }
                catch { wgm.DeleteGroup(g); }
            }
            wgm.m_Groups.Clear();
            wgm.Load(n);
            try { AccessTools.Method(typeof(TabWorkers), "UpdateContents").Invoke(tw, null); } catch { }
            if (edit && editId >= 0)
            {
                var g = wgm.GetGroupFromID(editId);
                if (g != null) edit.SetGroup(g); else edit.gameObject.SetActive(false);
            }
        }

        /// Joiner: sync rebuilt a bot; put the new copy back in its group (the group still lists its uid).
        public static void Relink(Worker w)
        {
            if (!w || w.m_Group != null || WorkerGroupManager.Instance == null || !TabWorkers.Instance) return;
            foreach (var g in WorkerGroupManager.Instance.m_Groups)
                if (g.m_WorkerUIDs.Contains(w.m_UniqueID))
                {
                    g.m_WorkerUIDs.Remove(w.m_UniqueID);
                    try { TabWorkers.Instance.SetWorkerGroup(w, g); } catch (Exception e) { Plugin.Log("group relink: " + e.Message); }
                    return;
                }
        }

        // ---------- group buttons ----------
        public static int Sent; public static string LastCmd = "";
        public static void SendCmd(string c, WorkerGroup g, bool paused = false)
        {
            var n = new JSONObject(); n["a"] = "group_cmd"; n["c"] = c; n["g"] = g != null ? g.m_ID : -1; n["p"] = paused;
            Sent++; Coop.I.Client.Host.Send(Msg.Input, n.ToString());
        }

        static Worker W(int uid) { var o = ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false); return o ? o.GetComponent<Worker>() : null; }

        static void Each(WorkerGroup g, Action<Worker> a, int depth = 0)
        {
            if (g == null || depth > 20) return;
            foreach (var uid in new List<int>(g.m_WorkerUIDs)) { var w = W(uid); if (w) a(w); }
            foreach (var gid in new List<int>(g.m_GroupUIDs)) Each(WorkerGroupManager.Instance.GetGroupFromID(gid), a, depth + 1);
        }

        public static bool HostApply(string a, JSONNode n, FarmerPlayer fp)
        {
            if (a != "group_cmd") return false;
            var g = WorkerGroupManager.Instance.GetGroupFromID(n["g"].AsInt);
            if (g == null) { Plugin.Log("group_cmd: no group " + n["g"].AsInt); return true; }
            string c = n["c"];
            switch (c)
            {
                case "stop":   // EditGroup.StopChildren
                    Each(g, w =>
                    {
                        if (w.m_BeingHeld || w.m_WorkerInterpreter.GetCurrentScript() == null) return;
                        if (w.m_InterruptState == Farmer.State.Paused) w.TogglePauseScript();
                        w.StopAllScripts();
                        if (w.m_WorkerInterpreter.m_LinkedScriptChanged) w.m_WorkerInterpreter.CopyLinkedScript();
                    });
                    break;
                case "play":   // EditGroup.PlayChildren
                    Each(g, w =>
                    {
                        if (w.m_BeingHeld) return;
                        if (w.m_WorkerInterpreter.GetCurrentScript() == null)
                        {
                            var list = w.m_WorkerInterpreter.GetHighInstructionList().m_List;
                            if (list != null) w.NewHighScriptTaught(list, true, false);
                        }
                        else if (w.m_InterruptState == Farmer.State.Paused) w.TogglePauseScript();
                    });
                    break;
                case "pause":  // EditGroup.PauseChildren
                {
                    bool paused = n["p"].AsBool;
                    Each(g, w =>
                    {
                        if (!w.m_BeingHeld && w.m_WorkerInterpreter.GetCurrentScript() != null && (w.m_InterruptState != Farmer.State.Paused || paused)) w.TogglePauseScript();
                    });
                    break;
                }
                case "drop":   // EditGroup.DropAllChildren
                    Each(g, w => { if (w.m_WorkerInterpreter.GetCurrentScript() == null) { w.m_FarmerCarry.DropAllObjects(); w.m_FarmerInventory.DropAllObjects(); } });
                    break;
                case "call":   // EditGroup.CallAllChildren, to the joiner who pressed it
                    if (fp) Each(g, w => { if (w.m_WorkerInterpreter.GetCurrentScript() == null) w.GoToPlayer(fp); });
                    break;
                default: return true;
            }
            LastCmd = c;
            Plugin.Log("joiner group " + c + " on " + g.m_Name);
            return true;
        }
    }

    // ---------- joiner: mark group edits ----------
    [HarmonyPatch]
    static class P_GroupTouch
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (var n in new[] { "NewGroup", "SetWorkerGroup", "SetGroupGroup", "RemoveGroup", "AddWorkersToGroup", "AddSelectedGroupToGroup", "EndDrag" })
            { var m = AccessTools.Method(typeof(TabWorkers), n); if (m != null) yield return m; }
            foreach (var n in new[] { "OnNameInputChanged", "OnDescriptionInputChanged", "OnColourChanged", "OnUngroupClicked" })
            { var m = AccessTools.Method(typeof(EditGroup), n); if (m != null) yield return m; }
            yield return AccessTools.Method(typeof(WorkerGroup), nameof(WorkerGroup.SetColour));
        }
        static void Postfix() { Groups.Touch(); }
    }

    // ---------- joiner: group buttons run on the host ----------
    [HarmonyPatch(typeof(EditGroup), "StopChildren")]
    static class P_GroupStop { static bool Prefix(WorkerGroup NewGroup) { if (!Build.Intercept) return true; Groups.SendCmd("stop", NewGroup); return false; } }

    [HarmonyPatch(typeof(EditGroup), "PlayChildren")]
    static class P_GroupPlay { static bool Prefix(WorkerGroup NewGroup) { if (!Build.Intercept) return true; Groups.SendCmd("play", NewGroup); return false; } }

    [HarmonyPatch(typeof(EditGroup), "PauseChildren")]
    static class P_GroupPause { static bool Prefix(WorkerGroup NewGroup, bool Paused) { if (!Build.Intercept) return true; Groups.SendCmd("pause", NewGroup, Paused); return false; } }

    [HarmonyPatch(typeof(EditGroup), "DropAllChildren")]
    static class P_GroupDrop { static bool Prefix(WorkerGroup NewGroup) { if (!Build.Intercept) return true; Groups.SendCmd("drop", NewGroup); return false; } }

    [HarmonyPatch(typeof(EditGroup), "CallAllChildren")]
    static class P_GroupCall { static bool Prefix(WorkerGroup NewGroup) { if (!Build.Intercept) return true; Groups.SendCmd("call", NewGroup); return false; } }
}
