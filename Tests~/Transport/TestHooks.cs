using System;
using System.Net.WebSockets;
using System.Threading.Tasks;

namespace MuxyHypeline
{
    /// <summary>
    /// Hooks that run.sh adds to the test build of the transport. They are not
    /// in the shipped runtime.
    /// </summary>
    internal static class TestHooks
    {
        // Called by Update after it has delivered what was received and before
        // it hands payloads to the connection.
        internal static Action AfterDrain;

        // Called with each socket send and receive the transport starts. The
        // transport waits for the returned task instead, so a test can stand in
        // for an operation that never completes, as one on Mono can after Abort.
        internal static Func<Task, Task> Sending;
        internal static Func<Task<WebSocketReceiveResult>, Task<WebSocketReceiveResult>> Receiving;
    }
}
