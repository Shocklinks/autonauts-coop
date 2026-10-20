// AutonautsCoop - TestBot.cs - automated two-instance QA driver (COOP_TEST=host|client)
// Version 0.0.2 - 2026-10-20 - Created with Claude
using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AutonautsCoop
{
    public static class TestBot
    {
        public static string Mode = Environment.GetEnvironmentVariable("COOP_TEST");
        static string ip = Environment.GetEnvironmentVariable("COOP_IP") ?? "127.0.0.1";
        static int port = int.TryParse(Environment.GetEnvironmentVariable("COOP_PORT"), out var p) ? p : 27015;
        static string resultPath;
        static float t0 = -1, stepT;
        static int step;
        static readonly List<IEnumeratorStep> steps = new List<IEnumeratorStep>();
        public static int Pass, Fail;

        public delegate bool IEnumeratorStep(float elapsed); // return true when step done

        public static bool Active => !string.IsNullOrEmpty(Mode);

        public static void Assert(string name, bool ok, string detail = "")
        {
            if (ok) Pass++; else Fail++;
            var line = (ok ? "PASS " : "FAIL ") + name + (detail != "" ? " (" + detail + ")" : "");
            Plugin.Log("[test] " + line);
            File.AppendAllText(resultPath, line + "\n");
        }

        static void Done()
        {
            File.AppendAllText(resultPath, "DONE " + Pass + "/" + (Pass + Fail) + "\n");
            Plugin.Log("[test] done " + Pass + "/" + (Pass + Fail));
            step = 9999;
        }

        public static void Tick()
        {
            if (!Active) return;
            if (t0 < 0)
            {
                t0 = Time.realtimeSinceStartup;
                resultPath = Path.Combine(Paths.BepInExRootPath, "coop_test_" + Mode + ".txt");
                File.WriteAllText(resultPath, "");
                if (Mode == "host") TestScript.BuildHost(steps); else TestScript.BuildClient(steps);
                stepT = Time.realtimeSinceStartup;
            }
            SkipCeremonies();
            if (step >= steps.Count) { if (step != 9999) Done(); return; }
            float el = Time.realtimeSinceStartup - stepT;
            bool done;
            try { done = steps[step](el); }
            catch (Exception e) { Plugin.Log(e.ToString()); Assert("step " + step + " threw", false, e.Message); done = true; }
            if (done) { step++; stepT = Time.realtimeSinceStartup; Plugin.Log("[test] step " + step + " at " + (Time.realtimeSinceStartup - t0).ToString("F1") + "s"); }
            else if (el > 240f) { Assert("step " + step + " timeout", false); step = steps.Count; }
        }

        static float nextSkip, nextStateLog;
        /// Ceremonies (rocket intro, comms dialogs) wait for clicks; the bot skips them like a player would.
        static void SkipCeremonies()
        {
            if (GameStateManager.Instance == null || !GeneralUtils.m_InGame) return;
            float now = Time.realtimeSinceStartup;
            var st = GameStateManager.Instance.GetActualState();
            if (now > nextStateLog) { nextStateLog = now + 10f; Plugin.Log("[test] state " + st); }
            if (st != GameStateManager.State.Ceremony || now < nextSkip) return;
            nextSkip = now + 1.5f;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<CeremonyBase>())
            {
                try
                {
                    var m = c.GetType().GetMethod("OnAcceptClicked");
                    if (m != null) m.Invoke(c, new object[] { null }); else c.Skip();
                    Plugin.Log("[test] advanced ceremony " + c.GetType().Name);
                }
                catch (Exception e) { Plugin.Log("skip failed: " + e.Message); }
            }
        }

        public static bool AtMainMenu() => SceneManager.GetActiveScene().name == "MainMenu";

        public static void StartNewGame()
        {
            var o = GameOptionsManager.Instance.m_NewOptions;
            o.m_GameMode = GameOptions.GameMode.ModeCampaign;
            o.m_TutorialEnabled = false;
            o.m_RecordingEnabled = false;
            SessionManager.Instance.LoadLevel(false, "Main");
        }

        public static string Ip => ip;
        public static int Port => port;
    }
}
