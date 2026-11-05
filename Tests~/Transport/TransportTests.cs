using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace MuxyHypeline.Tests
{
    /// <summary>
    /// Regression tests for Runtime/HypelineWebsocketTransport.cs against a
    /// loopback server. The test thread plays the game's main thread: it is the
    /// only caller of Update, so every SDK callback runs on it.
    /// </summary>
    internal static class TransportTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private static int Main(string[] args)
        {
            var tests = new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("MessageThenClose", MessageThenClose),
                new KeyValuePair<string, Action>("MessageCloseReconnect", MessageCloseReconnect),
                new KeyValuePair<string, Action>("ClosedReconnectIsSkipped", ClosedReconnectIsSkipped),
                new KeyValuePair<string, Action>("PayloadsWaitForReconnect", PayloadsWaitForReconnect),
                new KeyValuePair<string, Action>("NothingAfterStop", NothingAfterStop),
                new KeyValuePair<string, Action>("StopFromCallback", StopFromCallback),
                new KeyValuePair<string, Action>("CloseDuringStuckSend", CloseDuringStuckSend),
                new KeyValuePair<string, Action>("StopDuringStuckReceive", StopDuringStuckReceive),
            };
            int failed = 0, ran = 0;
            foreach (KeyValuePair<string, Action> test in tests)
            {
                if (args.Length != 0 && Array.IndexOf(args, test.Key) < 0) continue;
                ran++;
                try
                {
                    test.Value();
                    Console.WriteLine("PASS " + test.Key);
                }
                catch (Exception error)
                {
                    failed++;
                    Console.WriteLine("FAIL " + test.Key + ": " + error.Message);
                }
            }
            Console.WriteLine(failed == 0 ? ran + " transport tests passed." : failed + " of " + ran + " transport tests failed.");
            return failed == 0 && ran != 0 ? 0 : 1;
        }

        // The server sends a message and closes before the game's next Update.
        // The message must still reach the SDK.
        private static void MessageThenClose()
        {
            using (var server = new LoopbackServer(1, async (index, connection) =>
            {
                await connection.SendText("first");
                await connection.SendClose();
            }))
            using (var transport = new WebsocketTransport())
            {
                var sdk = new SDK();
                Start(transport, server, sdk);
                WaitUntil(() => !transport.IsConnected, "the transport has processed the close");
                transport.Update(sdk);
                Expect(sdk, "message:first");
                if (sdk.PayloadRequests != 0) throw new Exception("payloads were requested without an active connection");
            }
        }

        // Callbacks keep arrival order across a reconnect: the closed connection's
        // messages, then HandleReconnect, then the new connection's messages.
        private static void MessageCloseReconnect()
        {
            using (var server = new LoopbackServer(2, async (index, connection) =>
            {
                if (index == 0)
                {
                    await connection.SendText("first");
                    await connection.SendClose();
                }
                else await connection.SendText("second");
            }))
            using (var transport = new WebsocketTransport())
            {
                var sdk = new SDK();
                Start(transport, server, sdk);
                // No Update until the transport has reconnected, so "first" is
                // still queued when the reconnect happens.
                WaitUntil(() => server.Accepted == 2 && transport.IsConnected, "the transport has reconnected");
                WaitUntil(() => { transport.Update(sdk); return sdk.Events.Contains("message:second"); }, "the second connection's message is delivered");
                Expect(sdk, "message:first", "reconnect", "message:second");
                if (sdk.PayloadRequests == 0) throw new Exception("payloads were not requested while connected");
            }
        }

        // A reconnect whose connection closed before any Update is not reported:
        // the next connection reports its own, and the native SDK would queue its
        // re-authentication once per HandleReconnect.
        private static void ClosedReconnectIsSkipped()
        {
            using (var server = new LoopbackServer(3, async (index, connection) =>
            {
                if (index == 0) await connection.SendText("first");
                if (index < 2) await connection.SendClose();
            }))
            using (var transport = new WebsocketTransport())
            {
                var sdk = new SDK();
                Start(transport, server, sdk);
                WaitUntil(() => server.Accepted == 3 && transport.IsConnected, "the transport has reconnected twice");
                transport.Update(sdk);
                Expect(sdk, "message:first", "reconnect");
            }
        }

        // A reconnect can complete while Update is between delivering what was
        // received and handing payloads over, as when the main thread is
        // preempted there. The new connection must not get payloads before its
        // HandleReconnect: the native SDK queues its re-authentication then, and
        // ws-api refuses requests that arrive ahead of it. TestHooks.AfterDrain
        // (added by run.sh to the test build) makes the reconnect land there.
        private static void PayloadsWaitForReconnect()
        {
            using (var server = new LoopbackServer(2, async (index, connection) =>
            {
                if (index == 0) await connection.SendClose();
            }))
            using (var transport = new WebsocketTransport())
            {
                var sdk = new SDK();
                Start(transport, server, sdk);
                WaitUntil(() => !transport.IsConnected, "the first connection has closed");
                transport.Update(sdk);
                int before = sdk.PayloadRequests;

                bool armed = true;
                TestHooks.AfterDrain = () =>
                {
                    if (!armed) return;
                    armed = false;
                    WaitUntil(() => transport.IsConnected, "the transport has reconnected");
                };
                try { transport.Update(sdk); }
                finally { TestHooks.AfterDrain = null; }
                if (sdk.PayloadRequests != before && !sdk.Events.Contains("reconnect"))
                    throw new Exception("payloads were requested before HandleReconnect");

                transport.Update(sdk);
                Expect(sdk, "reconnect");
                if (sdk.PayloadRequests == before) throw new Exception("payloads were not requested after the reconnect");
            }
        }

        // Nothing is delivered after StopAsync, including messages received before it.
        private static void NothingAfterStop()
        {
            using (var server = new LoopbackServer(1, async (index, connection) =>
            {
                await connection.SendText("first");
                await connection.SendClose();
            }))
            using (var transport = new WebsocketTransport())
            {
                var sdk = new SDK();
                Start(transport, server, sdk);
                WaitUntil(() => !transport.IsConnected, "the transport has processed the close");
                if (!transport.StopAsync().Wait(Timeout)) throw new TimeoutException("StopAsync did not complete.");
                transport.Update(sdk);
                Expect(sdk);
            }
        }

        // A callback that stops the transport ends delivery of the queued messages.
        private static void StopFromCallback()
        {
            using (var server = new LoopbackServer(1, async (index, connection) =>
            {
                await connection.SendText("first");
                await connection.SendText("second");
                await connection.SendClose();
            }))
            using (var transport = new WebsocketTransport())
            {
                var sdk = new SDK();
                sdk.OnMessage = message => transport.StopAsync();
                Start(transport, server, sdk);
                WaitUntil(() => !transport.IsConnected, "the transport has processed the close");
                transport.Update(sdk);
                Expect(sdk, "message:first");
            }
        }

        // On Mono, a send that is in flight when the server's close frame arrives
        // can stay pending forever, even after Abort and cancellation. Teardown
        // must not wait for it: the transport still reconnects and can be stopped.
        // TestHooks.Sending (added by run.sh) makes the first send one that never
        // completes.
        private static void CloseDuringStuckSend()
        {
            var stuck = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var server = new LoopbackServer(2, async (index, connection) =>
            {
                if (index != 0) return;
                await stuck.Task;
                await connection.SendClose();
            }))
            {
                // Not disposed if the test fails: Dispose would wait for the stuck worker.
                var transport = new WebsocketTransport();
                var sdk = new SDK();
                sdk.Payloads.Enqueue("payload");
                TestHooks.Sending = send => stuck.TrySetResult(true) ? new TaskCompletionSource<bool>().Task : send;
                try
                {
                    Start(transport, server, sdk);
                    WaitUntil(() => { transport.Update(sdk); return stuck.Task.IsCompleted; }, "a send is in progress");
                    WaitUntil(() => server.Accepted == 2 && transport.IsConnected, "the transport has reconnected");
                }
                finally { TestHooks.Sending = null; }
                Stop(transport);
            }
        }

        // The same holds for a receive: StopAsync completes although the receive
        // never does. TestHooks.Receiving (added by run.sh) makes it never complete.
        private static void StopDuringStuckReceive()
        {
            var stuck = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var server = new LoopbackServer(1, (index, connection) => Task.CompletedTask))
            {
                // Not disposed if the test fails: Dispose would wait for the stuck worker.
                var transport = new WebsocketTransport();
                TestHooks.Receiving = receive => stuck.TrySetResult(true) ? new TaskCompletionSource<WebSocketReceiveResult>().Task : receive;
                try
                {
                    Start(transport, server, new SDK());
                    if (!stuck.Task.Wait(Timeout)) throw new TimeoutException("timed out waiting until a receive is in progress");
                }
                finally { TestHooks.Receiving = null; }
                Stop(transport);
            }
        }

        private static void Start(WebsocketTransport transport, LoopbackServer server, SDK sdk)
        {
            if (!transport.Open(server.Uri).Wait(Timeout)) throw new TimeoutException("Open did not complete.");
            transport.Run(sdk);
        }

        // Fails instead of blocking when the transport's worker never finishes.
        private static void Stop(WebsocketTransport transport)
        {
            if (!transport.StopAsync().Wait(Timeout)) throw new TimeoutException("StopAsync did not complete.");
            transport.Dispose();
        }

        private static void WaitUntil(Func<bool> condition, string what)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.Elapsed > Timeout) throw new TimeoutException("timed out waiting until " + what);
                Thread.Sleep(10);
            }
        }

        private static void Expect(SDK sdk, params string[] expected)
        {
            string want = string.Join(", ", expected), got = string.Join(", ", sdk.Events);
            if (want != got) throw new Exception("expected callbacks [" + want + "] but got [" + got + "]");
        }
    }
}
