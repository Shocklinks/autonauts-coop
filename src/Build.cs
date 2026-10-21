// AutonautsCoop - Build.cs - joiner edit mode: place / drag-place / delete / move buildings via the host
// Version 0.3.0 - 2026-10-20 - Created with Claude
//
// The joiner's edit mode runs normally (palette, cursor, previews, rotation). At the points where it
// would change the world, the request goes to the host instead and nothing happens locally; the
// result comes back through normal sync. Undo: the host remembers each joiner's last edit and reverts it on request.
using System;
using System.Collections.Generic;
using HarmonyLib;
using SimpleJSON;
using UnityEngine;

namespace AutonautsCoop
{
    public static class Build
    {
        public static bool Intercept => Coop.Role == Role.Client && Coop.I.ClientLoaded && !Sync.Applying;
        public static int Sent, UndoSent;   // stats / tests
        public static bool UndoAvailable;

        /// Joiner: host says whether it holds an undo step for me; show it on the edit palette.
        public static void SetUndoAvailable(bool on)
        {
            UndoAvailable = on;
            var ed = GameStateEdit.Instance;
            if (ed == null) return;
            try
            {
                var f = AccessTools.Field(typeof(GameStateEdit), "m_UndoAction");
                f.SetValue(ed, Enum.ToObject(f.FieldType, on ? 0 : 3));   // UndoAction.Add / UndoAction.Total
                AccessTools.Method(typeof(GameStateEdit), "UpdateButtons").Invoke(ed, null);
            }
            catch (Exception e) { Plugin.Log("undo button: " + e.Message); }
        }

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
        // ---------- host: per-joiner undo (one step, like the game's own) ----------
        class UndoRec
        {
            public string Kind;                                     // add | delete | move
            public List<int> Added = new List<int>();
            public List<int[]> Deleted = new List<int[]>();         // type, x, y, rot, flip
            public List<int[]> Moved = new List<int[]>();           // uid, old x, old y, old rot, old flip
            public float At;
        }
        static readonly Dictionary<Peer, UndoRec> undo = new Dictionary<Peer, UndoRec>();
        public static int Undone;   // stats/tests

        static UndoRec Rec(Peer p, string kind, bool extend = false)
        {
            if (p == null) return new UndoRec();
            // one area delete arrives as several messages: keep them as one step
            if (extend && undo.TryGetValue(p, out var r0) && r0.Kind == kind && Time.realtimeSinceStartup - r0.At < 0.5f) { r0.At = Time.realtimeSinceStartup; return r0; }
            var r = new UndoRec { Kind = kind, At = Time.realtimeSinceStartup };
            undo[p] = r; return r;
        }

        static void TellUndo(Peer p)
        {
            if (p == null) return;
            var n = new JSONObject(); n["undo"] = undo.ContainsKey(p); p.Send(Msg.Info, n.ToString());
        }

        static void HostUndo(Peer p)
        {
            if (!undo.TryGetValue(p, out var r)) return;
            undo.Remove(p);
            int done = 0;
            switch (r.Kind)
            {
                case "add":
                    foreach (var uid in r.Added)
                    {
                        var o = ObjectTypeList.Instance.GetObjectFromUniqueID(uid, false);
                        var b = o ? o.GetComponent<Building>() : null;
                        if (!b || !(bool)b.GetActionInfo(new GetActionInfo(GetAction.IsDeletable))) continue;
                        BuildingManager.Instance.DestroyBuilding(b, true); done++;
                    }
                    break;
                case "delete":
                    // deleted buildings come back as blueprints (no free resources)
                    foreach (var d in r.Deleted)
                        if (BuildingManager.Instance.AddBuilding(new TileCoord(d[1], d[2]), (ObjectType)d[0], d[3], d[4] == 1, null, false, false, false)) done++;
                    break;
                case "move":
                {
                    var list = new List<MovingBuilding>(); int i = 0;
                    foreach (var m in r.Moved)
                    {
                        var o = ObjectTypeList.Instance.GetObjectFromUniqueID(m[0], false);
                        var b = o ? o.GetComponent<Building>() : null;
                        if (b) list.Add(new MovingBuilding(i++, b, new TileCoord(m[1], m[2]), m[3], m[4] == 1));
                    }
                    if (list.Count > 0) { BuildingManager.Instance.MoveBuildings(list); done = list.Count; }
                    break;
                }
            }
            Undone++;
            Plugin.Log("joiner undo " + r.Kind + ": " + done + " building(s)");
        }

        public static bool HostApply(string a, JSONNode n, Peer from = null)
        {
            bool r = HostApplyInner(a, n, from);
            if (r && from != null && (a == "build" || a == "unbuild" || a == "move" || a == "drag" || a == "undo")) TellUndo(from);
            return r;
        }

