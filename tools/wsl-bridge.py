#!/usr/bin/env python3
"""
WSL -> Windows-host TCP bridge for UnityExplorerTLD.MCP.

In WSL2 NAT mode, the Windows host is only reachable via the default-route
gateway IP, which changes across reboots - so a hardcoded MCP server URL breaks.
This bridge listens on 127.0.0.1:<port> inside WSL and forwards each connection
to the *current* Windows-host gateway, resolved live per connection. That lets
every MCP client use a stable `http://localhost:<port>/` regardless of the
gateway IP.

Usage:
    python3 wsl-bridge.py [port]        # default port 3000

Leave it running (or autostart it - see the README). Requires only python3.
Not needed if your MCP client runs on Windows, or if WSL uses mirrored
networking (in both cases localhost already reaches the host).
"""
import socket
import subprocess
import sys
import threading

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 3000


def windows_host():
    # In WSL2 NAT mode the default-route gateway is the Windows host.
    out = subprocess.check_output(["ip", "route", "show", "default"], text=True)
    return out.split()[2]


def pump(src, dst):
    try:
        while True:
            data = src.recv(65536)
            if not data:
                break
            dst.sendall(data)
    except OSError:
        pass
    finally:
        for s in (src, dst):
            try:
                s.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass


def handle(client):
    try:
        upstream = socket.create_connection((windows_host(), PORT), timeout=5)
    except OSError:
        client.close()
        return
    threading.Thread(target=pump, args=(client, upstream), daemon=True).start()
    pump(upstream, client)
    client.close()


def main():
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        srv.bind(("127.0.0.1", PORT))
    except OSError as e:
        print(f"Could not bind 127.0.0.1:{PORT} ({e}). Already running?", file=sys.stderr)
        sys.exit(1)
    srv.listen(64)
    print(f"WSL bridge up: http://localhost:{PORT}/ -> Windows host:{PORT} "
          f"(gateway {windows_host()}). Ctrl-C to stop.")
    try:
        while True:
            client, _ = srv.accept()
            threading.Thread(target=handle, args=(client,), daemon=True).start()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
