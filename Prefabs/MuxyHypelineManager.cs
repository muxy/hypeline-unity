using System;
using System.Collections.Generic;
using UnityEngine;
using MuxyHypeline;
using UnityEngine.Events;


[UnityEngine.Scripting.APIUpdating.MovedFrom(true, null, "Muxy.Gateway", "MuxyGatewayManager")]
public class MuxyHypelineManager : MonoBehaviour
{

    [InspectorName("Game ID")]
    [Tooltip("Your game's Hypeline Game ID from https://dev.muxy.io/games. Set it before Play mode. gamelink-testing is a shared test ID for trying the samples; create your own Game ID before release.")]
    public String GameID = HypelineGameId.SharedTestId;

    [Tooltip("Hypeline backend to connect to. Both stages use encrypted wss:// connections.")]
    public Stage Stage = Stage.Production;

    [SerializeField] private GameMetadata GameMetadata = new GameMetadata();
    [Tooltip("Optional logo texture. It is encoded as a PNG data URI when game metadata is published.")]
    public Texture2D GameLogo;
    [Tooltip("Remember the broadcaster session on this device using Unity PlayerPrefs, which is not encrypted.")]
    public bool RememberSession = true;
    [SerializeField] private List<GameAction> GameActions = new List<GameAction>();
    [SerializeField] private List<GameText> GameTexts = new List<GameText>();
    [SerializeField] private PollConfiguration PollConfiguration = new PollConfiguration();
    public MuxyHypelinePollUpdate OnPollUpdate = new MuxyHypelinePollUpdate();

    [Header("Events")]
    public MuxyHypelineAuthenticationEvent OnAuthentication = new MuxyHypelineAuthenticationEvent();
    public MuxyHypelineGameActionUsedEvent OnAnyGameActionUsed = new MuxyHypelineGameActionUsedEvent();
    public MuxyHypelineBitsUsedEvent OnBitsUsed = new MuxyHypelineBitsUsedEvent();
    public HypelineTransportStateEvent OnTransportStateChanged = new HypelineTransportStateEvent();

    [Serializable]
    public class MuxyHypelineAuthenticationEvent : UnityEvent<AuthenticationResponse> { }
    [Serializable]
    public class MuxyHypelineBitsUsedEvent : UnityEvent<BitsUsed> { }
    [Serializable]
    public class MuxyHypelinePollUpdate : UnityEvent<PollUpdate> { }
    [Serializable]
    public class HypelineTransportStateEvent : UnityEvent<HypelineTransportState> { }


    // Private //

    private const string PLAYER_PREFS_REFRESH_TOKEN = "MuxyGatewayRefreshToken";
    private string RefreshTokenKey => Stage == Stage.Staging ? PLAYER_PREFS_REFRESH_TOKEN + ".staging" : PLAYER_PREFS_REFRESH_TOKEN;

    private SDK SDK;
    private SDK.OnAuthenticateDelegate AuthCB;
    private SDK.OnGameActionUsedDelegate ActionCB;
    private SDK.OnBitsUsedDelegate BitsCB;

    private bool DidOpenAndRun = false;
    private HypelineTransportState lastTransportState = HypelineTransportState.Disconnected;
    private float pinStartedAt = -1f;
    private float transportStartedAt = -1f;
    private int connectionGeneration;

    private void SetupCallbacks()
    {
        int generation = connectionGeneration;
        ActionCB = (Used) =>
        {
            if (generation != connectionGeneration) return;
            OnAnyGameActionUsed.Invoke(Used);
        };

        BitsCB = (Used) =>
        {
            if (generation != connectionGeneration) return;
            OnBitsUsed.Invoke(Used);
        };

        AuthCB = (Response) =>
        {
            if (generation != connectionGeneration) return;
            if (pinStartedAt >= 0f)
            {
                Debug.Log("Hypeline: Authentication response received after " + (Time.realtimeSinceStartup - pinStartedAt).ToString("F1") + " seconds.");
                pinStartedAt = -1f;
            }
            if (Response.HasError)
            {
                Debug.LogWarning("Hypeline: Twitch authentication failed. Request a new code and check the extension configuration. An unregistered, suspended or archived Game ID (\"" + GameID + "\") is also rejected at sign-in; select MuxyHypelineManager and use Check next to Game ID in the Inspector.");
                PlayerPrefs.SetString(RefreshTokenKey, "");
                OnAuthentication.Invoke(Response);
                return;
            }

            Debug.Log("Hypeline: Twitch authentication succeeded.");
            if (RememberSession) PlayerPrefs.SetString(RefreshTokenKey, Response.RefreshToken);
            else ClearSavedSession();

            SDK.OnGameActionUsed(ActionCB);
            SDK.OnBitsUsed(BitsCB);
            SDK.SetGameActions(GameActions.ToArray());
            SDK.SetGameMetadata(BuildGameMetadata());
            SDK.SetGameTexts(GameTexts.ToArray());
            OnAuthentication.Invoke(Response);
        };

    }

