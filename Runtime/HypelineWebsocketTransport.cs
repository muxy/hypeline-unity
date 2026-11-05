using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MuxyHypeline
{
    public enum Stage
    {
        Production = 0,
        Sandbox = 1
    };

    public enum HypelineTransportState { Disconnected, Connecting, Connected, Reconnecting, Error }

    /// <summary>
    /// Owns one connection session. Call Update on the SDK's owning/main thread:
    /// native payload extraction, reconnect handling and callbacks run there.
    /// Socket tasks never call into the native SDK or Unity APIs.
    /// Ported from the Gateway Unity 1.1.0-rc.1 transport.
    /// </summary>
    public class WebsocketTransport : IDisposable
    {
        // Some Unity runtimes do not finish ConnectAsync on cancellation during
        // TLS negotiation. Bound all in-flight handshakes per managed domain,
        // including those residual operations, rather than opening more sockets.
        private static readonly SemaphoreSlim ConnectSlots = new SemaphoreSlim(4, 4);

        // Incoming messages larger than this close the connection.
        private const int MaximumMessageBytes = 16 * 1024 * 1024;

        private sealed class Connection : IDisposable
        {
            private readonly object SocketGate = new object();
            private bool Disposed;
            internal readonly ClientWebSocket Socket = new ClientWebSocket();
            internal readonly ConcurrentQueue<byte[]> Outgoing = new ConcurrentQueue<byte[]>();
            // Payloads queued but not yet handed to the socket.
            internal int PendingSends;
            internal bool Active;

            internal async Task ConnectAsync(Uri target, CancellationToken token)
            {
                await ConnectSlots.WaitAsync(token).ConfigureAwait(false);
                bool tracked = false;
                try
                {
                    token.ThrowIfCancellationRequested();
                    Socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    // The runtime owns TLS/certificate validation, HTTP upgrade,
                    // proxies, framing and control messages. No custom handshake.
                    Task connect = Socket.ConnectAsync(target, token);
                    _ = ObserveConnect(connect);
                    tracked = true;
                    var canceled = NewCompletion();
                    using (token.Register(() => { canceled.TrySetResult(true); Abort(); }))
                    {
                        await Task.WhenAny(connect, canceled.Task).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        await connect.ConfigureAwait(false);
                    }
                }
                finally { if (!tracked) ConnectSlots.Release(); }
            }

            private async Task ObserveConnect(Task connect)
            {
                try { await connect.ConfigureAwait(false); }
                catch (Exception) { /* Observe late failures after the session has stopped. */ }
                finally
                {
                    try
                    {
                        lock (SocketGate)
                        {
                            // A runtime may publish its socket only after an
                            // earlier Dispose. Close again when it finally exits.
                            if (Disposed) { Socket.Abort(); Socket.Dispose(); }
                        }
                    }
                    finally { ConnectSlots.Release(); }
                }
            }

            internal void Abort()
            {
                lock (SocketGate)
                {
                    Socket.Abort();
                }
            }

            public void Dispose()
            {
                lock (SocketGate)
                {
                    if (Disposed) return;
                    Disposed = true;
                    Socket.Abort();
                    Socket.Dispose();
                }
                while (Outgoing.TryDequeue(out byte[] bytes)) Array.Clear(bytes, 0, bytes.Length);
                Interlocked.Exchange(ref PendingSends, 0);
            }
        }

        // One entry in a session's receive queue: a message, or, when Message is
        // null, the point at which Reconnected became the active connection.
        private struct Inbound
        {
            internal string Message;
            internal Connection Reconnected;
        }

        private sealed class Session
        {
            internal readonly Uri Target;
            internal readonly CancellationTokenSource Lifetime = new CancellationTokenSource();
            internal readonly TaskCompletionSource<bool> Ready = NewCompletion();
            internal readonly TaskCompletionSource<bool> RunRequested = NewCompletion();
            // Received messages and reconnects in arrival order. The queue outlives
            // each connection, so a message that arrives just before a close is still
            // delivered by the next Update instead of being dropped with its socket.
            internal readonly ConcurrentQueue<Inbound> Incoming = new ConcurrentQueue<Inbound>();
            internal SDK Instance;
            internal Connection Connection;
            internal Task Worker;
            internal bool ConnectedBefore;
            internal Session(Uri target, SDK instance) { Target = target; Instance = instance; }
        }

        private readonly object Gate = new object();
        private Session Current;
        private Task Stopping = Task.CompletedTask;
        private bool Disposed;
        private bool Faulted;

        private static TaskCompletionSource<bool> NewCompletion()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public bool IsConnected
        {
            get { lock (Gate) return Current?.Connection?.Active == true; }
        }

        /// <summary>Coarse connection state for UI. Polled from the main thread.</summary>
        public HypelineTransportState ConnectionState
        {
            get
            {
                lock (Gate)
                {
                    if (Current == null) return Faulted ? HypelineTransportState.Error : HypelineTransportState.Disconnected;
                    if (Current.Connection != null && Current.Connection.Active) return HypelineTransportState.Connected;
                    return Current.ConnectedBefore ? HypelineTransportState.Reconnecting : HypelineTransportState.Connecting;
                }
            }
        }

        /// <summary>Last connection/reconnect failure; application payloads are never logged.</summary>
        public Exception LastConnectionError { get; private set; }

        public WebsocketTransport()
        {
#if UNITY_EDITOR
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.quitting += Dispose;
            AssemblyReloadEvents.beforeAssemblyReload += Dispose;
#elif UNITY_STANDALONE
            UnityEngine.Application.quitting += Dispose;
#endif
        }

#if UNITY_EDITOR
        private void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode) Disconnect();
        }
