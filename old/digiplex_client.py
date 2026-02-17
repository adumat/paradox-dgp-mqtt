#!/usr/bin/env python3
"""
digiplex_client.py  —  Direct-serial (DGP-848-friendly) client
- 19200 8N1 by default
- INIT + multi-variant LOGIN using PC password
- Robust READ: ignores ACK/other frames and waits for the READ reply
- EVENT/LOGIN/INIT decoding (best-effort)
- Zone/tamper monitor from 0x153
- Listen mode and a probe tool for READ sequencing

Usage examples:
  python3 digiplex_client.py --port /dev/ttyUSB1 --handshake --pcpass 000000 --debug
  python3 digiplex_client.py --port /dev/ttyUSB1 --probe 0x0000 32 --pcpass 000000 --debug
  python3 digiplex_client.py --port /dev/ttyUSB1 --read 0x153 12
"""
from __future__ import annotations
import argparse, sys, threading, time
from dataclasses import dataclass, asdict
from typing import Optional, Tuple, List, Dict, Any

try:
    import serial  # pip install pyserial
except ImportError:
    print("Missing dependency. Run: pip install pyserial", file=sys.stderr); raise

FRAME_PAYLOAD_SIZE = 36
FRAME_TOTAL_SIZE   = 37

# Type nibbles
TYPE_INIT  = 0x0
TYPE_LOGIN = 0x1
TYPE_SPEED = 0x2
TYPE_TIME  = 0x3
TYPE_MON   = 0x4
TYPE_READ  = 0x5
TYPE_WRITE = 0x6
TYPE_ACK   = 0x7   # observed 0x70 ... 0x70
TYPE_EVENT = 0xE

# ---------- helpers ----------
def crc8_sum(payload: bytes) -> int:
    return sum(payload) & 0xFF

def build_frame(payload: bytes) -> bytes:
    if len(payload) > FRAME_PAYLOAD_SIZE: raise ValueError("payload too long")
    pad = FRAME_PAYLOAD_SIZE - len(payload)
    return payload + (b"\x00"*pad) + bytes([crc8_sum(payload)])

def parse_frame(frame: bytes) -> Tuple[bytes, bool]:
    if len(frame) != FRAME_TOTAL_SIZE: raise ValueError(f"bad frame len {len(frame)}")
    payload, rx_crc = frame[:-1], frame[-1]
    s_full = sum(payload) & 0xFF
    s_stripped = sum(payload.rstrip(b"\x00")) & 0xFF
    return payload, (rx_crc == s_full) or (rx_crc == s_stripped)

def bcd_to_int(x: int) -> int: return (x>>4)*10 + (x & 0x0F)
def bits_lsb(b: bytes) -> List[int]:
    out=[];
    for by in b:
        for i in range(8): out.append((by>>i)&1)
    return out

# ---------- READ ----------
def encode_read(count: int, bus_addr: int, address: int) -> bytes:
    if not (1 <= count <= 32): raise ValueError("count 1..32")
    if not (0 <= bus_addr <= 0x7F): raise ValueError("bus 0..127")
    if not (0 <= address  <= 0xFFFF): raise ValueError("addr 0..65535")
    c = 0 if count == 32 else count
    b0 = (TYPE_READ<<4) | ((c>>1)&0x0F)
    b1 = ((c & 0x01)<<7) | (bus_addr & 0x7F)
    b2 = (address>>8)&0xFF; b3 = address&0xFF
    return build_frame(bytes([b0,b1,b2,b3]))

@dataclass
class ReadResponse:
    msg_type:int; msg_center:int; bus_addr:int; address:int; data:bytes
def decode_read_response(payload: bytes) -> ReadResponse:
    if len(payload) < 4: raise ValueError("short READ resp")
    b0,b1,b2,b3 = payload[0:4]
    return ReadResponse((b0>>4)&0xF, b0&0xF, b1, (b2<<8)|b3, payload[4:])

# ---------- INIT/LOGIN/EVENT (best-effort) ----------
def encode_init(bus_addr=0) -> bytes:
    return build_frame(bytes([(TYPE_INIT<<4)|0x0, bus_addr & 0x7F]))

@dataclass
class InitResponse:
    msg_type:int; msg_center:int; bus_addr:int; raw:bytes
    product_id:Optional[int]=None; proto_version:Optional[Tuple[int,int]]=None
    sw_version:Optional[Tuple[int,int]]=None; hw_version:Optional[Tuple[int,int]]=None
    serial:Optional[int]=None
