using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace MuxyHypeline
{
    /// <summary>
    /// Checks a scene's Hypeline setup against the getting-started guide and reports what it
    /// finds. It changes nothing.
    /// </summary>
    internal static class HypelineDoctor
    {
        private const int GameIdLookupTimeoutSeconds = 10;

        [Serializable]
        private sealed class PublicGameResponse
        {
            public string status = "";
            public bool verified = false;
        }

        public static void Run(HypelineAgentJob job, HypelineReport report, bool batch)
        {
            Scene scene;
            bool closeAfter;
            if (!HypelineAgent.OpenScene(job, report, batch, out scene, out closeAfter)) return;

            try
            {
                Check(job, report, scene);
            }
            finally
            {
                if (closeAfter) EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void Check(HypelineAgentJob job, HypelineReport report, Scene scene)
        {
            bool inBuild = false;
            foreach (EditorBuildSettingsScene buildScene in EditorBuildSettings.scenes)
                if (buildScene.enabled && buildScene.path == scene.path) inBuild = true;
            if (!inBuild)
                report.Info("scene_not_in_build", scene.path + " is not an enabled scene in the build settings.", "Add it before building a player, if this is a scene the game loads.");

            List<MuxyHypelineManager> managers = HypelineAgent.FindInScene<MuxyHypelineManager>(scene);
            List<MuxyHypelineManager> active = managers.FindAll(delegate (MuxyHypelineManager m) { return m.isActiveAndEnabled; });

            if (managers.Count == 0)
            {
                report.Error("no_manager", "The scene has no Hypeline manager.", "Run the setup tool on this scene, or add the MuxyHypelineManager prefab.");
                return;
            }
            if (active.Count == 0)
            {
                report.Error("manager_inactive", "The scene's Hypeline manager " + HypelineAgent.PathOf(managers[0].gameObject) + " is inactive or disabled.", "Activate it.");
                return;
            }
            if (active.Count > 1)
                report.Error("multiple_managers", "The scene has " + active.Count + " active Hypeline managers.", "Keep one active manager and remove or disable the others.");

            MuxyHypelineManager manager = active[0];
            SerializedObject serialized = new SerializedObject(manager);

            CheckGameId(job, report, serialized);
            report.stage = ((Stage)serialized.FindProperty("Stage").enumValueIndex).ToString().ToLowerInvariant();
            if (report.stage == "sandbox" || report.stage == "staging")
                report.Info(report.stage + "_stage", "The manager connects to " + report.stage + ".", "Set its Stage to Production before release.");

            if (string.IsNullOrWhiteSpace(serialized.FindProperty("GameMetadata").FindPropertyRelative("Name").stringValue))
                report.Warning("no_game_name", "The manager has no game name, so viewers cannot identify the game in the extension.", "Set Game Metadata > Name on the manager.");

            CheckActions(report, manager, serialized);
            CheckFollower<MuxyHypelineMenu>(report, scene, manager, "menu", "MuxyHypelineMenu",
                "Without it the broadcaster has no panel to enter a sign-in code, unless the game has its own.");
            MuxyHypelineInteractionAlert alert = CheckFollower<MuxyHypelineInteractionAlert>(report, scene, manager, "alert", "MuxyHypelineInteractionAlert",
                "The visible in-game alert for viewer actions is a required part of the integration.");
            if (alert != null && !alert.isActiveAndEnabled)
                report.Warning("alert_inactive", "The interaction alert " + HypelineAgent.PathOf(alert.gameObject) + " is inactive, so viewers' actions will not show in the game.", "Activate it.");

            CheckEventSystem(report, scene);
        }

        private static void CheckGameId(HypelineAgentJob job, HypelineReport report, SerializedObject serialized)
        {
            Stage stage = (Stage)serialized.FindProperty("Stage").intValue;
            string raw = serialized.FindProperty("GameID").stringValue;
            string gameId = HypelineGameId.Normalize(raw);
            report.gameIdStatus = "not_checked";

            if (gameId.Length == 0)
            {
                report.gameId = HypelineGameId.SharedTestId;
                report.Warning("empty_game_id", "The manager's Game ID is empty, so it uses the shared test ID " + HypelineGameId.SharedTestId + ".",
                    "Create a Game ID at " + HypelineGameId.GamesPageUrlForStage(stage) + " and set it on the manager before release.");
                return;
            }

            report.gameId = gameId;
            if (!HypelineGameId.IsValidFormat(gameId))
            {
                report.Error("invalid_game_id", "The manager's Game ID \"" + raw + "\" is not a valid Game ID, so sign-in will be rejected.",
                    "Copy the ID from " + HypelineGameId.GamesPageUrlForStage(stage) + ".");
                return;
            }

            if (HypelineGameId.IsSharedTestId(gameId))
                report.Warning("shared_test_id", "The manager uses the shared test ID " + HypelineGameId.SharedTestId + ".",
                    "Create a Game ID at " + HypelineGameId.GamesPageUrlForStage(stage) + " and set it on the manager before release.");

            if (job.offline) return;

            long code;
            string body, error;
            Lookup(gameId, stage, out code, out body, out error);

            if (code == 404)
            {
                report.gameIdStatus = "not_registered";
                report.Error("game_id_not_registered", "\"" + gameId + "\" is not registered, so sign-in will be rejected.",
                    "Create it at " + HypelineGameId.GamesPageUrlForStage(stage) + ", or correct the manager's Game ID.");
                return;
            }

            PublicGameResponse game = null;
            if (code == 200)
            {
                try { game = JsonUtility.FromJson<PublicGameResponse>(body ?? ""); }
                catch (Exception) { game = null; }
            }

            if (game == null || string.IsNullOrEmpty(game.status))
            {
                report.gameIdStatus = "unknown";
                string detail = code == 0 ? (string.IsNullOrEmpty(error) ? "network error" : error)
                    : code == 200 ? "the answer was not a Game ID record"
                    : "HTTP " + code;
                report.Warning("game_id_not_checked", "The Game ID could not be looked up on " + HypelineGameId.PortalUrlForStage(stage) + " (" + detail + ").",
                    "Check the network connection and run the check again, or set \"offline\" to true to skip the lookup.");
                return;
            }

            report.gameIdStatus = game.status;
            if (game.status == "active")
            {
                if (!game.verified && !HypelineGameId.IsSharedTestId(gameId))
                    report.Info("game_id_unverified", "\"" + gameId + "\" is registered and accepted at sign-in. Muxy has not verified the game yet.", "");
            }
            else if (game.status == "suspended")
                report.Error("game_id_suspended", "\"" + gameId + "\" is suspended by Muxy, so sign-in is rejected.", "See " + HypelineGameId.GamesPageUrlForStage(stage) + " or contact Muxy.");
            else if (game.status == "archived")
                report.Error("game_id_archived", "\"" + gameId + "\" is archived, so sign-in is rejected.", "Its owner can restore it at " + HypelineGameId.GamesPageUrlForStage(stage) + ".");
            else
                report.Warning("game_id_status", "\"" + gameId + "\" has the status \"" + game.status + "\"; sign-in may be rejected.", "");
        }

        /// <summary>Looks the Game ID up on the portal, waiting for the answer.</summary>
        private static void Lookup(string gameId, Stage stage, out long code, out string body, out string error)
        {
            code = 0;
            body = null;
            error = null;

            using (UnityWebRequest request = UnityWebRequest.Get(HypelineGameId.PublicLookupUrl(gameId, stage)))
            {
                request.timeout = GameIdLookupTimeoutSeconds;
                request.SetRequestHeader("Accept", "application/json");
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();

                // The request runs on its own thread, so the Editor can wait for it here.
                DateTime deadline = DateTime.UtcNow.AddSeconds(GameIdLookupTimeoutSeconds + 1);
                while (!operation.isDone && DateTime.UtcNow < deadline) Thread.Sleep(20);

                if (!operation.isDone)
                {
                    request.Abort();
                    error = "no response within " + GameIdLookupTimeoutSeconds + " seconds";
                    return;
                }

                code = request.responseCode;
                error = request.error;
                body = request.downloadHandler == null ? null : request.downloadHandler.text;
            }
        }

        private static void CheckActions(HypelineReport report, MuxyHypelineManager manager, SerializedObject serialized)
        {
            SerializedProperty actions = serialized.FindProperty("GameActions");
            if (actions.arraySize == 0)
            {
                report.Info("no_actions", "The manager has no viewer actions set in the Inspector.", "Add them with the setup tool, or publish them from code.");
                return;
            }

            bool anyListener = manager.OnAnyGameActionUsed != null && manager.OnAnyGameActionUsed.GetPersistentEventCount() > 0;
            HashSet<string> ids = new HashSet<string>();

            for (int i = 0; i < actions.arraySize; i++)
            {
                SerializedProperty action = actions.GetArrayElementAtIndex(i);
                string id = action.FindPropertyRelative("ID").stringValue;

                if (string.IsNullOrWhiteSpace(id))
                {
                    report.Error("action_without_id", "Viewer action " + (i + 1) + " has no ID.", "Give it a unique ID.");
                    continue;
                }
                if (!ids.Add(id))
                {
                    report.Error("duplicate_action_id", "Two viewer actions have the ID \"" + id + "\".", "Give each action a unique ID.");
                    continue;
                }

                int impact = action.FindPropertyRelative("Impact").intValue;
                if (impact < 1 || impact > 5)
                    report.Warning("action_impact", "Viewer action \"" + id + "\" has Impact " + impact + ".", "Set Impact to 1-5.");

                if (string.IsNullOrWhiteSpace(action.FindPropertyRelative("Name").stringValue))
                    report.Warning("action_without_name", "Viewer action \"" + id + "\" has no name for viewers to see.", "Set its Name.");

                int listeners;
                int broken = HypelineAgent.BrokenHandlers(manager, id, out listeners).Count;
                if (broken > 0)
                    report.Error("action_handler_broken", "Viewer action \"" + id + "\" has " + (broken == 1 ? "a handler" : broken + " handlers")
                        + " that cannot run: the object is gone, or it no longer has a method with that name and parameter.",
                        "Bind the action again with the setup tool, which replaces a handler that cannot run, or fix it in the Inspector.");
                if (listeners == 0 && !anyListener)
                    report.Info("action_without_handler", "Viewer action \"" + id + "\" has no handler set in the Inspector.",
                        "That is fine if a script subscribes to it; otherwise bind one with the setup tool.");
                if (listeners - broken > 1)
                    report.Warning("action_many_handlers", "Viewer action \"" + id + "\" has " + (listeners - broken) + " handlers.", "Keep one handler that accepts or refunds the action, so it is fulfilled once.");
            }
        }

        private static T CheckFollower<T>(HypelineReport report, Scene scene, MuxyHypelineManager manager, string code, string name, string why) where T : Component
        {
            List<T> found = HypelineAgent.FindInScene<T>(scene);
            if (found.Count == 0)
            {
                report.Warning("no_" + code, "The scene has no " + name + ". " + why, "Run the setup tool on this scene, or add the prefab.");
                return null;
            }

            T component = found[0];
            UnityEngine.Object reference = new SerializedObject(component).FindProperty("HypelineManager").objectReferenceValue;
            if (reference == null)
                report.Error(code + "_without_manager", HypelineAgent.PathOf(component.gameObject) + " does not reference a Hypeline manager.", "Run the setup tool on this scene, or assign the manager to its Hypeline Manager field.");
            else if (reference != manager)
                report.Warning(code + "_other_manager", HypelineAgent.PathOf(component.gameObject) + " references a different Hypeline manager than the active one.", "Assign the active manager if that was not intended.");
            return component;
        }

        private static void CheckEventSystem(HypelineReport report, Scene scene)
        {
            List<EventSystem> systems = HypelineAgent.FindInScene<EventSystem>(scene).FindAll(delegate (EventSystem s) { return s.isActiveAndEnabled; });
            if (systems.Count == 0)
            {
                report.Warning("no_event_system", "The scene has no active EventSystem, so the Hypeline panel takes no input unless another loaded scene provides one.",
                    "Run the setup tool on this scene, or add an EventSystem.");
                return;
            }
            if (systems.Count > 1)
                report.Warning("multiple_event_systems", "The scene has " + systems.Count + " active EventSystems.", "Keep one.");

            BaseInputModule[] modules = systems[0].GetComponents<BaseInputModule>();
            if (modules.Length == 0)
            {
                report.Warning("no_input_module", "The EventSystem has no input module.", "Add the input module that matches the project's Active Input Handling.");
                return;
            }

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            Type expected = Type.GetType(HypelineSetup.InputSystemModuleType);
            bool compatible = false;
            foreach (BaseInputModule module in modules)
                if (expected != null && expected.IsInstanceOfType(module)) compatible = true;
            if (!compatible)
                report.Warning("input_module", "The project uses only the new Input System, but the EventSystem has no Input System UI Input Module, so the Hypeline panel will not take input.",
                    "Replace the EventSystem's input module with Input System UI Input Module.");
#elif !ENABLE_INPUT_SYSTEM
            bool legacy = false;
            foreach (BaseInputModule module in modules)
                if (module is StandaloneInputModule) legacy = true;
            if (!legacy)
                report.Warning("input_module", "The project uses the legacy Input Manager, but the EventSystem has no Standalone Input Module.",
                    "Replace the EventSystem's input module with Standalone Input Module.");
#endif
        }
    }
}