    private void CheckForRefreshToken()
    {
        if (!RememberSession) return;
        String RefreshToken = PlayerPrefs.GetString(RefreshTokenKey, "");
        if (RefreshToken != "")
        {
            Debug.Log("Hypeline: Restoring the saved Twitch session.");
            OpenAndRunSDK();
            SDK.AuthenticateWithRefreshToken(RefreshToken, AuthCB);
        }
    }

    protected void OnDisable()
    {
        connectionGeneration++;
        DidOpenAndRun = false;
        if (SDK != null)
        {
            SDK.Dispose();
            SDK = null;
        }
    }

    protected void OnEnable()
    {
        if (SDK == null)
        {
            SDK = new SDK(ResolveGameID());
            DidOpenAndRun = false;
            SetupCallbacks();
            CheckForRefreshToken();
        }

        // Texts and actions are published by the authentication callback;
        // the server rejects them before login.
        if (IsAuthenticated())
        {
            SubmitGameTexts();
            SubmitGameActions();
        }
    }


    public void OpenAndRunSDK()
    {
        if (DidOpenAndRun) return;
        if (SDK.TransportState == HypelineTransportState.Error)
        {
            ResetConnection();
        }
        transportStartedAt = Time.realtimeSinceStartup;
        Debug.Log("Hypeline: Starting the Twitch connection (" + Stage + ").");
#pragma warning disable 618
        if (Stage == Stage.Sandbox)
        {
            Debug.LogWarning("Hypeline: the sandbox stage is retired and may not connect. Set the manager's Stage to Production; games are tested on production.");
            SDK.RunInSandbox();
        }
#pragma warning restore 618
        else if (Stage == Stage.Staging) SDK.RunInStaging();
        else SDK.RunInProduction();
        DidOpenAndRun = true;
    }

    protected void Update()
    {
        if (SDK == null) return;
        HypelineTransportState state = SDK.TransportState;
        if (state != lastTransportState)
        {
            lastTransportState = state;
            if (state == HypelineTransportState.Connected && transportStartedAt >= 0f)
            {
                Debug.Log("Hypeline: Connection transport opened after " + (Time.realtimeSinceStartup - transportStartedAt).ToString("F1") + " seconds.");
                transportStartedAt = -1f;
            }
            if (state == HypelineTransportState.Error)
                Debug.LogWarning("Hypeline: Connection transport failed. Check the network and earlier transport warnings.");
            else
                Debug.Log("Hypeline: Connection transport is " + state + ".");
            OnTransportStateChanged.Invoke(state);
            if (SDK == null) return;
        }
        if (SDK.TransportState == HypelineTransportState.Error) DidOpenAndRun = false;
        SDK.Update();
    }

    /// <summary>Abandons the current connection so a fresh attempt cannot receive stale callbacks.</summary>
    public void ResetConnection()
    {
        connectionGeneration++;
        SDK previous = SDK;
        SDK = null;
        DidOpenAndRun = false;
        lastTransportState = HypelineTransportState.Disconnected;
        pinStartedAt = -1f;
        transportStartedAt = -1f;
        if (previous != null) previous.Dispose();
        if (isActiveAndEnabled)
        {
            SDK = new SDK(ResolveGameID());
            SetupCallbacks();
        }
        Debug.Log("Hypeline: Connection reset. Enter a new Twitch code to retry.");
    }

    /// <summary>Normalizes GameID (trimmed, lower case) and falls back to the shared test ID when empty.</summary>
    private string ResolveGameID()
    {
        string normalized = HypelineGameId.Normalize(GameID);
        if (normalized.Length == 0) normalized = HypelineGameId.SharedTestId;
        else if (!HypelineGameId.IsValidFormat(normalized))
            Debug.LogWarning("Hypeline: Game ID \"" + normalized + "\" is not a valid Game ID; sign-in will be rejected. Use the ID shown at " + HypelineGameId.GamesPageUrl + ".");
        GameID = normalized;
        return normalized;
    }

    public void AuthenticateWithPIN(String PIN)
    {
        pinStartedAt = Time.realtimeSinceStartup;
        Debug.Log("Hypeline: Twitch code submitted; waiting for authentication.");
        OpenAndRunSDK();
        SDK.AuthenticateWithPIN(PIN, AuthCB);
    }

    public bool IsAuthenticated()
    {
        return SDK != null && SDK.IsAuthenticated;
    }

    /// <summary>Remove the locally stored session without disconnecting the current session.</summary>
    public void ClearSavedSession()
    {
        PlayerPrefs.DeleteKey(RefreshTokenKey);
    }

    public HypelineTransportState TransportState
    {
        get { return SDK == null ? HypelineTransportState.Disconnected : SDK.TransportState; }
    }

