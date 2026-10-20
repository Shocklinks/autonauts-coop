// AutonautsCoop - Teach.cs - joiners teach and control bots
// Version 0.4.0 - 2026-10-20 - Created with Claude
//
// The joiner's teach UI (record, script editor, play/stop/step) runs locally on the mirror bot.
// The world-changing calls are forwarded to the host, which runs them on the real bot and on the
// joiner's FarmerPlayer there:
//   Farmer.StartLearning / Worker.RequestStopLearning      bot starts/stops following the teacher
//   GameStateTeachWorker2.SetTarget / Close                 host-side FarmerPlayer records actions
//   Worker.NewHighScriptTaught                              the finished script (HighInstruction JSON)
//   Worker.StopAllScripts / TogglePauseScript / StartStep   play controls
//   FarmerPlayer.UseWhistle                                 whistle calls
// While teaching, the joiner performs actions on the host (normal input forwarding). The host's
// FarmerPlayer records them in m_TeachingActions; the host streams them back and the joiner's editor
// turns them into instructions exactly as in single player.
// While the joiner has a bot open in the editor, sync doesn't rebuild that bot (the UI holds it).
using System;
using System.Collections.Generic;
using HarmonyLib;
using SimpleJSON;
using UnityEngine;

namespace AutonautsCoop
{
    public static class Teach
    {
        public static bool Intercept => Build.Intercept;
        public static int Sent;
        public static int LastStarted = -1;   // host: last bot started from a joiner's script (tests)
        static void Send(JSONObject n) { Sent++; Coop.I.Client.Host.Send(Msg.Input, n.ToString()); }
        static JSONObject Req(string a, BaseClass w) { var n = new JSONObject(); n["a"] = a; n["w"] = w ? w.m_UniqueID : -1; return n; }

        // ---------- client: requests ----------
        public static void SendLearnStart(Worker w) => Send(Req("learn_start", w));
        public static void SendLearnStop(Worker w) => Send(Req("learn_stop", w));
        public static void SendTeachOn(Worker w) => Send(Req("teach_on", w));
        public static void SendTeachOff() => Send(Req("teach_off", null));
        public static void SendStop(Worker w) => Send(Req("bot_stop", w));
        public static void SendPause(Worker w) => Send(Req("bot_pause", w));
        public static void SendStep(Worker w) => Send(Req("bot_step", w));
        public static void SendWhistle(FarmerPlayer.WhistleCall c) { var n = Req("whistle", null); n["c"] = (int)c; Send(n); }

        public static void SendScript(Worker w, List<HighInstruction> list, bool start, bool updateLinked)
        {
            var n = Req("bot_script", w);
            var hl = new HighInstructionList();
            hl.m_List.AddRange(list);
            var s = new JSONObject(); hl.Save(s);
            n["s"] = s; n["start"] = start; n["link"] = updateLinked;
            Send(n);
        }

        // ---------- host ----------
        static Worker W(JSONNode n)
        {
            var o = ObjectTypeList.Instance.GetObjectFromUniqueID(n["w"].AsInt, false);
            return o ? o.GetComponent<Worker>() : null;
        }

        /// Plot UIDs are local to each machine; instructions that point at a plot get the host's plot at that tile.
        static void RemapPlots(List<HighInstruction> list)
        {
            if (list == null) return;
            foreach (var h in list)
            {
                var ai = h.m_ActionInfo;
                if (ai != null && ai.m_ObjectType == ObjectType.Plot && ai.m_ObjectUID != 0)
                {
                    var p = PlotManager.Instance.GetPlotAtTile(ai.m_Position);
                    ai.m_ObjectUID = p ? p.m_UniqueID : 0;
                    ai.m_Object = p ? p.GetComponent<Actionable>() : null;
                }
                RemapPlots(h.m_Children); RemapPlots(h.m_Children2);
            }
        }

