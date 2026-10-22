#!/usr/bin/env python3
# AutonautsCoop - test/protobot.py - a second joiner that speaks the mod's TCP protocol without running the game.
# Version 0.11.0 - 2026-10-21 - Created with Claude
#
# Three full game instances don't fit in the test machine's memory, so the three-player test (COOP_MULTI=1) uses
# one real game joiner plus this bot. The host can't tell the difference: it spawns a character for the bot, the bot
# walks and builds through normal input messages, leaves, and comes back.
# Usage: protobot.py <host> <port> <mod version> <result file>
import gzip, json, socket, struct, sys, time

HOST, PORT, VERSION, OUT = sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4]
HELLO, SNAPSHOT, DELTA, INPUT, PING, INFO, BYE = 1, 2, 3, 4, 5, 6, 7
open(OUT, "w").close()
passes = fails = 0


def log(s):
    print(time.strftime("%H:%M:%S"), s, flush=True)


def check(name, ok, detail=""):
    global passes, fails
    if ok: passes += 1
    else: fails += 1
    line = ("PASS " if ok else "FAIL ") + name + (" (" + detail + ")" if detail else "")
    log(line)
    with open(OUT, "a") as f: f.write(line + "\n")


class Conn:
    def __init__(self):
        while True:
            try:
                self.s = socket.create_connection((HOST, PORT), timeout=5); break
            except OSError:
                time.sleep(2)
        self.s.settimeout(0.2)
        self.buf = b""; self.closed = False
        self.me = -1; self.pos = {}; self.notes = []; self.replies = []; self.ended = None; self.snapshot = False; self.ups = {}
        self.send(HELLO, {"protocol": 2, "version": VERSION, "name": "player"})

    def send(self, t, obj):
        data = json.dumps(obj).encode()
        self.s.sendall(struct.pack("<i", len(data) + 1) + bytes([t]) + data)

    def pump(self, secs=0.0):
        end = time.time() + secs
        while True:
            try:
                chunk = self.s.recv(1 << 20)
                if not chunk: self.closed = True
                else: self.buf += chunk
            except socket.timeout:
                pass
            except OSError:
                self.closed = True
            while len(self.buf) >= 4:
                n = struct.unpack("<i", self.buf[:4])[0]
                if len(self.buf) < 4 + n: break
                t, body = self.buf[4], self.buf[5:4 + n]; self.buf = self.buf[4 + n:]
                self.handle(t, body)
            if time.time() >= end or self.closed: return

    def handle(self, t, body):
        if t == SNAPSHOT: self.snapshot = True
        elif t == INFO:
            m = json.loads(body)
            if "you" in m: self.me = m["you"]
            if "note" in m: self.notes.append(m["note"])
            if "tr" in m: self.replies.append(m)
            if "end" in m: self.ended = m["end"]
        elif t == DELTA:
            m = json.loads(gzip.decompress(body))
            for o in m.get("up", []): self.ups[o["UID"]] = o
            for a in m.get("mv", []): self.pos[a[0]] = (a[1], a[3])

    def tile(self):
        o = self.ups.get(self.me)
        return (o["TX"], o["TY"]) if o else None

    def wait(self, cond, secs):
        end = time.time() + secs
        while time.time() < end:
            self.pump(0.3)
            if cond(): return True
        return False

    def barrier(self, k):
        def got(): return any(r.get("tr") == "mb" and r.get("k") == k and r.get("n", 0) >= 2 for r in self.replies)
        end = time.time() + 300
        while time.time() < end:
            self.send(INPUT, {"a": "mb", "k": k}); self.pump(1.0)
            if got(): return True
        check("bot: barrier %d" % k, False); return False


c = Conn()
check("bot: joined (snapshot and my character)", c.wait(lambda: c.snapshot and c.me >= 0 and c.tile() is not None, 120), "uid %d" % c.me)
mine = c.me
c.barrier(1)
c.barrier(2)
x, y = c.tile()
c.pump(2)
start = c.pos.get(mine)
moved = False
for dx, dy in [(-4, -2), (-2, -5), (-6, 0), (0, -4), (-3, 3)]:   # the bot can't see the map: try a few spots
    c.send(INPUT, {"a": "go", "x": max(1, x + dx), "y": max(1, y + dy), "t": -2, "k": 0})
    if c.wait(lambda: c.pos.get(mine) is not None and c.pos.get(mine) != start, 10): moved = True; break
check("bot: my character walked (host moved it)", moved, "from %s" % (start,))
c.barrier(3)
x, y = c.tile()
before = {u for u, o in c.ups.items() if o.get("ID") == "ConverterFoundation"}
placed = False
for dx, dy in [(-6, -6), (-8, -2), (-3, -8), (-10, -5), (-6, 2)]:
    c.send(INPUT, {"a": "build", "x": max(1, x + dx), "y": max(1, y + dy), "type": "Workbench", "rot": 0, "flip": False, "orig": -1, "inst": False, "force": False, "upg": False})
    if c.wait(lambda: any(o.get("ID") == "ConverterFoundation" and u not in before for u, o in c.ups.items()), 5): placed = True; break
check("bot: my blueprint was placed (via host)", placed)
c.barrier(4)
c.barrier(5)
c.barrier(6)
# leave without a word (connection drop), then come back
c.s.close()
time.sleep(6)
c = Conn()
check("bot: rejoined", c.wait(lambda: c.snapshot and c.me >= 0, 120))
check("bot: got my own character back", c.me == mine, "now %d was %d" % (c.me, mine))
c.barrier(7)
c.send(INPUT, {"a": "multi_done"})
check("bot: host ended -> lobby ended message", c.wait(lambda: c.ended is not None, 120), c.ended or "")
with open(OUT, "a") as f: f.write("DONE %d/%d\n" % (passes, passes + fails))
