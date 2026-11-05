using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MuxyHypeline.Tests
{
    /// <summary>
    /// A loopback WebSocket server for the transport tests. It performs the HTTP
    /// upgrade itself and writes raw frames, so a test decides exactly which
    /// frames reach the client and in which order.
    /// </summary>
    internal sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener Listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly Func<int, ServerConnection, Task> Script;
        private readonly int MaximumConnections;
        private readonly List<TcpClient> Clients = new List<TcpClient>();
        private int AcceptedCount;

        /// <summary>
        /// Runs script once per accepted connection, after the upgrade, with the
        /// connection's zero-based index. Once maximumConnections have been
        /// accepted the listener stops, so later reconnect attempts are refused.
        /// </summary>
        internal LoopbackServer(int maximumConnections, Func<int, ServerConnection, Task> script)
        {
            MaximumConnections = maximumConnections;
            Script = script;
            Listener.Start();
            Uri = "ws://127.0.0.1:" + ((IPEndPoint)Listener.LocalEndpoint).Port + "/";
            _ = AcceptLoop();
        }

        internal string Uri { get; }

        internal int Accepted { get { return Volatile.Read(ref AcceptedCount); } }

        private async Task AcceptLoop()
        {
            try
            {
                for (int index = 0; index < MaximumConnections; index++)
                {
                    TcpClient client = await Listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    lock (Clients) Clients.Add(client);
                    Interlocked.Increment(ref AcceptedCount);
                    _ = Serve(client, index);
                }
            }
            catch (Exception) { /* Dispose stopped the listener. */ }
            finally { Listener.Stop(); }
        }

        private async Task Serve(TcpClient client, int index)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                await Upgrade(stream).ConfigureAwait(false);
                await Script(index, new ServerConnection(stream)).ConfigureAwait(false);
                // Read until the client goes away. Closing with unread data would
                // reset the connection, and the client could lose frames sent above.
                var buffer = new byte[1024];
                while (await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false) > 0) { }
            }
            catch (Exception) { /* The client aborted or the test ended. */ }
            finally { client.Close(); }
        }

        private static async Task Upgrade(NetworkStream stream)
        {
            var request = new MemoryStream();
            var one = new byte[1];
            while (!EndsWithBlankLine(request))
            {
                if (await stream.ReadAsync(one, 0, 1).ConfigureAwait(false) == 0) throw new EndOfStreamException();
                request.WriteByte(one[0]);
            }
            string key = null;
            foreach (string line in Encoding.ASCII.GetString(request.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                    key = line.Substring("Sec-WebSocket-Key:".Length).Trim();
            }
            if (key == null) throw new InvalidDataException("The upgrade request has no Sec-WebSocket-Key.");
            string accept;
            using (SHA1 sha1 = SHA1.Create())
                accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            byte[] response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + accept + "\r\n\r\n");
            await stream.WriteAsync(response, 0, response.Length).ConfigureAwait(false);
        }

        private static bool EndsWithBlankLine(MemoryStream request)
        {
            byte[] bytes = request.GetBuffer();
            long n = request.Length;
            return n >= 4 && bytes[n - 4] == '\r' && bytes[n - 3] == '\n' && bytes[n - 2] == '\r' && bytes[n - 1] == '\n';
        }

        public void Dispose()
        {
            Listener.Stop();
            lock (Clients)
            {
                foreach (TcpClient client in Clients) client.Close();
                Clients.Clear();
            }
        }
    }

    /// <summary>Writes unmasked server frames to one upgraded connection.</summary>
    internal sealed class ServerConnection
    {
        private readonly Stream Stream;

        internal ServerConnection(Stream stream) { Stream = stream; }

        internal Task SendText(string text) { return Send(0x1, Encoding.UTF8.GetBytes(text)); }

        /// <summary>Sends a close frame with status 1000 (normal closure).</summary>
        internal Task SendClose() { return Send(0x8, new byte[] { 0x03, 0xE8 }); }

        private async Task Send(int opcode, byte[] payload)
        {
            if (payload.Length > ushort.MaxValue) throw new ArgumentException("Test frames are limited to 64 KiB.", nameof(payload));
            var frame = new MemoryStream();
            frame.WriteByte((byte)(0x80 | opcode));
            if (payload.Length < 126)
            {
                frame.WriteByte((byte)payload.Length);
            }
            else
            {
                frame.WriteByte(126);
                frame.WriteByte((byte)(payload.Length >> 8));
                frame.WriteByte((byte)payload.Length);
            }
            frame.Write(payload, 0, payload.Length);
            byte[] bytes = frame.ToArray();
            await Stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }
    }
}