        public static bool HostApply(string a, JSONNode n, FarmerPlayer fp)
        {
            if (a.StartsWith("learn") || a.StartsWith("teach") || a.StartsWith("bot_"))
            {
                var w0 = W(n);
                Plugin.Log("joiner op " + a + " bot " + n["w"].AsInt + (w0 ? " learning=" + w0.m_Learning + " state=" + w0.m_State + " instr=" + w0.m_WorkerInterpreter.m_HighInstructions.m_List.Count : "") + " fp=" + (fp ? fp.m_UniqueID.ToString() : "none"));
            }
            switch (a)
            {
                case "learn_start": { var w = W(n); if (w && fp) { w.StopAllScripts(); w.StartLearning(fp); Plugin.Log("joiner: bot " + w.m_UniqueID + " learning"); } return true; }
                case "learn_stop":  { var w = W(n); if (w && w.m_Learning) w.RequestStopLearning(); return true; }
                case "teach_on":
                {
                    var w = W(n);
                    if (w && fp) { fp.SetIsTeaching(true, w); w.m_WorkerInterpreter.StopAll(); relayed[fp.m_UniqueID] = 0; Plugin.Log("joiner: teaching bot " + w.m_UniqueID); }
                    return true;
                }
                case "teach_off": { if (fp) { fp.SetIsTeaching(false); relayed.Remove(fp.m_UniqueID); } return true; }
                case "bot_stop":  { var w = W(n); if (w) w.StopAllScripts(); return true; }
                case "bot_pause": { var w = W(n); if (w) w.TogglePauseScript(); return true; }
                case "bot_step":  { var w = W(n); if (w) w.StartStep(); return true; }
                case "whistle":   { if (fp) fp.UseWhistle((FarmerPlayer.WhistleCall)n["c"].AsInt); return true; }
                case "bot_script":
                {
                    var w = W(n);
                    if (!w) return true;
                    var hl = new HighInstructionList();
                    hl.Load(n["s"]);
                    hl.PostLoad();
                    RemapPlots(hl.m_List);
                    if (w.m_Learning) w.RequestStopLearning();
                    w.NewHighScriptTaught(hl.m_List, n["start"].AsBool, n["link"].AsBool);
                    if (n["start"].AsBool && w.m_WorkerInterpreter.GetCurrentScript() != null) LastStarted = w.m_UniqueID;
                    Plugin.Log("joiner: bot " + w.m_UniqueID + " got script, " + hl.m_List.Count + " instructions, start " + n["start"].AsBool);
                    return true;
                }
            }
            return false;
        }

        // host -> joiner: actions recorded while the joiner teaches
        static readonly Dictionary<int, int> relayed = new Dictionary<int, int>();
        public static void HostTick()
        {
            foreach (var kv in Sync.PeerPlayers)
            {
                var peer = kv.Key;
                if (!peer.Alive || !relayed.TryGetValue(kv.Value, out int sent)) continue;
                var o = ObjectTypeList.Instance.GetObjectFromUniqueID(kv.Value, false);
                var fp = o ? o.GetComponent<FarmerPlayer>() : null;
                if (!fp || !fp.m_Teaching || fp.m_TeachingActions.Count <= sent) continue;
                var arr = new JSONArray();
                for (int i = sent; i < fp.m_TeachingActions.Count; i++) arr.Add(SaveAction(fp.m_TeachingActions[i]));
                relayed[kv.Value] = fp.m_TeachingActions.Count;
                var m = new JSONObject(); m["teach"] = arr;
                peer.Send(Msg.Info, m.ToString());
            }
        }

        static JSONObject SaveAction(ActionInfo ai)
        {
            var j = new JSONObject();
            j["A"] = ai.m_Action.ToString(); j["X"] = ai.m_Position.x; j["Y"] = ai.m_Position.y;
            bool plot = ai.m_Object && ai.m_Object.GetComponent<Plot>();
            j["P"] = plot;
            j["UID"] = ai.m_Object ? ai.m_Object.m_UniqueID : ai.m_ObjectUID;
            j["OT"] = ObjectTypeList.Instance.GetSaveNameFromIdentifier(ai.m_ObjectType);
            j["V1"] = ai.m_Value ?? ""; j["V2"] = ai.m_Value2 ?? "";
            j["AT"] = (int)ai.m_ActionType; j["R"] = ai.m_ActionRequirement ?? "";
            j["AOT"] = ObjectTypeList.Instance.GetSaveNameFromIdentifier(ai.m_ActionObjectType);
            return j;
        }

        // ---------- client ----------
        public static readonly List<ActionInfo> MyTeachActions = new List<ActionInfo>();
        public static bool MyTeaching;
        public static Worker MyTeachTarget;
        public static int Received;

        public static void ClientReceive(JSONArray arr)
        {
            foreach (JSONNode j in arr)
            {
                var pos = new TileCoord(j["X"].AsInt, j["Y"].AsInt);
                Actionable obj = null;
                if (j["P"].AsBool) { var p = PlotManager.Instance.GetPlotAtTile(pos); obj = p ? p.GetComponent<Actionable>() : null; }
                else if (j["UID"].AsInt > 0) { var b = ObjectTypeList.Instance.GetObjectFromUniqueID(j["UID"].AsInt, false); obj = b ? b.GetComponent<Actionable>() : null; }
                var ai = new ActionInfo(Actionable.GetActionFromName(j["A"]), pos, obj, j["V1"], j["V2"], (AFO.AT)j["AT"].AsInt, j["R"],
                                        ObjectTypeList.Instance.GetIdentifierFromSaveName(j["AOT"], false));
                if (!obj) { ai.m_ObjectUID = j["UID"].AsInt; ai.m_ObjectType = ObjectTypeList.Instance.GetIdentifierFromSaveName(j["OT"], false); }
                MyTeachActions.Add(ai);
                Received++;
            }
            AttachToMyPlayer();
        }

