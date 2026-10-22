// AutonautsCoop - Feel.cs - joiner-side fixes from the first real Steam test (0.12.0)
// Version 0.12.1 - 2026-10-21 - Created with Claude
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

    // ---------------- picking bots with the whistle on the joiner ----------------
    // The whistle screen (GameStateSelectWorker) only lets you pick a bot that has stopped to listen, and finds it
    // through the tile under the cursor (Tile.m_Farmer). On the joiner neither held up:
    //   - bots stop to listen in the joiner's own simulation, which is frozen, and every sync update of a busy bot
    //     reset it, so a busy bot was "not listening": hovering didn't pick it and clicking made you walk to it
    //   - a moving bot is drawn a moment behind the tile it is registered on, so the tile under the cursor often
    //     pointed at no bot, or at the wrong one
    // Now, on the joiner, every bot counts as listening while the whistle screen is open (as all bots do in single
    // player), and the bot drawn under the cursor is the one the screen finds.
    static class SelectFix { public static bool Off = Environment.GetEnvironmentVariable("COOP_NOFIX_SELECT") == "1"; }   // tests: show the old behaviour

    /// While the joiner's whistle screen is open, its bots stand still where they are drawn, as listening bots do in
    /// single player (on the host they keep working). A moving, hopping bot never stayed under the cursor for the
    /// ten frames the screen needs to pick it.
    public static class SelectHold
    {
        static readonly Dictionary<int, KeyValuePair<Vector3, Quaternion>> held = new Dictionary<int, KeyValuePair<Vector3, Quaternion>>();
        static int frame = -1; static bool active;
        public static bool Active
        {
            get
            {
                if (frame == Time.frameCount) return active;
                frame = Time.frameCount;
                var gsm = GameStateManager.Instance;
                active = !SelectFix.Off && Coop.Role == Role.Client && Coop.I.ClientLoaded && gsm && gsm.GetActualState() == GameStateManager.State.SelectWorker;
                if (!active && held.Count > 0) held.Clear();
                return active;
            }
        }
        /// Sync.ClientFrame: true = this object is held still this frame (its transform is set here).
        public static bool Hold(BaseClass b, Vector3 basePos)
        {
            if (!(b is Worker) || !Active) return false;
            if (!held.TryGetValue(b.m_UniqueID, out var h)) held[b.m_UniqueID] = h = new KeyValuePair<Vector3, Quaternion>(basePos, b.transform.rotation);
            b.transform.position = h.Key; b.transform.rotation = h.Value;
            return true;
        }
    }

    [HarmonyPatch(typeof(Worker), nameof(Worker.GetIsListening))]
    static class P_JoinerBotsListen
    {
        static void Postfix(Worker __instance, ref bool __result)
        {
            if (__result || SelectFix.Off || Coop.Role != Role.Client || !Coop.I.ClientLoaded || __instance.m_BeingHeld) return;
            var gsm = GameStateManager.Instance;
            if (gsm && gsm.GetActualState() == GameStateManager.State.SelectWorker) __result = true;
        }
    }

    // The whistle screen asks every bot to stop and listen (and lets them go when it closes). On the joiner those bots
    // are copies of the host's: pausing them locally changed their saved state, so every sync update failed its
    // check and the bot was rebuilt (dozens a second with a busy base). Listening is handled above instead.
    [HarmonyPatch]
    static class P_JoinerNoLocalPause
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Worker), nameof(Worker.RequestPause));
            yield return AccessTools.Method(typeof(Worker), nameof(Worker.Unpause));
        }
        static bool Prefix() => SelectFix.Off || Coop.Role != Role.Client || !Coop.I.ClientLoaded || Sync.Applying;
    }

    // The whistle screen keeps references to the bot under the cursor; on the joiner a bot can be rebuilt by sync
    // while hovered, leaving a destroyed object there (SetWorker then threw every frame).
    [HarmonyPatch(typeof(GameStateSelectWorker), nameof(GameStateSelectWorker.SetWorker))]
    static class P_SelectDropDestroyed
    {
        static void Prefix(GameStateSelectWorker __instance, ref Worker NewWorker)
        {
            if (Coop.Role != Role.Client) return;
            var t = Traverse.Create(__instance);
            foreach (var f in new[] { "m_CurrentTarget", "m_NextTarget", "m_SelectedBot", "m_TargetObject" })
            {
                var w = t.Field(f).GetValue<Worker>();
                if ((object)w != null && !w) t.Field(f).SetValue(null);
            }
            if ((object)NewWorker != null && !NewWorker) NewWorker = null;
        }
    }

    [HarmonyPatch(typeof(GameStateSelectWorker), "Awake")]
    static class P_SelectPurgeFirst { static void Prefix() { Sync.PurgeDead(); } }   // its Awake walks every bot

    [HarmonyPatch(typeof(GameStateSelectWorker), "UpdateIdle")]
    static class P_JoinerPickDrawnBot
    {
        /// The bot the whistle screen's cursor test returns this frame (P_FakeMouse in Diag.cs hands it over).
        public static Worker Override;
        static Worker last; static Vector3 lastMouse; static float lastAt; static int lastFrame;
        static void Prefix(GameStateSelectWorker __instance)
        {
            Override = null;
            if (SelectFix.Off || Coop.Role != Role.Client || !Coop.I.ClientLoaded) return;
            // what is drawn under the cursor: a bot's body (any bot layer), else the bot standing closest to where the
            // cursor ray lands on the ground (the ray passes through the body and lands just behind it)
            var mouse = Diag.Mouse;
            var go = __instance.TestCollisionOffset(out var cp, true, false, true, false, true, mouse);
            Worker w = null;
            if (go)
            {
                w = go.GetComponentInParent<Worker>();
                if (!w) w = Diag.NearestWorker(cp, Tile.m_Size * 0.9f, out _);
            }
            // a sync update can rebuild a bot's colliders for a frame; the screen needs 10 steady frames, so keep the
            // bot that was under a still cursor a moment ago
            if (!w && last && !last.m_BeingHeld && (mouse - lastMouse).sqrMagnitude < 64f && (Time.realtimeSinceStartup - lastAt < 0.5f || Time.frameCount - lastFrame <= 3)) w = last;
            if (w && !w.m_BeingHeld) { Override = w; last = w; lastMouse = mouse; lastAt = Time.realtimeSinceStartup; lastFrame = Time.frameCount; }
        }
        static void Finalizer() { Override = null; }
    }
}
