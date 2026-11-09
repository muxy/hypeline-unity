using MuxyHypeline.Imports;
using MuxyHypeline.Imports.Schema;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System;
using System.Threading.Tasks;
using System.Net.NetworkInformation;
using System.Reflection;

#if UNITY_EDITOR || UNITY_STANDALONE
using AOT;
using UnityEngine;
using UnityEngine.Events;
#endif

namespace MuxyHypeline
{
    public class AuthenticationResponse
    {
        public string JWT { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public string TwitchUsername { get; set; } = string.Empty;

        public bool HasError { get; set; } = false;
    }

    public class Payload
    {
        public byte[] Bytes { get; set; } = new byte[0];
    }

    [Serializable]
    public class GameMetadata
    {
        public string Name;
        public string Logo;
        public string Theme;
    }

    [Serializable]
    public class GameText
    {
        [Tooltip("Label shown next to this value in the Twitch Game Info tab.")]
        public string Label;

        [Tooltip("Current value shown to viewers. Update it as your game changes.")]
        public string Value;

        [Tooltip("Optional icon name from the Hypeline icon library, such as fa-solid:heart.")]
        public string Icon;
    }

    [Serializable]
    public enum GameActionCategory
    {
        Neutral = 0,
        Hinder = 1,
        Help = 2
    }

    [Serializable]
    public enum GameActionState
    {
        Unavailable = 0,
        Available = 1,
        Hidden = 2
    }

    [Serializable]
    public class MuxyHypelineGameActionUsedEvent : UnityEvent<GameActionUsed> { }

    [Serializable]
    public class GameAction
    {
        public static int InfiniteCount = 0xFFFF;

        public string ID;

        public GameActionCategory Category = GameActionCategory.Neutral;
        public GameActionState State = GameActionState.Unavailable;
        [Range(1, 5)] public int Impact;
        public string Name;
        public string Description;
        public string Icon;

        [SerializeField, Range(0, 65535)] public int Count = 65535;

        public MuxyHypelineGameActionUsedEvent OnGameActionUsed;
    }

    [Serializable]
    public class PollUpdate
    {
        public int Winner;
        public int WinningVoteCount;

        public List<int> Results = new List<int>();

        public int Count;
        public double Mean;
        public bool IsFinal = false;
    }

    [Serializable]
    public enum PollLocation
    {
        Default = 0
    }

    [Serializable]
    public enum PollMode
    {
        Chaos = 0,
        Order = 1
    }

    [Serializable]
    public class PollConfiguration
    {
        public static int InfiniteDuration = 0;
        public delegate void OnUpdateDelegate(PollUpdate Update);

        public string Prompt;
        public PollLocation Location = PollLocation.Default;
        public PollMode Mode = PollMode.Order;

        public List<string> Options = new List<string>();

        public Int32 DurationInSeconds = InfiniteDuration;

        public OnUpdateDelegate OnPollUpdate = (Update) => { };
    }

    public class BitsUsed
    {
        public string TransactionID;
        public string SKU;
        public int Bits;
        public string UserID;
        public string Username;
    }

    public class GameActionUsed
    {
        public string TransactionID;
        public string ActionID;
        public int Cost;
        public string UserID;
        public string Username;
    }

    public class SDK : IDisposable
    {
        // Limits enforced by the bundled native library and the Gamelink server.
        // Requests over these limits are dropped silently by native code, so
        // the managed API rejects them up front.
        public const int MaximumPollOptions = 10;
        public const int MaximumPollPromptBytes = 256;
        public const int MaximumPollOptionBytes = 128;
        public const int MaximumActionIDLength = 64;
        public const int MaximumGameNameBytes = 256;
        public const int MaximumLogoBytes = 1024 * 1024;

        private GatewaySDK Instance;
        private string GameID;
        private Encoding UTF8WithoutBOM = new UTF8Encoding(false);

        private GCHandle DebugMessage;

        WebsocketTransport Transport;
        private bool DisposeRequested;
        private int NativeDispatchDepth;
        private readonly Dictionary<IntPtr, GCHandle> AuthenticationHandles = new Dictionary<IntPtr, GCHandle>();
        private GameMetadata LastMetadata = new GameMetadata();

        public bool IsDisposed { get { return DisposeRequested; } }
        public bool IsConnected { get { return !DisposeRequested && Transport.IsConnected; } }
        public Exception LastConnectionError { get { return Transport.LastConnectionError; } }
        public HypelineTransportState TransportState { get { return DisposeRequested ? HypelineTransportState.Disconnected : Transport.ConnectionState; } }

        /// <summary>Opt-in native diagnostics. Leave disabled when exchanging credentials.</summary>
        public bool DebugLoggingEnabled { get; set; }

        public void Update()
        {
            if (!DisposeRequested) Transport.Update(this);
        }

        public void StopWebsocketTransport()
        {
            Transport.Disconnect();
        }

