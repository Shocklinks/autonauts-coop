// AutonautsCoop - Net.cs - framed TCP transport
// Version 0.12.1 - 2026-10-21 - Created with Claude
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AutonautsCoop
{
    public enum Msg : byte
    {
        Hello = 1,      // C->H: protocol version, player name
        Snapshot = 2,   // H->C: gzip world JSON
        Delta = 3,      // H->C: gzip JSON batch of changes
        Input = 4,      // C->H: player action request
        Ping = 5,
        Info = 6,       // H->C: small JSON (counts, test assertions)
        Bye = 7,
        Echo = 8,       // both ways: round-trip time (Diag.cs)
    }

    public class Packet { public Msg Type; public byte[] Data; public Peer From; }

    /// A connection to one other player, over any transport.
    public abstract class Peer
    {
        static int nextId = 1;
        public readonly int Id = nextId++;
        public volatile bool Alive = true;
        public string Name = "?";
        public float LastHeard = -1f;   // set on the main thread by SteamNet.Pump
        public static long BytesSent, BytesRecv;   // stats (all peers)
        public float Rtt = -1f;                    // seconds, measured with Msg.Echo
        public bool Hold;                          // host: not yet in the broadcast (its snapshot is being made)
        public abstract void Send(Msg t, byte[] data);
        public void Send(Msg t, string s)
        {
            if (t == Msg.Input && Coop.Role == Role.Client) Diag.Out(s);
            Send(t, Encoding.UTF8.GetBytes(s));
        }
        public abstract void Close();
        /// Close after whatever is queued has gone out (used for "lobby ended" / refusal messages).
        public virtual void CloseGracefully() { Close(); }
        public abstract string Describe();
    }

    /// One TCP connection. Frames: int32 length (LE, includes type byte), byte type, payload.
    public class TcpPeer : Peer
    {
        readonly TcpClient tcp;
        readonly NetworkStream stream;
        readonly Queue<byte[]> sendQ = new Queue<byte[]>();
        readonly AutoResetEvent sendSignal = new AutoResetEvent(false);
        readonly Queue<Packet> inbox;

        public TcpPeer(TcpClient c, Queue<Packet> inbox)
        {
            tcp = c; tcp.NoDelay = true; stream = c.GetStream(); this.inbox = inbox;
            new Thread(RecvLoop) { IsBackground = true, Name = "coop-recv" }.Start();
            new Thread(SendLoop) { IsBackground = true, Name = "coop-send" }.Start();
        }

        public override string Describe() { try { return "tcp " + tcp.Client.RemoteEndPoint; } catch { return "tcp"; } }

        public override void Send(Msg t, byte[] data)
        {
            Interlocked.Add(ref BytesSent, data.Length + 5);
            var frame = new byte[5 + data.Length];
            BitConverter.GetBytes(data.Length + 1).CopyTo(frame, 0);
            frame[4] = (byte)t;
            Buffer.BlockCopy(data, 0, frame, 5, data.Length);
            lock (sendQ) sendQ.Enqueue(frame);
            sendSignal.Set();
        }
        void SendLoop()
        {
            try
            {
                while (Alive)
                {
                    sendSignal.WaitOne(500);
                    while (true)
                    {
                        byte[] f;
                        lock (sendQ) { if (sendQ.Count == 0) break; f = sendQ.Dequeue(); }
                        stream.Write(f, 0, f.Length);
                    }
                    if (closing) { stream.Flush(); Thread.Sleep(200); Fail("closed after flush"); }
                }
            }
            catch (Exception e) { Fail("send: " + e.Message); }
        }

        void RecvLoop()
        {
            try
            {
                var hdr = new byte[4];
                while (Alive)
                {
                    ReadExact(hdr, 4);
                    int len = BitConverter.ToInt32(hdr, 0);
                    if (len < 1 || len > 256 * 1024 * 1024) throw new IOException("bad frame length " + len);
                    var body = new byte[len];
                    ReadExact(body, len);
                    Interlocked.Add(ref BytesRecv, len + 4);
                    var p = new Packet { Type = (Msg)body[0], Data = new byte[len - 1], From = this };
                    Buffer.BlockCopy(body, 1, p.Data, 0, len - 1);
                    lock (inbox) inbox.Enqueue(p);
                }
            }
            catch (Exception e) { Fail("recv: " + e.Message); }
        }

        void ReadExact(byte[] b, int n)
        {
            int got = 0;
            while (got < n)
            {
                int r = stream.Read(b, got, n - got);
                if (r <= 0) throw new IOException("closed");
                got += r;
            }
        }

        void Fail(string why)
        {
            if (!Alive) return;
            Alive = false;
            Plugin.Log("peer " + Id + " closed (" + why + ")");
            try { tcp.Close(); } catch { }
        }
        public override void Close() { Fail("local close"); }
        volatile bool closing;
        public override void CloseGracefully() { closing = true; sendSignal.Set(); }
    }

    public class NetHost
    {
        TcpListener listener;
        public readonly List<Peer> Peers = new List<Peer>();
        public readonly Queue<Packet> Inbox = new Queue<Packet>();
        public void Add(Peer p) { lock (Peers) Peers.Add(p); Plugin.Log("peer " + p.Id + " added (" + p.Describe() + ")"); }

        public void StartTcp(int port)
        {
            listener = new TcpListener(Kit.Bind != null ? IPAddress.Parse(Kit.Bind) : IPAddress.Any, port);
            listener.Start();
            new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        var c = listener.AcceptTcpClient();
                        Add(new TcpPeer(c, Inbox));
                    }
                }
                catch (Exception e) { Plugin.Log("listener stopped: " + e.Message); }
            }) { IsBackground = true, Name = "coop-accept" }.Start();
            Plugin.Log("hosting on TCP " + port);
        }

        public void Broadcast(Msg t, byte[] d)
        {
            lock (Peers) foreach (var p in Peers) if (p.Alive && p.Name != "?" && !p.Hold) p.Send(t, d);
        }

        public bool AnyRecipient() { lock (Peers) return Peers.Exists(p => p.Alive && p.Name != "?" && !p.Hold); }

        public void Stop()
        {
            try { listener?.Stop(); } catch { }
            lock (Peers) { foreach (var p in Peers) p.CloseGracefully(); Peers.Clear(); }
        }
    }

    public class NetClient
    {
        public Peer Host;
        public readonly Queue<Packet> Inbox = new Queue<Packet>();
        public void Connect(string ip, int port)
        {
            var c = new TcpClient();
            c.Connect(ip, port);
            Host = new TcpPeer(c, Inbox);
            Plugin.Log("connected to " + ip + ":" + port);
        }
    }

    /// Test option: pretend the other player is far away. Every packet a machine receives waits LagMs (+ random
    /// JitterMs) before the game sees it, so the round trip is about twice that. Order is kept (TCP and Steam's
    /// reliable messages keep order too). Set with COOP_LAG / COOP_JITTER (milliseconds); off by default.
    public static class NetSim
    {
        public static int LagMs = Env("COOP_LAG"), JitterMs = Env("COOP_JITTER");
        static int Env(string k) => int.TryParse(Environment.GetEnvironmentVariable(k), out var v) && v > 0 ? v : 0;
        public static bool On => LagMs > 0 || JitterMs > 0;
        class Held { public float Due; public Packet P; }
        static readonly Dictionary<Queue<Packet>, List<Held>> held = new Dictionary<Queue<Packet>, List<Held>>();
        static readonly Dictionary<Queue<Packet>, float> lastDue = new Dictionary<Queue<Packet>, float>();
        static readonly System.Random rng = new System.Random();

        /// Next packet the game may see now (all of them at once when the option is off).
        public static bool Take(Queue<Packet> inbox, out Packet p)
        {
            p = null;
            if (!On) { lock (inbox) { if (inbox.Count == 0) return false; p = inbox.Dequeue(); return true; } }
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (!held.TryGetValue(inbox, out var l)) held[inbox] = l = new List<Held>();
            lock (inbox)
                while (inbox.Count > 0)
                {
                    lastDue.TryGetValue(inbox, out float ld);
                    float due = Math.Max(now + (LagMs + (float)rng.NextDouble() * JitterMs) / 1000f, ld);
                    lastDue[inbox] = due;
                    l.Add(new Held { Due = due, P = inbox.Dequeue() });
                }
            if (l.Count == 0 || l[0].Due > now) return false;
            p = l[0].P; l.RemoveAt(0);
            return true;
        }

        public static bool Pending(Queue<Packet> inbox) => inbox != null && held.TryGetValue(inbox, out var l) && l.Count > 0;
    }

    public static class Gz
    {
        public static byte[] Pack(string s)
        {
            var raw = Encoding.UTF8.GetBytes(s);
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionMode.Compress)) gz.Write(raw, 0, raw.Length);
                return ms.ToArray();
            }
        }
        public static string Unpack(byte[] b)
        {
            using (var ms = new MemoryStream(b))
            using (var gz = new GZipStream(ms, CompressionMode.Decompress))
            using (var r = new StreamReader(gz, Encoding.UTF8)) return r.ReadToEnd();
        }
    }
}
