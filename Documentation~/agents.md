# Muxy Hypeline for coding agents

This page is for a coding agent adding Hypeline to a Unity game on a developer's
behalf. With one approval from the developer you can do the integration yourself:

1. [Get access](#1-get-access) to the developer's Muxy account. They approve once in a
   browser.
2. [Register a Game ID](#2-register-a-game-id) for the game.
3. [Set up and check the scene](#3-set-up-and-check-the-scene) with the package's
   Editor tools, without the Unity Editor UI.
4. [Test in the sandbox](#4-test-in-the-sandbox): sign the game in and send it a
   viewer action, without a Twitch channel.

The [getting-started guide](https://muxy.github.io/hypeline-unity/) is the reference for
the integration itself: installing the package, what each prefab does, writing an action
handler, and the checks before release. Going live still takes a person: a broadcaster
installs the Twitch extension and signs in from inside the game (the guide's steps 5 and
6), and that live check is not replaced by anything here.

The developer portal is `https://dev.muxy.io`. Its API answers with JSON. If a request
on this page gets an HTML page back instead, that portal does not offer the feature yet:
tell the developer, and fall back to the guide (they create the Game ID at
<https://dev.muxy.io/games> and give it to you).

## 1. Get access

Ask for access with a name the developer will recognise and the scopes you need:

```sh
curl -s -X POST https://dev.muxy.io/api/agent/device \
  -H 'Content-Type: application/json' \
  -d '{"client_name": "Your agent name", "scopes": ["games:read", "games:create", "sandbox:test"]}'
```

```json
{
  "device_code": "mxd_...",
  "user_code": "BCDF-GHJK",
  "verification_uri": "https://dev.muxy.io/agent/approve",
  "verification_uri_complete": "https://dev.muxy.io/agent/approve?code=BCDF-GHJK",
  "expires_in": 600,
  "interval": 5,
  "scope": "games:create games:read sandbox:test"
}
```

| Scope | Lets you |
|---|---|
| `games:read` | List the developer's games and check whether a Game ID is available. |
| `games:create` | Register a Game ID in the developer's name. |
| `sandbox:test` | Sign their games in to the sandbox and send test viewer actions. |

Show the developer `verification_uri_complete` and the `user_code`, and ask them to open
the link, check that the code matches, and approve. They sign in with Twitch, and accept
the Hypeline terms if you asked for `games:create`. You cannot accept the terms for them.

Then ask for your token every `interval` seconds, until the request expires:

```sh
curl -s -X POST https://dev.muxy.io/api/agent/token \
  -H 'Content-Type: application/json' \
  -d '{"device_code": "mxd_..."}'
```

Until the developer approves, the answer is HTTP 400 with an `error`:

| `error` | Meaning |
|---|---|
| `authorization_pending` | Not approved yet. Keep asking. |
| `slow_down` | You asked too soon. Wait at least `interval` seconds between requests. |
| `access_denied` | The developer denied the request. Stop, and ask them what they want. |
| `expired_token` | Ten minutes passed. Start again with a new request. |
| `invalid_grant` | Unknown device code, or its token was already issued. |

Once approved, you get the token, once:

```json
{ "access_token": "mxa_...", "token_type": "Bearer", "expires_in": 2592000, "scope": "games:create games:read sandbox:test", "token_id": "tok_..." }
```

Send it as `Authorization: Bearer mxa_...` on the requests below.

- The token is the developer's access. Keep it out of source files, commits, logs and
  your reports. Hold it in memory or the operating system's credential store.
- It lasts 30 days, and the developer can remove it at any time at
  <https://dev.muxy.io/agent>. `GET /api/agent/whoami` tells you whether it still works
  and which scopes it has.
- HTTP 401 with `"error_code": "invalid_token"` means it expired or was removed: start
  again. HTTP 403 with `insufficient_scope` means you need a scope you did not ask for.
  `token_not_allowed` means that request is not open to agents.
- Errors from here on have the shape `{"error_code": "...", "message": "..."}`.

## 2. Register a Game ID

A Game ID is what the game sends when it connects. **It is permanent: it can never be
renamed or reused.** Agree the ID and the game's name with the developer before you
create it, and reuse an ID they already have rather than making another.

```sh
# The developer's games.
curl -s https://dev.muxy.io/api/games -H "Authorization: Bearer $TOKEN"

# Is an ID free? "reason" is "", "invalid", "reserved" or "taken".
curl -s "https://dev.muxy.io/api/games/availability?game_id=my-game" -H "Authorization: Bearer $TOKEN"

# Register it. Answers 201 with the game.
curl -s -X POST https://dev.muxy.io/api/games -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"game_id": "my-game", "name": "My Game"}'
```

A Game ID is 3 to 64 lowercase letters, numbers and single hyphens, starting and ending
with a letter or number. A new ID works straight away.

| `error_code` | Meaning |
|---|---|
| `invalid_game_id`, `reserved_game_id`, `game_id_taken` | Choose another ID with the developer. |
| `invalid_name` | The name must be 1 to 128 characters. |
| `terms_not_accepted` | The Hypeline terms changed since the developer approved you. Ask them to approve again (step 1). |
| `creation_limit` | The developer has created five games in 24 hours. Try again later. |

You can read a game (`GET /api/games/{game_id}`), but you cannot rename, archive or hand
over a game; the developer does that at <https://dev.muxy.io/games>.

## 3. Set up and check the scene

The Hypeline Unity package includes two Editor tools you can run without the Unity
Editor UI:

- **setup** adds the Hypeline prefabs to a scene and wires them: the manager, the menu
  and the required interaction alert both referencing it, an EventSystem, and
  optionally Game Texts and viewer actions bound to your handler methods.
- **doctor** checks a scene's Hypeline setup and changes nothing.

Each takes a JSON job file and writes a JSON report. Use them instead of editing
`.unity` scene files by hand. They cover steps 2 and 3 of the guide (scene setup and
action configuration). Install the package first, from the Git URL in the guide, and let
Unity compile it.

### Run a job

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

### Setup job

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
- Binding an action whose existing handler can no longer run, for example because its
  method was renamed, replaces that handler instead of adding a second one.
- A new EventSystem gets the input module that matches the project's Active Input
  Handling.

### Doctor job

```json
{ "tool": "doctor", "scene": "Assets/Scenes/Main.unity" }
```

The doctor reports, among other things: no manager or more than one active; an invalid,
unregistered, suspended or archived Game ID; a menu or alert that does not reference the
manager; a missing or mismatched EventSystem input module; and actions with duplicate
IDs or with a handler that cannot run (its object is gone, or it no longer has a method
with the bound name and parameter). It looks the Game ID up on the developer portal unless the job
sets `"offline": true`.

### Report

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

## 4. Test in the sandbox

The sandbox is a separate Hypeline backend for testing. With the `sandbox:test` scope
you can sign the game in and act as a viewer, on a test channel that belongs to this
developer and game. It shows that the game receives an action, that your handler runs,
and that the game's accept or refund reaches Hypeline.

It does not show the Twitch extension, a real broadcaster's sign-in, or the alert on a
stream. Those are the guide's steps 5 and 6, and they are still required before release.

**Before you start**

- Set the manager's stage to the sandbox: a setup job with `"stage": "sandbox"`.
- The game must be running in Play mode. The package has no tool that enters Play mode
  for you. Use whatever control of the Editor you have, or ask the developer to press
  Play.

**Sign the game in**

```sh
curl -s -X POST https://dev.muxy.io/api/agent/sandbox/my-game/pin -H "Authorization: Bearer $TOKEN"
```

```json
{ "pin": "ABC234", "expires_in": 300, "stage": "sandbox", "game_id": "my-game", "channel_id": "hypeline-test-..." }
```

The code works once, for five minutes. The game signs in with it the way a broadcaster
does: type it into the game's Hypeline panel, or call
`MuxyHypelineManager.AuthenticateWithPIN(pin)` from a script. If you cannot do either,
give the developer the code to enter.

**Wait for the game's actions**

```sh
curl -s https://dev.muxy.io/api/agent/sandbox/my-game/state -H "Authorization: Bearer $TOKEN"
```

The game publishes its actions after it signs in. Ask every second or two until
`state.game.actions` lists them; if it stays empty for half a minute, the game did not
sign in (check its Console, the stage, and that the code had not expired).

**Use an action as a viewer**

```sh
curl -s -X POST https://dev.muxy.io/api/agent/sandbox/my-game/actions -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"action_id": "healthpack"}'
```

```json
{ "transaction_id": "...", "action_id": "healthpack", "cost": 10, "user_id": "hypeline-test-viewer", "username": "TestViewer", "game_id": "my-game", "channel_id": "hypeline-test-..." }
```

Add `"username"` to choose the viewer name the game is shown. The game receives the
action as it would from a real viewer: the handler runs and the interaction alert shows.

| `error_code` | Meaning |
|---|---|
| `unknown_action` | The game has not published an action with that ID. Check that it signed in with this game's code. |
| `action_unavailable` | The game has published nothing yet, or marks the action unavailable or hidden. The `message` says which. |
| `no_uses_left` | The action's count has run out. |

**See what the game did with it**

```sh
curl -s https://dev.muxy.io/api/agent/sandbox/my-game/transactions/TRANSACTION_ID -H "Authorization: Bearer $TOKEN"
```

```json
{ "transaction_id": "...", "action_id": "healthpack", "cost": 10, "status": "accepted", "details": "..." }
```

`status` is `pending` until the game answers, then `accepted` (the handler called
`AcceptGameAction`) or `refunded` (`RefundGameAction`). Ask every second or two. If it
is still `pending` after about fifteen seconds, the handler did not run or did not
answer, which is a failed check: read the game's Console.

**Afterwards**

- Set the stage back with a setup job: `"stage": "production"`. The doctor reports a
  manager left on the sandbox.
- Report what you checked as a sandbox check, separately from the live Twitch check.

Other answers: `sandbox_unavailable` (HTTP 503) means this portal does not offer sandbox
testing; `sandbox_error` (HTTP 502) means the sandbox could not be reached, so try
again; `rate_limited` (HTTP 429) means wait a minute. You can test games the developer
owns or administers, and the shared test ID `gamelink-testing`.

## A typical run

1. Get access (one approval from the developer) and agree the Game ID with them.
2. Register the Game ID, or pick the one they already have.
3. Install the package from the Git URL in the guide and let Unity compile it.
4. Write the game's handler component and let Unity compile it.
5. Run a setup job naming the scene, the Game ID, the sandbox stage, and the actions
   with their handlers (`"addIfMissing": true` attaches the component for you).
6. Run a doctor job and resolve its errors and warnings.
7. With the game in Play mode, sign it in to the sandbox, send each action, and confirm
   each is accepted. Check the refund path too if the handler has one.
8. Set the stage back to production and run the doctor again.
9. Report what you did and what you verified, and what is left for the developer:
   positioning the menu and alert, and the live Twitch check in the guide's steps 5 and 6.