        public Task StopWebsocketTransportAsync() { return Transport.StopAsync(); }

        /// <summary>
        /// Connects to an explicit endpoint, for local tools and tests. Only wss:// is
        /// accepted for remote hosts; ws:// is limited to loopback addresses.
        /// </summary>
        public async Task RunInCustomAsync(String uri)
        {
            ThrowIfDisposed();
            await Transport.Open(uri).ConfigureAwait(false);
            Transport.Run(this);
        }

        public Task RunInSandboxAsync() { ThrowIfDisposed(); return Transport.OpenAndRunInSandbox(this); }
        public Task RunInProductionAsync() { ThrowIfDisposed(); return Transport.OpenAndRunInProduction(this); }

        public void RunInSandbox()
        {
            ObserveConnection(RunInSandboxAsync());
        }

        public void RunInProduction()
        {
            ObserveConnection(RunInProductionAsync());
        }

        private static async void ObserveConnection(Task connection)
        {
            try { await connection.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                // Do not log endpoint query strings, payloads or credentials.
                LogWarning("Hypeline transport could not start (" + error.GetType().Name + "). Await RunInProductionAsync/RunInSandboxAsync for details.");
            }
        }

        private void ThrowIfDisposed()
        {
            if (DisposeRequested) throw new ObjectDisposedException(nameof(SDK));
        }

        private static void LogWarning(string message)
        {
#if UNITY_EDITOR || UNITY_STANDALONE
            Debug.LogWarning(message);
#else
            Console.Error.WriteLine(message);
#endif
        }

        private class InvokeOnDebugMessageParameters
        {
            public SDK SDK;
        }

#if UNITY_EDITOR || UNITY_STANDALONE
        [MonoPInvokeCallback(typeof(GatewayDebugMessageDelegate))]
#endif
        private static void InvokeOnDebugMessage(IntPtr Data, string Message)
        {
            if (Data == IntPtr.Zero)
            {
                return;
            }

            GCHandle handle = GCHandle.FromIntPtr(Data);
            if (handle.Target == null)
            {
                return;
            }

            InvokeOnDebugMessageParameters args = handle.Target as InvokeOnDebugMessageParameters;

            if (args == null)
            {
                return;
            }

            try
            {
                if (args.SDK != null && args.SDK.DebugLoggingEnabled)
                {
                    args.SDK.LogMessage(Message);
                }
            }
            catch (Exception)
            {
                // Exceptions must not unwind across a reverse P/Invoke boundary.
            }
        }

        public SDK(string GameID)
        {
            // Trim and lowercase so " My-Game " and "my-game" reach the server as the same ID.
            string normalizedGameID = HypelineGameId.Normalize(GameID);
            if (normalizedGameID.Length == 0) throw new ArgumentException("A game ID is required.", nameof(GameID));

            Instance = Imported.MGW_MakeSDK(normalizedGameID);
            this.GameID = normalizedGameID;
            this.Transport = new WebsocketTransport();

            InvokeOnDebugMessageParameters args = new InvokeOnDebugMessageParameters();
            args.SDK = this;

            DebugMessage = GCHandle.Alloc(args);
            Imported.MGW_SDK_OnDebugMessage(Instance, InvokeOnDebugMessage, GCHandle.ToIntPtr(DebugMessage));
#if UNITY_EDITOR
            UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeChanged;
            UnityEditor.EditorApplication.quitting += Dispose;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Dispose;
#elif UNITY_STANDALONE
            Application.quitting += Dispose;
#endif
        }

        /// <summary>
        /// Stop socket tasks, detach lifecycle hooks, then destroy the native SDK
        /// and its callback handles. Call on the owning/main thread; idempotent.
        /// No network close handshake or Unity continuation is needed to join.
        /// </summary>
        public void Dispose()
        {
            if (DisposeRequested) return;
            DisposeRequested = true;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            UnityEditor.EditorApplication.quitting -= Dispose;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
#elif UNITY_STANDALONE
            Application.quitting -= Dispose;
#endif
            Transport.Dispose();
            // A user callback may dispose its SDK while native ReceiveMessage
            // is on the stack. Destroy it only after that dispatch returns.
            if (NativeDispatchDepth == 0) ReleaseNativeResources();
            GC.SuppressFinalize(this);
        }

#if UNITY_EDITOR
        private void OnPlayModeChanged(UnityEditor.PlayModeStateChange state)
        {
            if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode) Dispose();
        }
#endif

        private void ReleaseNativeResources()
        {
            if (Instance.SDK == IntPtr.Zero) return;
            Imported.MGW_SDK_DetachOnDebugMessage(Instance);
            Imported.MGW_KillSDK(Instance);
            Instance = default(GatewaySDK);
            if (DebugMessage.IsAllocated) DebugMessage.Free();
            if (PollDelegateHandle.IsAllocated) PollDelegateHandle.Free();
            if (GameActionUsedDelegateHandle.IsAllocated) GameActionUsedDelegateHandle.Free();
            if (BitsUsedDelegateHandle.IsAllocated) BitsUsedDelegateHandle.Free();
            foreach (GCHandle handle in AuthenticationHandles.Values) if (handle.IsAllocated) handle.Free();
            AuthenticationHandles.Clear();
            GameActionCallbacks.Clear();
        }

