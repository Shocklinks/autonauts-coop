// AutonautsCoop - Menus.cs - building menus (recipes, research, storage, animal stations, signs) for joiners
// Version 0.7.0 - 2026-10-20 - Created with Claude
//
// In single player, a building opens its menu when the player engages it (Converter/Storage/ResearchStation/
// AnimalStation/BotServer.SendAction(Engaged) -> GameStateManager.StartSelectBuilding; Sign -> StartRenameSign).
// A joiner's engage runs on the host, so without this the *host's* screen would pop the joiner's menu.
// Host: those calls are redirected to the joiner who engaged. Joiner: opens the menu on its mirror building;
// menu actions (recipe SetValue, disengage, cancel, research choice, storage lock, upgrade, sign text/area) go to the host.
using System;
using System.Collections.Generic;
using HarmonyLib;
using SimpleJSON;
using UnityEngine;

namespace AutonautsCoop
{
    public static class Menus
    {
        public static int Opened, Sent;   // stats/tests
        public static int EngagedUid = -1;   // joiner: building whose menu/screen is open

        /// Joiner: sync may rebuild the building behind an open menu; the new copy needs my player as engager again
        /// (menus call m_Engager.SendAction on close).
        public static void KeepEngager()
        {
            if (EngagedUid < 0 || Coop.Role != Role.Client) return;
            if (!MenuOpen()) { EngagedUid = -1; return; }
            var o = ObjectTypeList.Instance.GetObjectFromUniqueID(EngagedUid, false);
            var me = ObjectTypeList.Instance.GetObjectFromUniqueID(Sync.MyPlayerUID, false);
            if (o && me && !o.GetComponent<Actionable>().m_Engager) o.GetComponent<Actionable>().m_Engager = me.GetComponent<Actionable>();
        }

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
            EngagedUid = b.m_UniqueID;
            Opened++;
            Sync.Applying = true;   // the open itself is host-driven, nothing to forward
            try
            {
                string kind = n["open"];
                if (kind == "sign") GameStateManager.Instance.StartRenameSign(b.GetComponent<Sign>());
                else if (kind == "wardrobe") GameStateManager.Instance.StartWardrobe(b.GetComponent<Wardrobe>());
                else if (kind == "aquarium") GameStateManager.Instance.StartAquarium(b.GetComponent<Aquarium>());
                else if (kind == "hostonly")
                {
                    b.m_Engager = null;
                    Menu.Toast("Only the host can use this building for now");
                }
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

        public static bool IsRemote(Actionable a)
        {
            return Coop.Role == Role.Host && a && a.m_TypeIdentifier == ObjectType.FarmerPlayer && a.m_UniqueID != Sync.HostPlayerUID;
        }

        /// Host: a joiner engaged a building whose screen we can't run for them yet: let go of it and tell them.
        public static bool HostOnly(Actionable b)
        {
            if (Coop.Role != Role.Host || !b || !IsRemote(b.m_Engager)) return false;
            var fp = b.m_Engager.GetComponent<FarmerPlayer>();
            HostRedirect(b, b.m_TypeIdentifier, "hostonly");
            b.m_Engager = null;
            if (fp) fp.SendAction(new ActionInfo(ActionType.DisengageObject, fp.m_TileCoord));
            return true;
        }

        /// Wardrobe/Aquarium only let a player engage while *the host's* screen is in Normal state. For a joiner the
        /// host's screen is irrelevant; pretend it's Normal during those checks.
        public static bool ForceNormal;

        public static bool MenuOpen()
        {
            if (!GameStateManager.Instance) return false;
            var st = GameStateManager.Instance.GetActualState();
            return st == GameStateManager.State.BuildingSelect || st == GameStateManager.State.EditArea || st == GameStateManager.State.Inventory;
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
            bool wardrobeClose = Info.m_Action == ActionType.DisengageObject && Info.m_Object && (Info.m_Object.m_TypeIdentifier == ObjectType.Wardrobe || Aquarium.GetIsTypeAquiarium(Info.m_Object.m_TypeIdentifier));
            if (!Build.Intercept || Keys.InKeys || __instance.m_UniqueID != Sync.MyPlayerUID || (!Menus.MenuOpen() && !wardrobeClose)) return;
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

    // ---------- host: wardrobe, aquarium, catapult, spaceport ----------
    [HarmonyPatch(typeof(GameStateManager), nameof(GameStateManager.StartWardrobe))]
    static class P_HostWardrobe { static bool Prefix(Wardrobe NewWardrobe) { return !Menus.HostRedirect(NewWardrobe, ObjectType.Wardrobe, "wardrobe"); } }

    [HarmonyPatch(typeof(GameStateManager), nameof(GameStateManager.StartAquarium))]
    static class P_HostAquarium { static bool Prefix(Aquarium NewAquarium) { return !Menus.HostRedirect(NewAquarium, NewAquarium ? NewAquarium.m_TypeIdentifier : ObjectType.Aquarium, "aquarium"); } }

    [HarmonyPatch(typeof(GameStateManager), nameof(GameStateManager.StartCatapult))]
    static class P_HostCatapult { static bool Prefix(Catapult NewCatapult) { return !Menus.HostOnly(NewCatapult); } }

    [HarmonyPatch(typeof(GameStateManager), nameof(GameStateManager.StartSpacePort))]
    static class P_HostSpacePort { static bool Prefix(SpacePort NewSpacePort) { return !Menus.HostOnly(NewSpacePort); } }

    [HarmonyPatch(typeof(GameStateManager), nameof(GameStateManager.GetActualState))]
    static class P_ForceNormal { static bool Prefix(ref GameStateManager.State __result) { if (!Menus.ForceNormal) return true; __result = GameStateManager.State.Normal; return false; } }

    [HarmonyPatch]
    static class P_RemoteEngageGate
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (var t in new[] { typeof(Wardrobe), typeof(Aquarium) })
            {
                yield return AccessTools.DeclaredMethod(t, "SendAction");
                yield return AccessTools.DeclaredMethod(t, "CanDoAction");
            }
        }
        static void Prefix(ActionInfo Info, out bool __state)
        {
            __state = Info != null && Info.m_Action == ActionType.Engaged && Menus.IsRemote(Info.m_Object);
            if (__state) Menus.ForceNormal = true;
        }
        static void Finalizer(bool __state) { if (__state) Menus.ForceNormal = false; }
    }

    [HarmonyPatch(typeof(GameStateInventory), nameof(GameStateInventory.Close))]
    static class P_InvCloseKeepEngager
    {
        // The panel may still point at the pre-sync copy (destroyed), so the game's own disengage never fires.
        // Make sure the host hears that the joiner left the wardrobe/aquarium.
        static void Prefix(out int __state) { Menus.KeepEngager(); __state = Menus.Sent; }
        static void Postfix(int __state)
        {
            if (!Build.Intercept || Menus.EngagedUid < 0) return;
            int uid = Menus.EngagedUid; Menus.EngagedUid = -1;
            if (Menus.Sent != __state) return;
            var n = new JSONObject(); n["a"] = "menu_act"; n["A"] = ActionType.DisengageObject.ToString(); n["V1"] = ""; n["V2"] = ""; n["O"] = uid;
            Menus.Sent++; Coop.I.Client.Host.Send(Msg.Input, n.ToString());
        }
    }
}
