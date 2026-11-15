using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MuxyHypeline
{
    /// <summary>
    /// A request for one of the Hypeline Editor tools, read from a JSON file. Coding agents
    /// and scripts use it to set up or check a scene without the Editor UI.
    /// </summary>
    [Serializable]
    public class HypelineAgentJob
    {
        /// <summary>"setup" adds and wires the Hypeline prefabs; "doctor" only checks.</summary>
        public string tool = "";

        /// <summary>Scene asset path, such as Assets/Scenes/Main.unity. Empty means the
        /// active scene in an open Editor, or the first enabled build scene in batch mode.</summary>
        public string scene = "";

        /// <summary>Setup: the Game ID to set on the manager. Empty keeps the current one.</summary>
        public string gameId = "";
        /// <summary>Setup: the game name viewers see. Empty keeps the current one.</summary>
        public string gameName = "";
        /// <summary>Setup: "production" or "sandbox". Empty keeps the current stage.</summary>
        public string stage = "";

        public bool skipMenu;
        public bool skipAlert;
        public bool skipEventSystem;
        public bool addPolling;

        /// <summary>Setup in an open Editor: save a scene that already has unsaved changes.
        /// Without it, setup stops rather than save someone's work in progress.</summary>
        public bool allowDirtyScene;

        /// <summary>Doctor: do not look the Game ID up on the developer portal.</summary>
        public bool offline;

        public HypelineAgentText[] texts = new HypelineAgentText[0];
        public HypelineAgentAction[] actions = new HypelineAgentAction[0];
    }

    /// <summary>A Game Text to add, or to update if one with the same label exists.</summary>
    [Serializable]
    public class HypelineAgentText
    {
        public string label = "";
        public string value = "";
        public string icon = "";
    }

    /// <summary>A viewer action to add, or to update if one with the same ID exists.</summary>
    [Serializable]
    public class HypelineAgentAction
    {
        public string id = "";
        public string name = "";
        public string description = "";
        /// <summary>"Neutral", "Hinder" or "Help". Empty means Neutral for a new action.</summary>
        public string category = "";
        /// <summary>"Available", "Unavailable" or "Hidden". Empty means Available for a new action.</summary>
        public string state = "";
        public string icon = "";
        /// <summary>1 to 5. Leave it out, or use 0, for 1 on a new action and no change on an
        /// existing one.</summary>
        public int impact;
        /// <summary>How many times viewers can use the action; 65535 is unlimited. Leave it
        /// out, or use a negative value, for unlimited on a new action and no change on an
        /// existing one.</summary>
        public int count = -1;

        public HypelineAgentHandler handler = new HypelineAgentHandler();
    }

    /// <summary>
    /// The method an action calls when a viewer uses it: a public instance method taking one
    /// GameActionUsed, on a component of a GameObject in the scene.
    /// </summary>
    [Serializable]
    public class HypelineAgentHandler
    {
        /// <summary>Name or hierarchy path ("Parent/Child") of the GameObject.</summary>
        public string gameObject = "";
        /// <summary>Type name of the component, with or without its namespace.</summary>
        public string component = "";
        public string method = "";
        /// <summary>Create a root GameObject with that name if the scene has none, and add
        /// the component if the object does not have it. Without it, both must exist.</summary>
        public bool addIfMissing;
    }

    [Serializable]
    public class HypelineFinding
    {
        /// <summary>"error", "warning" or "info". Any error makes the report not ok.</summary>
        public string severity = "";
        /// <summary>Stable identifier for the finding, for scripts.</summary>
        public string code = "";
        public string message = "";
        /// <summary>What to do about it. Empty when nothing needs doing.</summary>
        public string fix = "";
    }

    /// <summary>The result of a Hypeline Editor tool, written as JSON.</summary>
    [Serializable]
    public class HypelineReport
    {
        public string tool = "";
        /// <summary>True when there are no error findings.</summary>
        public bool ok;
        public string unityVersion = "";
        public string packageVersion = "";
        public string packageSource = "";
        public string inputHandling = "";
        public string scene = "";
        public string gameId = "";
        /// <summary>Doctor: "active", "suspended", "archived", "not_registered", "unknown"
        /// or "not_checked".</summary>
        public string gameIdStatus = "";
        public string stage = "";

        /// <summary>Setup: what was added to the scene.</summary>
        public List<string> created = new List<string>();
        /// <summary>Setup: what was changed on objects already in the scene.</summary>
        public List<string> changed = new List<string>();
        /// <summary>Setup: whether the scene was written to disk.</summary>
        public bool sceneSaved;

        public List<HypelineFinding> findings = new List<HypelineFinding>();

        public void Error(string code, string message, string fix) { Add("error", code, message, fix); }
        public void Warning(string code, string message, string fix) { Add("warning", code, message, fix); }
        public void Info(string code, string message, string fix) { Add("info", code, message, fix); }

        private void Add(string severity, string code, string message, string fix)
        {
            HypelineFinding finding = new HypelineFinding();
            finding.severity = severity;
            finding.code = code;
            finding.message = message;
            finding.fix = fix ?? "";
            findings.Add(finding);
        }

        public bool HasErrors
        {
            get
            {
                foreach (HypelineFinding finding in findings)
                    if (finding.severity == "error") return true;
                return false;
            }
        }
    }

    /// <summary>
    /// Runs the Hypeline Editor tools from a job file, so a coding agent or a script can set
    /// up and check a scene without the Editor UI.
    ///
    /// With the project closed, run Unity in batch mode:
    ///   Unity -batchmode -projectPath PROJECT -executeMethod MuxyHypeline.HypelineAgent.Run
    ///         -hypelineJob job.json -hypelineReport report.json
    /// The process exits with 0 when the report is ok, 1 when it has errors and 2 when the
    /// job could not be read.
    ///
    /// With the project open in the Editor (Temp/UnityLockfile exists, and batch mode cannot
    /// open the project), write the job to Library/Hypeline/jobs/NAME.job.json. The Editor
    /// runs it within a few seconds, writes NAME.report.json beside it and removes the job.
    /// </summary>
    [InitializeOnLoad]
    public static class HypelineAgent
    {
        public const string JobArgument = "-hypelineJob";
        public const string ReportArgument = "-hypelineReport";
        public const string JobFolder = "Library/Hypeline/jobs";
        public const string JobSuffix = ".job.json";
        public const string ReportSuffix = ".report.json";

        private const double WatchIntervalSeconds = 1.0;
        private static double nextWatchAt;

        static HypelineAgent()
        {
            if (!Application.isBatchMode) EditorApplication.update += WatchForJobs;
        }

        /// <summary>Batch-mode entry point. See the class summary.</summary>
        public static void Run()
        {
            string jobPath = Argument(JobArgument);
            string reportPath = Argument(ReportArgument);
            if (string.IsNullOrEmpty(reportPath)) reportPath = Path.Combine(ProjectRoot, "Library/Hypeline/report.json");

            HypelineReport report;
            int exitCode;
            try
            {
                HypelineAgentJob job = ReadJob(jobPath);
                report = Execute(job, true);
                exitCode = report.ok ? 0 : 1;
            }
            catch (Exception e)
            {
                report = NewReport("");
                report.Error("job_failed", e.Message, "Check the job file and the command line, then run it again.");
                report.ok = false;
                exitCode = 2;
            }

            WriteReport(report, reportPath);
            Debug.Log("Hypeline " + report.tool + (report.ok ? " finished" : " found problems") + "; report: " + reportPath);
            if (Application.isBatchMode) EditorApplication.Exit(exitCode);
        }

        [MenuItem("Tools/Muxy Hypeline/Check Active Scene")]
        private static void CheckActiveSceneFromMenu()
        {
            HypelineAgentJob job = new HypelineAgentJob();
            job.tool = "doctor";
            HypelineReport report = Execute(job, false);

            int errors = 0, warnings = 0;
            foreach (HypelineFinding finding in report.findings)
            {
                string line = "Hypeline: " + finding.message + (finding.fix.Length > 0 ? " " + finding.fix : "");
                if (finding.severity == "error") { errors++; Debug.LogError(line); }
                else if (finding.severity == "warning") { warnings++; Debug.LogWarning(line); }
                else Debug.Log(line);
            }
            Debug.Log("Hypeline check of " + report.scene + ": " + errors + " error(s), " + warnings + " warning(s). Details are above.");
        }

        /// <summary>Runs one job in the current Editor and returns its report.</summary>
        public static HypelineReport Execute(HypelineAgentJob job, bool batch)
        {
            string tool = (job.tool ?? "").Trim().ToLowerInvariant();
            HypelineReport report = NewReport(tool);

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                report.Error("play_mode", "The Editor is in Play mode.", "Leave Play mode and run the job again.");
            }
            else if (tool == "setup")
            {
                HypelineSetup.Run(job, report, batch);
            }
            else if (tool == "doctor")
            {
                HypelineDoctor.Run(job, report, batch);
            }
            else
            {
                report.Error("unknown_tool", "The job's \"tool\" is \"" + job.tool + "\".", "Set \"tool\" to \"setup\" or \"doctor\".");
            }

            report.ok = !report.HasErrors;
            return report;
        }

        private static void WatchForJobs()
        {
            if (EditorApplication.timeSinceStartup < nextWatchAt) return;
            nextWatchAt = EditorApplication.timeSinceStartup + WatchIntervalSeconds;

            // A job waits while the Editor cannot safely change a scene.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;

            string folder = Path.Combine(ProjectRoot, JobFolder);
            if (!Directory.Exists(folder)) return;

            string[] jobs = Directory.GetFiles(folder, "*" + JobSuffix);
            if (jobs.Length == 0) return;
            Array.Sort(jobs, StringComparer.Ordinal);

            // One per tick, oldest name first.
            string jobPath = jobs[0];
            string reportPath = jobPath.Substring(0, jobPath.Length - JobSuffix.Length) + ReportSuffix;

            HypelineReport report;
            try
            {
                report = Execute(ReadJob(jobPath), false);
            }
            catch (Exception e)
            {
                report = NewReport("");
                report.Error("job_failed", e.Message, "Check the job file, then write it again.");
                report.ok = false;
            }

            // The job is removed first, so a report that cannot be written does not leave
            // the job to run again every second.
            try { File.Delete(jobPath); }
            catch (Exception e) { Debug.LogWarning("Hypeline: could not remove " + jobPath + ": " + e.Message); return; }

            WriteReport(report, reportPath);
            Debug.Log("Hypeline " + report.tool + (report.ok ? " finished" : " found problems") + "; report: " + reportPath);
        }

        internal static string ProjectRoot
        {
            get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..")); }
        }

        private static string Argument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : "";
        }

        private static HypelineAgentJob ReadJob(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("No job file was given. Pass " + JobArgument + " <file>.");
            if (!File.Exists(path)) throw new FileNotFoundException("The job file does not exist: " + path);

            HypelineAgentJob job;
            try
            {
                job = JsonUtility.FromJson<HypelineAgentJob>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                throw new ArgumentException("The job file is not valid JSON: " + e.Message);
            }
            if (job == null) throw new ArgumentException("The job file is empty: " + path);

            if (job.texts == null) job.texts = new HypelineAgentText[0];
            if (job.actions == null) job.actions = new HypelineAgentAction[0];
            return job;
        }

        private static HypelineReport NewReport(string tool)
        {
            HypelineReport report = new HypelineReport();
            report.tool = tool;
            report.unityVersion = Application.unityVersion;

            UnityEditor.PackageManager.PackageInfo package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(SDK).Assembly);
            if (package != null)
            {
                report.packageVersion = package.version;
                report.packageSource = package.source.ToString();
            }

