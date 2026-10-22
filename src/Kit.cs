// AutonautsCoop - Kit.cs - what the Windows test kit (kit/RunTests.bat) needs to run two game copies on one PC
// Version 0.12.1 - 2026-10-21 - Created with Claude
//
// Only active through environment variables the kit sets; a normal game never sets them.
//   COOP_DATA=<folder>   saves and autosaves go to <folder>\Saves and \Autosaves (not the player's real saves, and
//                        not shared between the two copies)
//   COOP_NOSTEAM=1       don't start Steam: two copies on one account would share Steam stats, achievements and
//                        playtime; the tests connect over the local network instead
//   COOP_BIND=127.0.0.1  host listens on this address only (no Windows firewall prompt for a local test)
//   COOP_SOAK=1 + COOP_SAVE=<save name>   scenario on the player's own save: Soak.cs
using System;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace AutonautsCoop
{
    public static class Kit
    {
        public static readonly string DataDir = Environment.GetEnvironmentVariable("COOP_DATA");
        public static readonly bool NoSteam = Environment.GetEnvironmentVariable("COOP_NOSTEAM") == "1";
        public static readonly string Bind = Environment.GetEnvironmentVariable("COOP_BIND");

        public static void LogSetup()
        {
            if (DataDir != null) Plugin.Log("test kit: saves in " + DataDir);
            if (NoSteam) Plugin.Log("test kit: Steam not started");
            if (Bind != null) Plugin.Log("test kit: host binds " + Bind);
            if (NetSim.On) Plugin.Log("test: simulated lag " + NetSim.LagMs + " ms + " + NetSim.JitterMs + " ms jitter each way");
        }
    }

    [HarmonyPatch(typeof(MyFile), nameof(MyFile.GetBasePath))]
    static class P_KitSaveDir
    {
        static void Postfix(bool Autosave, ref string __result)
        {
            if (Kit.DataDir == null || SaveLoadManager.m_SpecificFile) return;
            __result = Path.Combine(Kit.DataDir, Autosave ? "Autosaves" : "Saves").Replace('\\', '/') + "/";
        }
    }

    [HarmonyPatch(typeof(SteamManager), "Awake")]
    static class P_KitNoSteam
    {
        static bool Prefix(SteamManager __instance)
        {
            if (!Kit.NoSteam) return true;
            var inst = Traverse.Create(typeof(SteamManager)).Field("s_instance");
            if (inst.GetValue<SteamManager>() != null) { UnityEngine.Object.Destroy(__instance.gameObject); return false; }
            inst.SetValue(__instance);
            UnityEngine.Object.DontDestroyOnLoad(__instance.gameObject);
            Plugin.Log("test kit: Steam skipped");
            return false;
        }
    }
}
