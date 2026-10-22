// AutonautsCoop - Feel.cs - joiner-side fixes from the first real Steam test (0.12.0)
// Version 0.12.0 - 2026-10-21 - Created with Claude
//
// The joiner's simulation is frozen with TimeManager.EnableNormalTime(false), which zeroes m_NormalDelta for the whole
// game. Plenty of UI and camera code also counts time with m_NormalDelta, so on the joiner:
//   - WASD/arrow camera pan, keyboard rotate and zoom did nothing (CameraManager uses m_NormalDeltaUnscaled)
//   - the bot script editor kept its "not editable" cover forever (TeachWorkerScriptEdit.m_NonEditableTimer)
//   - selection rings, flashes, delays (catapult aim) never advanced
// Fix: while these UI/camera components update, the joiner sees real frame time; the simulation stays frozen.
//
// Also: every FarmerPlayer turns to face the *local* mouse cursor (FarmerPlayer.UpdateLookAtCursor), so on the host the
// joiners' characters followed the host's cursor (and the other way round). Now only your own character looks at your
// cursor; joiners send where they look and the host turns their character.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SimpleJSON;
using UnityEngine;

namespace AutonautsCoop
{
    public static class RealTime
    {
        static int depth; static float savedNormal, savedUnscaled;
        public static float LastUiDelta;   // tests
        public static bool Active => Coop.Role == Role.Client && TimeManager.Instance && !TimeManager.Instance.m_NormalTimeEnabled;

        public static void Enter()
        {
            if (!Active) { depth++; return; }
            if (depth++ > 0) return;
            var tm = TimeManager.Instance;
            savedNormal = tm.m_NormalDelta; savedUnscaled = tm.m_NormalDeltaUnscaled;
            tm.m_NormalDeltaUnscaled = Time.deltaTime;
            tm.m_NormalDelta = Time.deltaTime * tm.m_TimeScale;
            if (Time.deltaTime > 0) LastUiDelta = Time.deltaTime;
        }

        public static void Exit()
        {
            if (depth == 0) return;
            if (--depth > 0) return;
            var tm = TimeManager.Instance;
            if (!tm || tm.m_NormalTimeEnabled) return;
            tm.m_NormalDelta = savedNormal; tm.m_NormalDeltaUnscaled = savedUnscaled;
        }
    }

    [HarmonyPatch]
    static class P_UiRealTime
    {
        // UI, camera and input-state code that times itself with m_NormalDelta (none of it runs the simulation)
        static readonly string[] Types =
        {
            "CameraManager", "CameraCrossFade", "GameStateManager", "TeachWorkerScriptEdit", "HudInstruction", "BotServerSelect",
            "FarmerPlayerWorkerSelect", "ObjectSelect", "Cursor", "BuildingPalette", "StandardButtonImage", "BrainButton",
            "ConverterRollover", "FolkRollover", "StorageBeehiveRollover", "TileSelectEffect", "HudManager", "TutorialPanel",
            "FastButton", "TeachingEffect",
        };

        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var tn in Types)
            {
                var t = typeof(TimeManager).Assembly.GetType(tn);   // the game's own types (not UnityEngine.Cursor)
                if (t == null) continue;
                foreach (var mn in new[] { "Update", "LateUpdate" })
                {
                    var m = t.GetMethod(mn, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                    if (m != null && !m.IsAbstract) yield return m;
                }
            }
        }

