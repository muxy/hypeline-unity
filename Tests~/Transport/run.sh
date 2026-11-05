#!/bin/sh
# Builds Runtime/HypelineWebsocketTransport.cs with a stub SDK and a loopback
# WebSocket server, then runs the transport regression tests under Mono.
# Needs Mono (mono and its csc on PATH, or set MONO and CSC). Pass test names
# to run only those. Unity ignores this folder because its name ends in ~.
set -eu

HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/../.." && pwd)
MONO=${MONO:-mono}
CSC=${CSC:-csc}
OUT=$(mktemp -d)
trap 'rm -rf "$OUT"' EXIT

# C# 8 is what Unity 2020.3 compiles the runtime with.
"$CSC" -nologo -langversion:8 -target:exe -out:"$OUT/TransportTests.exe" \
    "$ROOT/Runtime/HypelineWebsocketTransport.cs" "$HERE"/*.cs
"$MONO" --debug "$OUT/TransportTests.exe" "$@"
