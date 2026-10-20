// AutonautsCoop - Bots.cs - bot edit locks, bot renaming, inventory/upgrade transfers, inventory keys, UI rebinding
// Version 0.5.0 - 2026-10-20 - Created with Claude
using System;
using System.Collections.Generic;
using HarmonyLib;
using SimpleJSON;
using UnityEngine;

namespace AutonautsCoop
{
    // ======================= edit locks ("Being programmed by ...") =======================
    public static class Locks
    {
        /// bot uid -> player name. Host owns the table and broadcasts it; everyone draws it.
        public static readonly Dictionary<int, string> Table = new Dictionary<int, string>();
        static readonly Dictionary<string, int> byOwner = new Dictionary<string, int>();   // owner key -> bot (host)
        static int myLast = -1;
        static float nextCheck;

        public static string HostKey = "host";

        /// The bot this machine has open in the teach/script editor.
        public static int LocalEditing()
        {
            var ed = TeachWorkerScriptEdit.Instance;
            if (ed == null || !ed.m_CurrentTarget || GameStateManager.Instance == null) return -1;
            var st = GameStateManager.Instance.GetActualState();
            if (st != GameStateManager.State.TeachWorker && st != GameStateManager.State.SelectWorker) return -1;
            return ed.m_CurrentTarget.m_UniqueID;
        }

        public static string MyKey = "host";                                  // joiners get "peer<N>" from the host
        static readonly Dictionary<int, string> ownerOf = new Dictionary<int, string>(); // bot uid -> owner key

        public static string LockedByOther(int uid)
        {
            if (uid < 0 || Coop.Role == Role.Off) return null;
            if (!Table.TryGetValue(uid, out string who)) return null;
            if (ownerOf.TryGetValue(uid, out string key) && key == MyKey) return null;
            return who;
        }

        public static void Tick()
        {
            if (Coop.Role == Role.Off || Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + 0.25f;
            int now = LocalEditing();
            if (now == myLast) return;
            myLast = now;
            if (Coop.Role == Role.Client)
            {
                var n = new JSONObject(); n["a"] = "lock"; n["w"] = now;
                Coop.I.Client.Host.Send(Msg.Input, n.ToString());
            }
            else HostSet(HostKey, SteamNet.MyName() + " (host)", now);
        }

        /// Host: owner starts/stops editing a bot. Returns false if someone else holds it.
        public static bool HostSet(string owner, string name, int uid)
        {
            if (uid >= 0 && Table.TryGetValue(uid, out string who) && (!byOwner.TryGetValue(owner, out int mine) || mine != uid))
            {
                Plugin.Log("lock refused: " + name + " wants " + uid + " held by " + who);
                return false;
            }
            if (byOwner.TryGetValue(owner, out int old)) { Table.Remove(old); ownerOf.Remove(old); byOwner.Remove(owner); }
            if (byOwner.TryGetValue(owner, out int prev2)) ownerOf.Remove(prev2);
            if (uid >= 0) { Table[uid] = name; byOwner[owner] = uid; ownerOf[uid] = owner; }
            Broadcast();
            return true;
        }

        public static void HostPeerGone(Peer p) { if (byOwner.ContainsKey("peer" + p.Id)) HostSet("peer" + p.Id, p.Name, -1); }

        /// Host: may this owner change this bot?
        public static bool HostAllowed(string owner, int uid)
        {
            if (!Table.TryGetValue(uid, out _)) return true;
            return byOwner.TryGetValue(owner, out int mine) && mine == uid;
        }

        static void Broadcast()
        {
            if (Coop.I.Host == null) return;
            var n = new JSONObject(); var arr = new JSONArray();
            foreach (var kv in Table) { var e = new JSONArray(); e.Add(kv.Key); e.Add(kv.Value); e.Add(ownerOf.TryGetValue(kv.Key, out string k) ? k : ""); arr.Add(e); }
            n["locks"] = arr;
            Coop.I.Host.Broadcast(Msg.Info, System.Text.Encoding.UTF8.GetBytes(n.ToString()));
        }

        public static void ClientReceive(JSONArray arr)
        {
            Table.Clear(); ownerOf.Clear();
            foreach (JSONNode e in arr) { Table[e[0].AsInt] = e[1]; ownerOf[e[0].AsInt] = e[2]; }
        }

        // ---- drawing: a label over every bot someone else is programming ----
        static GUIStyle style;
        public static void OnGUI()
        {
            if (Coop.Role == Role.Off || Table.Count == 0 || Camera.main == null) return;
            if (style == null) { style = new GUIStyle(GUI.skin.box) { fontSize = 14, alignment = TextAnchor.MiddleCenter }; style.normal.textColor = new Color(1f, 0.9f, 0.4f); }
            foreach (var kv in Table)
            {
                if (LockedByOther(kv.Key) == null) continue;
                var b = ObjectTypeList.Instance != null ? ObjectTypeList.Instance.GetObjectFromUniqueID(kv.Key, false) : null;
                if (!b) continue;
                var sp = Camera.main.WorldToScreenPoint(b.transform.position + Vector3.up * 4f);
                if (sp.z < 0) continue;
                string text = "Being programmed by " + kv.Value;
                var size = style.CalcSize(new GUIContent(text));
                GUI.Box(new Rect(sp.x - size.x / 2 - 6, Screen.height - sp.y - size.y, size.x + 12, size.y + 4), text, style);
            }
        }
    }

