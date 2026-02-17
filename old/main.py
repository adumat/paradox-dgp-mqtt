import argparse
from digiplex_server import DigiplexServer

# ---------- CLI ----------
def main():
    ap=argparse.ArgumentParser(description="Paradox Digiplex direct-serial server")
    ap.add_argument("--port", required=True)
    ap.add_argument("--baud", type=int, default=19200)
    ap.add_argument("--debug",  type=bool, default=False)
    ap.add_argument("--password", type=str, default="0000", help="PC password (4 digits)")

    args=ap.parse_args()

    c=DigiplexServer(args.port, args.baud, args.password, args.debug)
    try:
      c.main_loop()
    except Exception as e:
      print("Probe failed:", e)
    finally:
      c.close()

if __name__=="__main__":
    main()
