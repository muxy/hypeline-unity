#!/bin/sh
# Builds Runtime/HypelineWebsocketTransport.cs with a stub SDK and a loopback
# WebSocket server, then runs the transport regression tests under Mono.
# Needs Mono (mono and its csc on PATH, or set MONO and CSC) and perl. Pass
# test names to run only those. Unity ignores this folder because its name
# ends in ~.
set -eu

HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/../.." && pwd)
MONO=${MONO:-mono}
CSC=${CSC:-csc}
OUT=$(mktemp -d)
trap 'rm -rf "$OUT"' EXIT

# The tests build a copy of the transport with test hooks added (TestHooks.cs):
# a call to TestHooks.AfterDrain right after Update delivers what was received,
# so a test can make a reconnect land before Update hands payloads over, and
# TestHooks.Sending and TestHooks.Receiving, which can replace the task of a
# socket send or receive with one that never completes. The shipped runtime
# has no such hooks. Fail loudly if a line a hook goes after has changed.
perl -0777 -ne '
    my $n = s/(\n[ \t]*else instance\.HandleReconnect\(\);\n([ \t]*)\}\n)/$1$2TestHooks.AfterDrain?.Invoke();\n/g;
    die "run.sh: could not find where to add the test hook in Update\n" unless $n == 1;
    $n = s/(\n([ \t]*)Task send = connection\.Socket\.SendAsync\([^\n]*\);\n)/$1$2send = TestHooks.Sending?.Invoke(send) ?? send;\n/g;
    die "run.sh: could not find where to add the test hook in SendLoop\n" unless $n == 1;
    $n = s/(\n([ \t]*)Task<WebSocketReceiveResult> receive = connection\.Socket\.ReceiveAsync\([^\n]*\);\n)/$1$2receive = TestHooks.Receiving?.Invoke(receive) ?? receive;\n/g;
    die "run.sh: could not find where to add the test hook in ReceiveLoop\n" unless $n == 1;
    print;
' "$ROOT/Runtime/HypelineWebsocketTransport.cs" > "$OUT/HypelineWebsocketTransport.cs"

# C# 8 is what Unity 2020.3 compiles the runtime with.
"$CSC" -nologo -langversion:8 -target:exe -out:"$OUT/TransportTests.exe" \
    "$OUT/HypelineWebsocketTransport.cs" "$HERE"/*.cs
"$MONO" --debug "$OUT/TransportTests.exe" "$@"