    // Block teaching/playing a bot that someone else has open (both host and joiners).
    [HarmonyPatch(typeof(TeachWorkerScriptEdit), nameof(TeachWorkerScriptEdit.OnClickTeach))]
    static class P_LockTeach
    {
        static bool Prefix(TeachWorkerScriptEdit __instance)
        {
            string who = __instance.m_CurrentTarget ? Locks.LockedByOther(__instance.m_CurrentTarget.m_UniqueID) : null;
            if (who == null) return true;
            Menu.Toast(who + " is programming this bot");
            AudioManager.Instance.StartEvent("PlayerActionFail");
            return false;
        }
    }

    [HarmonyPatch(typeof(TeachWorkerScriptEdit), nameof(TeachWorkerScriptEdit.OnClickGo))]
    static class P_LockGo
    {
        static bool Prefix(TeachWorkerScriptEdit __instance)
        {
            string who = __instance.m_CurrentTarget ? Locks.LockedByOther(__instance.m_CurrentTarget.m_UniqueID) : null;
            if (who == null) return true;
            Menu.Toast(who + " is programming this bot");
            AudioManager.Instance.StartEvent("PlayerActionFail");
            return false;
        }
    }

    // ======================= bot renaming =======================
    [HarmonyPatch(typeof(Worker), nameof(Worker.SetWorkerName))]
    static class P_Rename
    {
        static void Prefix(Worker __instance, string NewName)
        {
            if (!Build.Intercept || __instance.GetWorkerName() == NewName) return;
            var n = new JSONObject(); n["a"] = "bot_name"; n["w"] = __instance.m_UniqueID; n["name"] = NewName;
            Coop.I.Client.Host.Send(Msg.Input, n.ToString());
        }
    }

    // ======================= inventory, upgrades, clothes =======================
    public static class Inv
    {
        public static int Sent;

        static ButtonList Upg(InventoryBar b) => Traverse.Create(b).Field("m_UpgradeSlots").GetValue<ButtonList>();

        // ---- client: describe a slot ----
        static void Describe(InventorySlot s, JSONObject into, string prefix)
        {
            var bar = s.m_Parent;
            into[prefix + "h"] = bar.m_Object ? bar.m_Object.m_UniqueID : -1;
            into[prefix + "t"] = (int)s.m_Type;
            int idx = -1;
            if (bar.m_CarrySlots != null && bar.m_CarrySlots.m_Buttons.Contains(s)) idx = bar.m_CarrySlots.m_Buttons.IndexOf(s);
            else if (bar.m_InventorySlots != null && bar.m_InventorySlots.m_Buttons.Contains(s)) idx = bar.m_InventorySlots.m_Buttons.IndexOf(s);
            else if (Upg(bar) != null && Upg(bar).m_Buttons.Contains(s)) idx = Upg(bar).m_Buttons.IndexOf(s);
            else if (bar.m_WardrobeSlots != null && bar.m_WardrobeSlots.m_Buttons.Contains(s)) idx = bar.m_WardrobeSlots.m_Buttons.IndexOf(s);
            into[prefix + "i"] = idx;
            into[prefix + "o"] = s.m_Object ? s.m_Object.m_UniqueID : -1;
        }

        public static void SendMove(InventorySlot from, InventorySlot to)
        {
            var n = new JSONObject(); n["a"] = "inv_move";
            Describe(from, n, "s"); Describe(to, n, "d");
            Sent++; Coop.I.Client.Host.Send(Msg.Input, n.ToString());
        }

        public static void SendClick(InventorySlot slot, InventorySlot ctrlTarget)
        {
            var n = new JSONObject(); n["a"] = "inv_click";
            Describe(slot, n, "s");
            if (ctrlTarget) Describe(ctrlTarget, n, "d");
            Sent++; Coop.I.Client.Host.Send(Msg.Input, n.ToString());
        }

