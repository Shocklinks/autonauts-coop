// AutonautsCoop - AllocProf.cs - which game code makes the garbage (test kit soak runs only)
// Version 0.12.9 - 2026-10-22 - Created with Claude
// Wraps every Update/LateUpdate/FixedUpdate in the game's code and counts the heap growth across each call, by
// class. The totals go into the 10 s stats line ("game garbage: ..."). Costs some fps, so only on in soak runs
// (COOP_SOAK=1) or with COOP_ALLOCPROF=1.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AutonautsCoop
{
    public static class AllocProf
    {
        public static bool On;
        static readonly Dictionary<MethodBase, string> names = new Dictionary<MethodBase, string>();
        static readonly Dictionary<string, long> bytes = new Dictionary<string, long>();
        static int depth;

        public static void Init(Harmony h)
        {
            On = Soak.On || Environment.GetEnvironmentVariable("COOP_ALLOCPROF") == "1";
            if (!On) return;
            int n = 0;
            var pre = new HarmonyMethod(typeof(AllocProf).GetMethod(nameof(Pre), BindingFlags.Static | BindingFlags.NonPublic));
            var fin = new HarmonyMethod(typeof(AllocProf).GetMethod(nameof(Post), BindingFlags.Static | BindingFlags.NonPublic));   // finalizer: runs even when the update throws
            foreach (var t in typeof(BaseClass).Assembly.GetTypes())
            {
                if (!typeof(MonoBehaviour).IsAssignableFrom(t) || t.IsGenericTypeDefinition) continue;
                foreach (var name in new[] { "Update", "LateUpdate", "FixedUpdate" })
                {
                    var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                    if (m == null || m.IsAbstract || m.GetMethodBody() == null) continue;
                    try { names[m] = t.Name + (name == "Update" ? "" : "." + name); h.Patch(m, prefix: pre, finalizer: fin); n++; }
                    catch (Exception e) { Plugin.Log("allocprof " + t.Name + "." + name + ": " + e.Message); }
                }
            }
            Plugin.Log("allocprof: watching " + n + " update methods");
        }

        static void Pre(out long __state) { depth++; __state = depth == 1 ? GC.GetTotalMemory(false) : 0; }
        public static void FrameStart() { depth = 0; }
        static void Post(long __state, MethodBase __originalMethod)
        {
            depth--;
            if (depth != 0) return;
            long d = GC.GetTotalMemory(false) - __state;
            if (d <= 0 || !names.TryGetValue(__originalMethod, out var k)) return;
            bytes.TryGetValue(k, out long v); bytes[k] = v + d;
        }

        public static string TakeReport(float seconds)
        {
            if (!On || bytes.Count == 0) return null;
            var list = new List<KeyValuePair<string, long>>(bytes);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            long total = 0; foreach (var kv in list) total += kv.Value;
            var sb = new System.Text.StringBuilder("game garbage ").Append((total / 1048576.0 / seconds).ToString("F1")).Append(" MB/s in updates: ");
            for (int i = 0; i < list.Count && i < 10; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(list[i].Key).Append(' ').Append((list[i].Value / 1048576.0 / seconds).ToString("F2"));
            }
            bytes.Clear();
            return sb.ToString();
        }
    }
}
