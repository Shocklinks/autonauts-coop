// AutonautsCoop - Steam.cs - Steam lobbies, friend invites and P2P messages (no IPs, no port forwarding)
// Version 0.2.0 - 2026-10-20 - Created with Claude
//
// Uses the game's own Steamworks.NET 15.0.1 / SDK 1.51 and its own app id (979120): friends see
// "Playing Autonauts" with Join Game / Invite to Game. Every player needs their own copy.
//
// Wire: ISteamNetworkingMessages, reliable, channel 0. Our frames (type byte + payload) are cut into
// 60000-byte pieces with a 1-byte header so snapshots of any size fit. A 1 s keep-alive runs both
// ways; a peer silent for 60 s is dropped.
//
// For automated tests without Steam, a UDP wire on 127.0.0.1 stands in (COOP_TRANSPORT=udp): same
// chunking, peers, keep-alives and session logic; only the lobby/invite part is Steam-only.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Steamworks;
using UnityEngine;

namespace AutonautsCoop
{
    public interface IWire
    {
        ulong Me { get; }
        /// false = try again later (buffer full)
        bool Send(ulong to, byte[] buf, int len);
        bool Receive(out ulong from, out byte[] data);
        void Close(ulong peer);
    }

    // ---------------- real Steam ----------------
    public class SteamWire : IWire
    {
        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        static extern void SteamAPI_SteamNetworkingMessage_t_Release(IntPtr self);
        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr SteamAPI_SteamNetworkingUtils_SteamAPI_v003();
        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        static extern bool SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(IntPtr self, int eValue, int val);

        readonly IntPtr[] msgs = new IntPtr[64];
        int msgCount, msgIndex;
        public ulong Me { get; }

        public SteamWire()
        {
            Me = SteamUser.GetSteamID().m_SteamID;
            try
            {
                var u = SteamAPI_SteamNetworkingUtils_SteamAPI_v003();
                // Snapshots are a few MB late-game; the default 512 KB send buffer would stall them.
                SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(u, (int)ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, 32 * 1024 * 1024);
                SteamNetworkingUtils.InitRelayNetworkAccess();
            }
            catch (Exception e) { Plugin.Log("steam net config: " + e.Message); }
        }

        public bool Send(ulong to, byte[] buf, int len)
        {
            var id = new SteamNetworkingIdentity(); id.SetSteamID64(to);
            var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                var r = SteamNetworkingMessages.SendMessageToUser(ref id, h.AddrOfPinnedObject(), (uint)len,
                    Constants.k_nSteamNetworkingSend_ReliableNoNagle, 0);
                if (r == EResult.k_EResultOK) return true;
                if (r == EResult.k_EResultLimitExceeded) return false;
                Plugin.Log("steam send to " + to + " failed: " + r);
                return true;   // drop; the keep-alive timeout will notice a dead peer
            }
            finally { h.Free(); }
        }

        public bool Receive(out ulong from, out byte[] data)
        {
            from = 0; data = null;
            if (msgIndex >= msgCount)
            {
                msgIndex = 0;
                msgCount = SteamNetworkingMessages.ReceiveMessagesOnChannel(0, msgs, msgs.Length);
                if (msgCount <= 0) { msgCount = 0; return false; }
            }
            IntPtr p = msgs[msgIndex++];
            // Steamworks.NET 15's SteamNetworkingMessage_t.Release() passes the release fn pointer instead of
            // the message; call the flat API with the message pointer ourselves.
            var m = (SteamNetworkingMessage_t)Marshal.PtrToStructure(p, typeof(SteamNetworkingMessage_t));
            data = new byte[m.m_cbSize];
            Marshal.Copy(m.m_pData, data, 0, m.m_cbSize);
            from = m.m_identityPeer.GetSteamID64();
            SteamAPI_SteamNetworkingMessage_t_Release(p);
            return true;
        }

