"""
ME ACS SQL Patcher — Update Feed Server
Serves the feed/ folder over HTTP. No admin required.
Usage: python serve.py [port]
"""

import http.server
import os
import sys
import socket

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 39000
FEED_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "feed")

if not os.path.isdir(FEED_DIR):
    print(f"ERROR: Feed folder not found: {FEED_DIR}")
    sys.exit(1)

def get_local_ip():
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("8.8.8.8", 80))
        ip = s.getsockname()[0]
        s.close()
        return ip
    except Exception:
        return "localhost"

class FeedHandler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=FEED_DIR, **kwargs)

    def log_message(self, format, *args):
        print(f"  {self.address_string()} -> {args[0]}")

os.chdir(FEED_DIR)
local_ip = get_local_ip()

print()
print("=" * 42)
print("  MagEtegra Update Feed Server")
print("=" * 42)
print()
print(f"  Feed folder : {FEED_DIR}")
print(f"  Listening on: http://{local_ip}:{PORT}")
print()
print("  Set this URL in Admin Tools -> App Update Feed")
print("  on ALL PCs (including this one).")
print()
print("  Press Ctrl+C to stop.")
print()

with http.server.HTTPServer(("", PORT), FeedHandler) as httpd:
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\nServer stopped.")
