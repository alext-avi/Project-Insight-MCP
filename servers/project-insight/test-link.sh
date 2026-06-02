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
BIN="$ROOT/src-stdio/bin/Debug/net8.0/projectinsight-mcp"
[ -x "$BIN" ] || BIN="$ROOT/mcpb/build/server/darwin/projectinsight-mcp"

echo "binary:   $BIN"
echo "base_url: $BASE_URL"
echo "token:    ${API_TOKEN:-(blank)}"

TMPHOME="$(mktemp -d)"   # isolated HOME -> no cached credential
echo "temp HOME: $TMPHOME"
echo "---- link_pi_account result ----"
printf '%s\n' \
'{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"cowork-sim","version":"1"}}}' \
'{"jsonrpc":"2.0","method":"notifications/initialized"}' \
'{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"link_pi_account","arguments":{}}}' \
| HOME="$TMPHOME" ProjectInsight__BaseUrl="$BASE_URL" ProjectInsight__ApiToken="$API_TOKEN" \
  perl -e 'alarm 40; exec @ARGV' "$BIN" 2>"$TMPHOME/stderr.log" \
| python3 -c "
import sys,json
for line in sys.stdin:
    line=line.strip()
    if not line: continue
    try: d=json.loads(line)
    except: print('NON-JSON:',line); continue
    if d.get('id')==2:
        print(json.dumps(d.get('result',{}),indent=2))
"
echo "---- stderr ----"; cat "$TMPHOME/stderr.log" 2>/dev/null || true
echo "---- link-debug.log (raw PI response) ----"
cat "$TMPHOME/.projectinsight/link-debug.log" 2>/dev/null || echo "(no debug log written)"
rm -rf "$TMPHOME"
