// AutonautsCoop - Creative.cs - joiners in Creative mode: the spawn menu, and no "Load" on a joiner's pause screen
// Version 0.13.0 - 2026-10-26 - Created with Claude
//
// Creative mode's object menu made the item on the joiner's own copy of the world: a local object the host never
// had, so sync removed it again (nothing appeared to spawn). The joiner's click now goes to the host, which spawns the
// same thing into the joiner's character's hands (bots and folk next to them), exactly as the game's menu does; the
// result comes back through sync like any other change.
// A joiner loading a save from the pause screen crashed the game (the mirrored world is not a save of their own):
// the Load button is hidden for joiners and its action refused.
using System;
using System.Collections.Generic;
using HarmonyLib;
using SimpleJSON;
using UnityEngine;

namespace AutonautsCoop
{
    public static class Creative
    {
        // ---------------- host ----------------
        public static bool HostOp(string a, JSONNode n, Peer from)
        {
            if (a != "creative") return false;
            try
            {
                if (GameOptionsManager.Instance.m_Options.m_GameMode != GameOptions.GameMode.ModeCreative && !CheatManager.Instance.m_CheatsEnabled) return true;
                if (!Sync.PeerPlayers.TryGetValue(from, out int pu)) return true;
                var po = ObjectTypeList.Instance.GetObjectFromUniqueID(pu, false);
                var f = po ? po.GetComponent<Farmer>() : null;
                if (!f) return true;
                Spawn(f, (ObjectType)n["t"].AsInt, (ObjectInfo.Type)n["k"].AsInt, (ObjectType)n["x"].AsInt, n["s"].AsBool, n["cheat"].AsBool);
                Diag.Log("creative: " + from.Name + " spawned " + (ObjectType)n["t"].AsInt);
            }
            catch (Exception e) { Plugin.Log("creative spawn: " + e); }
            return true;
        }

        /// CreativeTools.OnObjectClicked, for a given farmer instead of the local player.
        static void Spawn(Farmer f, ObjectType t, ObjectInfo.Type kind, ObjectType extra, bool shift, bool cheat)
        {
            if (kind == ObjectInfo.Type.Bot)
            {
                var tc = f.m_TileCoord + new TileCoord(0, 1);
                if (!tc.GetIsValid()) return;
                var w = ObjectTypeList.Instance.CreateObjectFromIdentifier(ObjectType.Worker, tc.ToWorldPositionTileCentered(), Quaternion.identity).GetComponent<Worker>();
                switch ((int)extra)
                {
                    case 0: w.SetFrame(ObjectType.WorkerFrameMk0); w.SetDrive(ObjectType.WorkerDriveMk0); w.SetHead(ObjectType.WorkerHeadMk0); break;
                    case 1: w.SetFrame(ObjectType.WorkerFrameMk1); w.SetDrive(ObjectType.WorkerDriveMk1); w.SetHead(ObjectType.WorkerHeadMk1); break;
                    case 2: w.SetFrame(ObjectType.WorkerFrameMk2); w.SetDrive(ObjectType.WorkerDriveMk2); w.SetHead(ObjectType.WorkerHeadMk2); break;
                    case 3: w.SetFrame(ObjectType.WorkerFrameMk3); w.SetDrive(ObjectType.WorkerDriveMk3); w.SetHead(ObjectType.WorkerHeadMk3); break;
                }
                w.UpdateModel();
                Scan.MarkDirty(w);
                return;
            }
            if (f.m_State != Farmer.State.None || (!cheat && QuestManager.Instance.m_ObjectsLocked.ContainsKey(t))) return;
            bool more = shift && f.m_State != Farmer.State.Moving;
            do
            {
                var b = ObjectTypeList.Instance.CreateObjectFromIdentifier(t, f.m_TileCoord.ToWorldPositionTileCentered(), Quaternion.identity);
                if (!b) return;
                if (kind == ObjectInfo.Type.Container) { var tf = b.GetComponent<ToolFillable>(); tf.Fill(extra, tf.m_Capacity); }
                if (kind == ObjectInfo.Type.Folk) b.GetComponent<Folk>().SetTier((int)extra);
                if (b.GetComponent<Vehicle>() || b.m_TypeIdentifier == ObjectType.AnimalSilkmoth) break;
                if (!ToolFillable.GetIsTypeFillable(f.m_FarmerCarry.GetTopObjectType()))
                    f.SendAction(new ActionInfo(ActionType.SetTool, f.m_TileCoord, b.GetComponent<Actionable>()));
                else more = false;
            }
            while (more && f.m_FarmerCarry.CanAddCarry(t));
            Scan.MarkDirty(f);
        }
    }

