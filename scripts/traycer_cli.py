#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""
Traycer HUD test sender (Windows, Python 3.9+)
- Talks to \\.\pipe\TraycerHud (Named Pipe)
- Commands: config, add, remove, resize, set, bind, placement, bulk, demo, repl
- No deps required. If you install pywin32, we'll use it for a sturdier connect.
"""

import argparse, json, time, sys
from typing import Iterable, Dict, Any

PIPE_NAME = r"\\.\pipe\TraycerHud"

HAS_PYWIN32 = False
try:
    import win32file  # type: ignore
    HAS_PYWIN32 = True
except Exception:
    HAS_PYWIN32 = False

def _send_line(line: str, timeout_sec: float = 5.0) -> None:
    if HAS_PYWIN32:
        import win32file  # type: ignore
        t0 = time.time(); last = None
        while time.time() - t0 < timeout_sec:
            try:
                h = win32file.CreateFile(PIPE_NAME, win32file.GENERIC_WRITE, 0, None,
                                         win32file.OPEN_EXISTING, 0, None)
                try: win32file.WriteFile(h, line.encode("utf-8"))
                finally: win32file.CloseHandle(h)
                return
            except Exception as e:
                last = e; time.sleep(0.1)
        raise RuntimeError(f"Open/write failed: {last}")
    else:
        t0 = time.time(); last = None
        while time.time() - t0 < timeout_sec:
            try:
                with open(PIPE_NAME, "w", encoding="utf-8", newline="\n") as f:
                    f.write(line); f.flush()
                return
            except Exception as e:
                last = e; time.sleep(0.1)
        raise RuntimeError(f"Open/write failed: {last}")

def send_json(obj: Dict[str, Any]) -> None:
    _send_line(json.dumps(obj, ensure_ascii=False) + "\n")

def normalize_color(s: str | None) -> str | None:
    if not s: return s
    # Accept '#AARRGGBB', '#RRGGBB', 'hex:AARRGGBB', '0xAARRGGBB'
    if s.lower().startswith("hex:"):
        return "#" + s[4:]
    return s

# ---- Commands ----
def cmd_config(a):  # wells from args or file
    if a.file:
        data = json.load(open(a.file, "r", encoding="utf-8"))
        wells = data["wells"] if isinstance(data, dict) and "wells" in data else data
    else:
        wells = []
        for spec in a.well or []:
            id_, width = spec.split(":", 1)
            wells.append({"id": id_, "width": float(width)})
    send_json({"op": "config", "wells": wells}); print("sent: config")

def cmd_add(a):
    msg = {"op": "add", "well": a.well, "width": float(a.width)}
    if a.index is not None: msg["index"] = int(a.index)
    send_json(msg); print(f"sent: add {a.well}")

def cmd_remove(a):
    send_json({"op":"remove","well":a.well}); print(f"sent: remove {a.well}")

def cmd_resize(a):
    send_json({"op":"resize","well":a.well,"width":float(a.width)}); print(f"sent: resize {a.well}")

def cmd_set(a):
    msg = {"op":"set","well":a.well}
    if a.text is not None: msg["text"] = a.text
    if a.fg: msg["fg"] = normalize_color(a.fg)
    if a.bg: msg["bg"] = normalize_color(a.bg)
    if a.action: msg["action"] = a.action
    if a.blink is not None: msg["blink"] = bool(a.blink)
    send_json(msg); print(f"sent: set {a.well}")

def cmd_bind(a):
    send_json({"op":"bind","well":a.well,"action":a.action}); print(f"sent: bind {a.well}")

def cmd_placement(a):
    m={"op":"placement"}
    if a.height is not None: m["height"]=float(a.height)
    if a.bottomOffset is not None: m["bottomOffset"]=float(a.bottomOffset)
    if a.padding is not None: m["padding"]=float(a.padding)
    if a.cornerRadius is not None: m["cornerRadius"]=float(a.cornerRadius)
    send_json(m); print("sent: placement")

def cmd_bulk(a):
    if a.file:
        data = json.load(open(a.file,"r",encoding="utf-8"))
        updates = data["updates"] if isinstance(data, dict) and "updates" in data else data
    else:
        updates=[]
        for entry in a.set or []:
            kv = parse_kv(entry)
            if "well" not in kv: raise SystemExit("bulk --set requires well=...")
            u={"op":"set","well":kv.pop("well")}
            if "fg" in kv: kv["fg"] = normalize_color(kv["fg"])
            if "bg" in kv: kv["bg"] = normalize_color(kv["bg"])
            u.update(kv); updates.append(u)
    send_json({"op":"bulk","updates":updates}); print("sent: bulk")

def cmd_demo(a):
    send_json({"op":"placement","height": a.height, "bottomOffset": a.bottomOffset, "padding": a.padding})
    try:
        t0=time.time()
        while True:
            if int(time.time()-t0)%2==0:
                send_json({"op":"set","well":"weather","text":"🌦️  71°F Light rain"})
                send_json({"op":"set","well":"build","text":"🟡 Running…","bg":"#33333322"})
            else:
                send_json({"op":"set","well":"weather","text":"⛅  73°F Overcast"})
                send_json({"op":"set","well":"build","text":"✅ Passing","bg":"#33305533"})
            import random
            send_json({"op":"bulk","updates":[
                {"op":"set","well":"net","text":f"📶  {random.randint(20,600)} Mbps"},
                {"op":"set","well":"cpu","text":f"🧠  {random.randint(5,90)}%"},
                {"op":"set","well":"ram","text":f"🧵  {random.randint(20,92)}%"},
                {"op":"set","well":"meeting","text":"🗓️  1:00 PM • Standup"}
            ]})
            time.sleep(a.interval/1000.0)
    except KeyboardInterrupt:
        print("\nDemo stopped.")

def cmd_repl(a):
    print("REPL. JSON or friendly cmds. Examples:")
    print('  set weather text="⛅  73°F Overcast" bg="#33223333"')
    print('  set build action="start https://ci.example.com"')
    print('  add alerts 160 --index 2')
    print('  resize weather 280')
    print('  remove ram')
    try:
        while True:
            line=input("traycer> ").strip()
            if not line: continue
            if line.startswith("{"):
                try: send_json(json.loads(line))
                except Exception as e: print("  ! bad JSON:", e)
                continue
            parts=line.split()
            cmd=parts[0].lower(); rest=" ".join(parts[1:])
            if cmd=="set":
                well, *kvs = parts[1:]
                kv = parse_kv(" ".join(kvs))
                if "fg" in kv: kv["fg"] = normalize_color(kv["fg"])
                if "bg" in kv: kv["bg"] = normalize_color(kv["bg"])
                o={"op":"set","well":well}; o.update(kv); send_json(o)
            elif cmd=="bind":
                well, *kvs = parts[1:]
                kv = parse_kv(" ".join(kvs))
                o={"op":"bind","well":well}; o.update(kv); send_json(o)
            elif cmd=="placement":
                o={"op":"placement"}; o.update(parse_kv(rest)); send_json(o)
            elif cmd=="config":
                wells=[]; 
                for spec in rest.split():
                    if ":" in spec:
                        id_, w = spec.split(":",1); wells.append({"id":id_, "width":float(w)})
                send_json({"op":"config","wells":wells})
            elif cmd=="add":
                well, width, *more = parts[1:]
                o={"op":"add","well":well,"width":float(width)}
                if more and more[0].startswith("--index"):
                    o["index"]=int(more[0].split("=",1)[-1]) if "=" in more[0] else int(more[1])
                send_json(o)
            elif cmd=="remove":
                send_json({"op":"remove","well":parts[1]})
            elif cmd=="resize":
                send_json({"op":"resize","well":parts[1],"width":float(parts[2])})
            elif cmd=="bulk":
                updates=[]
                for chunk in rest.split("  "):
                    kv=parse_kv(chunk)
                    if "fg" in kv: kv["fg"] = normalize_color(kv["fg"])
                    if "bg" in kv: kv["bg"] = normalize_color(kv["bg"])
                    if "well" in kv:
                        u={"op":"set","well":kv.pop("well")}; u.update(kv); updates.append(u)
                send_json({"op":"bulk","updates":updates})
            else:
                print("unknown cmd")
    except KeyboardInterrupt:
        print("\nbye.")

def parse_kv(s: str) -> Dict[str, Any]:
    out: Dict[str, Any] = {}
    tokens=[]; buf=[]; q=None
    for ch in s:
        if q:
            if ch==q: q=None
            else: buf.append(ch)
        else:
            if ch in ('"', "'"): q=ch
            elif ch in [' ', ';', '\t', '\n', '\r']:
                if buf: tokens.append("".join(buf)); buf=[]
            else: buf.append(ch)
    if buf: tokens.append("".join(buf))
    for t in tokens:
        if "=" not in t: continue
        k,v=t.split("=",1); v=v.strip()
        if v.lower() in ("true","false"): out[k]=(v.lower()=="true")
        else:
            if len(v)>=2 and v[0]==v[-1] and v[0] in ('"',"'"): v=v[1:-1]
            out[k]=v
    return out

def main(argv: Iterable[str]) -> int:
    ap=argparse.ArgumentParser(description="Traycer HUD test sender")
    sub=ap.add_subparsers(dest="cmd", required=True)

    p=sub.add_parser("config"); p.add_argument("--well",action="append"); p.add_argument("--file"); p.set_defaults(func=cmd_config)
    p=sub.add_parser("add");    p.add_argument("well"); p.add_argument("width",type=float); p.add_argument("--index",type=int); p.set_defaults(func=cmd_add)
    p=sub.add_parser("remove"); p.add_argument("well"); p.set_defaults(func=cmd_remove)
    p=sub.add_parser("resize"); p.add_argument("well"); p.add_argument("width",type=float); p.set_defaults(func=cmd_resize)

    p=sub.add_parser("set"); p.add_argument("well"); p.add_argument("--text"); p.add_argument("--fg"); p.add_argument("--bg"); p.add_argument("--action"); p.add_argument("--blink", type=lambda s: s.lower() in ("1","true","yes","on")); p.set_defaults(func=cmd_set)
    p=sub.add_parser("bind"); p.add_argument("well"); p.add_argument("action"); p.set_defaults(func=cmd_bind)

    p=sub.add_parser("placement"); p.add_argument("--height",type=float); p.add_argument("--bottomOffset",type=float); p.add_argument("--padding",type=float); p.add_argument("--cornerRadius",type=float); p.set_defaults(func=cmd_placement)
    p=sub.add_parser("bulk"); p.add_argument("--set",action="append"); p.add_argument("--file"); p.set_defaults(func=cmd_bulk)

    p=sub.add_parser("demo"); p.add_argument("--interval",type=int,default=800); p.add_argument("--height",type=float,default=26); p.add_argument("--bottomOffset",type=float,default=2); p.add_argument("--padding",type=float,default=6); p.set_defaults(func=cmd_demo)
    p=sub.add_parser("repl"); p.set_defaults(func=cmd_repl)

    args=ap.parse_args(list(argv)); args.func(args); return 0

if __name__=="__main__":
    raise SystemExit(main(sys.argv[1:]))