        private void LogMessage(string Message)
        {
            // Native debug messages can include authentication requests and tokens.
            // Never write those payloads to the Unity Console or player log.
            if (Message != null &&
                (Message.IndexOf("\"authenticate\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 Message.IndexOf("\"pin\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 Message.IndexOf("\"jwt\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 Message.IndexOf("refresh", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 Message.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 Message.IndexOf("authorization", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                Message = "Hypeline authentication debug payload omitted.";
            }
#if UNITY_EDITOR || UNITY_STANDALONE
            Debug.Log(Message);
#else
            Console.Error.WriteLine(Message);
#endif
        }

        // Native code copies strings into std::string; a null pointer there is undefined behaviour.
        private static string NotNull(string value) { return value ?? string.Empty; }

        private static void RequireID(string id, string parameter)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("An action ID is required.", parameter);
        }

        #region Network
        public bool ReceiveMessage(String Message)
        {
            ThrowIfDisposed();
            if (Message == null)
            {
                return false;
            }

            byte[] Bytes = UTF8WithoutBOM.GetBytes(Message);
            NativeDispatchDepth++;
            try { return Imported.MGW_SDK_ReceiveMessage(Instance, Bytes, (uint)Bytes.Length) != 0; }
            finally
            {
                NativeDispatchDepth--;
                if (DisposeRequested && NativeDispatchDepth == 0) ReleaseNativeResources();
            }
        }

        public bool HasPayloads()
        {
            ThrowIfDisposed();
            return Imported.MGW_SDK_HasPayloads(Instance) != 0;
        }

        public delegate void ForeachPayloadDelegate(Payload Payload);

        private class InvokeForeachPayloadParameters
        {
            public ForeachPayloadDelegate Callback;
        }

#if UNITY_EDITOR || UNITY_STANDALONE
        [MonoPInvokeCallback(typeof(GatewayForeachPayloadDelegate))]
#endif
        private static void InvokeForeachPayload(IntPtr Data, IntPtr Message)
        {
            if (Data == IntPtr.Zero)
            {
                return;
            }

            GCHandle handle = GCHandle.FromIntPtr(Data);
            if (handle.Target == null)
            {
                return;
            }

            InvokeForeachPayloadParameters args = handle.Target as InvokeForeachPayloadParameters;

            if (args == null)
            {
                return;
            }

            if (args.Callback != null)
            {
                try
                {
                    Payload p = new Payload();
                    MGW_Payload first = Marshal.PtrToStructure<MGW_Payload>(Message);

                    p.Bytes = new byte[first.Length];
                    Marshal.Copy(first.Bytes, p.Bytes, 0, (int)first.Length);

                    args.Callback(p);
                }
                catch (Exception error)
                {
                    LogWarning("Hypeline payload callback failed (" + error.GetType().Name + ").");
                }
            }
        }

        public void ForeachPayload(ForeachPayloadDelegate Delegate)
        {
            ThrowIfDisposed();
            InvokeForeachPayloadParameters args = new InvokeForeachPayloadParameters();
            args.Callback = Delegate;

            GCHandle Handle = GCHandle.Alloc(args);
            NativeDispatchDepth++;
            try { Imported.MGW_SDK_ForeachPayload(Instance, InvokeForeachPayload, GCHandle.ToIntPtr(Handle)); }
            finally
            {
                Handle.Free();
                NativeDispatchDepth--;
                if (DisposeRequested && NativeDispatchDepth == 0) ReleaseNativeResources();
            }
        }

        private struct Version
        {
            public int Major;
            public int Minor;
            public int Patch;
        }

        private Version ParseVersion(string str)
        {
            Version result = new Version();

            if (str == null)
            {
                return result;
            }

            // Release tags may include a SemVer prerelease suffix (for example,
            // v1.1.0-rc.1). The native URL API takes the numeric core only.
            int suffix = str.IndexOf('-');
            if (suffix >= 0) str = str.Substring(0, suffix);
            string[] parts = str.Split('.');
            if (parts.Length != 3)
            {
                return result;
            }

            if (parts[0].StartsWith("v"))
            {
                parts[0] = parts[0].TrimStart('v');
            }

            try
            {
                result.Major = int.Parse(parts[0]);
                result.Minor = int.Parse(parts[1]);
                result.Patch = int.Parse(parts[2]);
            }
            catch
            {
                result.Major = 0;
                result.Minor = 0;
                result.Patch = 0;
            }

            return result;
        }

        public String GetSandboxURL()
        {
            ThrowIfDisposed();
            Version v = ParseVersion(Constants.Version);
            string s = NativeString.StringFromUTF8AndDeallocate(Imported.MGW_SDK_GetProjectionSandboxURL(Instance, "gateway-unity", v.Major, v.Minor, v.Patch));
            return s;
        }

        public String GetProductionURL()
        {
            ThrowIfDisposed();
            Version v = ParseVersion(Constants.Version);
            string s = NativeString.StringFromUTF8AndDeallocate(Imported.MGW_SDK_GetProjectionProductionURL(Instance, "gateway-unity", v.Major, v.Minor, v.Patch));
            return s;
        }

        public void HandleReconnect()
        {
            ThrowIfDisposed();
            Imported.MGW_SDK_HandleReconnect(Instance);
        }
        #endregion

        #region Authentication
        public delegate void OnAuthenticateDelegate(AuthenticationResponse Response);
        private class InvokeOnAuthenticateParameters
        {
            public OnAuthenticateDelegate Callback;
            public SDK Owner;
        }

#if UNITY_EDITOR || UNITY_STANDALONE
        [MonoPInvokeCallback(typeof(GatewayAuthenticateResponseDelegate))]
#endif
        private static void InvokeOnAuthenticate(IntPtr Data, IntPtr Msg)
        {
            if (Data == IntPtr.Zero)
            {
                return;
            }

            GCHandle handle = GCHandle.FromIntPtr(Data);
            if (handle.Target == null)
            {
                return;
            }

            InvokeOnAuthenticateParameters args = handle.Target as InvokeOnAuthenticateParameters;
            if (args == null)
            {
                return;
            }

            args.Owner.AuthenticationHandles.Remove(Data);
            handle.Free();
            try
            {
                AuthenticationResponse Response = new AuthenticationResponse();
                MGW_AuthenticateResponse resp = Marshal.PtrToStructure<MGW_AuthenticateResponse>(Msg);

                Response.JWT = NativeString.StringFromUTF8(resp.JWT);
                Response.RefreshToken = NativeString.StringFromUTF8(resp.RefreshToken);
                Response.TwitchUsername = NativeString.StringFromUTF8(resp.TwitchName);
                Response.HasError = resp.HasError != 0;

                args.Callback?.Invoke(Response);
            }
            catch (Exception error)
            {
                // Exceptions must not unwind across a reverse P/Invoke boundary.
                LogWarning("Hypeline authentication callback failed (" + error.GetType().Name + ").");
            }
        }

        private void Authenticate(Func<GatewaySDK, IntPtr, ushort> submit, OnAuthenticateDelegate Delegate)
        {
            InvokeOnAuthenticateParameters args = new InvokeOnAuthenticateParameters();
            args.Callback = Delegate;
            args.Owner = this;

            GCHandle Handle = GCHandle.Alloc(args);
            IntPtr key = GCHandle.ToIntPtr(Handle);
            AuthenticationHandles.Add(key, Handle);
            NativeDispatchDepth++;
            try { submit(Instance, key); }
            catch { if (AuthenticationHandles.Remove(key)) Handle.Free(); throw; }
            finally
            {
                NativeDispatchDepth--;
                if (DisposeRequested && NativeDispatchDepth == 0) ReleaseNativeResources();
            }
        }

        /// <summary>
        /// Authenticates with the six-character code from the broadcaster's extension
        /// configuration. Codes are case-insensitive; the SDK normalises them before sending.
        /// </summary>
        public void AuthenticateWithPIN(String PIN, OnAuthenticateDelegate Delegate)
        {
            ThrowIfDisposed();
            string normalized = (PIN ?? string.Empty).Trim().ToUpperInvariant();
            if (normalized.Length == 0) throw new ArgumentException("A PIN is required.", nameof(PIN));
            Authenticate((sdk, key) => Imported.MGW_SDK_AuthenticateWithPIN(sdk, normalized, InvokeOnAuthenticate, key), Delegate);
        }

        public void AuthenticateWithRefreshToken(String Refresh, OnAuthenticateDelegate Delegate)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(Refresh)) throw new ArgumentException("A refresh token is required.", nameof(Refresh));
            Authenticate((sdk, key) => Imported.MGW_SDK_AuthenticateWithRefreshToken(sdk, Refresh, InvokeOnAuthenticate, key), Delegate);
        }