    // ---------------- joiner: the click goes to the host ----------------
    [HarmonyPatch(typeof(CreativeTools), nameof(CreativeTools.OnObjectClicked))]
    static class P_ClientCreativeSpawn
    {
        static readonly AccessTools.FieldRef<ObjectSelect, List<ObjectInfo>> list = AccessTools.FieldRefAccess<ObjectSelect, List<ObjectInfo>>("m_ObjectList");
        static readonly AccessTools.FieldRef<ObjectSelect, List<BaseButtonImage>> buttons = AccessTools.FieldRefAccess<ObjectSelect, List<BaseButtonImage>>("m_ObjectButtons");

        static bool Prefix(CreativeTools __instance, BaseGadget NewGadget)
        {
            if (Coop.Role != Role.Client) return true;
            try
            {
                int i = buttons(__instance).IndexOf(NewGadget.GetComponent<BaseButtonImage>());
                if (i < 0) return false;
                var info = list(__instance)[i];
                var me = ObjectTypeList.Instance.GetObjectFromUniqueID(Sync.MyPlayerUID, false);
                var f = me ? me.GetComponent<Farmer>() : null;
                if (info.m_Type != ObjectInfo.Type.Bot && f && f.m_State != Farmer.State.None) return false;   // as the game: not while busy
                var n = new JSONObject();
                n["a"] = "creative"; n["t"] = (int)info.m_ObjectType; n["k"] = (int)info.m_Type; n["x"] = (int)info.m_Extra;
                n["s"] = MyInputManager.Instance.GetShiftHeld(); n["cheat"] = __instance is CheatTools;
                Coop.I.Client.Host.Send(Msg.Input, n.ToString());
                Diag.Log("send creative " + info.m_ObjectType);
                GameStateManager.Instance.PopState();
            }
            catch (Exception e) { Plugin.Log("creative click: " + e); }
            return false;
        }
    }

    // ---------------- joiner: no Load on the pause screen ----------------
    [HarmonyPatch(typeof(Paused), "Start")]
    static class P_ClientPausedNoLoad
    {
        static void Postfix(Paused __instance)
        {
            if (Coop.Role != Role.Client) return;
            var l = __instance.transform.Find("LoadButton");
            if (l) l.gameObject.SetActive(false);
        }
    }

    [HarmonyPatch(typeof(Paused), nameof(Paused.OnLoad))]
    static class P_ClientPausedOnLoad
    {
        static bool Prefix()
        {
            if (Coop.Role != Role.Client) return true;
            Menu.Toast("Loading a save while connected would crash the game. Quit to the main menu first.");
            return false;
        }
    }

    // anything else that opens the Load screen while connected (keyboard shortcut, another menu): back out of it
    [HarmonyPatch(typeof(GameStateLoad), "Init")]
    static class P_ClientNoLoadScreen
    {
        static void Postfix()
        {
            if (Coop.Role != Role.Client || !Coop.I.ClientLoaded) return;
            Menu.Toast("Loading a save while connected would crash the game. Quit to the main menu first.");
            GameStateManager.Instance.PopState();
        }
    }
}
