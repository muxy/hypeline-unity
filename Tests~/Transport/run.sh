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

# The tests build a copy of the transport with one test hook added: a call to
# TestHooks.AfterDrain (TestHooks.cs) right after Update delivers what was
# received, so a test can make a reconnect land before Update hands payloads
# over. The shipped runtime has no such hook. Fail loudly if Update no longer
# has the line the hook goes after.
perl -0777 -ne '
    my $n = s/(\n([ \t]*)else instance\.HandleReconnect\(\);\n[ \t]*\}\n)/$1$2TestHooks.AfterDrain?.Invoke();\n/g;
    die "run.sh: could not find where to add the test hook in Update\n" unless $n == 1;
    print;
' "$ROOT/Runtime/HypelineWebsocketTransport.cs" > "$OUT/HypelineWebsocketTransport.cs"

# C# 8 is what Unity 2020.3 compiles the runtime with.
"$CSC" -nologo -langversion:8 -target:exe -out:"$OUT/TransportTests.exe" \
    "$OUT/HypelineWebsocketTransport.cs" "$HERE"/*.cs
"$MONO" --debug "$OUT/TransportTests.exe" "$@"
