using System;
using System.Collections.Generic;
using System.Text;

namespace MuxyHypeline
{
    public class Payload
    {
        public byte[] Bytes { get; set; } = new byte[0];
    }

    /// <summary>
    /// Stands in for the native-backed SDK in Runtime/HypelineSDK.cs. It has the
    /// members the transport calls, with the same signatures, and records what
    /// the transport delivers so a test can check the order.
    /// </summary>
    public class SDK
    {
        public delegate void ForeachPayloadDelegate(Payload Payload);

        public readonly List<string> Events = new List<string>();

        // How many times the transport asked for outgoing payloads.
        public int PayloadRequests;

        // Payloads to hand over the next time the transport asks for them.
        public readonly Queue<string> Payloads = new Queue<string>();

        // Runs inside ReceiveMessage, like a user callback would.
        public Action<string> OnMessage;

        public String GetSandboxURL() { throw new NotSupportedException(); }
        public String GetStagingURL() { throw new NotSupportedException(); }

        public String GetProductionURL() { throw new NotSupportedException(); }

        public void HandleReconnect() { Events.Add("reconnect"); }

        public bool ReceiveMessage(String Message)
        {
            Events.Add("message:" + Message);
            OnMessage?.Invoke(Message);
            return true;
        }

        public void ForeachPayload(ForeachPayloadDelegate Delegate)
        {
            PayloadRequests++;
            while (Payloads.Count != 0) Delegate(new Payload { Bytes = Encoding.UTF8.GetBytes(Payloads.Dequeue()) });
        }
    }
}
