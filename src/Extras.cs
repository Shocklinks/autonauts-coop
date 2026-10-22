// AutonautsCoop - Extras.cs - joiner saving rules and the bug report bundle
// Version 0.12.1 - 2026-10-21 - Created with Claude
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using HarmonyLib;
using SimpleJSON;
using UnityEngine;

namespace AutonautsCoop
{
    // ======================= joiner saves =======================
    // Joiners don't autosave (it would fill their save list with copies of someone else's world every few minutes),
    // and quick save has no name to go to. "Save As" works: it writes a normal world the joiner can load later,
    // playing their own character (Persist.Write marks it).
    public static class JoinerSaves
    {
        public static int AutosavesSkipped;
    }

    [HarmonyPatch(typeof(SaveLoadManager), "DoAutosave")]
    static class P_NoJoinerAutosave
    {
        static bool Prefix()
        {
            if (Coop.Role != Role.Client) return true;
            JoinerSaves.AutosavesSkipped++;
            Plugin.Log("autosave skipped (joiner)");
            return false;
        }
    }

    [HarmonyPatch(typeof(SaveLoadManager), nameof(SaveLoadManager.QuickSave))]
    static class P_JoinerQuickSave
    {
        static bool Prefix(SaveLoadManager __instance)
        {
            if (Coop.Role != Role.Client) return true;
            string last = Traverse.Create(__instance).Field("m_LastName").GetValue<string>();
            if (string.IsNullOrEmpty(last) || last == Coop.CoopSaveName)
            {
                Menu.Toast("Use Save As to keep your own copy of this world");
                return false;
            }
            return true;
        }
    }

    // ======================= bug report =======================
    /// One zip with everything needed to look at a problem: both logs (the joiner fetches the host's), versions,
    /// session state and sync stats.
    public static class BugReport
    {
        public static string LastPath, Status = "";
        static string hostLog, hostDiag; static bool waitingHost; static float askedAt;

        public static void Begin()
        {
            hostLog = null; hostDiag = null;
            if (Coop.Role == Role.Client && Coop.I.Client?.Host != null && Coop.I.Client.Host.Alive)
            {
                var n = new JSONObject(); n["a"] = "hostlog";
                Coop.I.Client.Host.Send(Msg.Input, n.ToString());
                waitingHost = true; askedAt = Time.realtimeSinceStartup;
                Status = "Collecting the host's log...";
                return;
            }
            Write();
        }

        public static void ClientGotHostLog(string b64, string diagB64) { hostLog = b64; hostDiag = diagB64; if (waitingHost) { waitingHost = false; Write(); } }

        public static void Tick()
        {
            if (waitingHost && Time.realtimeSinceStartup - askedAt > 8f) { waitingHost = false; Write(); }
        }

        /// Host: send the tail of our log to the joiner who asked.
        public static void HostSendLog(Peer to)
        {
            var bytes = Tail(Plugin.LogPath, 4 * 1024 * 1024);
            var n = new JSONObject(); n["hostlog"] = Convert.ToBase64String(Gz.Pack(Encoding.UTF8.GetString(bytes)));
            if (Diag.LogPath != null) n["hostdiag"] = Convert.ToBase64String(Gz.Pack(Encoding.UTF8.GetString(Tail(Diag.LogPath, 8 * 1024 * 1024))));
            to.Send(Msg.Info, n.ToString());
            Plugin.Log("sent log to " + to.Name + " for a bug report");
        }

        static byte[] Tail(string path, int max)
        {
            try
            {
                if (!File.Exists(path)) return new byte[0];
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = Math.Max(0, f.Length - max);
                    f.Seek(start, SeekOrigin.Begin);
                    var b = new byte[f.Length - start]; int got = 0;
                    while (got < b.Length) { int r = f.Read(b, got, b.Length - got); if (r <= 0) break; got += r; }
                    return b;
                }
            }
            catch (Exception e) { return Encoding.UTF8.GetBytes("could not read " + path + ": " + e.Message); }
        }

