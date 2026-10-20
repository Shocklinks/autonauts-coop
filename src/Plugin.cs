// AutonautsCoop - host-authoritative co-op for Autonauts
// Version 0.1.0 - 2026-10-20 - Created with Claude
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
        public const string Version = "0.1.0";

        void Awake()
        {
            Instance = this;
            LogPath = Path.Combine(Paths.BepInExRootPath, "coop.log");
            File.WriteAllText(LogPath, "");
            Log("AutonautsCoop 0.1.0 loaded. Unity " + Application.unityVersion);
            Application.runInBackground = true;
            new Harmony("autonauts.coop").PatchAll();
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