        // ---- host: apply ----
        static BaseClass H(JSONNode n, string p) => ObjectTypeList.Instance.GetObjectFromUniqueID(n[p + "h"].AsInt, false);

        static Holdable Remove(BaseClass holder, InventorySlot.Type t, int idx, int objUid)
        {
            var f = holder.GetComponent<Farmer>();
            Holdable h = null;
            if (f)
            {
                switch (t)
                {
                    case InventorySlot.Type.Carry: h = f.m_FarmerCarry.RemoveObject(idx); break;
                    case InventorySlot.Type.Inventory: h = f.m_FarmerInventory.ReleaseObject(idx); break;
                    case InventorySlot.Type.Upgrade: h = f.m_FarmerUpgrades.ReleaseObject(idx); break;
                    default:
                        var o = ObjectTypeList.Instance.GetObjectFromUniqueID(objUid, false);
                        h = o ? o.GetComponent<Holdable>() : null;
                        if (h) f.m_FarmerClothes.Remove(h);
                        break;
                }
            }
            else if (holder.m_TypeIdentifier == ObjectType.Wardrobe) h = holder.GetComponent<Wardrobe>().ReleaseObject(idx);
            else if (Aquarium.GetIsTypeAquiarium(holder.m_TypeIdentifier)) h = holder.GetComponent<Aquarium>().ReleaseObject(idx);
            return h;
        }

        static void Add(BaseClass holder, InventorySlot.Type t, Holdable obj, BaseClass original)
        {
            var f = holder.GetComponent<Farmer>();
            if (f)
            {
                switch (t)
                {
                    case InventorySlot.Type.Carry: f.m_FarmerCarry.AddCarry(obj); break;
                    case InventorySlot.Type.Inventory: f.m_FarmerInventory.AttemptAdd(obj); break;
                    case InventorySlot.Type.Upgrade: f.m_FarmerUpgrades.AttemptAdd(obj); break;
                    default: f.m_FarmerClothes.Add(obj, original); break;
                }
            }
            else if (holder.m_TypeIdentifier == ObjectType.Wardrobe) holder.GetComponent<Wardrobe>().AttemptAdd(obj);
            else if (Aquarium.GetIsTypeAquiarium(holder.m_TypeIdentifier)) holder.GetComponent<Aquarium>().AttemptAdd(obj);
        }

        /// Joiners may only touch their own player, bots, wardrobes and aquariums (not another player's pockets).
        static bool MayTouch(BaseClass h, int myPlayer)
        {
            if (!h) return false;
            if (h.m_TypeIdentifier == ObjectType.FarmerPlayer) return h.m_UniqueID == myPlayer;
            return h.m_TypeIdentifier == ObjectType.Worker || h.m_TypeIdentifier == ObjectType.Wardrobe || Aquarium.GetIsTypeAquiarium(h.m_TypeIdentifier);
        }

