// AutonautsCoop - Net.cs - framed TCP transport
// Version 0.0.2 - 2026-10-20 - Created with Claude
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
    }

    public class Packet { public Msg Type; public byte[] Data; public Peer From; }

    /// One TCP connection. Frames: int32 length (LE, includes type byte), byte type, payload.
    public class Peer
    {
        public readonly int Id;
        readonly TcpClient tcp;
        readonly NetworkStream stream;
        readonly Queue<byte[]> sendQ = new Queue<byte[]>();
        readonly AutoResetEvent sendSignal = new AutoResetEvent(false);
        readonly Queue<Packet> inbox;
        public volatile bool Alive = true;
        public string Name = "?";

        public Peer(int id, TcpClient c, Queue<Packet> inbox)
        {
            Id = id; tcp = c; tcp.NoDelay = true; stream = c.GetStream(); this.inbox = inbox;
            new Thread(RecvLoop) { IsBackground = true, Name = "coop-recv" }.Start();
            new Thread(SendLoop) { IsBackground = true, Name = "coop-send" }.Start();
        }

        public void Send(Msg t, byte[] data)
        {
            var frame = new byte[5 + data.Length];
            BitConverter.GetBytes(data.Length + 1).CopyTo(frame, 0);
            frame[4] = (byte)t;
            Buffer.BlockCopy(data, 0, frame, 5, data.Length);
            lock (sendQ) sendQ.Enqueue(frame);
            sendSignal.Set();
        }
        public void Send(Msg t, string s) { Send(t, Encoding.UTF8.GetBytes(s)); }

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
        public void Close() { Fail("local close"); }
    }

    public class NetHost
    {
        TcpListener listener;
        public readonly List<Peer> Peers = new List<Peer>();
        public readonly Queue<Packet> Inbox = new Queue<Packet>();
        int nextId = 1;

        public void Start(int port)
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        var c = listener.AcceptTcpClient();
                        var p = new Peer(nextId++, c, Inbox);
                        lock (Peers) Peers.Add(p);
                        Plugin.Log("accepted peer " + p.Id + " from " + c.Client.RemoteEndPoint);
                    }
                }
                catch (Exception e) { Plugin.Log("listener stopped: " + e.Message); }
            }) { IsBackground = true, Name = "coop-accept" }.Start();
            Plugin.Log("hosting on TCP " + port);
        }

        public void Broadcast(Msg t, byte[] d)
        {
            lock (Peers) foreach (var p in Peers) if (p.Alive && p.Name != "?") p.Send(t, d);
        }

        public void Stop()
        {
            try { listener?.Stop(); } catch { }
            lock (Peers) { foreach (var p in Peers) p.Close(); Peers.Clear(); }
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
            Host = new Peer(0, c, Inbox);
            Plugin.Log("connected to " + ip + ":" + port);
        }
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
