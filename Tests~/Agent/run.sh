#!/bin/sh
# Runs the Hypeline Editor tools (Editor/HypelineAgent.cs) in batch mode against a
# throwaway Unity project and checks their reports and the scene they write.
#
#   UNITY=/path/to/Unity Tests~/Agent/run.sh
#
# Needs a Unity Editor with an active license (sign in to Unity Hub first) and
# python3. It covers batch mode only; running jobs through an open Editor's
# Library/Hypeline/jobs folder is checked by hand. UNITY defaults to the newest
# Editor Unity Hub installed on macOS. The
# project is created in a temporary folder and uses this checkout as a local
# package; set KEEP=1 to keep it for inspection. Unity ignores this folder because
# its name ends in ~.
set -eu

HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/../.." && pwd)

if [ -z "${UNITY:-}" ]; then
    UNITY=$(ls -d /Applications/Unity/Hub/Editor/*/Unity.app/Contents/MacOS/Unity 2>/dev/null | sort | tail -1)
fi
if [ -z "${UNITY:-}" ] || [ ! -x "$UNITY" ]; then
    echo "run.sh: set UNITY to the Unity Editor executable" >&2
    exit 2
fi

WORK=$(mktemp -d)
PROJECT="$WORK/project"
if [ -z "${KEEP:-}" ]; then trap 'rm -rf "$WORK"' EXIT; else echo "Keeping $WORK"; fi

mkdir -p "$PROJECT/Assets/Game" "$PROJECT/Assets/Editor" "$PROJECT/Packages" "$PROJECT/ProjectSettings"

cat > "$PROJECT/Packages/manifest.json" <<EOF
{
  "dependencies": {
    "com.muxy.gateway": "file:$ROOT"
  }
}
EOF

cat > "$PROJECT/Assets/Game/PlayerHealth.cs" <<'EOF'
using UnityEngine;

public class PlayerHealth : MonoBehaviour
{
    public int Health = 50;

    public void OnHealthpack(MuxyHypeline.GameActionUsed used) { Health += 10; }
    public void OnShield(MuxyHypeline.GameActionUsed used) { }
    public void NotAHandler(int amount) { }
}
EOF

cat > "$PROJECT/Assets/Editor/Fixture.cs" <<'EOF'
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// A saved scene with World/Player, where Player has a handler component, and an
// empty second scene.
public static class Fixture
{
    public static void CreateScenes()
    {
        System.IO.Directory.CreateDirectory("Assets/Scenes");

        var empty = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorSceneManager.SaveScene(empty, "Assets/Scenes/Empty.unity");

        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var world = new GameObject("World");
        var player = new GameObject("Player");
        player.transform.SetParent(world.transform);
        player.AddComponent<PlayerHealth>();
        EditorSceneManager.SaveScene(scene, "Assets/Scenes/Main.unity");

        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene("Assets/Scenes/Main.unity", true) };
        AssetDatabase.SaveAssets();
    }
}
EOF

# unity NAME ARGS... runs the Editor in batch mode and leaves its exit code in STATUS.
unity() {
    name=$1
    shift
    STATUS=0
    "$UNITY" -batchmode -nographics -projectPath "$PROJECT" -logFile "$WORK/$name.log" "$@" || STATUS=$?
    if grep -q "No valid Unity Editor license" "$WORK/$name.log"; then
        echo "run.sh: Unity has no active license. Sign in to Unity Hub and run this again." >&2
        exit 2
    fi
    if grep -q "error CS[0-9]" "$WORK/$name.log"; then
        echo "run.sh: compiler errors:" >&2
        grep "error CS[0-9]" "$WORK/$name.log" | sort -u >&2
        exit 1
    fi
}

# job NAME JSON runs one job and leaves its report at $WORK/NAME.report.json.
job() {
    printf '%s' "$2" > "$WORK/$1.job.json"
    unity "$1" -executeMethod MuxyHypeline.HypelineAgent.Run -hypelineJob "$WORK/$1.job.json" -hypelineReport "$WORK/$1.report.json"
}

# check NAME EXIT PYTHON asserts the exit code and runs PYTHON with the report as r
# and the Main scene's text as scene.
check() {
    if [ "$STATUS" != "$2" ]; then
        echo "FAIL $1: exit code $STATUS, want $2" >&2
        cat "$WORK/$1.report.json" >&2 2>/dev/null || true
        exit 1
    fi
    REPORT="$WORK/$1.report.json" SCENE="$PROJECT/Assets/Scenes/Main.unity" python3 - "$1" <<EOF
import json, os, sys
r = json.load(open(os.environ["REPORT"]))
scene = open(os.environ["SCENE"]).read()
codes = [f["code"] for f in r["findings"]]
errors = [f["code"] for f in r["findings"] if f["severity"] == "error"]
def expect(condition, message):
    if not condition:
        print("FAIL %s: %s" % (sys.argv[1], message), file=sys.stderr)
        print(json.dumps(r, indent=2), file=sys.stderr)
        sys.exit(1)
$3
print("PASS " + sys.argv[1])
EOF
}

checksum() { cksum < "$PROJECT/Assets/Scenes/Main.unity"; }

unity fixture -quit -executeMethod Fixture.CreateScenes
[ -f "$PROJECT/Assets/Scenes/Main.unity" ] || { echo "FAIL fixture: no scene was created; see $WORK/fixture.log" >&2; exit 1; }
echo "PASS fixture"

# A mistake in the job changes nothing.
BEFORE=$(checksum)
job badjob '{"tool":"setup","scene":"Assets/Scenes/Main.unity","gameId":"agent-test-game","actions":[
  {"id":"healthpack","name":"Spawn Healthpack","handler":{"gameObject":"Player","component":"PlayerHealth","method":"NotAHandler"}},
  {"id":"shield","category":"Sideways","handler":{"gameObject":"Nobody","component":"PlayerHealth","method":"OnShield"}},
  {"id":"wave","handler":{"gameObject":"New Object","component":"NoSuchComponent","method":"OnWave","addIfMissing":true}},
  {"id":"storm","handler":{"gameObject":"World/New Child","component":"PlayerHealth","method":"OnShield","addIfMissing":true}}]}'
check badjob 1 '
expect(not r["ok"], "a bad job was reported ok")
expect("handler_method_not_found" in errors, "no error for a method with the wrong signature")
expect("handler_object_not_found" in errors, "no error for a missing GameObject")
expect("invalid_action_category" in errors, "no error for an unknown category")
expect("handler_component_not_found" in errors, "no error for a component type that does not exist")
expect(errors.count("handler_object_not_found") == 2, "no error for creating an object at a hierarchy path")
expect(r["created"] == [] and r["changed"] == [] and not r["sceneSaved"], "a bad job changed the scene")'
[ "$BEFORE" = "$(checksum)" ] || { echo "FAIL badjob: the scene file changed" >&2; exit 1; }

job badid '{"tool":"setup","scene":"Assets/Scenes/Main.unity","gameId":"Not A Valid ID!"}'
check badid 1 'expect(errors == ["invalid_game_id"], "errors = %s" % errors)'

job noscene '{"tool":"setup","scene":"Assets/Scenes/Missing.unity"}'
check noscene 1 '
expect(errors == ["scene_not_found"], "errors = %s" % errors)
expect("Assets/Scenes/Main.unity" in r["findings"][0]["fix"], "the fix does not list the project scenes")'

job notool '{"scene":"Assets/Scenes/Main.unity"}'
check notool 1 'expect(errors == ["unknown_tool"], "errors = %s" % errors)'

STATUS=0
"$UNITY" -batchmode -nographics -projectPath "$PROJECT" -logFile "$WORK/nojob.log" -executeMethod MuxyHypeline.HypelineAgent.Run \
    -hypelineJob "$WORK/does-not-exist.json" -hypelineReport "$WORK/nojob.report.json" || STATUS=$?
check nojob 2 'expect(errors == ["job_failed"], "errors = %s" % errors)'

# The doctor on a scene with nothing in it.
job doctorbefore '{"tool":"doctor","scene":"Assets/Scenes/Main.unity","offline":true}'
check doctorbefore 1 'expect(errors == ["no_manager"], "errors = %s" % errors)'

# Setup adds and wires everything. The scene comes from the build settings.
job setup '{"tool":"setup","gameId":" Agent-Test-Game ","gameName":"Agent Test Game","stage":"sandbox",
  "texts":[{"label":"Health","value":"50","icon":"fa-solid:heart"}],
  "actions":[
    {"id":"healthpack","name":"Spawn Healthpack","description":"Add up to 10 health to the player.","category":"help","impact":2,"count":25,"icon":"fa-solid:heart",
     "handler":{"gameObject":"World/Player","component":"PlayerHealth","method":"OnHealthpack"}},
    {"id":"shield","name":"Shield","handler":{"gameObject":"Player","component":"PlayerHealth","method":"OnShield"}},
    {"id":"wave","name":"Wave"},
    {"id":"storm","name":"Storm","handler":{"gameObject":"Hypeline Handlers","component":"PlayerHealth","method":"OnShield","addIfMissing":true}},
    {"id":"quake","name":"Quake","handler":{"gameObject":"Hypeline Handlers","component":"PlayerHealth","method":"OnHealthpack","addIfMissing":true}}]}'
check setup 0 '
expect(r["ok"] and errors == [], "errors = %s" % errors)
expect(r["scene"] == "Assets/Scenes/Main.unity", "scene = %s" % r["scene"])
expect(r["gameId"] == "agent-test-game" and r["stage"] == "sandbox", "gameId/stage = %s/%s" % (r["gameId"], r["stage"]))
for item in ["MuxyHypelineManager", "MuxyHypelineMenu", "MuxyHypelineInteractionAlert", "Game Text Health", "Action healthpack", "Action shield", "Action wave",
             "GameObject Hypeline Handlers", "PlayerHealth on Hypeline Handlers"]:
    expect(item in r["created"], "%s was not created: %s" % (item, r["created"]))
expect(any(c.startswith("EventSystem") for c in r["created"]), "no EventSystem was created")
expect("MuxyHypelinePolling" not in r["created"], "polling was added without being asked for")
expect(any("OnHealthpack" in c for c in r["changed"]) and any("OnShield" in c for c in r["changed"]), "handlers were not bound: %s" % r["changed"])
expect(r["sceneSaved"], "the scene was not saved")
expect(r["packageVersion"] != "" and r["unityVersion"] != "", "the report has no versions")
for guid in ["9745ee0d0c5e9cd40950241c07bbe75c", "b4309354bd8d4cf9a192c8ac551fd015", "735114d3755e4a82947d8de60c54caf1"]:
    expect(guid in scene, "the scene does not reference prefab %s" % guid)
expect("3d3fd97c690c5b19b8eba259d51cfc67" not in scene, "the scene references the polling prefab")
expect("agent-test-game" in scene and "Agent Test Game" in scene, "the Game ID or name is not in the scene")
expect(r["created"].count("GameObject Hypeline Handlers") == 1 and r["created"].count("PlayerHealth on Hypeline Handlers") == 1, "the shared handler object was made more than once")
import re
bound = re.findall(r"m_MethodName\s+value: (\w+)", scene)
expect(sorted(bound) == ["OnHealthpack", "OnHealthpack", "OnShield", "OnShield"], "bound methods = %s" % bound)'

# What the job left out of an action gets the documented defaults.
SCENE="$PROJECT/Assets/Scenes/Main.unity" python3 - <<'EOF'
import os, re, sys
scene = open(os.environ["SCENE"]).read()
def field(action, name):
    # Prefab overrides are stored as propertyPath/value pairs.
    index = re.search(r"propertyPath: GameActions\.Array\.data\[(\d+)\]\.ID\s+value: %s\s" % action, scene)
    if not index:
        sys.exit("FAIL defaults: action %s is not in the scene" % action)
    value = re.search(r"propertyPath: GameActions\.Array\.data\[%s\]\.%s\s+value: (\S*)" % (index.group(1), name), scene)
    return value.group(1) if value else None
expected = {
    ("healthpack", "Impact"): "2", ("healthpack", "Count"): "25", ("healthpack", "Category"): "2", ("healthpack", "State"): "1",
    ("wave", "Impact"): "1", ("wave", "Count"): "65535", ("wave", "State"): "1",
}
for (action, name), want in expected.items():
    got = field(action, name)
    # A value equal to the prefab's own default is not stored as an override.
    if got is not None and got != want:
        sys.exit("FAIL defaults: %s.%s = %s, want %s" % (action, name, got, want))
if field("wave", "Count") is None and field("healthpack", "Count") is None:
    sys.exit("FAIL defaults: no Count override was stored for either action")
print("PASS defaults")
EOF

# Running the same job again changes nothing.
AFTER=$(checksum)
job again "$(cat "$WORK/setup.job.json")"
check again 0 '
expect(r["ok"], "errors = %s" % errors)
expect(r["created"] == [] and r["changed"] == [], "a second run changed things: %s %s" % (r["created"], r["changed"]))
expect(not r["sceneSaved"], "a second run saved the scene")'
[ "$AFTER" = "$(checksum)" ] || { echo "FAIL again: the scene file changed on a second run" >&2; exit 1; }

# A later job updates what it names and leaves the rest.
job update '{"tool":"setup","scene":"Assets/Scenes/Main.unity","stage":"production","addPolling":true,
  "texts":[{"label":"Health","value":"60"}],"actions":[{"id":"wave","state":"Unavailable"}]}'
check update 0 '
expect(r["ok"], "errors = %s" % errors)
expect(r["created"] == ["MuxyHypelinePolling"], "created = %s" % r["created"])
expect(sorted(r["changed"]) == ["Action wave", "Game Text Health", "Manager Stage = Production"], "changed = %s" % r["changed"])
expect(r["gameId"] == "agent-test-game", "the Game ID changed: %s" % r["gameId"])
expect("3d3fd97c690c5b19b8eba259d51cfc67" in scene, "the polling prefab was not added")
expect("fa-solid:heart" in scene, "an icon the job did not mention was cleared")'

# The doctor is satisfied with what setup made, and changes nothing.
AFTER=$(checksum)
job doctor '{"tool":"doctor","scene":"Assets/Scenes/Main.unity","offline":true}'
check doctor 0 '
expect(r["ok"] and errors == [], "errors = %s" % errors)
expect(r["gameId"] == "agent-test-game" and r["gameIdStatus"] == "not_checked" and r["stage"] == "production", "unexpected report")
warnings = [f["code"] for f in r["findings"] if f["severity"] == "warning"]
expect(warnings == [], "warnings = %s" % warnings)
expect("action_without_handler" in codes, "the action with no handler was not mentioned")'
[ "$AFTER" = "$(checksum)" ] || { echo "FAIL doctor: the doctor changed the scene file" >&2; exit 1; }

# A handler whose method no longer exists is an error, though its object and a
# method name are still stored.
cp "$PROJECT/Assets/Scenes/Main.unity" "$WORK/Main.unity.good"
SCENE="$PROJECT/Assets/Scenes/Main.unity" python3 - <<'EOF'
import os, re
path = os.environ["SCENE"]
scene = open(path).read()
changed, count = re.subn(r"(m_MethodName\s+value: )OnShield$", r"\1OnShieldRenamedAway", scene, flags=re.M)
assert count == 2, "expected the two OnShield bindings, found %d" % count
open(path, "w").write(changed)
EOF
job doctorbroken '{"tool":"doctor","scene":"Assets/Scenes/Main.unity","offline":true}'
check doctorbroken 1 '
expect(not r["ok"], "a scene with handlers that cannot run was reported ok")
expect(errors == ["action_handler_broken", "action_handler_broken"], "errors = %s" % errors)
expect("action_many_handlers" not in codes, "a handler that cannot run was counted as a working one")'

# Binding the action again replaces the handler that cannot run.
job rebind '{"tool":"setup","scene":"Assets/Scenes/Main.unity","actions":[
  {"id":"shield","handler":{"gameObject":"Player","component":"PlayerHealth","method":"OnShield"}},
  {"id":"storm","handler":{"gameObject":"Hypeline Handlers","component":"PlayerHealth","method":"OnShield"}}]}'
check rebind 0 '
expect(r["ok"], "errors = %s" % errors)
expect(sum("removed a handler that could not run" in c for c in r["changed"]) == 2, "changed = %s" % r["changed"])
expect(sum("calls PlayerHealth.OnShield" in c for c in r["changed"]) == 2, "changed = %s" % r["changed"])
import re
bound = re.findall(r"m_MethodName\s+value: (\w+)", scene)
expect(sorted(bound) == ["OnHealthpack", "OnHealthpack", "OnShield", "OnShield"], "bound methods = %s" % bound)'

job doctorrepaired '{"tool":"doctor","scene":"Assets/Scenes/Main.unity","offline":true}'
check doctorrepaired 0 'expect(r["ok"] and errors == [], "errors = %s" % errors)'

# With the lookup on, an ID that is not registered is an error. The lookup needs
# the network and the portal's public lookup, so no answer is noted rather than
# failed.
job doctoronline '{"tool":"doctor","scene":"Assets/Scenes/Main.unity"}'
REPORT="$WORK/doctoronline.report.json" python3 - "$STATUS" <<'EOF'
import json, os, sys
r = json.load(open(os.environ["REPORT"]))
errors = [f["code"] for f in r["findings"] if f["severity"] == "error"]
if r["gameIdStatus"] == "not_registered":
    if errors != ["game_id_not_registered"] or sys.argv[1] != "1":
        sys.exit("FAIL doctoronline: errors = %s, exit code %s" % (errors, sys.argv[1]))
    print("PASS doctoronline")
elif r["gameIdStatus"] == "unknown":
    print("NOTE doctoronline: the portal gave no Game ID record, so the lookup result was not exercised")
else:
    sys.exit("FAIL doctoronline: gameIdStatus = %s" % r["gameIdStatus"])
EOF

echo "Agent tool checks finished."