        static bool HostApplyInner(string a, JSONNode n, Peer from)
        {
            switch (a)
            {
                case "undo": HostUndo(from); return true;
                case "build":
                {
                    var type = TypeOf(n["type"]);
                    if (type == ObjectTypeList.m_Total) { Plugin.Log("build: unknown type " + n["type"]); return true; }
                    var origObj = n["orig"].AsInt >= 0 ? ObjectTypeList.Instance.GetObjectFromUniqueID(n["orig"].AsInt, false) : null;
                    var t = new TileCoord(n["x"].AsInt, n["y"].AsInt);
                    if (TileManager.Instance.GetTile(t) == null) return true;
                    var b = BuildingManager.Instance.AddBuilding(t, type, n["rot"].AsInt, n["flip"].AsBool, origObj ? origObj.GetComponent<Building>() : null,
                                                                 n["inst"].AsBool, n["force"].AsBool, n["upg"].AsBool);
                    if (b) { QuestManager.Instance.AddEvent(QuestEvent.Type.AddBlueprint, Bot: false, type, null); Rec(from, "add").Added.Add(b.m_UniqueID); }
                    Plugin.Log("joiner placed " + n["type"] + " at " + t.x + "," + t.y + (b ? " uid " + b.m_UniqueID : " (refused)"));
                    return true;
                }
                case "unbuild":
                {
                    var o = ObjectTypeList.Instance.GetObjectFromUniqueID(n["u"].AsInt, false);
                    var b = o ? o.GetComponent<Building>() : null;
                    if (!b || !b.GetIsSavable()) return true;
                    if (!(bool)b.GetActionInfo(new GetActionInfo(GetAction.IsDeletable))) { Plugin.Log("unbuild refused: not deletable " + b.m_UniqueID); return true; }
                    var keep = b.GetComponent<ConverterFoundation>() && b.GetComponent<ConverterFoundation>().m_NewBuilding ? b.GetComponent<ConverterFoundation>().m_NewBuilding.m_TypeIdentifier : b.m_TypeIdentifier;
                    Rec(from, "delete", true).Deleted.Add(new[] { (int)keep, b.m_TileCoord.x, b.m_TileCoord.y, b.m_Rotation, b.m_Flipped ? 1 : 0 });
                    b.PlayerDeleted();
                    BuildingManager.Instance.DestroyBuilding(b, n["ret"].AsBool);
                    Plugin.Log("joiner deleted " + b.m_TypeIdentifier + " " + b.m_UniqueID);
                    return true;
                }
                case "move":
                {
                    var list = new List<MovingBuilding>();
                    int i = 0;
                    var rec = Rec(from, "move");
                    foreach (JSONNode e in n["m"].AsArray)
                    {
                        var o = ObjectTypeList.Instance.GetObjectFromUniqueID(e[0].AsInt, false);
                        var b = o ? o.GetComponent<Building>() : null;
                        if (!b) continue;
                        rec.Moved.Add(new[] { b.m_UniqueID, b.m_TileCoord.x, b.m_TileCoord.y, b.m_Rotation, b.m_Flipped ? 1 : 0 });
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
                    var drec = Rec(from, "add");
                    foreach (JSONNode e in n["t"].AsArray)
                    {
                        var t = new TileCoord(e[0].AsInt, e[1].AsInt);
                        var tile = TileManager.Instance.GetTile(t);
                        if (tile == null) continue;
                        // same occupancy rules as GameStateEdit.ConvertDragModels (train-track crossings excepted)
                        if (tile.m_Building && !isFloor && tile.m_Building.m_NumLevels == tile.m_Building.m_MaxLevels) continue;
                        if (tile.m_Floor && isFloor) continue;
                        var b = BuildingManager.Instance.AddBuilding(t, type, e[2].AsInt, false, null, false, n["force"].AsBool);
                        if (b) { placed++; drec.Added.Add(b.m_UniqueID); }
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

    // Joiner: the game's own undo history is local and meaningless here (nothing happened locally).
    [HarmonyPatch]
    static class P_NoUndo
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(GameStateEdit)))
                if (m.Name == "AddUndoAction") yield return m;
        }
        static bool Prefix() { return Coop.Role != Role.Client; }
    }

    // Joiner: Undo (button or key) asks the host to revert my last edit.
    [HarmonyPatch(typeof(GameStateEdit), nameof(GameStateEdit.Undo))]
    static class P_ClientUndo
    {
        static bool Prefix()
        {
            if (Coop.Role != Role.Client) return true;
            if (!Build.Intercept) return false;
            var n = new JSONObject(); n["a"] = "undo"; Coop.I.Client.Host.Send(Msg.Input, n.ToString());
            Build.UndoSent++;
            Build.SetUndoAvailable(false);
            return false;
        }
    }

}