        static void Prefix() { RealTime.Enter(); }
        static void Finalizer() { RealTime.Exit(); }
    }

    // ---------------- who looks where ----------------
    public static class Look
    {
        static float nextSend, lastSent = float.NaN;
        static readonly Dictionary<int, float> remoteYaw = new Dictionary<int, float>();

        public static int LocalPlayer => Coop.Role == Role.Client ? Sync.MyPlayerUID : Coop.Role == Role.Host ? Sync.HostPlayerUID : -1;

        /// Joiner: tell the host where my character faces (only while idle; actions turn it on the host).
        public static void ClientTick()
        {
            if (Coop.Role != Role.Client || !Coop.I.ClientLoaded || Time.realtimeSinceStartup < nextSend) return;
            nextSend = Time.realtimeSinceStartup + 0.2f;
            var o = ObjectTypeList.Instance.GetObjectFromUniqueID(Sync.MyPlayerUID, false);
            var fp = o ? o.GetComponent<FarmerPlayer>() : null;
            if (!fp || fp.m_State != Farmer.State.None) return;
            float y = fp.transform.eulerAngles.y;
            if (!float.IsNaN(lastSent) && Mathf.Abs(Mathf.DeltaAngle(y, lastSent)) < 4f) return;
            lastSent = y;
            var n = new JSONObject(); n["a"] = "look"; n["y"] = y;
            Coop.I.Client.Host.Send(Msg.Input, n.ToString());
        }

        public static bool HostApply(string a, JSONNode n, FarmerPlayer fp)
        {
            if (a != "look") return false;
            if (fp) remoteYaw[fp.m_UniqueID] = n["y"].AsFloat;
            return true;
        }

        /// Host: turn an idle joiner character the way its owner looks.
        public static void HostFace(FarmerPlayer fp)
        {
            if (!remoteYaw.TryGetValue(fp.m_UniqueID, out float y)) return;
            if (fp.m_State == Farmer.State.None && !fp.m_EngagedObject) fp.transform.rotation = Quaternion.Euler(0f, y, 0f);
        }
    }

    [HarmonyPatch(typeof(FarmerPlayer), "UpdateLookAtCursor")]
    static class P_LookAtMyCursorOnly
    {
        static bool Prefix(FarmerPlayer __instance)
        {
            if (Coop.Role == Role.Off) return true;
            if (__instance.m_UniqueID == Look.LocalPlayer) return true;
            if (Coop.Role == Role.Host) Look.HostFace(__instance);
            return false;
        }
    }

    // ---------------- animations keep playing on the joiner ----------------
    // These Update methods set their animator speed to 0 while normal time is off (always, on the joiner).
    [HarmonyPatch]
    static class P_AnimatorsRun
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var tn in new[] { "FarmerPlayer", "Folk", "AnimalPetDog" })
            {
                var t = AccessTools.TypeByName(tn);
                var m = t != null ? AccessTools.DeclaredMethod(t, "Update", Type.EmptyTypes) : null;
                if (m != null) yield return m;
            }
        }
        static void Postfix(MonoBehaviour __instance)
        {
            if (Coop.Role != Role.Client || !Coop.I.ClientLoaded) return;
            var an = Traverse.Create(__instance).Field("m_Animator").GetValue<Animator>();
            if (an) an.speed = TimeManager.Instance ? TimeManager.Instance.m_TimeScale : 1f;
        }
    }

    // ---------------- smooth movement on the joiner ----------------
    public static class Smooth
    {
        public static float Delay = 0.2f;   // play back this far behind the newest sample
        struct S { public float T; public Vector3 P; public float R; }
        static readonly Dictionary<int, List<S>> buf = new Dictionary<int, List<S>>();

        public static void Add(int uid, Vector3 p, float r)
        {
            float now = Time.realtimeSinceStartup;
            if (!buf.TryGetValue(uid, out var l)) buf[uid] = l = new List<S>();
            if (l.Count > 0)
            {
                var last = l[l.Count - 1];
                // starting again after standing still: begin from where it stood, not from the old timestamp
                if (now - last.T > 0.35f) l.Add(new S { T = now - 0.1f, P = last.P, R = last.R });
                if ((last.P - p).sqrMagnitude > 64f) l.Clear();   // teleport: no interpolation across it
            }
            l.Add(new S { T = now, P = p, R = r });
            while (l.Count > 12) l.RemoveAt(0);
        }

        public static void Forget(int uid) { buf.Remove(uid); }

        public static bool Sample(int uid, out Vector3 p, out float r, out bool moving)
        {
            p = default; r = 0; moving = false;
            if (!buf.TryGetValue(uid, out var l) || l.Count == 0) return false;
            float rt = Time.realtimeSinceStartup - Delay;
            if (rt <= l[0].T || l.Count == 1) { p = l[0].P; r = l[0].R; return true; }
            var newest = l[l.Count - 1];
            if (rt >= newest.T) { p = newest.P; r = newest.R; moving = false; return true; }
            for (int i = l.Count - 1; i > 0; i--)
            {
                var a = l[i - 1]; var b = l[i];
                if (rt < a.T) continue;
                float u = b.T > a.T ? (rt - a.T) / (b.T - a.T) : 1f;
                p = Vector3.Lerp(a.P, b.P, u);
                r = Mathf.LerpAngle(a.R, b.R, u);
                moving = (a.P - b.P).sqrMagnitude > 0.0004f;
                return true;
            }
            return true;
        }
    }
}
