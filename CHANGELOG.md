[Unreleased]
Security and reliability
- Connect to production and sandbox over `wss://` with normal certificate validation. Previously both stages used unencrypted `ws://`, which sent PINs, session tokens and refresh tokens in cleartext. Custom endpoints may use `ws://` only for loopback test servers.
- Replace the transport with the Gateway Unity 1.1.0-rc.1 implementation: tracked send/receive tasks, bounded reconnect backoff, 16 MiB message limit, and native calls only from `SDK.Update()` on the main thread.
- Add `SDK.Dispose()`, `RunInProductionAsync`/`RunInSandboxAsync`, `StopWebsocketTransportAsync`, `IsConnected` and `LastConnectionError`. The prefabs dispose their SDK on disable and reset instead of leaving it to the finalizer.
- Native debug logging is off by default (`SDK.DebugLoggingEnabled`); authentication payloads are still redacted when it is on.
- Normalise PINs to upper case before sending; the server only consumes the upper-case code.
- `Deauthenticate()` now sends the logout message before closing the socket, and the manager is ready for a new code afterwards.
- Contain exceptions thrown by authentication, action, Bits and poll callbacks instead of letting them cross the native boundary. Correct the `MonoPInvokeCallback` type on the action callback for IL2CPP.
- Keep action callbacks per SDK instance instead of in a static map.
- Validate actions (IDs, duplicates, Impact 0–5, Count 0–`InfiniteCount`), polls (at most 10 options, prompt ≤256 and options ≤128 UTF-8 bytes) and metadata limits before calling native code, which drops oversized requests silently. Null strings are sent as empty strings instead of null pointers.
- `SDK.SetGameLogo()` resends the last name and theme so they are no longer cleared.
- The manager forwards poll updates to its `OnPollUpdate` event, adds a `Stage` setting, and publishes texts, actions and metadata only after sign-in.
- The guide no longer tells developers to use a per-game ID; the service currently accepts only `gamelink-testing`.

- Make the in-game viewer-action alert a required setup, local preview, live-action checkpoint, and gameplay-adaptation requirement in steps 1–7 of the getting-started guide.
- Include a six-second viewer/action notification in the C# integration sample using the existing fulfilled healthpack handler.
- Present the Unity package as Muxy Hypeline. **Breaking:** the API namespace is now `MuxyHypeline` (was `MuxyGateway`) and the assembly is `Muxy.Hypeline` (was `Muxy.Gateway`). Update `using` directives and any assembly-definition references by name; references by GUID keep working.
- Retain the package ID, prefab and script GUIDs, saved-token key, and backend connection identifiers for existing integrations.
- Preserve reflection-loaded Mono TLS configuration types in stripped player builds through an Editor-only linker callback.
- Expand all 13 target-typed constructors for Unity 2020.3's C# 8 compiler.

- Replace bundled TextMesh Pro UI with uGUI text and input components; remove the TextMesh Pro package dependency. Existing customized scene instances may require migration.
- Preserve UTF-8 marshalling attributes without relying on the LPUTF8Str enum name missing from Unity 2020.3's .NET Standard 2.0 reference assemblies.
- Declare TextMeshPro, uGUI, and image-conversion dependencies. Newer Editors resolve their built-in TMP compatibility package and uGUI implementation.
- Add the explicit UnityEngine.UI assembly reference and isolate the UnityEditor import from standalone builds.
- Replace the beta minimum-version suffix with the intended Unity 2020.3.0f1 stable target.
- Preserve existing asset GUIDs.
- Add an isolated package-import/custom-assembly compilation harness and record scoped validation results.

[0.1.0] 2022/10/04
Initial release