    /// <summary>Signs out, clears the saved session and prepares a fresh connection for a new code.</summary>
    public void Deauthenticate()
    {
        ClearSavedSession();
        if (SDK != null) SDK.Deauthenticate();
        ResetConnection();
    }

    public void AddGameText(GameText Text)
    {
        GameTexts.Add(Text);
    }

    public void ClearGameTexts()
    {
        GameTexts.Clear();
    }

    /// <summary>Publishes the game texts now if signed in; otherwise they are published after login.</summary>
    public void SubmitGameTexts()
    {
        if (!IsAuthenticated()) return;
        SDK.SetGameTexts(GameTexts.ToArray());
    }

    public GameText GetGameTextAt(int Index)
    {
        return GameTexts[Index];
    }

    public void SetGameTextAt(GameText Text, int Index)
    {
        GameTexts[Index] = Text;
        SubmitGameTexts();
    }

    public void RemoveGameTextAt(int Index)
    {
        GameTexts.RemoveAt(Index);
        SubmitGameTexts();
    }

    public GameAction GetGameActionAt(int Index)
    {
        return GameActions[Index];
    }

    public GameAction FindGameAction(string ActionID)
    {
        foreach (GameAction Action in GameActions)
        {
            if (Action.ID == ActionID) return Action;
        }

        return null;
    }

    public void SetGameActionAt(GameAction Action, int Index)
    {
        GameActions[Index] = Action;
    }

    public void RemoveGameActionAt(int Index)
    {
        GameActions.RemoveAt(Index);
    }

    /// <summary>
    /// Publishes the action list now if signed in; otherwise it is published after login.
    /// Publishing resets each action's remaining count on the server to its Count.
    /// </summary>
    public void SubmitGameActions()
    {
        if (!IsAuthenticated()) return;
        SDK.SetGameActions(GameActions.ToArray());
    }

    public void SetPollConfiguration(PollConfiguration Config)
    {
        PollConfiguration = Config;
    }

    public PollConfiguration GetPollConfiguration()
    {
        return PollConfiguration;
    }

    /// <summary>
    /// Starts the configured poll. Updates go to the configuration's OnPollUpdate
    /// delegate and then to this manager's OnPollUpdate event.
    /// </summary>
    public void StartPoll()
    {
        if (!IsAuthenticated())
        {
            Debug.LogWarning("Hypeline: Sign in before starting a poll.");
            return;
        }

        PollConfiguration source = PollConfiguration;
        PollConfiguration.OnUpdateDelegate userCallback = source.OnPollUpdate;
        int generation = connectionGeneration;
        PollConfiguration bridged = new PollConfiguration
        {
            Prompt = source.Prompt,
            Location = source.Location,
            Mode = source.Mode,
            Options = new List<string>(source.Options),
            DurationInSeconds = source.DurationInSeconds,
            OnPollUpdate = update =>
            {
                if (generation != connectionGeneration) return;
                if (userCallback != null) userCallback(update);
                OnPollUpdate.Invoke(update);
            }
        };
        SDK.StartPoll(bridged);
    }

    public void StopPoll()
    {
        if (SDK == null) return;
        SDK.StopPoll();
    }
    public void SetGameMetadata(GameMetadata Metadata)
    {
        GameMetadata = Metadata;
        if (IsAuthenticated()) SDK.SetGameMetadata(BuildGameMetadata());
    }

    private GameMetadata BuildGameMetadata()
    {
        GameMetadata metadata = new GameMetadata
        {
            Name = GameMetadata.Name,
            Logo = GameMetadata.Logo,
            Theme = GameMetadata.Theme
        };

#if UNITY_EDITOR || UNITY_STANDALONE
        if (GameLogo != null)
        {
            try
            {
                metadata.Logo = SDK.ConvertTextureToBase64(GameLogo);
            }
            catch (Exception exception)
            {
                Debug.LogError("Hypeline logo could not be encoded. Enable Read/Write and use RGB24, RGBA32, or ARGB32 on the texture. " + exception.Message);
            }
        }
#endif

        return metadata;
    }

    public GameMetadata GetGameMetadata()
    {
        return GameMetadata;
    }

    public void AcceptGameAction(GameActionUsed Used, string Description)
    {
        if (SDK == null) { Debug.LogWarning("Hypeline: No active connection; the action was not accepted."); return; }
        SDK.AcceptGameAction(Used, Description);
    }

    public void RefundGameAction(GameActionUsed Used, string Description)
    {
        if (SDK == null) { Debug.LogWarning("Hypeline: No active connection; the action was not refunded."); return; }
        SDK.RefundGameAction(Used, Description);
    }

    public void SetAllGameActionStates(GameActionState State)
    {
        foreach (var Action in GameActions)
        {
            Action.State = State;
        }
        SubmitGameActions();
    }
}