        public static bool HostApply(string a, JSONNode n, FarmerPlayer fp, Peer from)
        {
            if (a == "bot_name")
            {
                var o = ObjectTypeList.Instance.GetObjectFromUniqueID(n["w"].AsInt, false);
                var w = o ? o.GetComponent<Worker>() : null;
                if (w) { w.SetWorkerName(n["name"]); Plugin.Log("joiner renamed bot " + w.m_UniqueID + " to " + (string)n["name"]); }
                return true;
            }
            if (a != "inv_move" && a != "inv_click") return false;
            int me = fp ? fp.m_UniqueID : -1;
            var sh = H(n, "s");
            if (!MayTouch(sh, me)) { Plugin.Log(a + " refused: source " + n["sh"].AsInt); return true; }
            var st = (InventorySlot.Type)n["st"].AsInt;
            if (sh.m_TypeIdentifier == ObjectType.Worker && !Locks.HostAllowed("peer" + from.Id, sh.m_UniqueID)) { Plugin.Log(a + " refused: bot locked"); return true; }

            if (a == "inv_move")
            {
                var dh = H(n, "d");
                if (!MayTouch(dh, me)) { Plugin.Log("inv_move refused: target " + n["dh"].AsInt); return true; }
                var obj = Remove(sh, st, n["si"].AsInt, n["so"].AsInt);
                if (!obj) { Plugin.Log("inv_move: nothing at source"); return true; }
                if (obj.m_UniqueID != n["so"].AsInt) Plugin.Log("inv_move: slot held " + obj.m_UniqueID + ", expected " + n["so"].AsInt);
                var orig = sh.m_TypeIdentifier == ObjectType.Wardrobe ? sh : null;
                Add(dh, (InventorySlot.Type)n["dt"].AsInt, obj, orig);
                Plugin.Log("joiner moved " + obj.m_TypeIdentifier + " " + sh.m_TypeIdentifier + ":" + st + " -> " + dh.m_TypeIdentifier + ":" + (InventorySlot.Type)n["dt"].AsInt);
                return true;
            }

            // inv_click: same rules as InventoryBar.OnClick
            if (n["dh"] != null && !n["dh"].IsNull)
            {
                var dh = H(n, "d");
                if (!MayTouch(dh, me)) return true;
                var obj = Remove(sh, st, n["si"].AsInt, n["so"].AsInt);
                if (obj) Add(dh, dh.GetComponent<Farmer>() ? InventorySlot.Type.Carry : (InventorySlot.Type)n["dt"].AsInt, obj, null);
                return true;
            }
            var f = sh.GetComponent<Farmer>();
            if (!f) return true;
            if (st == InventorySlot.Type.Carry) { f.m_FarmerCarry.StowObjects(); return true; }
            var slotObj = ObjectTypeList.Instance.GetObjectFromUniqueID(n["so"].AsInt, false);
            var slotHold = slotObj ? slotObj.GetComponent<Holdable>() : null;
            if (!slotHold) return true;
            var top = f.m_FarmerCarry.GetTopObjectType();
            if (top == ObjectTypeList.m_Total || (slotHold.m_TypeIdentifier == top && f.m_FarmerCarry.CanAddCarry(slotHold)))
            {
                var h = Remove(sh, st, n["si"].AsInt, n["so"].AsInt);
                if (h) f.m_FarmerCarry.AddCarry(h);
            }
            else if (st == InventorySlot.Type.Inventory && f.m_FarmerCarry.GetCarryCount() == 1)
            {
                var fromInv = f.m_FarmerInventory.ReleaseObject(n["si"].AsInt);
                var fromHands = f.m_FarmerCarry.RemoveTopObject();
                f.m_FarmerCarry.AddCarry(fromInv);
                f.m_FarmerInventory.AttemptAdd(fromHands);
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(InventoryBar), nameof(InventoryBar.DragEnd))]
    static class P_InvDragEnd
    {
        static bool Prefix(InventoryBar __instance, BaseGadget NewGadget)
        {
            if (!Build.Intercept) return true;
            var gs = GameStateManager.Instance.GetCurrentState();
            var drag = gs ? gs.GetComponent<GameStateDragInventorySlot>() : null;
            if (drag == null) return false;
            var src = drag.m_DragSlot; var dst = drag.m_DragTarget;
            src.EndDrag();
            GameStateManager.Instance.PopState();
            if (TestBot.Active) Plugin.Log("[inv] dragend dst " + (dst != null) + " gadget " + (bool)NewGadget + " accept " + (dst != null && dst.CanAcceptObject(src))
                + " src " + (src.m_Parent.m_Object ? src.m_Parent.m_Object.m_UniqueID : -1) + " dst " + (dst != null && dst.m_Parent.m_Object ? dst.m_Parent.m_Object.m_UniqueID : -1));
            if (dst != null && NewGadget && dst.CanAcceptObject(src))
            {
                string who = src.m_Parent.m_Object ? Locks.LockedByOther(src.m_Parent.m_Object.m_UniqueID) ?? (dst.m_Parent.m_Object ? Locks.LockedByOther(dst.m_Parent.m_Object.m_UniqueID) : null) : null;
                if (who != null) { Menu.Toast(who + " is programming this bot"); return false; }
                Inv.SendMove(src, dst);
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(InventoryBar), nameof(InventoryBar.OnClick))]
    static class P_InvClick
    {
        static bool Prefix(InventoryBar __instance, InventorySlot NewSlot, ref bool __result)
        {
            if (!Build.Intercept) return true;
            __result = false;
            if (!NewSlot || !NewSlot.m_Object) return false;
            InventorySlot ctrlTarget = null;
            var gs = GameStateManager.Instance.GetCurrentState();
            var inv = gs ? gs.GetComponent<GameStateInventory>() : null;
            if (MyInputManager.Instance.GetCTRLHeld() && inv && inv.m_SecondBar)
            {
                var other = NewSlot.m_Parent == inv.m_SecondBar ? inv.m_Bar : inv.m_SecondBar;
                ctrlTarget = other.m_Farmer ? other.m_CarrySlots.m_Buttons[0].GetComponent<InventorySlot>() : other.m_WardrobeSlots.m_Buttons[0].GetComponent<InventorySlot>();
            }
            Inv.SendClick(NewSlot, ctrlTarget);
            __result = true;
            return false;
        }
    }