        /// The joiner's FarmerPlayer can be rebuilt by sync (e.g. when its hands change); keep its teaching
        /// state on one list so the editor's action counter stays valid.
        public static void AttachToMyPlayer()
        {
            var o = ObjectTypeList.Instance.GetObjectFromUniqueID(Sync.MyPlayerUID, false);
            var fp = o ? o.GetComponent<FarmerPlayer>() : null;
            if (!fp) return;
            if (fp.m_TeachingActions != MyTeachActions) fp.m_TeachingActions = MyTeachActions;
            if (MyTeaching && !fp.m_Teaching) { fp.m_Teaching = true; Traverse.Create(fp).Field("m_TeachingTarget").SetValue(MyTeachTarget); }
        }

        /// When sync rebuilds a bot that is selected in the normal view, point the selection at the new copy.
        public static void Reselect(int uid)
        {
            var o = ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false);
            var w = o ? o.GetComponent<Worker>() : null;
            var gs = GameStateManager.Instance ? GameStateManager.Instance.GetCurrentState() : null;
            var normal = gs ? gs.GetComponent<GameStateNormal>() : null;
            if (w && normal) normal.SetSelectedWorker(w, false, false);
        }

        public static bool IsSelected(BaseClass b)
        {
            var ed = TeachWorkerScriptEdit.Instance;
            return b && ed != null && ed.m_CurrentTarget == b;
        }

        /// The bot the joiner is teaching or editing (sync leaves it alone until they're done).
        public static int EditingUid()
        {
            if (Coop.Role != Role.Client) return -1;
            var ed = TeachWorkerScriptEdit.Instance;
            if (ed == null || !ed.m_CurrentTarget) return -1;
            var st = GameStateManager.Instance ? GameStateManager.Instance.GetActualState() : GameStateManager.State.Normal;
            if (st != GameStateManager.State.TeachWorker && st != GameStateManager.State.SelectWorker) return -1;
            return ed.m_CurrentTarget.m_UniqueID;
        }
    }

    // ---------- client patches ----------

    [HarmonyPatch(typeof(Farmer), nameof(Farmer.StartLearning))]
    static class P_LearnStart { static void Prefix(Farmer __instance) { if (Teach.Intercept && __instance is Worker w) Teach.SendLearnStart(w); } }

    [HarmonyPatch(typeof(Worker), nameof(Worker.RequestStopLearning))]
    static class P_LearnStop { static void Prefix(Worker __instance) { if (Teach.Intercept) Teach.SendLearnStop(__instance); } }

    [HarmonyPatch(typeof(GameStateTeachWorker2), nameof(GameStateTeachWorker2.SetTarget))]
    static class P_TeachOn
    {
        static void Prefix(Worker Target)
        {
            if (!Teach.Intercept) return;
            Teach.MyTeachActions.Clear(); Teach.MyTeaching = true; Teach.MyTeachTarget = Target;
            Teach.SendTeachOn(Target);
        }
        static void Postfix() { if (Teach.Intercept) Teach.AttachToMyPlayer(); }
    }

    [HarmonyPatch(typeof(GameStateTeachWorker2), nameof(GameStateTeachWorker2.Close))]
    static class P_TeachOff { static void Prefix() { if (Teach.Intercept) { Teach.MyTeaching = false; Teach.MyTeachTarget = null; Teach.SendTeachOff(); } } }

    [HarmonyPatch(typeof(Worker), nameof(Worker.NewHighScriptTaught))]
    static class P_Script
    {
        static void Prefix(Worker __instance, List<HighInstruction> NewInstructions, bool Start, bool UpdateLinkedBots)
        {
            if (Teach.Intercept) Teach.SendScript(__instance, NewInstructions, Start, UpdateLinkedBots);
        }
    }

    [HarmonyPatch(typeof(Worker), nameof(Worker.StopAllScripts))]
    static class P_BotStop { static void Prefix(Worker __instance) { if (Teach.Intercept && Teach.EditingUid() == __instance.m_UniqueID) Teach.SendStop(__instance); } }

    [HarmonyPatch(typeof(Worker), nameof(Worker.TogglePauseScript))]
    static class P_BotPause { static void Prefix(Worker __instance) { if (Teach.Intercept) Teach.SendPause(__instance); } }

    [HarmonyPatch(typeof(Worker), nameof(Worker.StartStep))]
    static class P_BotStep { static void Prefix(Worker __instance) { if (Teach.Intercept) Teach.SendStep(__instance); } }

    [HarmonyPatch(typeof(FarmerPlayer), nameof(FarmerPlayer.UseWhistle))]
    static class P_Whistle
    {
        static void Prefix(FarmerPlayer __instance, FarmerPlayer.WhistleCall NewCall)
        {
            if (Teach.Intercept && __instance.m_UniqueID == Sync.MyPlayerUID && NewCall != FarmerPlayer.WhistleCall.Cancel) Teach.SendWhistle(NewCall);
        }
    }
}