        /// <summary>
        /// Tells the server this session is ending, then stops the transport. The
        /// logout message is flushed for up to one second before the socket closes.
        /// Create a new SDK to sign in again.
        /// </summary>
        public void Deauthenticate()
        {
            ThrowIfDisposed();
            Imported.MGW_SDK_Deauthenticate(Instance);
            if (Transport.IsConnected)
            {
                // Hand the queued logout payload to the socket before closing it.
                Transport.Update(this);
                Transport.WaitForPendingSends(TimeSpan.FromSeconds(1));
            }
            Transport.Disconnect();
        }

        public bool IsAuthenticated
        {
            get
            {
                return !DisposeRequested && Imported.MGW_SDK_IsAuthenticated(Instance) != 0;
            }
        }
        #endregion

        #region Game Metadata
        /// <summary>
        /// Replaces the game's name, logo and theme. The server stores all three as one
        /// object, so a field left empty here is cleared for viewers.
        /// </summary>
        public void SetGameMetadata(GameMetadata InMeta)
        {
            ThrowIfDisposed();
            if (InMeta == null) throw new ArgumentNullException(nameof(InMeta));
            if (InMeta.Name != null && UTF8WithoutBOM.GetByteCount(InMeta.Name) > MaximumGameNameBytes)
                throw new ArgumentException("Game name must be at most " + MaximumGameNameBytes + " UTF-8 bytes.", nameof(InMeta));
            if (InMeta.Logo != null && InMeta.Logo.Length > MaximumLogoBytes)
                throw new ArgumentException("Game logo must be at most 1 MiB of data-URI text.", nameof(InMeta));

            MGW_GameMetadata Meta = new MGW_GameMetadata();
            Meta.GameLogo = NotNull(InMeta.Logo);
            Meta.GameName = NotNull(InMeta.Name);
            Meta.Theme = NotNull(InMeta.Theme);

            Imported.MGW_SDK_SetGameMetadata(Instance, Meta);
            LastMetadata = new GameMetadata { Name = InMeta.Name, Logo = InMeta.Logo, Theme = InMeta.Theme };
        }

#if UNITY_EDITOR || UNITY_STANDALONE
        /// <summary>
        /// Updates the logo and resends the name and theme last passed to SetGameMetadata,
        /// so they are not cleared.
        /// </summary>
        public void SetGameLogo(Texture2D texture)
        {
            ThrowIfDisposed();
            SetGameMetadata(new GameMetadata
            {
                Name = LastMetadata.Name,
                Theme = LastMetadata.Theme,
                Logo = ConvertTextureToBase64(texture)
            });
        }

