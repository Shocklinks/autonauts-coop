// AutonautsCoop - Menus.cs - building menus (recipes, research, storage, animal stations, signs) for joiners
// Version 0.6.0 - 2026-10-20 - Created with Claude
//
// In single player, a building opens its menu when the player engages it (Converter/Storage/ResearchStation/
// AnimalStation/BotServer.SendAction(Engaged) -> GameStateManager.StartSelectBuilding; Sign -> StartRenameSign).
// A joiner's engage runs on the host, so without this the *host's* screen would pop the joiner's menu.
// Host: those calls are redirected to the joiner who engaged. Joiner: opens the menu on its mirror building;
// menu actions (recipe SetValue, disengage, cancel, research choice, storage lock, upgrade, sign text/area) go to the host.
using System;
using HarmonyLib;
using SimpleJSON;
using UnityEngine;

namespace AutonautsCoop
{
    public static class Menus
    {
        public static int Opened, Sent;   // stats/tests

        /// Host: which peer owns this FarmerPlayer (null = the host's own).
        static Peer PeerOf(Actionable engager)
        {
            if (!engager || engager.m_TypeIdentifier != ObjectType.FarmerPlayer) return null;
            foreach (var kv in Sync.PeerPlayers) if (kv.Value == engager.m_UniqueID && kv.Key.Alive) return kv.Key;
            return null;
        }

        public static bool HostRedirect(Actionable b, ObjectType type, string kind)
        {
            if (Coop.Role != Role.Host || !b) return false;
            var peer = PeerOf(b.m_Engager);
            if (peer == null) return false;
            var n = new JSONObject(); n["open"] = kind; n["b"] = b.m_UniqueID; n["t"] = ObjectTypeList.Instance.GetSaveNameFromIdentifier(type);
            peer.Send(Msg.Info, n.ToString());
            Plugin.Log("menu " + kind + " for " + b.m_TypeIdentifier + " " + b.m_UniqueID + " sent to peer " + peer.Id);
            return true;
        }

        // ---------- joiner ----------
        public static void ClientOpen(JSONNode n)
        {
            var o = ObjectTypeList.Instance.GetObjectFromUniqueID(n["b"].AsInt, false);
            var me = ObjectTypeList.Instance.GetObjectFromUniqueID(Sync.MyPlayerUID, false);
            var b = o ? o.GetComponent<Actionable>() : null;
            if (!b || !me) { Plugin.Log("menu open: building or player missing"); return; }
            b.m_Engager = me.GetComponent<Actionable>();   // menus talk to m_Engager
            Opened++;
            Sync.Applying = true;   // the open itself is host-driven, nothing to forward
            try
            {
                if ((string)n["open"] == "sign") GameStateManager.Instance.StartRenameSign(b.GetComponent<Sign>());
                else GameStateManager.Instance.StartSelectBuilding(b.GetComponent<Building>(), ObjectTypeList.Instance.GetIdentifierFromSaveName(n["t"], false));
            }
            finally { Sync.Applying = false; }
        }

        public static void Send(string m, BaseClass b, Action<JSONObject> args = null)
        {
            var n = new JSONObject(); n["a"] = "bop"; n["m"] = m; n["b"] = b ? b.m_UniqueID : -1;
            args?.Invoke(n);
            Sent++; Coop.I.Client.Host.Send(Msg.Input, n.ToString());
        }