def decode_init_response(payload: bytes)->InitResponse:
    b0=payload[0]; bus=payload[1]; rest=payload[2:]
    ir=InitResponse((b0>>4)&0xF, b0&0xF, bus, rest)
    if len(rest)>=2: ir.product_id=(rest[0]<<8)|rest[1]
    if len(rest)>=4: ir.proto_version=(rest[2],rest[3])
    if len(rest)>=6: ir.sw_version=(rest[4],rest[5])
    if len(rest)>=8: ir.hw_version=(rest[6],rest[7])
    if len(rest)>=12: ir.serial=(rest[8]<<24)|(rest[9]<<16)|(rest[10]<<8)|rest[11]
    return ir

@dataclass
class LoginResponse:
    msg_type:int; msg_center:int; bus_addr:int; answer_code:Optional[int]; callback:Optional[int]; raw:bytes
def decode_login_response(payload: bytes)->LoginResponse:
    b0=payload[0]; bus=payload[1] if len(payload)>1 else 0
    ans=None; cb=None
    if len(payload)>=4: ans=(payload[2]>>4)&0xF; cb=((payload[2]&0xF)<<8)|payload[3]
    return LoginResponse((b0>>4)&0xF, b0&0xF, bus, ans, cb, payload[2:])

@dataclass
class EventRecord:
    msg_type:int; msg_center:int; bus_addr:int; timestamp:Optional[Dict[str,int]]
    group:Optional[int]; number1:Optional[int]; number2:Optional[int]; partitions:Optional[int]
    serial:Optional[int]; data:bytes
def decode_event(payload: bytes)->EventRecord:
    b0=payload[0]; bus=payload[1] if len(payload)>1 else 0
    ts=None
    if len(payload)>=8:
        cc,yy,mm,dd,hh,mi=payload[2:8]
        ts={'century':bcd_to_int(cc),'year':bcd_to_int(yy),'month':bcd_to_int(mm),'day':bcd_to_int(dd),'hour':bcd_to_int(hh),'minute':bcd_to_int(mi)}
    grp = payload[8] if len(payload)>8 else None
    n1  = payload[9] if len(payload)>9 else None
    n2  = payload[10] if len(payload)>10 else None
    part= payload[11] if len(payload)>11 else None
    ser = ((payload[12]<<24)|(payload[13]<<16)|(payload[14]<<8)|payload[15]) if len(payload)>=16 else None
    data= payload[16:] if len(payload)>16 else b""
    return EventRecord((b0>>4)&0xF, b0&0xF, bus, ts, grp, n1, n2, part, ser, data)

# Login payload builders (we’ll try several)
def pack_bcd6(code:str)->bytes:
    s=code.zfill(6); out=[]
    for i in range(0,6,2): out.append(((int(s[i])&0xF)<<4)|(int(s[i+1])&0xF))
    return bytes(out)
def pack_hex4(code:str)->bytes:
    s=code.upper().zfill(4)
    return bytes([(int(s[0],16)<<4)|int(s[1],16), (int(s[2],16)<<4)|int(s[3],16)])
def build_login_bcd6(code:str,bus:int)->bytes:  return build_frame(bytes([(TYPE_LOGIN<<4)|0x0, bus&0x7F])+pack_bcd6(code))
def build_login_hex4(code:str,bus:int)->bytes:  return build_frame(bytes([(TYPE_LOGIN<<4)|0x0, bus&0x7F])+pack_hex4(code))
def build_login_ascii4(code:str,bus:int)->bytes: return build_frame(bytes([(TYPE_LOGIN<<4)|0x0, bus&0x7F])+code[:4].encode("ascii"))