        public static bool IsCompatibleFormat(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.ARGB32:
                case TextureFormat.RGB24:
                case TextureFormat.RGBA32:
                    return true;
                default:
                    return false;
            }
        }

        public static string ConvertTextureToBase64(Texture2D texture)
        {
            if (texture == null)
            {
                return "";
            }

            if (texture.isReadable)
            {
                if (IsCompatibleFormat(texture.format))
                {
                    byte[] fastPathBytes = texture.EncodeToPNG();
                    return String.Format("data:image/png;base64,{0}", Convert.ToBase64String(fastPathBytes));
                }
            }

            throw new ArgumentException("Texture must be readable and in RGB24, RGBA32 or ARGB32 format");
        }
#endif
        #endregion

        #region Game Texts
        public void SetGameTexts(GameText[] Texts)
        {
            ThrowIfDisposed();
            if (Texts == null) throw new ArgumentNullException(nameof(Texts));
            List<MGW_GameText> NativeTexts = new List<MGW_GameText>();

            foreach (GameText Text in Texts)
            {
                if (Text == null) throw new ArgumentException("Game texts cannot contain null entries.", nameof(Texts));
                MGW_GameText Value = new MGW_GameText();
                Value.Label = NotNull(Text.Label);
                Value.Value = NotNull(Text.Value);
                Value.Icon = NotNull(Text.Icon);

                NativeTexts.Add(Value);
            }

            Imported.MGW_SDK_SetGameTexts(Instance, NativeTexts.ToArray(), (UInt64)NativeTexts.Count);
        }
        #endregion

        #region Polls
        private GCHandle PollDelegateHandle;

        private class InvokePollUpdateParameters
        {
            public PollConfiguration.OnUpdateDelegate Callback;
        }

#if UNITY_EDITOR || UNITY_STANDALONE
        [MonoPInvokeCallback(typeof(GatewayPollUpdateDelegate))]