        public void Close(ulong peer)
        {
            var id = new SteamNetworkingIdentity(); id.SetSteamID64(peer);
            SteamNetworkingMessages.CloseSessionWithUser(ref id);
        }
    }

    // ---------------- UDP stand-in for tests ----------------
    public class UdpWire : IWire
    {
        readonly UdpClient udp;
        public ulong Me { get; }
        public UdpWire(int port)
        {
            Me = (ulong)port;
            udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            udp.Client.ReceiveBufferSize = 16 * 1024 * 1024;
            udp.Client.SendBufferSize = 16 * 1024 * 1024;
        }
        public bool Send(ulong to, byte[] buf, int len) { udp.Send(buf, len, new IPEndPoint(IPAddress.Loopback, (int)to)); return true; }
        public bool Receive(out ulong from, out byte[] data)
        {
            from = 0; data = null;
            if (udp.Available <= 0) return false;
            var ep = new IPEndPoint(IPAddress.Any, 0);
            data = udp.Receive(ref ep);
            from = (ulong)ep.Port;
            return true;
        }
        public void Close(ulong peer) { }
    }

    /// A player reached through an IWire (Steam or the UDP stand-in).
    public class WirePeer : Peer
    {
        public const int Chunk = 60000;
        const byte Whole = 0, Part = 1, Last = 2;
        public readonly ulong Remote;
        readonly IWire wire;
        readonly Queue<byte[]> outQ = new Queue<byte[]>();
        readonly List<byte> assembling = new List<byte>();
        public float LastSent;

        public WirePeer(IWire w, ulong remote) { wire = w; Remote = remote; LastHeard = Time.realtimeSinceStartup; }
        public override string Describe() { return (wire is SteamWire ? "steam " : "udp ") + Remote; }

        public override void Send(Msg t, byte[] data)
        {
            System.Threading.Interlocked.Add(ref BytesSent, data.Length + 1);
            if (!Alive) return;
            // frame = type + payload, cut into pieces: [hdr][<=Chunk bytes]
            int total = data.Length + 1, off = 0;
            while (off < total)
            {
                int n = Math.Min(Chunk, total - off);
                var piece = new byte[n + 1];
                piece[0] = total <= Chunk ? Whole : (off + n >= total ? Last : Part);
                for (int i = 0; i < n; i++)
                {
                    int src = off + i;
                    piece[1 + i] = src == 0 ? (byte)t : data[src - 1];
                }
                outQ.Enqueue(piece);
                off += n;
            }
        }

        public void Flush()
        {
            while (outQ.Count > 0)
            {
                var p = outQ.Peek();
                if (!wire.Send(Remote, p, p.Length)) return;   // buffer full: retry next frame
                outQ.Dequeue();
                LastSent = Time.realtimeSinceStartup;
            }
        }

        /// Returns a complete frame (type + payload) or null.
        public byte[] Feed(byte[] piece)
        {
            LastHeard = Time.realtimeSinceStartup;
            if (piece.Length < 1) return null;
            if (piece[0] == Whole) { assembling.Clear(); var f = new byte[piece.Length - 1]; Buffer.BlockCopy(piece, 1, f, 0, f.Length); return f; }
            for (int i = 1; i < piece.Length; i++) assembling.Add(piece[i]);
            if (piece[0] == Last) { var f = assembling.ToArray(); assembling.Clear(); return f; }
            return null;
        }

        public override void Close()
        {
            if (!Alive) return;
            try { Send(Msg.Bye, new byte[0]); Flush(); } catch { }
            Alive = false;
            wire.Close(Remote);
            Plugin.Log("peer " + Id + " closed (" + Describe() + ")");
        }
    }

    public static class SteamNet
    {
        public static IWire Wire;
        public static readonly Dictionary<ulong, WirePeer> Peers = new Dictionary<ulong, WirePeer>();
        public static ulong HostId;                 // client: who we joined
        public static CSteamID Lobby = CSteamID.Nil;
        public static string Status = "";
        static bool callbacksMade;
        static ulong pendingLobby;                  // join requested before we were at the main menu
        static Callback<GameLobbyJoinRequested_t> cbJoinReq;
        static Callback<GameRichPresenceJoinRequested_t> cbRichJoin;
        static Callback<LobbyEnter_t> cbEnter;
        static Callback<LobbyChatUpdate_t> cbChat;
        static Callback<SteamNetworkingMessagesSessionRequest_t> cbSession;
        static CallResult<LobbyCreated_t> crCreated;
        static float nextPing;
        public const float Timeout = 60f;   // world loads freeze the main thread; 20 s was too tight for big saves

        public static bool SteamReady
        {
            get { try { return SteamManager.Initialized; } catch { return false; } }
        }

        public static string MyName()
        {
            try { if (SteamReady) return SteamFriends.GetPersonaName(); } catch { }
            return "player";
        }

        static void EnsureSteam()
        {
            if (callbacksMade || !SteamReady) return;
            callbacksMade = true;
            Wire = new SteamWire();
            cbJoinReq = Callback<GameLobbyJoinRequested_t>.Create(e => RequestJoin(e.m_steamIDLobby.m_SteamID, "invite from " + e.m_steamIDFriend.m_SteamID));
            cbRichJoin = Callback<GameRichPresenceJoinRequested_t>.Create(e => { var id = ParseConnect(e.m_rgchConnect); if (id != 0) RequestJoin(id, "join game"); });
            cbEnter = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
            cbChat = Callback<LobbyChatUpdate_t>.Create(OnLobbyChat);
            cbSession = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
            crCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            // Launched by Steam's "Join Game" while the game wasn't running: +connect_lobby <id>
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out ulong l)) RequestJoin(l, "command line");
            Plugin.Log("steam ready as " + Wire.Me + " (" + MyName() + ")");
        }

        /// Test only: replace Steam with the UDP stand-in.
        public static void UseUdp(int myPort)
        {
            callbacksMade = true;
            Wire = new UdpWire(myPort);
            Plugin.Log("udp wire on " + myPort);
        }

        static ulong ParseConnect(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            var parts = s.Split(' ');
            for (int i = 0; i + 1 < parts.Length; i++)
                if (parts[i] == "+connect_lobby" && ulong.TryParse(parts[i + 1], out ulong l)) return l;
            return 0;
        }

        // ---------- host ----------
        public static void HostLobby()
        {
            if (!SteamReady) { Status = "Steam not running"; return; }
            EnsureSteam();
            if (Lobby != CSteamID.Nil) { InviteDialog(); return; }
            Status = "Creating Steam lobby...";
            crCreated.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 8));
        }

        static void OnLobbyCreated(LobbyCreated_t e, bool ioFailure)
        {
            if (ioFailure || e.m_eResult != EResult.k_EResultOK) { Status = "Lobby failed: " + e.m_eResult; Plugin.Log(Status); return; }
            if (Lobby.m_SteamID == e.m_ulSteamIDLobby) return;   // Steam can report it twice
            Lobby = new CSteamID(e.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(Lobby, "autonauts_coop", Coop.Protocol.ToString());
            SteamMatchmaking.SetLobbyData(Lobby, "version", Plugin.Version);
            SteamFriends.SetRichPresence("connect", "+connect_lobby " + Lobby.m_SteamID);
            SteamFriends.SetRichPresence("status", "Hosting co-op");
            Status = "Lobby ready. Invite friends, or they can right-click you > Join Game.";
            Plugin.Log("lobby " + Lobby.m_SteamID);
            InviteDialog();
        }

        public static void InviteDialog()
        {
            if (Lobby != CSteamID.Nil) SteamFriends.ActivateGameOverlayInviteDialog(Lobby);
        }

        static bool InOurLobby(ulong user)
        {
            if (Lobby == CSteamID.Nil) return false;
            int n = SteamMatchmaking.GetNumLobbyMembers(Lobby);
            for (int i = 0; i < n; i++) if (SteamMatchmaking.GetLobbyMemberByIndex(Lobby, i).m_SteamID == user) return true;
            return false;
        }

        static void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t e)
        {
            var id = e.m_identityRemote;
            ulong who = id.GetSteamID64();
            bool ok = Coop.Role == Role.Host ? InOurLobby(who) : who == HostId;
            Plugin.Log("session request from " + who + (ok ? " accepted" : " refused"));
            if (ok) SteamNetworkingMessages.AcceptSessionWithUser(ref id);
        }

        static void OnLobbyChat(LobbyChatUpdate_t e)
        {
            const uint gone = 0x2 | 0x4 | 0x8 | 0x10;   // left, disconnected, kicked, banned
            if ((e.m_rgfChatMemberStateChange & gone) == 0) return;
            if (Peers.TryGetValue(e.m_ulSteamIDUserChanged, out var p)) { Plugin.Log("lobby: " + e.m_ulSteamIDUserChanged + " left"); p.Close(); }
        }

        // ---------- client ----------
        static void RequestJoin(ulong lobby, string why)
        {
            Plugin.Log("join requested (" + why + ") lobby " + lobby);
            if (Coop.Role != Role.Off) { Status = "Already in a session; quit to desktop first."; return; }
            pendingLobby = lobby;
        }

        static void OnLobbyEnter(LobbyEnter_t e)
        {
            if (Coop.Role == Role.Host) return;   // our own lobby
            if (e.m_EChatRoomEnterResponse != 1) { Status = "Couldn't join lobby (" + e.m_EChatRoomEnterResponse + ")"; Plugin.Log(Status); return; }
            var lobby = new CSteamID(e.m_ulSteamIDLobby);
            string proto = SteamMatchmaking.GetLobbyData(lobby, "autonauts_coop");
            if (proto != Coop.Protocol.ToString())
            {
                Status = "Host runs a different co-op version (" + SteamMatchmaking.GetLobbyData(lobby, "version") + ")";
                Plugin.Log(Status); SteamMatchmaking.LeaveLobby(lobby); return;
            }
            Lobby = lobby;
            ConnectTo(SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID);
        }

        /// Client: talk to the host over the wire (Steam id, or a UDP port in tests).
        public static void ConnectTo(ulong host)
        {
            HostId = host;
            var c = new NetClient();
            var p = new WirePeer(Wire, host);
            Peers[host] = p;
            c.Host = p;
            Status = "Connecting to host...";
            Plugin.Log("connecting over " + p.Describe());
            Coop.I.BeginClient(c);
        }

        // ---------- every frame ----------
        public static void Pump()
        {
            EnsureSteam();
            if (pendingLobby != 0 && Coop.Role == Role.Off && TestBot.AtMainMenu())
            {
                Status = "Joining friend's lobby...";
                SteamMatchmaking.JoinLobby(new CSteamID(pendingLobby));
                pendingLobby = 0;
            }
            if (Wire == null) return;
            float now = Time.realtimeSinceStartup;

            // receive
            int guard = 0;
            while (guard++ < 4096 && Wire.Receive(out ulong from, out byte[] piece))
            {
                if (!Peers.TryGetValue(from, out var peer))
                {
                    if (Coop.Role != Role.Host || Coop.I.Host == null) continue;
                    peer = new WirePeer(Wire, from);
                    Peers[from] = peer;
                    Coop.I.Host.Add(peer);
                }
                if (!peer.Alive) continue;
                var frame = peer.Feed(piece);
                if (frame == null || frame.Length < 1) continue;
                var type = (Msg)frame[0];
                if (type == Msg.Ping) continue;
                if (type == Msg.Bye) { Plugin.Log("peer " + peer.Id + " said bye"); peer.Close(); continue; }
                var pkt = new Packet { Type = type, Data = new byte[frame.Length - 1], From = peer };
                Buffer.BlockCopy(frame, 1, pkt.Data, 0, pkt.Data.Length);
                var inbox = Coop.Role == Role.Host ? Coop.I.Host.Inbox : Coop.I.Client?.Inbox;
                if (inbox != null) lock (inbox) inbox.Enqueue(pkt);
            }

            // keep-alive + timeout + flush
            bool ping = now >= nextPing;
            if (ping) nextPing = now + 1f;
            var dead = new List<ulong>();
            foreach (var kv in Peers)
            {
                var p = kv.Value;
                if (!p.Alive) { dead.Add(kv.Key); continue; }
                if (now - p.LastHeard > Timeout) { Plugin.Log("peer " + p.Id + " timed out"); p.Close(); dead.Add(kv.Key); continue; }
                if (ping) p.Send(Msg.Ping, new byte[0]);
                p.Flush();
            }
            foreach (var d in dead) Peers.Remove(d);
        }

        public static void Shutdown()
        {
            foreach (var p in Peers.Values) p.Close();
            Peers.Clear();
            try
            {
                if (SteamReady)
                {
                    if (Lobby != CSteamID.Nil) SteamMatchmaking.LeaveLobby(Lobby);
                    SteamFriends.ClearRichPresence();
                }
            }
            catch { }
        }
    }
}
