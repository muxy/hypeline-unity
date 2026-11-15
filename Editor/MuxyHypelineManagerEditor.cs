using System;
using System.Collections.Generic;
using MuxyHypeline;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

[CustomEditor(typeof(MuxyHypelineManager), true)]
public sealed class MuxyHypelineManagerEditor : Editor
{
    private const double GameIdCheckTimeoutSeconds = 10.0;

    [Serializable]
    private sealed class PublicGameResponse
    {
        public string game_id = "";
        public string name = "";
        public string status = "";
        public bool verified = false;
    }

    private UnityWebRequest gameIdRequest;
    private UnityWebRequestAsyncOperation gameIdOperation;
    private string gameIdRequestId;
    private Stage gameIdRequestStage;
    private Stage gameIdResultStage;
    private double gameIdRequestStartedAt;
    private string gameIdResultId;
    private string gameIdResultMessage;
    private MessageType gameIdResultType;

    private void OnDisable()
    {
        CancelGameIdCheck();
    }

    public override void OnInspectorGUI()
    {
        if (EditorUtility.IsPersistent(target) && PrefabUtility.IsPartOfImmutablePrefab(target))
            EditorGUILayout.HelpBox("This package prefab is read-only. Drag MuxyHypelineManager into the scene and select it in the Hierarchy to edit its settings.", MessageType.Info);

        serializedObject.Update();
        SerializedProperty property = serializedObject.GetIterator();
        bool enterChildren = true;
        while (property.NextVisible(enterChildren))
        {
            enterChildren = false;
            if (property.propertyPath == "m_Script")
            {
                using (new EditorGUI.DisabledScope(true)) EditorGUILayout.PropertyField(property, true);
            }
            else if (property.propertyPath == "GameMetadata")
            {
                DrawMetadata(property);
            }
            else
            {
                EditorGUILayout.PropertyField(property, true);
                if (property.propertyPath == "GameID")
                    DrawGameIdTools(property);
                else if (property.propertyPath == "GameLogo")
                    EditorGUILayout.HelpBox("Optional: assign a readable logo texture here to show it in the Twitch extension.", MessageType.Info);
                else if (property.propertyPath == "GameTexts" && property.isExpanded)
                    EditorGUILayout.HelpBox("Game Texts appear in the viewer's Game Info tab. For each entry, set the label and value; an icon name is optional. Update values from your game as they change.", MessageType.Info);
            }
        }
        serializedObject.ApplyModifiedProperties();

        SerializedProperty metadata = serializedObject.FindProperty("GameMetadata");
        SerializedProperty name = metadata == null ? null : metadata.FindPropertyRelative("Name");
        if (name != null && string.IsNullOrWhiteSpace(name.stringValue))
            EditorGUILayout.HelpBox("Set a game name so viewers can identify your game in the extension.", MessageType.Info);

        SerializedProperty logo = serializedObject.FindProperty("GameLogo");
        Texture2D texture = logo == null ? null : logo.objectReferenceValue as Texture2D;
        if (texture != null)
        {
            string path = AssetDatabase.GetAssetPath(texture);
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null && !importer.isReadable)
                EditorGUILayout.HelpBox("Game Logo needs Read/Write Enabled in its texture import settings. The runtime also requires RGB24, RGBA32, or ARGB32; use an uncompressed compatible format.", MessageType.Warning);
            else if (!MuxyHypeline.SDK.IsCompatibleFormat(texture.format))
                EditorGUILayout.HelpBox("Game Logo must use RGB24, RGBA32, or ARGB32 at runtime. Set a compatible uncompressed texture format in the import settings.", MessageType.Warning);
        }

