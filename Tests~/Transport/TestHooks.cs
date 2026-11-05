using System;

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
    }
}
