# Muxy Hypeline for coding agents

The Hypeline Unity package includes two Editor tools you can run without the Unity
Editor UI:

- **setup** adds the Hypeline prefabs to a scene and wires them: the manager, the menu
  and the required interaction alert both referencing it, an EventSystem, and
  optionally Game Texts and viewer actions bound to your handler methods.
- **doctor** checks a scene's Hypeline setup and changes nothing.

Each takes a JSON job file and writes a JSON report. Use them instead of editing
`.unity` scene files by hand.

They cover steps 2 and 3 of the [getting-started guide](https://muxy.github.io/hypeline-unity/)
(scene setup and action configuration). They do not install the package, sign in to
Twitch, or prove that a real viewer action reaches the game; the guide's steps 5 and 6
still apply.

## Run a job

How you run a job depends on whether the project is open in the Unity Editor. Unity
keeps `Temp/UnityLockfile` in the project folder while it is open, and a second Unity
process cannot open the same project.

**Project closed:** run Unity in batch mode.

```sh
Unity -batchmode -nographics -projectPath /path/to/project \
  -executeMethod MuxyHypeline.HypelineAgent.Run \
  -hypelineJob /path/to/job.json -hypelineReport /path/to/report.json \
  -logFile /path/to/unity.log
```

Do not pass `-quit`; the tool exits by itself. The exit code is `0` when the report is
ok, `1` when it has errors, and `2` when the job file could not be read. Any other exit
code means Unity itself failed (for example a compile error or a missing license); read
the log file.

**Project open in the Editor:** write the job to
`Library/Hypeline/jobs/<name>.job.json` in the project folder. The Editor runs it
within a few seconds, writes `Library/Hypeline/jobs/<name>.report.json`, and deletes
the job file. Wait for the report file. A job waits while the Editor is compiling,
importing, in Play mode, or showing a dialog. If no report appears after a minute, ask
the developer to bring the Editor to the front and close any dialog it is showing.

## Setup job

```json
{
  "tool": "setup",
  "scene": "Assets/Scenes/Main.unity",
  "gameId": "my-game",
  "gameName": "My Game",
  "stage": "production",
  "texts": [
    { "label": "Health", "value": "50", "icon": "fa-solid:heart" }
  ],
  "actions": [
    {
      "id": "healthpack",
      "name": "Spawn Healthpack",
      "description": "Add up to 10 health to the player.",
      "category": "Help",
      "state": "Available",
      "impact": 1,
      "count": 65535,
      "icon": "fa-solid:heart",
      "handler": { "gameObject": "Player", "component": "PlayerHealth", "method": "OnHealthpack" }
    }
  ]
}
```

Only `tool` is required. Everything else is optional:

| Field | Meaning |
|---|---|
| `scene` | Scene asset path. Left out: the active scene in an open Editor, or the first enabled build scene in batch mode. |
| `gameId` | The game's ID from <https://dev.muxy.io/games>. Left out: the manager keeps its current ID (a new manager uses the shared test ID `gamelink-testing`). |
| `gameName` | The name viewers see in the extension. |
| `stage` | `"production"` or `"sandbox"`. |
| `texts` | Game Texts, matched by `label`. A new label is added; an existing one is updated. |
| `actions` | Viewer actions, matched by `id`. A new ID is added; an existing one is updated. |
| `skipMenu`, `skipAlert`, `skipEventSystem` | `true` to not add that object. The alert is a required part of the integration; skip it only if the game shows viewer actions another way. |
| `addPolling` | `true` to add the bundled poll display. |
| `allowDirtyScene` | Open Editor only: `true` to save a scene that already has unsaved changes. Without it, setup stops rather than save the developer's work in progress. |

Action fields: `category` is `Neutral`, `Hinder` or `Help`; `state` is `Available`,
`Unavailable` or `Hidden`; `impact` is 1 to 5; `count` is how many times viewers can use
the action, with 65535 meaning unlimited. A new action defaults to `Neutral`,
`Available`, impact 1 and unlimited. For an action that already exists, a field you
leave out keeps its current value.

A `handler` binds the action to a method, as the Inspector's dynamic
`On Game Action Used` binding does. The method must be public, return `void` and take
one `MuxyHypeline.GameActionUsed`:

```csharp
public void OnHealthpack(MuxyHypeline.GameActionUsed used)
```

`gameObject` is the object's name or its hierarchy path (`World/Player`), and
`component` is the component's type name. Both must exist in the saved scene, unless
the handler sets `"addIfMissing": true`: setup then creates a root GameObject with that
name if the scene has none, and adds the component if the object does not have it. That
lets you write a handler script and attach it in the same job:

```json
"handler": { "gameObject": "Hypeline Handlers", "component": "HealthpackHandler", "method": "OnHealthpack", "addIfMissing": true }
```

The script must have compiled before the job runs. The handler should apply the effect,
then call `AcceptGameAction` on the manager, or `RefundGameAction` if it could not; see
the guide's step 7.

Setup behaves like this:

- It checks the whole job first. If anything in it is wrong, it reports the errors and
  changes nothing.
- It reuses what the scene already has. It never adds a second manager, menu, alert or
  EventSystem, and it never removes anything.
- The prefabs are added at the scene root with their default layout. Position the menu
  button and the alert for the game afterwards; the guide's step 2 says what to check.
- Running the same job again changes nothing and does not save the scene.
- A new EventSystem gets the input module that matches the project's Active Input
  Handling.

## Doctor job

```json
{ "tool": "doctor", "scene": "Assets/Scenes/Main.unity" }
```

The doctor reports, among other things: no manager or more than one active; an invalid,
unregistered, suspended or archived Game ID; a menu or alert that does not reference the
manager; a missing or mismatched EventSystem input module; and actions with duplicate
IDs or broken handlers. It looks the Game ID up on the developer portal unless the job
sets `"offline": true`.

## Report

```json
{
  "tool": "setup",
  "ok": true,
  "unityVersion": "6000.4.4f1",
  "packageVersion": "0.2.0",
  "packageSource": "Git",
  "inputHandling": "both",
  "scene": "Assets/Scenes/Main.unity",
  "gameId": "my-game",
  "gameIdStatus": "",
  "stage": "production",
  "created": ["MuxyHypelineManager", "MuxyHypelineMenu", "MuxyHypelineInteractionAlert", "Action healthpack"],
  "changed": ["Manager Game ID = my-game", "Action healthpack calls PlayerHealth.OnHealthpack on Player"],
  "sceneSaved": true,
  "findings": [
    { "severity": "warning", "code": "shared_test_id", "message": "...", "fix": "..." }
  ]
}
```

`ok` is `true` when no finding has the severity `error`. Each finding has a stable
`code`, a `message`, and a `fix` saying what to do. `created`, `changed` and `sceneSaved`
are filled by setup; `gameIdStatus` is filled by the doctor (`active`, `suspended`,
`archived`, `not_registered`, `unknown` or `not_checked`). Report each finding you do
not resolve to the developer rather than hiding it.

## A typical run

1. Install the package from the Git URL in the guide and let Unity compile it.
2. Write the game's handler component and let Unity compile it.
3. Run a setup job naming the scene, the Game ID, and the actions with their handlers
   (`"addIfMissing": true` attaches the component for you).
4. Run a doctor job and resolve its errors and warnings.
5. Continue with the guide: connect the Twitch channel and verify a real viewer action,
   including the visible in-game alert.