        // ---------- host ----------
        public static bool HostApply(string a, JSONNode n, FarmerPlayer fp)
        {
            if (a != "bop") return false;
            var o = ObjectTypeList.Instance.GetObjectFromUniqueID(n["b"].AsInt, false);
            if (!o) return true;
            // only the building this joiner is engaged with
            if (!fp || o.GetComponent<Actionable>().m_Engager != fp.GetComponent<Actionable>()) { Plugin.Log("bop " + (string)n["m"] + " refused: not engaged with " + o.m_UniqueID); return true; }
            switch ((string)n["m"])
            {
                case "cancel_build": o.GetComponent<Converter>()?.CancelBuild(); break;
                case "cancel_research": o.GetComponent<ResearchStation>()?.CancelResearch(); break;
                case "research": o.GetComponent<ResearchStation>()?.SetCurrentResearchQuest((Quest.ID)n["q"].AsInt); break;
                case "animal_stop": o.GetComponent<AnimalStation>()?.StopAll(); break;
                case "storage_lock": o.GetComponent<Storage>()?.ToggleLock(); break;
                case "upgrade": o.GetComponent<Building>()?.BeginUpgrade(); break;
                case "sign":
                {
                    var s = o.GetComponent<Sign>();
                    if (!s) break;
                    s.SetName(n["name"]);
                    s.m_AreaLinkedToPosition = n["linked"].AsBool;
                    // the sign's area travels in its save JSON
                    if (ObjectTypeList.Instance.GetObjectFromUniqueID(s.m_UniqueID, false) == s) ObjectTypeList.Instance.RemoveActionable(s);
                    s.GetComponent<Savable>().Load(n["json"]);
                    s.GetComponent<Savable>().PostLoad();
                    break;
                }
                default: Plugin.Log("bop unknown " + (string)n["m"]); return true;
            }
            Plugin.Log("joiner menu op " + (string)n["m"] + " on " + o.m_TypeIdentifier + " " + o.m_UniqueID);
            return true;
        }

        public static bool MenuOpen()
        {
            if (!GameStateManager.Instance) return false;
            var st = GameStateManager.Instance.GetActualState();
            return st == GameStateManager.State.BuildingSelect || st == GameStateManager.State.EditArea;
        }
    }

    // ---------- host: send menus to the joiner who engaged ----------
    [HarmonyPatch(typeof(GameStateManager), nameof(GameStateManager.StartSelectBuilding))]
    static class P_HostSelectBuilding
    {
        static bool Prefix(Building NewBuilding, ObjectType BuildingType)
        {
            return !Menus.HostRedirect(NewBuilding, BuildingType == ObjectType.Nothing && NewBuilding ? NewBuilding.m_TypeIdentifier : BuildingType, "select");
        }
    }

    [HarmonyPatch(typeof(GameStateManager), nameof(GameStateManager.StartRenameSign))]
    static class P_HostRenameSign
    {
        static bool Prefix(Sign NewSign) { return !Menus.HostRedirect(NewSign, NewSign ? NewSign.m_TypeIdentifier : ObjectType.Nothing, "sign"); }
    }

    // ---------- joiner: menu actions ----------
    [HarmonyPatch(typeof(Converter), nameof(Converter.CancelBuild))]
    static class P_MCancelBuild { static bool Prefix(Converter __instance) { if (!Build.Intercept || !Menus.MenuOpen()) return true; Menus.Send("cancel_build", __instance); return false; } }

    [HarmonyPatch(typeof(ConverterFoundation), nameof(ConverterFoundation.CancelBuild))]
    static class P_MCancelBuild2 { static bool Prefix(ConverterFoundation __instance) { if (!Build.Intercept || !Menus.MenuOpen()) return true; Menus.Send("cancel_build", __instance); return false; } }

    [HarmonyPatch(typeof(ResearchStation), nameof(ResearchStation.CancelResearch))]
    static class P_MCancelResearch { static bool Prefix(ResearchStation __instance) { if (!Build.Intercept || !Menus.MenuOpen()) return true; Menus.Send("cancel_research", __instance); return false; } }

    [HarmonyPatch(typeof(ResearchStation), nameof(ResearchStation.SetCurrentResearchQuest))]
    static class P_MResearch
    {
        static bool Prefix(ResearchStation __instance, Quest.ID NewQuestID)
        {
            if (!Build.Intercept || !Menus.MenuOpen()) return true;
            Menus.Send("research", __instance, n => n["q"] = (int)NewQuestID);
            return false;
        }
    }

    [HarmonyPatch(typeof(AnimalStation), nameof(AnimalStation.StopAll))]
    static class P_MAnimalStop { static bool Prefix(AnimalStation __instance) { if (!Build.Intercept || !Menus.MenuOpen()) return true; Menus.Send("animal_stop", __instance); return false; } }