#endif
        private static void InvokePollUpdate(IntPtr Data, IntPtr Msg)
        {
            if (Data == IntPtr.Zero)
            {
                return;
            }

            GCHandle handle = GCHandle.FromIntPtr(Data);
            if (handle.Target == null)
            {
                return;
            }

            InvokePollUpdateParameters args = handle.Target as InvokePollUpdateParameters;
            if (args == null)
            {
                return;
            }

            if (args.Callback != null)
            {
                try
                {
                    MGW_PollUpdate NativeUpdate = Marshal.PtrToStructure<MGW_PollUpdate>(Msg);

                    PollUpdate Update = new PollUpdate();
                    Update.Winner = NativeUpdate.Winner;
                    Update.WinningVoteCount = NativeUpdate.WinningVoteCount;

                    int[] ManagedResults = new int[NativeUpdate.ResultCount];
                    if (NativeUpdate.ResultCount > 0 && NativeUpdate.Results != IntPtr.Zero)
                        Marshal.Copy(NativeUpdate.Results, ManagedResults, 0, (int)NativeUpdate.ResultCount);

                    Update.Results = new List<int>(ManagedResults);
                    Update.Count = NativeUpdate.Count;
                    Update.Mean = NativeUpdate.Mean;
                    Update.IsFinal = NativeUpdate.IsFinal != 0;

                    args.Callback(Update);
                }
                catch (Exception error)
                {
                    // Exceptions must not unwind across a reverse P/Invoke boundary.
                    LogWarning("Hypeline poll callback failed (" + error.GetType().Name + ").");
                }
            }
        }

        private void ValidatePoll(PollConfiguration Configuration)
        {
            if (Configuration == null) throw new ArgumentNullException(nameof(Configuration));
            if (Configuration.Options == null)
                throw new ArgumentException("Poll options are required.", nameof(Configuration));
            if (Configuration.Options.Count > MaximumPollOptions)
                throw new ArgumentException("A poll can have at most " + MaximumPollOptions + " options.", nameof(Configuration));
            if (UTF8WithoutBOM.GetByteCount(Configuration.Prompt ?? string.Empty) > MaximumPollPromptBytes)
                throw new ArgumentException("Poll prompt must be at most " + MaximumPollPromptBytes + " UTF-8 bytes.", nameof(Configuration));
            foreach (string option in Configuration.Options)
            {
                if (option == null) throw new ArgumentException("Poll options cannot be null.", nameof(Configuration));
                if (UTF8WithoutBOM.GetByteCount(option) > MaximumPollOptionBytes)
                    throw new ArgumentException("Each poll option must be at most " + MaximumPollOptionBytes + " UTF-8 bytes.", nameof(Configuration));
            }
            if (Configuration.DurationInSeconds < 0)
                throw new ArgumentException("Poll duration cannot be negative. Use PollConfiguration.InfiniteDuration (0) for no end time.", nameof(Configuration));
        }

        public void StartPoll(PollConfiguration Configuration)
        {
            ThrowIfDisposed();
            ValidatePoll(Configuration);

            MGW_PollConfiguration NativeConfig = new MGW_PollConfiguration();

            NativeConfig.Prompt = NotNull(Configuration.Prompt);
            NativeConfig.Location = (int)Configuration.Location;
            NativeConfig.Mode = (int)Configuration.Mode;

            List<IntPtr> Strings = new List<IntPtr>();
            GCHandle StringsArrayHandle = default(GCHandle);
            InvokePollUpdateParameters args = new InvokePollUpdateParameters();
            args.Callback = Configuration.OnPollUpdate;
            GCHandle NextPollDelegateHandle = GCHandle.Alloc(args);
            bool started = false;

            try
            {
                foreach (string Opt in Configuration.Options)
                {
                    int Len = UTF8WithoutBOM.GetByteCount(Opt);
                    byte[] buffer = new byte[Len + 1];
                    UTF8WithoutBOM.GetBytes(Opt, 0, Opt.Length, buffer, 0);
                    buffer[Len] = 0;

                    IntPtr NativeStr = Marshal.AllocHGlobal(buffer.Length);
                    Strings.Add(NativeStr);
                    Marshal.Copy(buffer, 0, NativeStr, buffer.Length);
                }

                IntPtr[] StringsArray = Strings.ToArray();
                StringsArrayHandle = GCHandle.Alloc(StringsArray, GCHandleType.Pinned);

                NativeConfig.Options = StringsArrayHandle.AddrOfPinnedObject();
                NativeConfig.OptionsCount = (UInt64)Configuration.Options.Count;
                NativeConfig.Duration = Configuration.DurationInSeconds;
                NativeConfig.OnUpdate = InvokePollUpdate;
                NativeConfig.User = GCHandle.ToIntPtr(NextPollDelegateHandle);

                Imported.MGW_SDK_StartPoll(Instance, NativeConfig);
                started = true;
            }
            finally
            {
                if (StringsArrayHandle.IsAllocated) StringsArrayHandle.Free();
                foreach (IntPtr Allocated in Strings)
                {
                    Marshal.FreeHGlobal(Allocated);
                }
                if (!started) NextPollDelegateHandle.Free();
            }

            if (PollDelegateHandle.IsAllocated)
            {
                PollDelegateHandle.Free();
            }

            PollDelegateHandle = NextPollDelegateHandle;
        }

        public void StopPoll()
        {
            ThrowIfDisposed();
            Imported.MGW_SDK_StopPoll(Instance);
        }

        #endregion

        #region Actions
        private Dictionary<string, MuxyHypelineGameActionUsedEvent> GameActionCallbacks = new Dictionary<string, MuxyHypelineGameActionUsedEvent>();

        /// <summary>
        /// Publishes the full action list. The server resets each action's remaining
        /// count to its Count whenever the list is published.
        /// </summary>
        public void SetGameActions(GameAction[] Actions)
        {
            ThrowIfDisposed();
            if (Actions == null) throw new ArgumentNullException(nameof(Actions));

            // Validate and build everything before touching native or callback state.
            var callbacks = new Dictionary<string, MuxyHypelineGameActionUsedEvent>();
            List<MGW_Action> NativeActions = new List<MGW_Action>();

            foreach (GameAction Action in Actions)
            {
                if (Action == null) throw new ArgumentException("Actions cannot contain null entries.", nameof(Actions));
                RequireID(Action.ID, nameof(Actions));
                if (Action.ID.Length > MaximumActionIDLength)
                    throw new ArgumentException("Action ID '" + Action.ID + "' is longer than " + MaximumActionIDLength + " characters.", nameof(Actions));
                if (callbacks.ContainsKey(Action.ID))
                    throw new ArgumentException("Duplicate action ID '" + Action.ID + "'.", nameof(Actions));
                if (Action.Impact < 0 || Action.Impact > 5)
                    throw new ArgumentException("Action '" + Action.ID + "' must have an Impact from 0 to 5.", nameof(Actions));
                if (Action.Count < 0 || Action.Count > GameAction.InfiniteCount)
                    throw new ArgumentException("Action '" + Action.ID + "' Count must be from 0 to GameAction.InfiniteCount.", nameof(Actions));
                if (!Enum.IsDefined(typeof(GameActionCategory), Action.Category) || !Enum.IsDefined(typeof(GameActionState), Action.State))
                    throw new ArgumentException("Action '" + Action.ID + "' has an invalid Category or State.", nameof(Actions));

                MGW_Action Value = new MGW_Action();
                Value.ID = Action.ID;
                Value.Category = ((int)Action.Category);
                Value.State = ((int)Action.State);
                Value.Impact = Action.Impact;
                Value.Name = NotNull(Action.Name);
                Value.Description = NotNull(Action.Description);
                Value.Icon = NotNull(Action.Icon);
                Value.Count = Action.Count;

                NativeActions.Add(Value);
                callbacks.Add(Action.ID, Action.OnGameActionUsed);
            }

            Imported.MGW_SDK_SetActions(Instance, NativeActions.ToArray(), (UInt64)NativeActions.Count);
            GameActionCallbacks = callbacks;
        }

        public void EnableGameAction(string ID)
        {
            ThrowIfDisposed();
            RequireID(ID, nameof(ID));
            Imported.MGW_SDK_EnableAction(Instance, ID);
        }

        public void DisableGameAction(string ID)
        {
            ThrowIfDisposed();
            RequireID(ID, nameof(ID));
            Imported.MGW_SDK_DisableAction(Instance, ID);
        }

        public void SetMaximumGameActionCount(string ID, int Count)
        {
            ThrowIfDisposed();
            RequireID(ID, nameof(ID));
            Imported.MGW_SDK_SetActionMaximumCount(Instance, ID, Count);
        }

        public void SetGameActionCount(string ID, int Count)
        {
            ThrowIfDisposed();
            RequireID(ID, nameof(ID));
            Imported.MGW_SDK_SetActionCount(Instance, ID, Count);
        }

        public void IncrementGameActionCount(string ID, int Delta)
        {
            ThrowIfDisposed();
            RequireID(ID, nameof(ID));
            Imported.MGW_SDK_IncrementActionCount(Instance, ID, Delta);
        }

        public void DecrementGameActionCount(string ID, int Delta)
        {
            ThrowIfDisposed();
            RequireID(ID, nameof(ID));
            Imported.MGW_SDK_DecrementActionCount(Instance, ID, Delta);
        }

        public delegate void OnGameActionUsedDelegate(GameActionUsed Used);
        private class InvokeOnActionUsedParameters
        {
            public OnGameActionUsedDelegate Callback;
            public SDK Owner;
        }