# ---------- client ----------
class DigiplexClient:
    def __init__(self, port:str, baud:int=19200, timeout:float=1.0, debug:bool=False):
        self.ser = serial.Serial(port=port, baudrate=baud, bytesize=serial.EIGHTBITS,
                                 parity=serial.PARITY_NONE, stopbits=serial.STOPBITS_ONE,
                                 timeout=timeout, write_timeout=timeout)
        try: self.ser.reset_input_buffer(); self.ser.reset_output_buffer()
        except: pass
        self.debug=debug; self.lock=threading.Lock(); self._stop=threading.Event()

    # --- IO ---
    def send_payload(self, payload: bytes)->None:
        frame=build_frame(payload)
        if self.debug: print("TX:", frame.hex(' '))
        with self.lock: self.ser.write(frame); self.ser.flush()

    def recv_frame(self)->Tuple[bytes,bool]:
        buf=b''; start=time.time()
        while len(buf) < FRAME_TOTAL_SIZE:
            chunk=self.ser.read(FRAME_TOTAL_SIZE-len(buf))
            if not chunk:
                if time.time()-start > self.ser.timeout: raise TimeoutError("timeout waiting for frame")
                continue
            buf+=chunk
        if self.debug: print("RX:", buf.hex(' '))
        payload, ok = parse_frame(buf)
        if self.debug and not ok: print("WARNING: checksum mismatch (accepted)")
        return payload, ok

    def recv_until(self, accept_types:set, idle_timeout:float=1.0, max_frames:int=10):
        """Keep reading frames until a type in accept_types is seen or idle timeout expires."""
        end = time.time()+idle_timeout
        seen=[]
        while len(seen)<max_frames:
            try:
                payload, _ = self.recv_frame()
                t = (payload[0]>>4)&0xF
                seen.append(t)
                if t in accept_types:
                    return payload, seen
                # print interesting stuff for debugging
                if self.debug:
                    if t==TYPE_ACK:   print("[ACK]")
                    elif t==TYPE_EVENT: print("[EVENT]", decode_event(payload))
                    elif t==TYPE_LOGIN: print("[LOGIN prompt/resp]", asdict(decode_login_response(payload)))
                    elif t==TYPE_READ:  print("[READ (unexpected order)]", decode_read_response(payload))
                end = time.time()+idle_timeout  # extend idle timer on any traffic
            except TimeoutError:
                if time.time()>end: break
        raise TimeoutError(f"no accepted frame types {accept_types} after seeing {seen}")

    # --- protocol ops ---
    def send_init(self, bus_addr:int=0):
        self.send_payload(bytes([(TYPE_INIT<<4)|0x0, bus_addr&0x7F]))
        payload,_ = self.recv_frame()
        t=(payload[0]>>4)&0xF
        if t==TYPE_INIT:  return 'INIT',  decode_init_response(payload)
        if t==TYPE_LOGIN: return 'LOGIN', decode_login_response(payload)
        if t==TYPE_EVENT: return 'EVENT', decode_event(payload)
        if t==TYPE_ACK:   return 'ACK',   {'ack':True,'raw':payload.hex(' ')}
        if t==TYPE_READ:  return 'READ',  decode_read_response(payload)
        return 'OTHER', payload

    def try_login(self, pcpass:str, bus:int=0):
        variants=[("bcd6", build_login_bcd6(pcpass if pcpass.isdigit() else "000000", bus)),
                  ("hex4", build_login_hex4(pcpass, bus)),
                  ("ascii4", build_login_ascii4(pcpass, bus))]
        for name,frame in variants:
            try:
                self.send_payload(frame[:-1])  # frame is already built in builder; but send_payload builds again -> use payload only
                payload,_ = self.recv_frame()
                t=(payload[0]>>4)&0xF
                if t==TYPE_LOGIN:
                    resp = decode_login_response(payload)
                    ok = (resp.answer_code is None) or (resp.answer_code==0)
                    return ok, name, resp
                if t==TYPE_ACK:
                    return True, name, {"ack":True,"raw":payload.hex(' ')}
            except TimeoutError:
                pass
        return False, "none", {"ack":False}

    def read(self, address:int, count:int=32, bus:int=0) -> bytes:
        # send request (NOTE: send_payload expects payload, not full frame)
        payload = bytes([(TYPE_READ<<4)|(((0 if count==32 else count)>>1)&0xF),
                         (((0 if count==32 else count)&1)<<7) | (bus&0x7F),
                         (address>>8)&0xFF, address&0xFF])
        self.send_payload(payload)
        # Some panels ACK first, then send the data. Wait until we see a READ.
        rx_payload, seen = self.recv_until({TYPE_READ}, idle_timeout=1.5, max_frames=10)
        rr = decode_read_response(rx_payload)
        # Slice to requested count
        need = 32 if count==32 else count
        return rr.data[:need]

    def read_full_memory_map(self, size:int=512, bus:int=0)->bytes:
        out=bytearray(size)
        for base in range(0,size,32):
            out[base:base+32] = self.read(base,32,bus)
            time.sleep(0.05)
        return bytes(out)

    @staticmethod
    def parse_zone_tamper_window(b:bytes):
        if len(b)<12: raise ValueError("need 12 bytes")
        return bits_lsb(b[:6]), bits_lsb(b[6:12])

    def monitor_zones(self, poll:float=1.0, bus:int=0):
        last_zone=last_t=None
        while not self._stop.is_set():
            try:
                win=self.read(0x153,12,bus)
                z,t=self.parse_zone_tamper_window(win)
                if last_zone is not None:
                    for i,(a,b) in enumerate(zip(last_zone,z),1):
                        if a!=b: print(f"Zone {i}: {'OPEN' if b else 'CLOSED'}")
                    for i,(a,b) in enumerate(zip(last_t,t),1):
                        if a!=b: print(f"Tamper {i}: {'TAMPER' if b else 'OK'}")
                else:
                    print("Initial zones (first 32):", z[:32])
                last_zone, last_t = z, t
                time.sleep(poll)
            except TimeoutError:
                print("Timeout during monitor; continuing...")

    def listen(self):
        print("Listening... Ctrl+C to stop")
        try:
            while True:
                p,_=self.recv_frame()
                t=(p[0]>>4)&0xF
                if t==TYPE_READ:  rr=decode_read_response(p); print(f"[READ] bus={rr.bus_addr} addr=0x{rr.address:04X} len={len(rr.data)} data={rr.data.hex(' ')}")
                elif t==TYPE_INIT: ir=decode_init_response(p); print("[INIT]", {k:v for k,v in asdict(ir).items() if k!='raw'}, "raw:", ir.raw.hex(' '))
                elif t==TYPE_LOGIN:lr=decode_login_response(p); print("[LOGIN]", {k:v for k,v in asdict(lr).items() if k!='raw'}, "raw:", lr.raw.hex(' '))
                elif t==TYPE_EVENT:ev=decode_event(p); print("[EVENT]", ev)
                elif t==TYPE_ACK: print("[ACK]", p.hex(' '))
                else: print(f"[TYPE 0x{t:X}]", p.hex(' '))
        except KeyboardInterrupt:
            pass

    def close(self):
        self._stop.set()
        try: self.ser.close()
        except: pass

