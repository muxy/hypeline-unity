using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace MuxyHypeline
{
    /// <summary>
    /// Adds the Hypeline prefabs to a scene and wires them, as the getting-started guide
    /// describes: one manager, the menu and the interaction alert both referencing it, an
    /// EventSystem, and optionally Game Texts and viewer actions bound to handler methods.
    /// It only adds what is missing and changes what the job asks for, so running it again
    /// changes nothing.
    /// </summary>
    internal static class HypelineSetup
    {
        private const string ManagerPrefabGuid = "9745ee0d0c5e9cd40950241c07bbe75c";
        private const string MenuPrefabGuid = "b4309354bd8d4cf9a192c8ac551fd015";
        private const string AlertPrefabGuid = "735114d3755e4a82947d8de60c54caf1";
        private const string PollingPrefabGuid = "3d3fd97c690c5b19b8eba259d51cfc67";

        internal const string InputSystemModuleType = "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem";

        public static void Run(HypelineAgentJob job, HypelineReport report, bool batch)
        {
            string gameId = HypelineGameId.Normalize(job.gameId);
            if (gameId.Length > 0 && !HypelineGameId.IsValidFormat(gameId))
            {
                report.Error("invalid_game_id", "\"" + job.gameId + "\" is not a valid Game ID.",
                    "Use 3-64 lowercase letters, numbers and single hyphens, starting and ending with a letter or number. Copy the ID from " + HypelineGameId.GamesPageUrl + ".");
                return;
            }

            Stage stage = Stage.Production;
            string stageName = (job.stage ?? "").Trim().ToLowerInvariant();
            if (stageName == "sandbox") stage = Stage.Sandbox;
            else if (stageName.Length > 0 && stageName != "production")
            {
                report.Error("invalid_stage", "\"" + job.stage + "\" is not a stage.", "Set \"stage\" to \"production\" or \"sandbox\", or leave it out.");
                return;
            }

            Scene scene;
            bool closeAfter;
            if (!HypelineAgent.OpenScene(job, report, batch, out scene, out closeAfter)) return;

            try
            {
                if (!batch && scene.isDirty && !job.allowDirtyScene)
                {
                    report.Error("scene_has_unsaved_changes", scene.path + " has unsaved changes in the open Editor.",
                        "Save the scene in the Editor and run the job again, or set \"allowDirtyScene\" to true to save those changes too.");
                    return;
                }

                // Everything the job asks for is checked before the scene is touched, so a
                // job with a mistake in it changes nothing.
                Dictionary<string, ResolvedHandler> handlers = Validate(job, report, scene);
                if (report.HasErrors) return;

                Apply(job, report, scene, gameId, stageName.Length > 0, stage, handlers);
                if (report.HasErrors) return;

                if (report.created.Count > 0 || report.changed.Count > 0)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    report.sceneSaved = EditorSceneManager.SaveScene(scene);
                    if (!report.sceneSaved)
                        report.Error("scene_not_saved", "Unity could not save " + scene.path + ".", "Check that the scene file is writable.");
                }
            }
            finally
            {
                if (closeAfter) EditorSceneManager.CloseScene(scene, true);
                else if (!batch && report.HasErrors && (report.created.Count > 0 || report.changed.Count > 0))
                    report.Warning("scene_left_modified", "Setup stopped partway and " + scene.path + " is open with unsaved changes from it.",
                        "Reload the scene without saving to discard them, fix the errors, and run the job again.");
            }
        }

        private sealed class ResolvedHandler
        {
            /// <summary>Null when setup is to create the object, named ObjectName.</summary>
            public GameObject Target;
            public string ObjectName;
            /// <summary>Null when setup is to add the component, of type ComponentType.</summary>
            public Component Component;
            public Type ComponentType;
            public MethodInfo Method;
        }

        /// <summary>
        /// Checks the job against the scene without changing anything, and returns the
        /// handler each action names, by action ID.
        /// </summary>
        private static Dictionary<string, ResolvedHandler> Validate(HypelineAgentJob job, HypelineReport report, Scene scene)
        {
            Dictionary<string, ResolvedHandler> handlers = new Dictionary<string, ResolvedHandler>();

            List<MuxyHypelineManager> managers = HypelineAgent.FindInScene<MuxyHypelineManager>(scene);
            List<MuxyHypelineManager> active = managers.FindAll(delegate (MuxyHypelineManager m) { return m.isActiveAndEnabled; });
            if (active.Count > 1)
                report.Error("multiple_managers", "The scene has " + active.Count + " active Hypeline managers: " + Paths(active) + ".",
                    "Keep one active manager and remove or disable the others, then run the job again.");
            else if (active.Count == 0 && managers.Count > 0)
                report.Error("manager_inactive", "The scene's Hypeline manager " + HypelineAgent.PathOf(managers[0].gameObject) + " is inactive or disabled.",
                    "Activate it, or remove it so setup can add a new one, then run the job again.");

            foreach (HypelineAgentText text in job.texts)
            {
                if ((text.label ?? "").Trim().Length == 0)
                    report.Error("text_without_label", "A Game Text in the job has no label.", "Give every entry of \"texts\" a \"label\".");
            }

            HashSet<string> seen = new HashSet<string>();
            foreach (HypelineAgentAction action in job.actions)
            {
                string id = (action.id ?? "").Trim();
                if (id.Length == 0 || id.Length > SDK.MaximumActionIDLength)
                {
                    report.Error("invalid_action_id", "An action in the job has " + (id.Length == 0 ? "no ID" : "an ID longer than " + SDK.MaximumActionIDLength + " characters") + ".",
                        "Give every entry of \"actions\" a unique \"id\" of at most " + SDK.MaximumActionIDLength + " characters.");
                    continue;
                }
                if (!seen.Add(id))
                {
                    report.Error("duplicate_action_id", "The job lists the action \"" + id + "\" twice.", "List each action once.");
                    continue;
                }

                GameActionCategory category;
                GameActionState state;
                if (!string.IsNullOrEmpty(action.category) && !TryParseEnum(action.category, out category))
                    report.Error("invalid_action_category", "Action \"" + id + "\" has the category \"" + action.category + "\".", "Use \"Neutral\", \"Hinder\" or \"Help\".");
                if (!string.IsNullOrEmpty(action.state) && !TryParseEnum(action.state, out state))
                    report.Error("invalid_action_state", "Action \"" + id + "\" has the state \"" + action.state + "\".", "Use \"Available\", \"Unavailable\" or \"Hidden\".");
                if (action.impact < 0 || action.impact > 5 || action.count > GameAction.InfiniteCount)
                    report.Error("invalid_action_number", "Action \"" + id + "\" has an impact outside 1-5 or a count above " + GameAction.InfiniteCount + ".",
                        "Use an \"impact\" of 1 to 5 and a \"count\" of 0 to " + GameAction.InfiniteCount + " (" + GameAction.InfiniteCount + " is unlimited).");

                if (action.handler == null || string.IsNullOrEmpty(action.handler.method)) continue;
                ResolvedHandler resolved = ResolveHandler(report, scene, id, action.handler);
                if (resolved != null) handlers[id] = resolved;
            }

            return handlers;
        }

        private static void Apply(HypelineAgentJob job, HypelineReport report, Scene scene, string gameId, bool setStage, Stage stage, Dictionary<string, ResolvedHandler> handlers)
        {
            MuxyHypelineManager manager = EnsureManager(report, scene);
            if (manager == null) return;

            SerializedObject serialized = new SerializedObject(manager);

            SerializedProperty gameIdProperty = serialized.FindProperty("GameID");
            if (gameId.Length > 0 && gameIdProperty.stringValue != gameId)
            {
                gameIdProperty.stringValue = gameId;
                report.changed.Add("Manager Game ID = " + gameId);
            }
            report.gameId = HypelineGameId.Normalize(gameIdProperty.stringValue);

            SerializedProperty stageProperty = serialized.FindProperty("Stage");
            if (setStage && stageProperty.enumValueIndex != (int)stage)
            {
                stageProperty.enumValueIndex = (int)stage;
                report.changed.Add("Manager Stage = " + stage);
            }
            report.stage = ((Stage)stageProperty.enumValueIndex).ToString().ToLowerInvariant();

            string gameName = (job.gameName ?? "").Trim();
            SerializedProperty nameProperty = serialized.FindProperty("GameMetadata").FindPropertyRelative("Name");
            if (gameName.Length > 0 && nameProperty.stringValue != gameName)
            {
                nameProperty.stringValue = gameName;
                report.changed.Add("Manager game name = " + gameName);
            }

            ApplyTexts(job, report, serialized);
            ApplyActions(job, report, serialized);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            // Listeners are added to the action objects themselves, after the list is written.
            foreach (KeyValuePair<string, ResolvedHandler> handler in handlers) BindHandler(report, scene, manager, handler.Key, handler.Value);

            if (!job.skipMenu) EnsureFollower<MuxyHypelineMenu>(report, scene, manager, MenuPrefabGuid, "MuxyHypelineMenu");
            if (!job.skipAlert)
            {
                MuxyHypelineInteractionAlert alert = EnsureFollower<MuxyHypelineInteractionAlert>(report, scene, manager, AlertPrefabGuid, "MuxyHypelineInteractionAlert");
                if (alert != null && !alert.gameObject.activeInHierarchy)
                    report.Warning("alert_inactive", "The interaction alert " + HypelineAgent.PathOf(alert.gameObject) + " is inactive, so viewers' actions will not show in the game.",
                        "Activate it; the visible alert is a required part of the integration.");
            }
            if (job.addPolling) EnsureFollower<MuxyHypelinePolling>(report, scene, manager, PollingPrefabGuid, "MuxyHypelinePolling");
            if (!job.skipEventSystem) EnsureEventSystem(report, scene);

            if (report.created.Count > 0 || report.changed.Count > 0)
            {
                EditorUtility.SetDirty(manager);
                PrefabUtility.RecordPrefabInstancePropertyModifications(manager);
            }
        }

        private static MuxyHypelineManager EnsureManager(HypelineReport report, Scene scene)
        {
            // Validate has already refused a scene with several active managers or only
            // an inactive one.
            foreach (MuxyHypelineManager manager in HypelineAgent.FindInScene<MuxyHypelineManager>(scene))
                if (manager.isActiveAndEnabled) return manager;

            GameObject instance = InstantiatePrefab(report, scene, ManagerPrefabGuid, "MuxyHypelineManager");
            return instance == null ? null : instance.GetComponent<MuxyHypelineManager>();
        }

        private static T EnsureFollower<T>(HypelineReport report, Scene scene, MuxyHypelineManager manager, string prefabGuid, string name) where T : Component
        {
            List<T> existing = HypelineAgent.FindInScene<T>(scene);
            T component;
            if (existing.Count > 0)
            {
                component = existing[0];
                if (existing.Count > 1)
                    report.Warning("duplicate_" + name, "The scene has " + existing.Count + " " + name + " components.", "Keep one unless you placed the others on purpose.");
            }
            else
            {
                GameObject instance = InstantiatePrefab(report, scene, prefabGuid, name);
                if (instance == null) return null;
                component = instance.GetComponent<T>();
            }

            SerializedObject serialized = new SerializedObject(component);
            SerializedProperty reference = serialized.FindProperty("HypelineManager");
            if (reference.objectReferenceValue == null)
            {
                reference.objectReferenceValue = manager;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                if (existing.Count > 0) report.changed.Add(name + " now references the manager");
            }
            else if (reference.objectReferenceValue != manager)
            {
                report.Warning("other_manager_" + name, HypelineAgent.PathOf(component.gameObject) + " references a different Hypeline manager.",
                    "Assign the scene's active manager to its Hypeline Manager field if that was not intended.");
            }
            return component;
        }

        private static GameObject InstantiatePrefab(HypelineReport report, Scene scene, string guid, string name)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject prefab = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                report.Error("prefab_missing", "The " + name + " prefab was not found in the Muxy Hypeline package.", "Reinstall the package, then run the job again.");
                return null;
            }

            GameObject instance = PrefabUtility.InstantiatePrefab(prefab, scene) as GameObject;
            if (instance == null)
            {
                report.Error("prefab_not_added", "Unity could not add the " + name + " prefab to the scene.", "");
                return null;
            }

            report.created.Add(name);
            return instance;
        }

        private static void EnsureEventSystem(HypelineReport report, Scene scene)
        {
            List<EventSystem> systems = HypelineAgent.FindInScene<EventSystem>(scene).FindAll(delegate (EventSystem s) { return s.isActiveAndEnabled; });
            if (systems.Count > 0)
            {
                // Checking an existing EventSystem is the doctor's job.
                return;
            }

            GameObject gameObject = new GameObject("EventSystem");
            SceneManager.MoveGameObjectToScene(gameObject, scene);
            gameObject.AddComponent<EventSystem>();

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            Type module = Type.GetType(InputSystemModuleType);
            if (module != null)
            {
                gameObject.AddComponent(module);
                report.created.Add("EventSystem (Input System UI Input Module)");
            }
            else
            {
                gameObject.AddComponent<StandaloneInputModule>();
                report.created.Add("EventSystem (Standalone Input Module)");
                report.Warning("input_module", "The project uses only the new Input System, but its UI input module was not found, so the EventSystem got the legacy module and the Hypeline panel will not take input.",
                    "Install the Input System package and replace the module with Input System UI Input Module.");
            }
#else
            gameObject.AddComponent<StandaloneInputModule>();
            report.created.Add("EventSystem (Standalone Input Module)");
#endif
        }

        private static void ApplyTexts(HypelineAgentJob job, HypelineReport report, SerializedObject serialized)
        {
            SerializedProperty texts = serialized.FindProperty("GameTexts");
            foreach (HypelineAgentText text in job.texts)
            {
                string label = text.label.Trim();

                SerializedProperty element = null;
                for (int i = 0; i < texts.arraySize; i++)
                {
                    SerializedProperty candidate = texts.GetArrayElementAtIndex(i);
                    if (candidate.FindPropertyRelative("Label").stringValue == label) { element = candidate; break; }
                }

                bool added = element == null;
                if (added)
                {
                    texts.InsertArrayElementAtIndex(texts.arraySize);
                    element = texts.GetArrayElementAtIndex(texts.arraySize - 1);
                    element.FindPropertyRelative("Label").stringValue = label;
                    // A new element copies the one before it.
                    element.FindPropertyRelative("Value").stringValue = "";
                    element.FindPropertyRelative("Icon").stringValue = "";
                }

                bool changed = SetString(element.FindPropertyRelative("Value"), text.value, added);
                changed |= SetString(element.FindPropertyRelative("Icon"), text.icon, added);
                if (added) report.created.Add("Game Text " + label);
                else if (changed) report.changed.Add("Game Text " + label);
            }
        }

        private static void ApplyActions(HypelineAgentJob job, HypelineReport report, SerializedObject serialized)
        {
            SerializedProperty actions = serialized.FindProperty("GameActions");

            foreach (HypelineAgentAction action in job.actions)
            {
                string id = action.id.Trim();

                GameActionCategory category = GameActionCategory.Neutral;
                GameActionState state = GameActionState.Available;
                bool hasCategory = !string.IsNullOrEmpty(action.category) && TryParseEnum(action.category, out category);
                bool hasState = !string.IsNullOrEmpty(action.state) && TryParseEnum(action.state, out state);

                SerializedProperty element = null;
                for (int i = 0; i < actions.arraySize; i++)
                {
                    SerializedProperty candidate = actions.GetArrayElementAtIndex(i);
                    if (candidate.FindPropertyRelative("ID").stringValue == id) { element = candidate; break; }
                }

                bool added = element == null;
                if (added)
                {
                    actions.InsertArrayElementAtIndex(actions.arraySize);
                    element = actions.GetArrayElementAtIndex(actions.arraySize - 1);
                    element.FindPropertyRelative("ID").stringValue = id;
                    // A new element copies the one before it, listeners included.
                    element.FindPropertyRelative("Name").stringValue = "";
                    element.FindPropertyRelative("Description").stringValue = "";
                    element.FindPropertyRelative("Icon").stringValue = "";
                    SerializedProperty calls = element.FindPropertyRelative("OnGameActionUsed.m_PersistentCalls.m_Calls");
                    if (calls != null) calls.ClearArray();
                }

                // What the job leaves out stays as it is on an action that already exists.
                bool changed = SetString(element.FindPropertyRelative("Name"), action.name, added);
                changed |= SetString(element.FindPropertyRelative("Description"), action.description, added);
                changed |= SetString(element.FindPropertyRelative("Icon"), action.icon, added);
                if (added || hasCategory) changed |= SetInt(element.FindPropertyRelative("Category"), (int)category);
                if (added || hasState) changed |= SetInt(element.FindPropertyRelative("State"), (int)state);
                if (added || action.impact > 0) changed |= SetInt(element.FindPropertyRelative("Impact"), action.impact > 0 ? action.impact : 1);
                if (added || action.count >= 0) changed |= SetInt(element.FindPropertyRelative("Count"), action.count >= 0 ? action.count : GameAction.InfiniteCount);

                if (added) report.created.Add("Action " + id);
                else if (changed) report.changed.Add("Action " + id);
            }
        }

        private static ResolvedHandler ResolveHandler(HypelineReport report, Scene scene, string id, HypelineAgentHandler handler)
        {
            string where = "Action \"" + id + "\": ";
            string objectName = (handler.gameObject ?? "").Trim().Trim('/');

            GameObject target = FindGameObject(scene, objectName);
            if (target == null && !handler.addIfMissing)
            {
                report.Error("handler_object_not_found", where + "there is no GameObject \"" + handler.gameObject + "\" in " + scene.path + ".",
                    "Set the handler's \"gameObject\" to the name or hierarchy path (\"Parent/Child\") of an object in the scene, or set \"addIfMissing\" to true to create it.");
                return null;
            }
            if (target == null && (objectName.Length == 0 || objectName.IndexOf('/') >= 0))
            {
                report.Error("handler_object_not_found", where + "\"" + handler.gameObject + "\" is not in " + scene.path + " and cannot be created.",
                    "\"addIfMissing\" creates a root object; give it a plain name, not a hierarchy path.");
                return null;
            }

            Component component = null;
            if (target != null)
            {
                foreach (Component candidate in target.GetComponents<Component>())
                {
                    if (candidate == null) continue;
                    Type candidateType = candidate.GetType();
                    if (candidateType.Name == handler.component || candidateType.FullName == handler.component) { component = candidate; break; }
                }
            }

            Type type = component == null ? null : component.GetType();
            if (type == null && !handler.addIfMissing)
            {
                report.Error("handler_component_not_found", where + HypelineAgent.PathOf(target) + " has no " + handler.component + " component.",
                    "Add the component to the object, correct the handler's \"component\", or set \"addIfMissing\" to true to add it.");
                return null;
            }
            if (type == null)
            {
                List<Type> matches = new List<Type>();
                foreach (Type candidate in TypeCache.GetTypesDerivedFrom<MonoBehaviour>())
                    if (!candidate.IsAbstract && !candidate.IsGenericTypeDefinition && (candidate.Name == handler.component || candidate.FullName == handler.component)) matches.Add(candidate);

                if (matches.Count != 1)
                {
                    report.Error("handler_component_not_found", where + (matches.Count == 0
                            ? "the project has no MonoBehaviour named " + handler.component + "."
                            : "the project has " + matches.Count + " MonoBehaviours named " + handler.component + "."),
                        matches.Count == 0
                            ? "Check that the script exists and compiles, and that the class name matches its file name."
                            : "Set the handler's \"component\" to the type's full name, including its namespace.");
                    return null;
                }
                type = matches[0];
            }

            MethodInfo method = type.GetMethod(handler.method, BindingFlags.Instance | BindingFlags.Public, null, new Type[] { typeof(GameActionUsed) }, null);
            if (method == null || method.ReturnType != typeof(void))
            {
                report.Error("handler_method_not_found", where + handler.component + " has no public method void " + handler.method + "(GameActionUsed).",
                    "Declare the method as: public void " + handler.method + "(MuxyHypeline.GameActionUsed used).");
                return null;
            }

            ResolvedHandler resolved = new ResolvedHandler();
            resolved.Target = target;
            resolved.ObjectName = objectName;
            resolved.Component = component;
            resolved.ComponentType = type;
            resolved.Method = method;
            return resolved;
        }

        private static void BindHandler(HypelineReport report, Scene scene, MuxyHypelineManager manager, string id, ResolvedHandler handler)
        {
            // Another action in the same job may have made the object or component already.
            if (handler.Target == null) handler.Target = FindGameObject(scene, handler.ObjectName);
            if (handler.Target == null)
            {
                handler.Target = new GameObject(handler.ObjectName);
                SceneManager.MoveGameObjectToScene(handler.Target, scene);
                report.created.Add("GameObject " + handler.ObjectName);
            }
            if (handler.Component == null) handler.Component = handler.Target.GetComponent(handler.ComponentType);
            if (handler.Component == null)
            {
                handler.Component = handler.Target.AddComponent(handler.ComponentType);
                report.created.Add(handler.ComponentType.Name + " on " + HypelineAgent.PathOf(handler.Target));
            }

            GameAction action = manager.FindGameAction(id);
            if (action == null)
            {
                report.Error("handler_action_missing", "Action \"" + id + "\" was not found on the manager after it was written.", "");
                return;
            }
            if (action.OnGameActionUsed == null) action.OnGameActionUsed = new MuxyHypelineGameActionUsedEvent();

            // A handler that can no longer run, such as one whose method was renamed,
            // is replaced rather than left beside the new one. Last first, so the
            // indexes of the rest stay valid.
            int total;
            List<int> broken = HypelineAgent.BrokenHandlers(manager, id, out total);
            for (int i = broken.Count - 1; i >= 0; i--) UnityEventTools.RemovePersistentListener(action.OnGameActionUsed, broken[i]);
            if (broken.Count > 0)
                report.changed.Add("Action " + id + ": removed " + (broken.Count == 1 ? "a handler" : broken.Count + " handlers") + " that could not run");

            for (int i = 0; i < action.OnGameActionUsed.GetPersistentEventCount(); i++)
            {
                if (action.OnGameActionUsed.GetPersistentTarget(i) == handler.Component && action.OnGameActionUsed.GetPersistentMethodName(i) == handler.Method.Name)
                    return; // Already bound.
            }

            UnityAction<GameActionUsed> call = (UnityAction<GameActionUsed>)Delegate.CreateDelegate(typeof(UnityAction<GameActionUsed>), handler.Component, handler.Method);
            UnityEventTools.AddPersistentListener(action.OnGameActionUsed, call);
            report.changed.Add("Action " + id + " calls " + handler.ComponentType.Name + "." + handler.Method.Name + " on " + HypelineAgent.PathOf(handler.Target));
        }

        internal static GameObject FindGameObject(Scene scene, string nameOrPath)
        {
            nameOrPath = (nameOrPath ?? "").Trim().Trim('/');
            if (nameOrPath.Length == 0) return null;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
                {
                    GameObject candidate = transform.gameObject;
                    if (candidate.name == nameOrPath || HypelineAgent.PathOf(candidate) == nameOrPath) return candidate;
                }
            }
            return null;
        }

        private static bool SetString(SerializedProperty property, string value, bool always)
        {
            // An empty value in the job leaves an existing entry as it is.
            if (!always && string.IsNullOrEmpty(value)) return false;
            value = value ?? "";
            if (property.stringValue == value) return false;
            property.stringValue = value;
            return true;
        }

        private static bool SetInt(SerializedProperty property, int value)
        {
            if (property.intValue == value) return false;
            property.intValue = value;
            return true;
        }

        private static bool TryParseEnum<T>(string value, out T result) where T : struct
        {
            result = default(T);
            foreach (string name in Enum.GetNames(typeof(T)))
            {
                if (string.Equals(name, value.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    result = (T)Enum.Parse(typeof(T), name);
                    return true;
                }
            }
            return false;
        }

        private static string Paths<T>(List<T> components) where T : Component
        {
            List<string> paths = new List<string>();
            foreach (T component in components) paths.Add(HypelineAgent.PathOf(component.gameObject));
            return string.Join(", ", paths.ToArray());
        }
    }
}