#if UNITY_EDITOR || UNITY_STANDALONE
        [MonoPInvokeCallback(typeof(GatewayOnActionUsedDelegate))]
#endif
        private static void InvokeOnActionUsed(IntPtr Data, IntPtr Msg)
        {
            if (Data == IntPtr.Zero)
            {
                return;
            }

            GCHandle handle = GCHandle.FromIntPtr(Data);
            if (handle.Target == null)
            {
                return;
            }

            InvokeOnActionUsedParameters args = handle.Target as InvokeOnActionUsedParameters;
            if (args == null)
            {
                return;
            }

            try
            {
                MGW_ActionUsed Value = Marshal.PtrToStructure<MGW_ActionUsed>(Msg);

                GameActionUsed Used = new GameActionUsed();
                Used.TransactionID = Value.TransactionID;
                Used.ActionID = Value.ActionID;
                Used.Cost = Value.Cost;
                Used.UserID = Value.UserID;
                Used.Username = Value.Username;

                MuxyHypelineGameActionUsedEvent Callback;
                if (Used.ActionID != null && args.Owner.GameActionCallbacks.TryGetValue(Used.ActionID, out Callback))
                {
                    Callback?.Invoke(Used);
                }
                args.Callback?.Invoke(Used);
            }
            catch (Exception error)
            {
                // Exceptions must not unwind across a reverse P/Invoke boundary.
                LogWarning("Hypeline action callback failed (" + error.GetType().Name + ").");
            }
        }

        private GCHandle GameActionUsedDelegateHandle;
        public void OnGameActionUsed(OnGameActionUsedDelegate Delegate)
        {
            ThrowIfDisposed();
            InvokeOnActionUsedParameters args = new InvokeOnActionUsedParameters();
            args.Callback = Delegate;
            args.Owner = this;

            GCHandle next = GCHandle.Alloc(args);
            try { Imported.MGW_SDK_OnActionUsed(Instance, InvokeOnActionUsed, GCHandle.ToIntPtr(next)); }
            catch { next.Free(); throw; }
            if (GameActionUsedDelegateHandle.IsAllocated) GameActionUsedDelegateHandle.Free();
            GameActionUsedDelegateHandle = next;
        }

        private static MGW_ActionUsed ToNative(GameActionUsed Used)
        {
            if (Used == null) throw new ArgumentNullException(nameof(Used));
            if (string.IsNullOrEmpty(Used.TransactionID)) throw new ArgumentException("TransactionID is required.", nameof(Used));
            RequireID(Used.ActionID, nameof(Used));

            MGW_ActionUsed Native = new MGW_ActionUsed();
            Native.ActionID = Used.ActionID;
            Native.TransactionID = Used.TransactionID;
            Native.Cost = Used.Cost;
            Native.UserID = NotNull(Used.UserID);
            Native.Username = NotNull(Used.Username);
            return Native;
        }

        /// <summary>
        /// Marks the transaction as fulfilled. The server does not reject a second accept
        /// or an accept after a refund, so track fulfilled TransactionIDs in the game.
        /// </summary>
        public void AcceptGameAction(GameActionUsed Used, string Description)
        {
            ThrowIfDisposed();
            Imported.MGW_SDK_AcceptAction(Instance, ToNative(Used), NotNull(Description));
        }

        /// <summary>
        /// Refunds the viewer's coins. The action's remaining count is not restored.
        /// </summary>
        public void RefundGameAction(GameActionUsed Used, string Description)
        {
            ThrowIfDisposed();
            Imported.MGW_SDK_RefundAction(Instance, ToNative(Used), NotNull(Description));
        }
        #endregion

        #region Bits
        public delegate void OnBitsUsedDelegate(BitsUsed Used);
        private class InvokeOnBitsUsedParameters
        {
            public OnBitsUsedDelegate Callback;
        }