        SerializedProperty actions = serializedObject.FindProperty("GameActions");
        if (actions != null)
        {
            HashSet<string> ids = new HashSet<string>();
            for (int i = 0; i < actions.arraySize; i++)
            {
                SerializedProperty id = actions.GetArrayElementAtIndex(i).FindPropertyRelative("ID");
                string value = id == null ? "" : id.stringValue;
                if (string.IsNullOrWhiteSpace(value))
                    EditorGUILayout.HelpBox("Game Action " + (i + 1) + " needs a unique ID.", MessageType.Warning);
                else if (!ids.Add(value))
                    EditorGUILayout.HelpBox("Duplicate Game Action ID: " + value, MessageType.Warning);
            }
        }
    }

    private void DrawGameIdTools(SerializedProperty gameId)
    {
        Stage stage = (Stage)serializedObject.FindProperty("Stage").intValue;
        if (gameIdRequest != null && gameIdRequestStage != stage) CancelGameIdCheck();
        string raw = gameId.stringValue;
        string normalized = HypelineGameId.Normalize(raw);
        bool valid = HypelineGameId.IsValidFormat(normalized);

        if (normalized.Length == 0)
            EditorGUILayout.HelpBox("Game ID is empty; the manager will use the shared test ID " + HypelineGameId.SharedTestId + ". Create your own Game ID before release.", MessageType.Info);
        else if (!valid)
            EditorGUILayout.HelpBox("Game ID must be 3-64 characters of lowercase letters, numbers and single hyphens, starting and ending with a letter or number. Copy the ID shown on the Hypeline games page.", MessageType.Warning);
        else if (normalized == HypelineGameId.SharedTestId)
            EditorGUILayout.HelpBox(HypelineGameId.SharedTestId + " is a shared test ID - create your own Game ID before release.", MessageType.Info);
        else if (normalized != raw)
            EditorGUILayout.HelpBox("The manager will use the normalized Game ID \"" + normalized + "\".", MessageType.Info);

        bool checking = gameIdRequest != null;
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(new GUIContent("Get a Game ID", "Open " + HypelineGameId.GamesPageUrlForStage(stage) + " to create or manage your game's ID.")))
                Application.OpenURL(HypelineGameId.GamesPageUrlForStage(stage));
            using (new EditorGUI.DisabledScope(checking || !valid))
            {
                if (GUILayout.Button(new GUIContent(checking ? "Checking..." : "Check", "Look up this Game ID on " + HypelineGameId.PortalUrlForStage(stage) + ".")))
                    StartGameIdCheck(normalized, stage);
            }
        }

        if (!checking && gameIdResultMessage != null && gameIdResultId == normalized && gameIdResultStage == stage)
            EditorGUILayout.HelpBox(gameIdResultMessage, gameIdResultType);
    }

    private void StartGameIdCheck(string id, Stage stage)
    {
        CancelGameIdCheck();
        gameIdResultMessage = null;
        gameIdRequestId = id;
        gameIdRequestStage = stage;
        gameIdRequestStartedAt = EditorApplication.timeSinceStartup;
        gameIdRequest = UnityWebRequest.Get(HypelineGameId.PublicLookupUrl(id, stage));
        gameIdRequest.timeout = (int)GameIdCheckTimeoutSeconds;
        gameIdRequest.SetRequestHeader("Accept", "application/json");
        gameIdOperation = gameIdRequest.SendWebRequest();
        EditorApplication.update += PollGameIdCheck;
    }

    private void PollGameIdCheck()
    {
        if (gameIdRequest == null)
        {
            EditorApplication.update -= PollGameIdCheck;
            return;
        }

        if (!gameIdOperation.isDone)
        {
            if (EditorApplication.timeSinceStartup - gameIdRequestStartedAt < GameIdCheckTimeoutSeconds + 1.0) return;
            SetGameIdResult(gameIdRequestId, "Could not check the Game ID: " + HypelineGameId.PortalUrlForStage(gameIdRequestStage) + " did not respond within " + GameIdCheckTimeoutSeconds.ToString("0") + " seconds.", MessageType.Warning);
            CancelGameIdCheck();
            Repaint();
            return;
        }

        string id = gameIdRequestId;
        long code = gameIdRequest.responseCode;
        string error = gameIdRequest.error;
        string body = gameIdRequest.downloadHandler == null ? null : gameIdRequest.downloadHandler.text;
        CancelGameIdCheck();
        InterpretGameIdResponse(id, code, error, body);
        Repaint();
    }

    private void InterpretGameIdResponse(string id, long code, string error, string body)
    {
        Stage stage = gameIdRequestStage;
        if (code == 404)
        {
            SetGameIdResult(id, "\"" + id + "\" is not registered, so sign-in will be rejected. Create it on the games page (Get a Game ID), or use " + HypelineGameId.SharedTestId + " for testing.", MessageType.Warning);
            return;
        }
        if (code != 200)
        {
            string detail = code == 0 ? (string.IsNullOrEmpty(error) ? "network error" : error) : "HTTP " + code;
            SetGameIdResult(id, "Could not check the Game ID (" + detail + "). Check your network connection and try again.", MessageType.Warning);
            return;
        }

        PublicGameResponse game = null;
        try
        {
            game = JsonUtility.FromJson<PublicGameResponse>(body ?? "");
        }
        catch (Exception)
        {
            game = null;
        }
        if (game == null || string.IsNullOrEmpty(game.status))
        {
            SetGameIdResult(id, "Could not check the Game ID: the response was not understood.", MessageType.Warning);
            return;
        }

        string name = string.IsNullOrEmpty(game.name) ? id : game.name;
        if (game.status == "active")
        {
            if (game.verified)
                SetGameIdResult(id, "Registered: " + name + " (verified by Muxy). Sign-in with this Game ID is accepted.", MessageType.Info);
            else
                SetGameIdResult(id, "Registered: " + name + " (unverified). Sign-in with this Game ID is accepted; Muxy has not verified this game yet.", MessageType.Info);
        }
        else if (game.status == "suspended")
            SetGameIdResult(id, name + " is suspended by Muxy, so sign-in is rejected. See " + HypelineGameId.GamesPageUrlForStage(stage) + " or contact Muxy.", MessageType.Error);
        else if (game.status == "archived")
            SetGameIdResult(id, name + " is archived, so sign-in is rejected. Its owner can restore it at " + HypelineGameId.GamesPageUrlForStage(stage) + ".", MessageType.Error);
        else
            SetGameIdResult(id, name + " has status \"" + game.status + "\"; sign-in may be rejected.", MessageType.Warning);
    }

    private void SetGameIdResult(string id, string message, MessageType type)
    {
        gameIdResultId = id;
        gameIdResultStage = gameIdRequestStage;
        gameIdResultMessage = message;
        gameIdResultType = type;
    }

    private void CancelGameIdCheck()
    {
        EditorApplication.update -= PollGameIdCheck;
        if (gameIdRequest != null)
        {
            if (gameIdOperation != null && !gameIdOperation.isDone) gameIdRequest.Abort();
            gameIdRequest.Dispose();
        }
        gameIdRequest = null;
        gameIdOperation = null;
    }

    private static void DrawMetadata(SerializedProperty metadata)
    {
        metadata.isExpanded = EditorGUILayout.Foldout(metadata.isExpanded, "Game Metadata", true);
        if (!metadata.isExpanded) return;
        EditorGUI.indentLevel++;
        DrawMetadataField(metadata, "Name", "Game name shown to viewers in the Twitch extension.");
        DrawMetadataField(metadata, "Theme", "Optional extension theme name; use default for the standard theme.");
        EditorGUI.indentLevel--;
    }

    private static void DrawMetadataField(SerializedProperty metadata, string name, string guidance)
    {
        SerializedProperty field = metadata.FindPropertyRelative(name);
        if (field == null) return;
        EditorGUILayout.PropertyField(field, new GUIContent(field.displayName, guidance));
        EditorGUILayout.HelpBox(guidance, MessageType.None);
    }
}