#endif

        /// <summary>
        /// Connects to an explicit endpoint. Production and sandbox helpers always use wss://.
        /// Plain ws:// is accepted only for loopback test servers, because authentication
        /// sends PINs and refresh tokens over this socket.
        /// </summary>
        public Task Open(string uri) { return Start(uri, null); }

        private Task Start(string uri, SDK instance)
        {
            var target = new Uri(uri, UriKind.Absolute);
            if (target.Scheme != "wss" && target.Scheme != "ws")
                throw new ArgumentException("A ws:// or wss:// endpoint is required.", nameof(uri));
            if (target.Scheme == "ws" && !target.IsLoopback)
                throw new ArgumentException("Unencrypted ws:// is only allowed for loopback test servers. Use wss://.", nameof(uri));
            if (target.UserInfo.Length != 0 || target.Fragment.Length != 0)
                throw new ArgumentException("WebSocket endpoints cannot include user info or fragments.", nameof(uri));
            lock (Gate)
            {
                if (Disposed) throw new ObjectDisposedException(nameof(WebsocketTransport));
                if (!Stopping.IsCompleted) throw new InvalidOperationException("Await StopAsync before opening another connection.");
                if (Current != null)
                {
                    if (Current.Target != target || (Current.Instance != null && instance != null && Current.Instance != instance))
                        throw new InvalidOperationException("Stop the current connection before changing its endpoint or SDK.");
                    if (instance != null) RequestRun(Current, instance);
                    return Current.Ready.Task;
                }
                var session = new Session(target, instance);
                Current = session;
                Faulted = false;
                LastConnectionError = null;
                if (instance != null) session.RunRequested.TrySetResult(true);
                session.Worker = Task.Run(() => RunSession(session));
                return session.Ready.Task;
            }
        }

        private static string StageUri(string address)
        {
            // Native versions may return a bare address or an already-prefixed URI.
            var uri = new Uri(address.Contains("://") ? address : "wss://" + address);
            if (uri.Scheme != "wss") throw new ArgumentException("Hypeline stages require WSS.");
            return uri.AbsoluteUri;
        }

        public Task OpenAndRunInSandbox(SDK instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            return Start(StageUri(instance.GetSandboxURL()), instance);
        }

        public Task OpenAndRunInProduction(SDK instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            return Start(StageUri(instance.GetProductionURL()), instance);
        }

        public void Run(SDK instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            lock (Gate)
            {
                if (Disposed) throw new ObjectDisposedException(nameof(WebsocketTransport));
                if (Current == null || !Current.Ready.Task.IsCompleted || Current.Ready.Task.IsFaulted || Current.Ready.Task.IsCanceled)
                    throw new InvalidOperationException("Await Open before starting the transport.");
                RequestRun(Current, instance);
            }
        }

        private static void RequestRun(Session session, SDK instance)
        {
            if (session.Instance != null && session.Instance != instance)
                throw new InvalidOperationException("The connection already belongs to another SDK.");
            session.Instance = instance;
            session.RunRequested.TrySetResult(true);
        }

        private async Task RunSession(Session session)
        {
            int failures = 0;
            try
            {
                while (!session.Lifetime.IsCancellationRequested)
                {
                    var connection = new Connection();
                    Task sender = Task.CompletedTask, receiver = Task.CompletedTask;
                    using (var cycle = CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime.Token))
                    {
                        try
                        {
                            lock (Gate)
                            {
                                session.Lifetime.Token.ThrowIfCancellationRequested();
                                session.Connection = connection;
                            }
                            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cycle.Token))
                            {
                                timeout.CancelAfter(5000);
                                try { await connection.ConnectAsync(session.Target, timeout.Token).ConfigureAwait(false); }
                                catch (OperationCanceledException) when (!cycle.IsCancellationRequested)
                                {
                                    throw new TimeoutException("Hypeline connection did not complete within five seconds.");
                                }
                            }
                            lock (Gate)
                            {
                                cycle.Token.ThrowIfCancellationRequested();
                                // Update calls HandleReconnect at this point in the queue: after
                                // the old connection's messages, before the new connection's.
                                // TryTakeIncoming relies on this entry being queued in the same
                                // lock section that makes the connection active.
                                if (session.ConnectedBefore) session.Incoming.Enqueue(new Inbound { Reconnected = connection });
                                connection.Active = true;
                                session.ConnectedBefore = true;
                            }
                            failures = 0;
                            session.Ready.TrySetResult(true);
                            await session.RunRequested.Task.ConfigureAwait(false);
                            cycle.Token.ThrowIfCancellationRequested();
                            sender = SendLoop(connection, cycle.Token);
                            receiver = ReceiveLoop(session, connection, cycle.Token);
                            await (await Task.WhenAny(sender, receiver).ConfigureAwait(false)).ConfigureAwait(false);
                        }
                        catch (Exception error) when (error is WebSocketException || error is SocketException || error is AuthenticationException || error is IOException || error is OperationCanceledException || error is ObjectDisposedException || error is TimeoutException)
                        {
                            if (!session.Lifetime.IsCancellationRequested)
                            {
                                LastConnectionError = error;
                                if (!session.ConnectedBefore)
                                {
                                    lock (Gate) Faulted = true;
                                    session.Ready.TrySetException(error);
                                    return;
                                }
                            }
                        }
                        finally
                        {
                            lock (Gate) connection.Active = false;
                            cycle.Cancel();
                            connection.Abort();
                            // No time limit: a receiver still running after this could queue a
                            // message behind the next connection's reconnect entry. Both loops
                            // end once cycle is canceled, because WithCancellation abandons a
                            // send or receive that the socket leaves pending.
                            try { await Task.WhenAll(sender, receiver).ConfigureAwait(false); }
                            catch (Exception) { /* Both tasks are observed; the first error is recorded above. */ }
                            lock (Gate)
                            {
                                if (session.Connection == connection) session.Connection = null;
                                connection.Dispose();
                            }
                        }
                    }
                    await Task.Delay(Math.Min(30000, 500 * ++failures), session.Lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested) { }
            catch (Exception error)
            {
                LastConnectionError = error;
                lock (Gate) Faulted = true;
                session.Ready.TrySetException(error);
            }
            finally
            {
                lock (Gate)
                {
                    session.Ready.TrySetCanceled();
                    if (Current == session) Current = null;
                    // An ended session delivers nothing more. This also drops anything
                    // the receiver queued while StopAsync was shutting it down.
                    while (session.Incoming.TryDequeue(out _)) { }
                    session.Lifetime.Dispose();
                }
            }
        }

        private static async Task SendLoop(Connection connection, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                while (connection.Outgoing.TryDequeue(out byte[] bytes))
                {
                    try
                    {
                        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            timeout.CancelAfter(5000);
                            Task send = connection.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token);
                            await WithCancellation(send, timeout.Token).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        // Also safe if the send was abandoned: the socket copies the payload
                        // only once it holds its send lock, and as this token is canceled
                        // by then, it aborts before it writes anything.
                        Array.Clear(bytes, 0, bytes.Length);
                        Interlocked.Decrement(ref connection.PendingSends);
                    }
                }
                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }

        private static async Task ReceiveLoop(Session session, Connection connection, CancellationToken token)
        {
            while (true)
            {
                using (var memory = new MemoryStream())
                {
                    var buffer = new byte[4096];
                    WebSocketReceiveResult result;
                    do
                    {
                        Task<WebSocketReceiveResult> receive = connection.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                        await WithCancellation(receive, token).ConfigureAwait(false);
                        result = await receive.ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close) throw new EndOfStreamException("Hypeline connection closed.");
                        if (result.MessageType != WebSocketMessageType.Text || memory.Length + result.Count > MaximumMessageBytes)
                            throw new InvalidDataException("Invalid Hypeline WebSocket message.");
                        memory.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    // Keep a complete message even when this connection is being torn
                    // down; Update still delivers it unless the session was stopped.
                    session.Incoming.Enqueue(new Inbound { Message = Encoding.UTF8.GetString(memory.ToArray()) });
                    token.ThrowIfCancellationRequested();
                }
            }
        }

        // Waits for a socket send or receive, or abandons it when token is canceled first.
        // On Mono, a send that is in flight when the server's close frame arrives can stay
        // pending forever, even after Abort and its own cancellation. Teardown waits for
        // both loops, so it would wait forever too: no reconnect, and StopAsync never
        // completes. A loop whose operation is abandoned throws, so a receiver never
        // queues a message after teardown.
        private static async Task WithCancellation(Task operation, CancellationToken token)
        {
            if (!operation.IsCompleted)
            {
                var canceled = NewCompletion();
                using (token.Register(() => canceled.TrySetResult(true)))
                {
                    await Task.WhenAny(operation, canceled.Task).ConfigureAwait(false);
                }
                if (!operation.IsCompleted)
                {
                    _ = ObserveAbandoned(operation);
                    throw new OperationCanceledException(token);
                }
            }
            await operation.ConfigureAwait(false);
        }

        private static async Task ObserveAbandoned(Task operation)
        {
            try { await operation.ConfigureAwait(false); }
            catch (Exception) { /* Observe a late failure; nothing waits for it any more. */ }
        }

        // Takes the next entry for Update to deliver, as long as the session is current.
        // When none is left, active is the connection to hand payloads to, or null. It
        // is read in the same lock section that found the queue empty. The worker queues
        // a reconnect entry in the lock section that makes its connection active, so the
        // active connection's entry, if it has one, has already been taken and Update has
        // run HandleReconnect for it. Reading it in a later lock section would let a
        // reconnect land in between, and payloads queued while disconnected would go out
        // ahead of the native re-authentication.
        private bool TryTakeIncoming(Session session, out Inbound next, out Connection active)
        {
            lock (Gate)
            {
                active = null;
                while (Current == session && session.Incoming.TryDequeue(out next))
                {
                    // A reconnect only matters while its connection is still the active
                    // one. If it has closed already, the next connection queues its own,
                    // and each HandleReconnect queues another native re-authentication.
                    if (next.Message != null || (next.Reconnected == session.Connection && next.Reconnected.Active)) return true;
                }
                next = default;
                if (Current == session && session.Connection != null && session.Connection.Active) active = session.Connection;
                return false;
            }
        }

        /// <summary>Dispatch callbacks and queue native payloads on the owning/main thread.</summary>
        public void Update(SDK instance)
        {
            Session session;
            lock (Gate)
            {
                session = Current;
                if (session == null || session.Instance != instance) return;
            }
            // Never hold Gate across a native/user callback: it may synchronously stop/dispose.
            // Received messages are delivered even after their connection has closed.
            Connection connection;
            while (TryTakeIncoming(session, out Inbound next, out connection))
            {
                if (next.Message != null) instance.ReceiveMessage(next.Message);
                else instance.HandleReconnect();
            }
            // Payloads stay queued in the native SDK until a connection is active and any
            // reconnect to it has been handled; TryTakeIncoming returns only such a one.
            if (connection != null)
                instance.ForeachPayload(payload =>
                {
                    lock (Gate)
                    {
                        if (Current == session && session.Connection == connection)
                        {
                            Interlocked.Increment(ref connection.PendingSends);
                            connection.Outgoing.Enqueue(payload.Bytes);
                        }
                        else Array.Clear(payload.Bytes, 0, payload.Bytes.Length);
                    }
                });
        }

        /// <summary>
        /// Waits until payloads already handed to the transport have been written to
        /// the socket, or the timeout passes. Returns true when everything was sent.
        /// </summary>
        public bool WaitForPendingSends(TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                Connection connection;
                lock (Gate)
                {
                    connection = Current?.Connection;
                    if (connection == null || !connection.Active) return false;
                }
                if (Volatile.Read(ref connection.PendingSends) <= 0) return true;
                if (DateTime.UtcNow >= deadline) return false;
                Thread.Sleep(10);
            }
        }

        /// <summary>Queues native payloads on the owning/main thread; Update normally does this.</summary>
        public Task SendMessages(SDK instance) { Update(instance); return Task.CompletedTask; }

        /// <summary>Compatibility entry point. The tracked receiver already owns socket reads.</summary>
        public Task ReceiveMessage(SDK instance) { Update(instance); return Task.CompletedTask; }

        public Task StopAsync()
        {
            lock (Gate)
            {
                Faulted = false;
                Session session = Current;
                if (session == null) return Stopping;
                Current = null;
                // Nothing is delivered after a stop, so drop messages that already
                // arrived now instead of when the worker exits.
                while (session.Incoming.TryDequeue(out _)) { }
                session.Lifetime.Cancel();
                session.RunRequested.TrySetCanceled();
                session.Ready.TrySetCanceled();
                session.Connection?.Abort();
                Stopping = session.Worker;
                return Stopping;
            }
        }

        // Workers never wait for Unity, so synchronous lifecycle wrappers can join safely.
        public void Disconnect() { StopAsync().GetAwaiter().GetResult(); }

        public Task DisposeAsync()
        {
            lock (Gate)
            {
                if (!Disposed)
                {
                    Disposed = true;
#if UNITY_EDITOR
                    EditorApplication.playModeStateChanged -= OnPlayModeChanged;
                    EditorApplication.quitting -= Dispose;
                    AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
#elif UNITY_STANDALONE
                    UnityEngine.Application.quitting -= Dispose;
#endif
                }
                return StopAsync();
            }
        }

        public void Dispose() { DisposeAsync().GetAwaiter().GetResult(); }
    }
}
