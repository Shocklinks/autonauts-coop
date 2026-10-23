// AutonautsCoop - host-authoritative co-op for Autonauts
// Version 0.12.14 - 2026-10-22 - Created with Claude
using System;
using System.IO;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace AutonautsCoop
{
    [BepInPlugin("autonauts.coop", "AutonautsCoop", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance;
        public static string LogPath;
        public const string Version = "0.12.14";

        void Awake()
        {
            Instance = this;
            LogPath = Path.Combine(Paths.BepInExRootPath, "coop.log");
            File.WriteAllText(LogPath, "");
            Log("AutonautsCoop " + Version + " loaded. Unity " + Application.unityVersion);
            Application.runInBackground = true;
            // stress test only: two big worlds in one small sandbox; low-res textures save the memory
            if (TestBot.Big || Multi.On || Environment.GetEnvironmentVariable("COOP_LOWTEX") == "1") { QualitySettings.masterTextureLimit = 3; Log("stress test: textures at 1/8 resolution"); }
            Diag.Init();
            Kit.LogSetup();
            var harmony = new Harmony("autonauts.coop"); harmony.PatchAll(); AllocProf.Init(harmony);
            Log("patched");
            var go = new GameObject("AutonautsCoop");
            DontDestroyOnLoad(go);
            go.AddComponent<Coop>();
            go.AddComponent<Menu>();
        }

        public static void Log(string s)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + "\n"); } catch { }
        }
    }
}
