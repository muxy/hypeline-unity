# Muxy Hypeline for coding agents

This page is for a coding agent adding Hypeline to a Unity game on a developer's
behalf. With one approval from the developer you can do the integration yourself:

1. [Get access](#1-get-access) to the developer's Muxy account. They approve once in a
   browser.
2. [Register a Game ID](#2-register-a-game-id) for the game.
3. [Set up and check the scene](#3-set-up-and-check-the-scene) with the package's
   Editor tools, without the Unity Editor UI.
4. [Test on the developer's channel](#4-test-on-the-developers-channel): sign the game
   in and send it a viewer action yourself, at no cost and without opening Twitch.
5. [Set up the game's page](#5-set-up-the-games-page), if the developer wants you to:
   how the game looks and what it says in the extension, and its links to the same game
   on other platforms.

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
  -d '{"client_name": "Your agent name", "scopes": ["games:read", "games:create", "games:test"]}'
```

```json
{
  "device_code": "mxd_...",
  "user_code": "BCDF-GHJK",
  "verification_uri": "https://dev.muxy.io/agent/approve",
  "verification_uri_complete": "https://dev.muxy.io/agent/approve?code=BCDF-GHJK",
  "expires_in": 600,
  "interval": 5,
  "scope": "games:create games:read games:test"
}
```

| Scope | Lets you |
|---|---|
| `games:read` | List the developer's games, read their pages and platform links, and check whether a Game ID is available. |
| `games:create` | Register a Game ID in the developer's name. |
| `games:test` | Sign their games in on their own Twitch channel and send test viewer actions, at no cost. |
| `games:page` | Change a game's page in the extension and upload its images. |
| `games:links` | Link a game to the same game on Twitch and other platforms, and remove those links. |

Ask only for what the work needs. The first three cover the integration, and a request
that names no scopes gets exactly those. `games:page` and `games:links` change what
viewers see about the game, so ask for them only when the developer wants
[the game's page set up](#5-set-up-the-games-page), and name them in the request.

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
{ "access_token": "mxa_...", "token_type": "Bearer", "expires_in": 2592000, "scope": "games:create games:read games:test", "token_id": "tok_..." }
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
over a game; the developer does that at <https://dev.muxy.io/games>. What you can change
about a game is in [step 5](#5-set-up-the-games-page).

## 3. Set up and check the scene

The Hypeline Unity package includes two Editor tools you can run without the Unity
Editor UI:

- **setup** adds the Hypeline prefabs to a scene and wires them: the manager, the menu
  and the required interaction alert both referencing it, an EventSystem, and
  optionally Game Texts and viewer actions bound to your handler methods.
- **doctor** checks a scene's Hypeline setup and changes nothing.

Each takes a JSON job file and writes a JSON report. Use them instead of editing
`.unity` scene files by hand. They cover steps 2 and 3 of the guide (scene setup and
action configuration). Install the latest SDK and agent tooling from
`https://github.com/muxy/hypeline-unity.git#main` first, and let Unity compile it.
Record the resolved commit in your QA report, since `main` changes over time.

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
| `stage` | `"production"`. Leave it out: a game is tested and released on the same stage. |
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

## 4. Test on the developer's channel

With the `games:test` scope you can sign the game in and act as a viewer yourself. The
game runs against the same Hypeline backend as a released game, on the developer's own
Twitch channel: the Twitch account they approved you with. It shows that the game
receives an action, that your handler runs, and that the game's accept or refund reaches
Hypeline.

A test action costs nothing. It comes from a test viewer, `hypeline-test-viewer`, and is
recorded at zero: no coins are created or spent. The game is still told the price the
channel has set, as it is for a real viewer.

It does not show a real viewer using the Twitch extension, a broadcaster signing in from
the extension, or the alert on a stream. Those are the guide's steps 5 and 6, and they
are still required before release.

**Before you start**

- Leave the manager on the production stage. There is no separate test backend to switch
  to, and nothing to switch back afterwards.
- Tell the developer that the test runs on their Twitch channel. A game that signs in
  publishes its actions to that channel, replacing what the extension showed there
  before. Do not test while they are live on that channel with another game.
- The game must be running in Play mode. The package has no tool that enters Play mode
  for you. Use whatever control of the Editor you have, or ask the developer to press
  Play.

**Sign the game in**

```sh
curl -s -X POST https://dev.muxy.io/api/agent/test/my-game/pin -H "Authorization: Bearer $TOKEN"
```

```json
{ "pin": "ABC234", "expires_in": 300, "stage": "production", "game_id": "my-game", "channel_id": "123456789" }
```

`channel_id` is the developer's Twitch channel. The code works once, for five minutes.
The game signs in with it the way a broadcaster does: type it into the game's Hypeline
panel, or call `MuxyHypelineManager.AuthenticateWithPIN(pin)` from a script. If you
cannot do either, give the developer the code to enter.

**Wait for the game's actions**

```sh
curl -s https://dev.muxy.io/api/agent/test/my-game/state -H "Authorization: Bearer $TOKEN"
```

The game publishes its actions after it signs in. Ask every second or two until
`state.game.actions` lists them; if it stays empty for half a minute, the game did not
sign in (check its Console, and that the code had not expired).

**Use an action as a viewer**

```sh
curl -s -X POST https://dev.muxy.io/api/agent/test/my-game/actions -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"action_id": "healthpack"}'
```

```json
{ "transaction_id": "...", "action_id": "healthpack", "cost": 10, "user_id": "hypeline-test-viewer", "username": "TestViewer", "game_id": "my-game", "channel_id": "123456789" }
```

Add `"username"` to choose the viewer name the game is shown. The game receives the
action as it would from a real viewer: the handler runs and the interaction alert shows.
`cost` is the price the channel has set for the action, which is what the game is told;
nothing is charged. An action with a limited count loses one use, as it does for a real
viewer.

| `error_code` | Meaning |
|---|---|
| `unknown_action` | The game has not published an action with that ID. Check that it is signed in on the developer's channel. |
| `action_unavailable` | The game has published nothing yet, or marks the action unavailable or hidden. The `message` says which. |
| `no_uses_left` | The action's count has run out. |

**See what the game did with it**

```sh
curl -s https://dev.muxy.io/api/agent/test/my-game/transactions/TRANSACTION_ID -H "Authorization: Bearer $TOKEN"
```

```json
{ "transaction_id": "...", "action_id": "healthpack", "status": "accepted", "details": "..." }
```

`status` is `pending` until the game answers, then `accepted` (the handler called
`AcceptGameAction`) or `refunded` (`RefundGameAction`). Ask every second or two. If it
is still `pending` after about fifteen seconds, the handler did not run or did not
answer, which is a failed check: read the game's Console.

**Afterwards**

- Stop Play mode. Nothing needs switching back.
- Report what you checked as a test with a test viewer, separately from the live Twitch
  check.

Other answers: `twitch_account_required` (HTTP 409) means the developer's portal account
has no Twitch account linked, so ask them to sign in to the portal with Twitch;
`test_unavailable` (HTTP 503) means this portal does not offer game testing;
`test_error` (HTTP 502) means Hypeline could not be reached, so try again;
`rate_limited` (HTTP 429) means wait a minute. You can test games the developer owns or
administers, and the shared test ID `gamelink-testing`.

## 5. Set up the game's page

The developer portal holds the game's page: how the game looks and what it says about
itself in the Twitch extension, and its links to the same game on other platforms. The
developer can fill it in at <https://dev.muxy.io/games>. With the scopes below you can
do it for them.

This step is optional: the game works without it. Do it only if the developer asks.
Everything here is shown to viewers under the game's name, so agree the text, the links
and the images with the developer first, and use only images they give you or approve.

| To | You need |
|---|---|
| Read the page and the links | `games:read` |
| Change the page, upload its images | `games:page` |
| Add or remove platform links, search Twitch | `games:links` |

### Read and change the page

```sh
curl -s https://dev.muxy.io/api/games/my-game/page -H "Authorization: Bearer $TOKEN"
```

The page has these sections. A game that never saved one has the defaults.

| Section | Holds |
|---|---|
| `appearance` | `logo` and `background` (uploaded images, or `null`), `overlay_color` and `overlay_opacity` (0 to 100) over the background, and a color, font and weight for `primary_text_*`, `secondary_text_*` and `button_text_*`, plus `button_color`. |
| `tabs` | Which tabs the extension shows: `info`, `news`, `gallery`, `key_features`. |
| `info` | `about`, `game_type`, `platforms`, and `website_url` and `steam_url`, each shown only while its `show_website_link` or `show_steam_link` is `true`. |
| `news` | `steam_url`: the game's Steam store page or community hub, whose Steam news the News tab shows. |
| `gallery` | The gallery images, in order. |
| `key_features` | `html`: the text of the Key Features tab. |
| `fonts` | The fonts a page may use. Read only. |

Change it with a `PATCH` that names only what changes. Fields you leave out keep their
values:

```sh
curl -s -X PATCH https://dev.muxy.io/api/games/my-game/page -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{
    "appearance": { "button_color": "FFAA00", "primary_text_font": "Roboto", "primary_text_weight": "regular" },
    "tabs": { "gallery": true },
    "info": { "about": "Step into the boots of an elite operative.", "game_type": "FPS",
              "show_website_link": true, "website_url": "https://example.com/my-game" },
    "news": { "steam_url": "https://store.steampowered.com/app/440" },
    "key_features": { "html": "<h1>Key Features</h1><p>Destructible cover.</p>" }
  }'
```

The answer is the whole page as stored. An edit is applied whole or not at all: if one
field is refused, nothing in the request is saved.

- Colors are six hex digits, such as `FFAA00`. A weight is `regular` or `bold`. A font
  is one of `fonts`.
- `about` is plain text of up to 2000 characters; `game_type` up to 64 and `platforms`
  up to 128, on one line.
- Links start with `https://`. `steam_url` must be a Steam link. `null` or `""` clears a
  link.
- Key features may use headings (`h1`, `h2`), paragraphs, line breaks, lists, bold,
  italic, underline and a text color (`<span style="color: #FFAA00">`). Anything else is
  removed, and the answer shows what was kept.
- The News tab only shows once `news.steam_url` is set. `GET
  /api/games/my-game/page/news` shows the news it would list.

| `error_code` | Meaning |
|---|---|
| `invalid_field` | A field was refused. `field` names it, such as `info.website_url`, and `message` says what it must be. |
| `asset_not_found` | An image id is not an upload of this game, or was uploaded as another kind. |
| `limit_reached` | Too many gallery images (12 at most). |
| `rate_limited` | Too many changes of the Steam link. Wait a minute. |

### Upload images

Upload an image as multipart form data, saying what it is for:

```sh
curl -s -X POST https://dev.muxy.io/api/games/my-game/assets -H "Authorization: Bearer $TOKEN" \
  -F kind=logo -F file=@/path/to/logo.png
```

```json
{ "asset_id": "0123456789abcdef0123456789abcdef", "kind": "logo", "file_name": "logo.png", "mime_type": "image/png", "size": 20480, "width": 600, "height": 240, "url": "https://..." }
```

`kind` is `logo`, `background` or `gallery`. An image is a PNG, JPEG or GIF of at most
2 MB and 4096 pixels on a side. Uploading does not change the page. Put the image to
use by its `asset_id`, of the kind it was uploaded as:

```sh
curl -s -X PATCH https://dev.muxy.io/api/games/my-game/page -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{ "appearance": { "logo_asset_id": "0123...", "background_asset_id": "4567..." },
       "gallery": { "asset_ids": ["89ab...", "cdef..."] } }'
```

`gallery.asset_ids` is the whole gallery in display order: send the full list each time.
`null` takes a logo or background off. A game holds up to 60 uploads, and uploads that
nothing uses are cleared after an hour.

| `error_code` | Meaning |
|---|---|
| `invalid_image` | Not a PNG, JPEG or GIF, or larger than 4096 pixels on a side. |
| `file_too_large` | Over 2 MB. |
| `limit_reached` | The game holds 60 uploads. Unused ones are cleared after an hour. |
| `uploads_unavailable` | This portal cannot store images right now. Tell the developer. |

### Link the game on other platforms

A link says that this game is the same game as a Twitch category, or a game on IGDB,
Giant Bomb or TikTok. Anyone with access to the game can add a link; Muxy reviews it
before it is used anywhere other people see. A link you add is reviewed the same way.

```sh
# Find the Twitch category by name.
curl -s "https://dev.muxy.io/api/external/twitch/search?q=my+game" -H "Authorization: Bearer $TOKEN"

# The game's links.
curl -s https://dev.muxy.io/api/games/my-game/external-ids -H "Authorization: Bearer $TOKEN"

# Add one. "input" is the id on that platform.
curl -s -X POST https://dev.muxy.io/api/games/my-game/external-ids -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"provider": "twitch", "input": "509658"}'

# Remove one.
curl -s -X DELETE https://dev.muxy.io/api/games/my-game/external-ids/twitch/509658 -H "Authorization: Bearer $TOKEN"
```

`provider` is `twitch` (the category id from the search), `igdb` (the numeric game id),
`giantbomb` (the game page URL or its GUID, such as `3030-41484`) or `tiktok` (the game's
name as TikTok LIVE shows it). A new link has the `status` `claimed` until Muxy reviews
it, then `verified` or `rejected`. Linking a Twitch category also adds the IGDB game
Twitch maps it to. A game can have three links per platform and eight in all.

Link only the platform pages the developer confirms are this game. Do not guess from a
similar name.

| `error_code` | Meaning |
|---|---|
| `invalid_provider`, `invalid_external_id` | Not a platform or an id of the kind above. |
| `external_id_not_found` | That platform has no game with this id. |
| `already_linked` | The game already has this link. |
| `already_verified_elsewhere` | Another game holds the verified link to it. Tell the developer. |
| `limit_reached` | Three links per platform, eight in all. |
| `provider_unavailable`, `provider_not_configured` | The platform could not be asked. Nothing was saved; try again later. |

### What stays with the developer

Renaming a game, its administrators, archiving and restoring it, and its insights are
not open to agents: those requests answer `token_not_allowed`. The developer does them
at <https://dev.muxy.io/games>.

## A typical run

1. Get access (one approval from the developer) and agree the Game ID with them.
2. Register the Game ID, or pick the one they already have.
3. Install the package from `https://github.com/muxy/hypeline-unity.git#main` and let
   Unity compile it. Record the resolved commit.
4. Write the game's handler component and let Unity compile it.
5. Run a setup job naming the scene, the Game ID, and the actions with their handlers
   (`"addIfMissing": true` attaches the component for you).
6. Run a doctor job and resolve its errors and warnings.
7. With the game in Play mode, sign it in on the developer's channel, send each action
   as the test viewer, and confirm each is accepted. Check the refund path too if the
   handler has one.
8. If the developer asked for it, set up the game's page and its platform links, with
   content they have approved.
9. Report what you did and what you verified, and what is left for the developer:
   positioning the menu and alert, and the live Twitch check in the guide's steps 5 and 6.