# ---------- CLI ----------
def main():
    ap=argparse.ArgumentParser(description="Paradox Digiplex direct-serial client")
    ap.add_argument("--port", required=True)
    ap.add_argument("--baud", type=int, default=19200)
    ap.add_argument("--bus",  type=int, default=0)
    ap.add_argument("--timeout", type=float, default=1.0)
    ap.add_argument("--debug", action="store_true")
    g=ap.add_mutually_exclusive_group(required=True)
    g.add_argument("--handshake", action="store_true")
    g.add_argument("--listen", action="store_true")
    g.add_argument("--memdump", metavar="FILE")
    g.add_argument("--monitor", action="store_true")
    g.add_argument("--read", nargs=2, metavar=("ADDR","COUNT"))
    g.add_argument("--probe", nargs=2, metavar=("ADDR","COUNT"), help="Send one READ and print every frame we see until the READ reply")
    ap.add_argument("--pcpass", default="0000", help="PC password (4 hex or 4-6 digits)")
    args=ap.parse_args()

    c=DigiplexClient(args.port, args.baud, args.timeout, args.debug)
    try:
        if args.handshake:
            print("Sending INIT...")
            t,obj=c.send_init(bus_addr=args.bus)
            print("INIT result:", t, (asdict(obj) if hasattr(obj,'__dict__') else obj))
            print(f"Trying LOGIN with pcpass={args.pcpass} (bcd6/hex4/ascii4)...")
            ok,variant,resp = c.try_login(args.pcpass, bus=args.bus)
            print("LOGIN:", {"ok":ok,"variant":variant,"resp":(asdict(resp) if hasattr(resp,'__dict__') else resp)})
            if ok:
                print("Doing READ 32 @ 0x0000...")
                try:
                    data=c.read(0x0000,32,args.bus)
                    print("READ OK:", data.hex(' '))
                except Exception as e:
                    print("READ failed after login:", e)
        elif args.listen:
            c.listen()
        elif args.memdump:
            data=c.read_full_memory_map(512, bus=args.bus)
            open(args.memdump,"wb").write(data)
            print(f"Wrote {len(data)} bytes to {args.memdump}")
        elif args.monitor:
            c.monitor_zones(bus=args.bus)
        elif args.read:
            addr=int(args.read[0],0); cnt=int(args.read[1],0)
            data=c.read(addr,cnt,args.bus)
            print(data.hex(' '))
        elif args.probe:
            addr=int(args.probe[0],0); cnt=int(args.probe[1],0)
            # send request but use recv_until to print each step until READ arrives
            payload = bytes([(TYPE_READ<<4)|(((0 if cnt==32 else cnt)>>1)&0xF),
                             (((0 if cnt==32 else cnt)&1)<<7)|(args.bus&0x7F),
                             (addr>>8)&0xFF, addr&0xFF])
            c.send_payload(payload)
            try:
                rx, seen = c.recv_until({TYPE_READ}, idle_timeout=2.0, max_frames=20)
                print("Seen types (by nibble):", seen)
                rr = decode_read_response(rx)
                print(f"Final READ: bus={rr.bus_addr} addr=0x{rr.address:04X} len={len(rr.data)} data={rr.data.hex(' ')}")
            except Exception as e:
                print("Probe failed:", e)
    finally:
        c.close()

if __name__=="__main__":
    main()