    [HarmonyPatch(typeof(Storage), nameof(Storage.ToggleLock))]
    static class P_MStorageLock { static bool Prefix(Storage __instance) { if (!Build.Intercept || !Menus.MenuOpen()) return true; Menus.Send("storage_lock", __instance); return false; } }

    [HarmonyPatch(typeof(Building), nameof(Building.BeginUpgrade))]
    static class P_MUpgrade { static bool Prefix(Building __instance) { if (!Build.Intercept || !Menus.MenuOpen()) return true; Menus.Send("upgrade", __instance); return false; } }

    [HarmonyPatch(typeof(RenameSign), "EndEdit")]
    static class P_MSign
    {
        static void Prefix(RenameSign __instance)
        {
            if (!Build.Intercept) return;
            var s = Traverse.Create(__instance).Field("m_Sign").GetValue<Sign>();
            if (!s) return;
            var js = new JSONObject(); js["ID"] = ObjectTypeList.Instance.GetSaveNameFromIdentifier(s.m_TypeIdentifier); s.GetComponent<Savable>().Save(js);
            Menus.Send("sign", s, n => { n["name"] = s.m_SignName; n["linked"] = s.m_AreaLinkedToPosition; n["json"] = js; });
        }
    }

    // A menu's own SendAction calls on my player (recipe choice = SetValue, closing = DisengageObject) go to the host
    // as well as running locally so the menu closes and shows the choice at once.
    [HarmonyPatch(typeof(FarmerPlayer), nameof(FarmerPlayer.SendAction))]
    static class P_MenuPlayerAction
    {
        static void Prefix(FarmerPlayer __instance, ActionInfo Info)
        {
            if (!Build.Intercept || Keys.InKeys || __instance.m_UniqueID != Sync.MyPlayerUID || !Menus.MenuOpen()) return;
            if (Info.m_Action != ActionType.SetValue && Info.m_Action != ActionType.DisengageObject) return;
            var n = new JSONObject(); n["a"] = "menu_act";
            n["A"] = Info.m_Action.ToString(); n["V1"] = Info.m_Value ?? ""; n["V2"] = Info.m_Value2 ?? "";
            n["O"] = Info.m_Object ? Info.m_Object.m_UniqueID : -1;
            Menus.Sent++; Coop.I.Client.Host.Send(Msg.Input, n.ToString());
        }
    }

    public static class MenuActApply
    {
        public static bool HostApply(string a, JSONNode n, FarmerPlayer fp)
        {
            if (a == "menu_closed")
            {
                if (fp && fp.m_EngagedObject && fp.m_EngagedObject.m_UniqueID == n["b"].AsInt) fp.SendAction(new ActionInfo(ActionType.DisengageObject, fp.m_TileCoord));
                return true;
            }
            if (a != "menu_act") return false;
            if (!fp) return true;
            var act = Actionable.GetActionFromName(n["A"]);
            if (act != ActionType.SetValue && act != ActionType.DisengageObject) return true;
            var o = n["O"].AsInt >= 0 ? ObjectTypeList.Instance.GetObjectFromUniqueID(n["O"].AsInt, false) : null;
            if (act == ActionType.SetValue && (!o || o.GetComponent<Actionable>().m_Engager != fp.GetComponent<Actionable>())) { Plugin.Log("menu SetValue refused: not engaged"); return true; }
            fp.SendAction(new ActionInfo(act, fp.m_TileCoord, o ? o.GetComponent<Actionable>() : null, n["V1"], n["V2"]));
            Plugin.Log("joiner menu action " + act + " " + (string)n["V1"] + "=" + (string)n["V2"] + (o ? " on " + o.m_TypeIdentifier : ""));
            return true;
        }
    }

    // Closing a menu with Escape just pops the state; make sure the host lets go of the building too.
    [HarmonyPatch(typeof(GameStateBuildingSelect), "OnDestroy")]
    static class P_MenuClosed
    {
        static void Prefix(GameStateBuildingSelect __instance)
        {
            if (!Build.Intercept || !__instance.m_Building) return;
            var n = new JSONObject(); n["a"] = "menu_closed"; n["b"] = __instance.m_Building.m_UniqueID;
            Coop.I.Client.Host.Send(Msg.Input, n.ToString());
        }
    }
}
