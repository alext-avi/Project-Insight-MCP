#!/usr/bin/env bash
# Faithful local test of the mcpb link_pi_account path.
# Launches the stdio binary the same way Claude Desktop / Cowork does:
#   - a clean, isolated HOME (no cached credential = fresh install)
#   - the same two env vars the manifest passes (base_url + blank api_token)
# Usage: ./test-link.sh [BASE_URL] [API_TOKEN]
set -uo pipefail

BASE_URL="${1:-https://yourdomain.projectinsight.net/api/}"
API_TOKEN="${2:-}"   # blank = SSO, the Cowork default

ROOT="$(cd "$(dirname "$0")" && pwd)"
# Prefer the freshly-built (instrumented) Debug binary; fall back to the bundle.
BIN="$ROOT/src-stdio/bin/Debug/net10.0/projectinsight-mcp"
[ -x "$BIN" ] || BIN="$ROOT/mcpb/build/server/darwin/projectinsight-mcp"

echo "binary:   $BIN"
echo "base_url: $BASE_URL"
echo "token:    ${API_TOKEN:-(blank)}"

TMPHOME="$(mktemp -d)"   # isolated HOME -> no cached credential
echo "temp HOME: $TMPHOME"
echo "---- link_pi_account result ----"
# Keep stdin open until the response arrives, as a real MCP client does (closing stdin is the
# stdio shutdown signal; the server still answers what it has read, but this matches real use).
HOME="$TMPHOME" ProjectInsight__BaseUrl="$BASE_URL" ProjectInsight__ApiToken="$API_TOKEN" \
python3 - "$BIN" 2>"$TMPHOME/stderr.log" <<'PY'
import json, subprocess, sys, threading
p = subprocess.Popen([sys.argv[1]], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
threading.Timer(40, p.kill).start()
for m in [
    {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "cowork-sim", "version": "1"}}},
    {"jsonrpc": "2.0", "method": "notifications/initialized"},
    {"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": "link_pi_account", "arguments": {}}},
]:
    p.stdin.write(json.dumps(m) + "\n"); p.stdin.flush()
for line in p.stdout:
    line = line.strip()
    if not line: continue
    try: d = json.loads(line)
    except ValueError: print("NON-JSON:", line); continue
    if d.get("id") == 2:
        print(json.dumps(d.get("result", {}), indent=2))
        break
p.stdin.close(); p.wait(timeout=10)
PY
echo "---- stderr ----"; cat "$TMPHOME/stderr.log" 2>/dev/null || true
echo "---- link-debug.log (raw PI response) ----"
cat "$TMPHOME/.projectinsight/link-debug.log" 2>/dev/null || echo "(no debug log written)"
rm -rf "$TMPHOME"