#if ENABLE_INPUT_SYSTEM && ENABLE_LEGACY_INPUT_MANAGER
            report.inputHandling = "both";
#elif ENABLE_INPUT_SYSTEM
            report.inputHandling = "input_system";
#else
            report.inputHandling = "legacy";
#endif
            return report;
        }

        private static void WriteReport(HypelineReport report, string path)
        {
            try
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                File.WriteAllText(path, JsonUtility.ToJson(report, true));
            }
            catch (Exception e)
            {
                Debug.LogError("Hypeline: could not write the report to " + path + ": " + e.Message);
            }
        }

        /// <summary>
        /// Finds the scene a job names and makes it available for editing. Returns false after
        /// adding an error finding. closeAfter is true when the scene was opened only for this job.
        /// </summary>
        internal static bool OpenScene(HypelineAgentJob job, HypelineReport report, bool batch, out Scene scene, out bool closeAfter)
        {
            scene = default(Scene);
            closeAfter = false;

            string path = (job.scene ?? "").Trim().Replace('\\', '/');
            if (path.Length == 0)
            {
                if (!batch)
                {
                    scene = SceneManager.GetActiveScene();
                    path = scene.path;
                }
                else
                {
                    foreach (EditorBuildSettingsScene buildScene in EditorBuildSettings.scenes)
                    {
                        if (buildScene.enabled && !string.IsNullOrEmpty(buildScene.path)) { path = buildScene.path; break; }
                    }
                }

                if (string.IsNullOrEmpty(path))
                {
                    report.Error("no_scene", batch ? "The job names no scene and the project has no enabled build scene." : "The active scene has never been saved.",
                        "Set \"scene\" to a scene asset path. Scenes in this project: " + SceneList() + ".");
                    return false;
                }
            }

            report.scene = path;
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
            {
                report.Error("scene_not_found", "There is no scene asset at " + path + ".",
                    "Set \"scene\" to a scene asset path. Scenes in this project: " + SceneList() + ".");
                return false;
            }

            if (batch)
            {
                scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                return true;
            }

            scene = SceneManager.GetSceneByPath(path);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                // Opened beside the scenes the developer has open, and closed again afterwards.
                scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                closeAfter = true;
            }
            return true;
        }

        private static string SceneList()
        {
            string[] guids = AssetDatabase.FindAssets("t:Scene", new string[] { "Assets" });
            List<string> paths = new List<string>();
            foreach (string guid in guids)
            {
                paths.Add(AssetDatabase.GUIDToAssetPath(guid));
                if (paths.Count == 20) { paths.Add("..."); break; }
            }
            return paths.Count == 0 ? "none under Assets" : string.Join(", ", paths.ToArray());
        }

        /// <summary>Every component of type T in the scene, on inactive objects too.</summary>
        internal static List<T> FindInScene<T>(Scene scene) where T : Component
        {
            List<T> found = new List<T>();
            foreach (GameObject root in scene.GetRootGameObjects())
                found.AddRange(root.GetComponentsInChildren<T>(true));
            return found;
        }

        internal static string PathOf(GameObject gameObject)
        {
            string path = gameObject.name;
            for (Transform parent = gameObject.transform.parent; parent != null; parent = parent.parent)
                path = parent.name + "/" + path;
            return path;
        }
    }
}
