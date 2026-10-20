// AutonautsCoop - Build.cs - joiner edit mode: place / drag-place / delete / move buildings via the host
// Version 0.3.0 - 2026-10-20 - Created with Claude
//
// The joiner's edit mode runs normally (palette, cursor, previews, rotation). At the points where it
// would change the world, the request goes to the host instead and nothing happens locally; the
// result comes back through normal sync. Undo is disabled for joiners (the host owns history).
using System.Collections.Generic;
using HarmonyLib;
using SimpleJSON;
using UnityEngine;

namespace AutonautsCoop
{
    public static class Build
    {
        public static bool Intercept => Coop.Role == Role.Client && Coop.I.ClientLoaded && !Sync.Applying;
        public static int Sent;   // stats / tests

        static void Send(JSONObject n) { Sent++; Coop.I.Client.Host.Send(Msg.Input, n.ToString()); }
        static string TypeName(ObjectType t) => ObjectTypeList.Instance.GetSaveNameFromIdentifier(t);
        static ObjectType TypeOf(string s) => ObjectTypeList.Instance.GetIdentifierFromSaveName(s, false);

        // ---------- client: requests ----------
        public static void SendAdd(TileCoord t, ObjectType type, int rot, bool flip, Building orig, bool instant, bool force, bool upgrades)
        {
            var n = new JSONObject();
            n["a"] = "build"; n["x"] = t.x; n["y"] = t.y; n["type"] = TypeName(type); n["rot"] = rot; n["flip"] = flip;
            n["orig"] = orig ? orig.m_UniqueID : -1; n["inst"] = instant; n["force"] = force; n["upg"] = upgrades;
            Send(n);
        }

        public static void SendDestroy(Building b, bool returnResource)
        {
            var n = new JSONObject(); n["a"] = "unbuild"; n["u"] = b.m_UniqueID; n["ret"] = returnResource; Send(n);
        }

        public static void SendMoves(List<MovingBuilding> list)
        {
            var n = new JSONObject(); n["a"] = "move"; var arr = new JSONArray();
            foreach (var m in list)
            {
                var e = new JSONArray(); e.Add(m.m_Building.m_UniqueID); e.Add(m.m_NewPosition.x); e.Add(m.m_NewPosition.y); e.Add(m.m_NewRotation); e.Add(m.m_NewFlipped ? 1 : 0);
                arr.Add(e);
            }
            n["m"] = arr; Send(n);
        }

        public static void SendDrag(ObjectType type, bool force, List<Building> dragModels)
        {
            var n = new JSONObject(); n["a"] = "drag"; n["type"] = TypeName(type); n["force"] = force;
            var arr = new JSONArray();
            foreach (var d in dragModels)
            {
                if (!d) continue;
                var t = new TileCoord(d.transform.localPosition);
                var e = new JSONArray(); e.Add(t.x); e.Add(t.y); e.Add(d.m_Rotation); arr.Add(e);
            }
            n["t"] = arr; Send(n);
        }

        // ---------- host: apply ----------
        public static bool HostApply(string a, JSONNode n)
        {
            switch (a)
            {
                case "build":
                {
                    var type = TypeOf(n["type"]);
                    if (type == ObjectTypeList.m_Total) { Plugin.Log("build: unknown type " + n["type"]); return true; }
                    var origObj = n["orig"].AsInt >= 0 ? ObjectTypeList.Instance.GetObjectFromUniqueID(n["orig"].AsInt, false) : null;
                    var t = new TileCoord(n["x"].AsInt, n["y"].AsInt);
                    if (TileManager.Instance.GetTile(t) == null) return true;
                    var b = BuildingManager.Instance.AddBuilding(t, type, n["rot"].AsInt, n["flip"].AsBool, origObj ? origObj.GetComponent<Building>() : null,
                                                                 n["inst"].AsBool, n["force"].AsBool, n["upg"].AsBool);
                    if (b) QuestManager.Instance.AddEvent(QuestEvent.Type.AddBlueprint, Bot: false, type, null);
                    Plugin.Log("joiner placed " + n["type"] + " at " + t.x + "," + t.y + (b ? " uid " + b.m_UniqueID : " (refused)"));
                    return true;
                }
                case "unbuild":
                {
                    var o = ObjectTypeList.Instance.GetObjectFromUniqueID(n["u"].AsInt, false);
                    var b = o ? o.GetComponent<Building>() : null;
                    if (!b || !b.GetIsSavable()) return true;
                    if (!(bool)b.GetActionInfo(new GetActionInfo(GetAction.IsDeletable))) { Plugin.Log("unbuild refused: not deletable " + b.m_UniqueID); return true; }
                    b.PlayerDeleted();
                    BuildingManager.Instance.DestroyBuilding(b, n["ret"].AsBool);
                    Plugin.Log("joiner deleted " + b.m_TypeIdentifier + " " + b.m_UniqueID);
                    return true;
                }
                case "move":
                {
                    var list = new List<MovingBuilding>();
                    int i = 0;
                    foreach (JSONNode e in n["m"].AsArray)
                    {
                        var o = ObjectTypeList.Instance.GetObjectFromUniqueID(e[0].AsInt, false);
                        var b = o ? o.GetComponent<Building>() : null;
                        if (!b) continue;
                        list.Add(new MovingBuilding(i++, b, new TileCoord(e[1].AsInt, e[2].AsInt), e[3].AsInt, e[4].AsInt == 1));
                    }
                    if (list.Count > 0) BuildingManager.Instance.MoveBuildings(list);
                    Plugin.Log("joiner moved " + list.Count + " building(s)");
                    return true;
                }
                case "drag":
                {
                    var type = TypeOf(n["type"]);
                    if (type == ObjectTypeList.m_Total) return true;
                    bool isFloor = Floor.GetIsTypeFloor(type);
                    int placed = 0;
                    foreach (JSONNode e in n["t"].AsArray)
                    {
                        var t = new TileCoord(e[0].AsInt, e[1].AsInt);
                        var tile = TileManager.Instance.GetTile(t);
                        if (tile == null) continue;
                        // same occupancy rules as GameStateEdit.ConvertDragModels (train-track crossings excepted)
                        if (tile.m_Building && !isFloor && tile.m_Building.m_NumLevels == tile.m_Building.m_MaxLevels) continue;
                        if (tile.m_Floor && isFloor) continue;
                        var b = BuildingManager.Instance.AddBuilding(t, type, e[2].AsInt, false, null, false, n["force"].AsBool);
                        if (b) placed++;
                    }
                    if (placed > 0) QuestManager.Instance.AddEvent(QuestEvent.Type.AddBlueprint, Bot: false, type, null);
                    Plugin.Log("joiner drag-placed " + placed + " " + n["type"]);
                    return true;
                }
            }
            return false;
        }
    }