        static string Info()
        {
            var sb = new StringBuilder();
            sb.AppendLine("AutonautsCoop " + Plugin.Version + "  Autonauts " + SaveLoadManager.GetVersion() + "  Unity " + Application.unityVersion);
            sb.AppendLine("Time " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  OS " + SystemInfo.operatingSystem + "  RAM " + SystemInfo.systemMemorySize + " MB  CPU " + SystemInfo.processorType);
            sb.AppendLine("Role " + Coop.Role + "  in game " + Coop.InGame() + "  loaded " + (Coop.I && Coop.I.ClientLoaded) + "  Steam " + SteamNet.SteamReady + " " + SteamNet.Status);
            if (Coop.Role == Role.Host && Coop.I.Host != null)
                lock (Coop.I.Host.Peers) foreach (var p in Coop.I.Host.Peers) sb.AppendLine("  peer " + p.Id + " " + p.Name + " " + p.Describe() + " alive " + p.Alive);
            if (Coop.Role == Role.Client && Coop.I.Client?.Host != null) sb.AppendLine("  host " + Coop.I.Client.Host.Describe() + " alive " + Coop.I.Client.Host.Alive);
            sb.AppendLine("Objects " + (ObjectTypeList.Instance != null ? Coop.ObjectCount() : -1) + "  host scan ms " + Sync.LastScanMs.ToString("F1"));
            sb.AppendLine("Sync: inplace " + Sync.StatInPlace + " recreated " + Sync.StatRecreated + " created " + Sync.StatCreated + " removed " + Sync.StatRemoved
                + " batches " + Sync.StatBatches + " resync " + Sync.StatResync + " errors " + Sync.StatErrors + " digests " + Sync.StatDigests + " fixes " + Sync.StatDigestFixes);
            sb.AppendLine("Diagnostics " + (Diag.On ? "on" : "off") + ": " + Diag.Lines + " lines, clicks " + Diag.Clicks + ", marks " + Diag.Marks + (NetSim.On ? "  SIMULATED LAG " + NetSim.LagMs + "+" + NetSim.JitterMs + " ms" : ""));
            if (Coop.Role == Role.Client && Coop.I.Client?.Host != null) sb.AppendLine("Round trip to host: " + (Coop.I.Client.Host.Rtt >= 0 ? (Coop.I.Client.Host.Rtt * 1000).ToString("F0") + " ms" : "?"));
            sb.AppendLine("Stored characters " + Persist.Parked.Count + "  seats " + Persist.Seats.Count + "  last disconnect: " + (Session.LastDisconnect ?? "-"));
            try { sb.AppendLine("State " + (GameStateManager.Instance ? GameStateManager.Instance.GetActualState().ToString() : "-") + "  scene " + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name); } catch { }
            return sb.ToString();
        }

        static void Write()
        {
            try
            {
                var dir = Path.Combine(Paths.BepInExRootPath, "bugreports");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "AutonautsCoop-report-" + (Coop.Role == Role.Client ? "joiner-" : Coop.Role == Role.Host ? "host-" : "") + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
                var z = new Zip();
                z.Add("info.txt", Encoding.UTF8.GetBytes(Info()));
                z.Add("coop.log", Tail(Plugin.LogPath, 16 * 1024 * 1024));
                if (Diag.LogPath != null) { z.Add("coop_diag.log", Tail(Diag.LogPath, 16 * 1024 * 1024)); z.Add("coop_diag.old.log", Tail(Diag.LogPath + ".old", 8 * 1024 * 1024)); }
                z.Add("LogOutput.log", Tail(Path.Combine(Paths.BepInExRootPath, "LogOutput.log"), 8 * 1024 * 1024));
                z.Add("Player.log", Tail(Path.Combine(Application.persistentDataPath, "Player.log"), 8 * 1024 * 1024));
                z.Add("config.cfg", Tail(Path.Combine(Paths.ConfigPath, "autonauts.coop.cfg"), 64 * 1024));
                if (hostLog != null)
                {
                    try { z.Add("host-coop.log", Encoding.UTF8.GetBytes(Gz.Unpack(Convert.FromBase64String(hostLog)))); }
                    catch (Exception e) { z.Add("host-coop.log", Encoding.UTF8.GetBytes("unreadable: " + e.Message)); }
                }
                else if (Coop.Role == Role.Client) z.Add("host-coop.log", Encoding.UTF8.GetBytes("the host did not answer"));
                if (hostDiag != null)
                {
                    try { z.Add("host-coop_diag.log", Encoding.UTF8.GetBytes(Gz.Unpack(Convert.FromBase64String(hostDiag)))); }
                    catch (Exception e) { z.Add("host-coop_diag.log", Encoding.UTF8.GetBytes("unreadable: " + e.Message)); }
                }
                File.WriteAllBytes(path, z.Finish());
                LastPath = path;
                Status = "Bug report saved: " + path;
                Plugin.Log(Status);
                Menu.Toast("Bug report saved in BepInEx\\bugreports");
                if (!TestBot.Active) try { Application.OpenURL("file:///" + dir.Replace('\\', '/')); } catch { }
            }
            catch (Exception e) { Status = "Bug report failed: " + e.Message; Plugin.Log(Status); }
        }

        /// Minimal zip writer (stored, no compression) — no extra libraries needed in the game's Mono.
        class Zip
        {
            readonly MemoryStream ms = new MemoryStream();
            readonly List<byte[]> central = new List<byte[]>();
            int count;

            static readonly uint[] crcTable = MakeTable();
            static uint[] MakeTable()
            {
                var t = new uint[256];
                for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[i] = c; }
                return t;
            }
            static uint Crc(byte[] d) { uint c = 0xFFFFFFFFu; foreach (var b in d) c = crcTable[(c ^ b) & 0xFF] ^ (c >> 8); return c ^ 0xFFFFFFFFu; }

            static void U16(BinaryWriter w, int v) => w.Write((ushort)v);
            static void U32(BinaryWriter w, uint v) => w.Write(v);

            public void Add(string name, byte[] data)
            {
                var nm = Encoding.UTF8.GetBytes(name);
                uint crc = Crc(data); uint off = (uint)ms.Position;
                var dt = DateTime.Now;
                int dosTime = (dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2);
                int dosDate = ((dt.Year - 1980) << 9) | (dt.Month << 5) | dt.Day;
                var w = new BinaryWriter(ms);
                U32(w, 0x04034b50); U16(w, 20); U16(w, 0x0800); U16(w, 0); U16(w, dosTime); U16(w, dosDate);
                U32(w, crc); U32(w, (uint)data.Length); U32(w, (uint)data.Length); U16(w, nm.Length); U16(w, 0);
                w.Write(nm); w.Write(data); w.Flush();
                var c = new MemoryStream(); var cw = new BinaryWriter(c);
                U32(cw, 0x02014b50); U16(cw, 20); U16(cw, 20); U16(cw, 0x0800); U16(cw, 0); U16(cw, dosTime); U16(cw, dosDate);
                U32(cw, crc); U32(cw, (uint)data.Length); U32(cw, (uint)data.Length); U16(cw, nm.Length); U16(cw, 0); U16(cw, 0);
                U16(cw, 0); U16(cw, 0); U32(cw, 0); U32(cw, off); cw.Write(nm); cw.Flush();
                central.Add(c.ToArray()); count++;
            }

            public byte[] Finish()
            {
                uint start = (uint)ms.Position; uint size = 0;
                foreach (var c in central) { ms.Write(c, 0, c.Length); size += (uint)c.Length; }
                var w = new BinaryWriter(ms);
                U32(w, 0x06054b50); U16(w, 0); U16(w, 0); U16(w, count); U16(w, count); U32(w, size); U32(w, start); U16(w, 0);
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