#if UNITY_EDITOR || UNITY_STANDALONE
        [MonoPInvokeCallback(typeof(GatewayOnBitsUsedDelegate))]
#endif
        private static void InvokeOnBitsUsed(IntPtr Data, IntPtr Msg)
        {
            if (Data == IntPtr.Zero)
            {
                return;
            }

            GCHandle handle = GCHandle.FromIntPtr(Data);
            if (handle.Target == null)
            {
                return;
            }

            InvokeOnBitsUsedParameters args = handle.Target as InvokeOnBitsUsedParameters;
            if (args == null)
            {
                return;
            }

            if (args.Callback != null)
            {
                try
                {
                    MGW_BitsUsed Value = Marshal.PtrToStructure<MGW_BitsUsed>(Msg);

                    BitsUsed Used = new BitsUsed();
                    Used.TransactionID = Value.TransactionID;
                    Used.SKU = Value.SKU;
                    Used.Bits = Value.Bits;
                    Used.UserID = Value.UserID;
                    Used.Username = Value.Username;

                    args.Callback(Used);
                }
                catch (Exception error)
                {
                    // Exceptions must not unwind across a reverse P/Invoke boundary.
                    LogWarning("Hypeline Bits callback failed (" + error.GetType().Name + ").");
                }
            }
        }

        private GCHandle BitsUsedDelegateHandle;
        public void OnBitsUsed(OnBitsUsedDelegate Delegate)
        {
            ThrowIfDisposed();
            InvokeOnBitsUsedParameters args = new InvokeOnBitsUsedParameters();
            args.Callback = Delegate;

            GCHandle next = GCHandle.Alloc(args);
            try { Imported.MGW_SDK_OnBitsUsed(Instance, InvokeOnBitsUsed, GCHandle.ToIntPtr(next)); }
            catch { next.Free(); throw; }
            if (BitsUsedDelegateHandle.IsAllocated) BitsUsedDelegateHandle.Free();
            BitsUsedDelegateHandle = next;
        }
        #endregion
    }
}