    // ---------- client patches ----------

    [HarmonyPatch(typeof(BuildingManager), nameof(BuildingManager.AddBuilding), new[] { typeof(TileCoord), typeof(ObjectType), typeof(int), typeof(bool), typeof(Building), typeof(bool), typeof(bool), typeof(bool) })]
    static class P_ClientAddBuilding
    {
        static bool Prefix(TileCoord TilePosition, ObjectType NewType, int Rotation, bool Flipped, Building OriginalBuilding, bool Instant, bool ForceBlueprint, bool IncludeUpgrades, ref Building __result)
        {
            if (!Build.Intercept) return true;
            Build.SendAdd(TilePosition, NewType, Rotation, Flipped, OriginalBuilding, Instant, ForceBlueprint, IncludeUpgrades);
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(BuildingManager), nameof(BuildingManager.DestroyBuilding))]
    static class P_ClientDestroyBuilding
    {
        static bool Prefix(Actionable TargetObject, bool ReturnResource, ref bool __result)
        {
            if (!Build.Intercept) return true;
            var b = TargetObject ? TargetObject.GetComponent<Building>() : null;
            if (b) { b.SetHighlight(false); Build.SendDestroy(b, ReturnResource); }
            __result = true;
            return false;
        }
    }

    // Building.PlayerDeleted runs before DestroyBuilding in edit mode; on the joiner it would drop contents locally.
    // It is virtual (Converter, ConverterFoundation override it), so patch every declaration.
    [HarmonyPatch]
    static class P_ClientPlayerDeleted
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (var t in new[] { typeof(Building), typeof(Converter), typeof(ConverterFoundation) })
            {
                var m = AccessTools.DeclaredMethod(t, "PlayerDeleted");
                if (m != null) yield return m;
            }
        }
        static bool Prefix() { return !Build.Intercept; }
    }

    [HarmonyPatch(typeof(BuildingManager), nameof(BuildingManager.MoveBuildings))]
    static class P_ClientMoveBuildings
    {
        static bool Prefix(List<MovingBuilding> MovingList)
        {
            if (!Build.Intercept) return true;
            Build.SendMoves(MovingList);
            return false;
        }
    }

    [HarmonyPatch(typeof(GameStateEdit), "ConvertDragModels")]
    static class P_ClientConvertDrag
    {
        static bool Prefix(GameStateEdit __instance)
        {
            if (!Build.Intercept) return true;
            var t = Traverse.Create(__instance);
            Build.SendDrag(GameStateEdit.m_CurrentObjectType, t.Field("m_CTRLHeld").GetValue<bool>(), t.Field("m_DragModels").GetValue<List<Building>>());
            return false;
        }
    }

    // Joiners have no undo: their changes happen on the host. (UndoAction is a private enum, so match by name.)
    [HarmonyPatch]
    static class P_NoUndo
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(GameStateEdit)))
                if (m.Name == "AddUndoAction" || m.Name == "Undo") yield return m;
        }
        static bool Prefix() { return Coop.Role != Role.Client; }
    }

}