    // ======================= inventory keys (stow / recall / cycle / swap / use) =======================
    // GameStateBase.UpdateInventoryKeys calls FarmerPlayer.SendAction directly (not GoToAndAction).
    public static class Keys { public static bool InKeys; }

    [HarmonyPatch(typeof(GameStateBase), nameof(GameStateBase.UpdateInventoryKeys))]
    static class P_InvKeys
    {
        static void Prefix() { Keys.InKeys = true; }
        static void Finalizer() { Keys.InKeys = false; }
    }

    [HarmonyPatch(typeof(FarmerPlayer), nameof(FarmerPlayer.SendAction))]
    static class P_PlayerKeyAction
    {
        static bool Prefix(FarmerPlayer __instance, ActionInfo Info)
        {
            if (!Keys.InKeys || !Build.Intercept || __instance.m_UniqueID != Sync.MyPlayerUID) return true;
            var n = new JSONObject(); n["a"] = "key";
            n["A"] = Info.m_Action.ToString(); n["V1"] = Info.m_Value ?? ""; n["V2"] = Info.m_Value2 ?? "";
            n["AT"] = (int)Info.m_ActionType; n["R"] = Info.m_ActionRequirement ?? "";
            n["AOT"] = ObjectTypeList.Instance.GetSaveNameFromIdentifier(Info.m_ActionObjectType);
            n["O"] = Info.m_Object ? Info.m_Object.m_UniqueID : -1;
            Coop.I.Client.Host.Send(Msg.Input, n.ToString());
            return false;
        }
    }

    public static class KeyApply
    {
        public static bool HostApply(string a, JSONNode n, FarmerPlayer fp)
        {
            if (a != "key" || !fp) return a == "key";
            var o = n["O"].AsInt >= 0 ? ObjectTypeList.Instance.GetObjectFromUniqueID(n["O"].AsInt, false) : null;
            var act = Actionable.GetActionFromName(n["A"]);
            // only the inventory-key actions, nothing else, may come through this path
            if (act != ActionType.CycleObject && act != ActionType.StowObject && act != ActionType.RecallObject && act != ActionType.SwapObject && act != ActionType.UseInHands)
            { Plugin.Log("key action refused: " + act); return true; }
            fp.SendAction(new ActionInfo(act, fp.m_TileCoord, o ? o.GetComponent<Actionable>() : null, n["V1"], n["V2"], (AFO.AT)n["AT"].AsInt, n["R"],
                                         ObjectTypeList.Instance.GetIdentifierFromSaveName(n["AOT"], false)));
            return true;
        }
    }

    // ======================= UI rebinding on the joiner =======================
    // Sync sometimes rebuilds an object (new instance, same UID). Inventory bars, the HUD bar and panels
    // then point at a destroyed object; rebind them to the new one. The HUD bar always shows *my* player.
    public static class UiRebind
    {
        static readonly Dictionary<InventoryBar, int> bound = new Dictionary<InventoryBar, int>();
        public static void Bound(InventoryBar bar, BaseClass o) { if (bar && o) bound[bar] = o.m_UniqueID; }
        static float next;

        public static void Tick()
        {
            if (Coop.Role != Role.Client || !Coop.I.ClientLoaded || Time.realtimeSinceStartup < next) return;
            next = Time.realtimeSinceStartup + 0.2f;
            var hud = HudManager.Instance ? HudManager.Instance.m_InventoryBar : null;
            var me = ObjectTypeList.Instance.GetObjectFromUniqueID(Sync.MyPlayerUID, false);
            if (hud && me && hud.m_Object != me) { hud.SetObject(me); if (TestBot.Active) Plugin.Log("HUD inventory bar bound to my player " + me.m_UniqueID); }

            foreach (var bar in new List<InventoryBar>(bound.Keys))
            {
                if (!bar || bar == hud || bar.m_Object) continue;
                var now = ObjectTypeList.Instance.GetObjectFromUniqueID(bound[bar], false);
                if (now) { bar.SetObject(now); if (TestBot.Active) Plugin.Log("rebind: " + bar.GetType().Name + " -> " + bound[bar]); }
            }
            var dead = new List<InventoryBar>();
            foreach (var k in bound.Keys) if (!k) dead.Add(k);
            foreach (var k in dead) bound.Remove(k);
        }
    }

    [HarmonyPatch(typeof(InventoryBar), nameof(InventoryBar.SetObject))]
    static class P_BarBound { static void Postfix(InventoryBar __instance, BaseClass NewObject) { UiRebind.Bound(__instance, NewObject); } }
}
