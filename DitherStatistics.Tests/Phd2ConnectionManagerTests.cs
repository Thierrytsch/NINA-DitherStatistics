using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DitherStatistics.Plugin;
using Xunit;

namespace DitherStatistics.Tests {
    /// <summary>
    /// Tests for the Phd2ConnectionManager reconnect policy against a real
    /// PHD2Client and FakePhd2Server over loopback, with the three delays
    /// shortened via the test-only constructor parameters. The lifecycle rules
    /// under test are the A1 semantics: an involuntary ConnectionLost triggers
    /// a reconnect, an explicit Disconnected never does, and after Stop() the
    /// manager is inert even if the server comes back.
    /// </summary>
    public class Phd2ConnectionManagerTests {
        private const int INITIAL_DELAY_MS = 50;
        private const int RETRY_DELAY_MS = 100;
        private const int RECONNECT_DELAY_MS = 100;

        /// <summary>
        /// Longer than initial + reconnect + several retry periods: if the manager
        /// were going to (re)connect, it would have happened well within this.
        /// </summary>
        private const int NO_RECONNECT_OBSERVATION_MS = 1000;

        private static Phd2ConnectionManager CreateManager(PHD2Client client) =>
            new Phd2ConnectionManager(client, INITIAL_DELAY_MS, RETRY_DELAY_MS, RECONNECT_DELAY_MS);

        /// <summary>
        /// Grabs a free loopback port and releases it immediately, so a client can
        /// be pointed at it while nothing is listening (connection refused) and a
        /// FakePhd2Server can later be bound to that exact same port.
        /// </summary>
        private static int ReserveFreeLoopbackPort() {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static async Task WaitUntil(Func<bool> condition, string description, int timeoutMs = 10000) {
            var sw = Stopwatch.StartNew();
            while (!condition()) {
                Assert.True(sw.ElapsedMilliseconds < timeoutMs, $"Timeout after {timeoutMs} ms waiting for: {description}");
                await Task.Delay(25);
            }
        }

        [Fact]
        public async Task ConnectionLost_TriggersReconnect() {
            using var server = new FakePhd2Server();
            using var client = new PHD2Client("127.0.0.1", server.Port);
            int connects = 0;
            client.ConnectionStatusChanged += (s, status) => {
                if (status == Phd2ConnectionStatus.Connected) Interlocked.Increment(ref connects);
            };
            using var manager = CreateManager(client);

            manager.Start();
            await WaitUntil(() => Volatile.Read(ref connects) == 1, "initial auto-connect");

            // Server drops the connection but keeps listening - the manager must
            // reconnect after the reconnect delay
            server.DropConnection();
            await WaitUntil(() => Volatile.Read(ref connects) >= 2, "reconnect after connection loss");
            Assert.True(client.IsConnected);
        }

        [Fact]
        public async Task Stop_PreventsReconnect_EvenThoughServerIsAvailable() {
            using var server = new FakePhd2Server();
            using var client = new PHD2Client("127.0.0.1", server.Port);
            int connects = 0;
            client.ConnectionStatusChanged += (s, status) => {
                if (status == Phd2ConnectionStatus.Connected) Interlocked.Increment(ref connects);
            };
            using var manager = CreateManager(client);

            manager.Start();
            await WaitUntil(() => Volatile.Read(ref connects) == 1, "initial auto-connect");

            // Stop the manager (plugin shutdown), then lose the connection while
            // the server stays available for a reconnect that must never come
            manager.Stop();
            server.DropConnection();
            await WaitUntil(() => !client.IsConnected, "read loop noticing the drop");

            await Task.Delay(NO_RECONNECT_OBSERVATION_MS);
            Assert.False(client.IsConnected);
            Assert.Equal(1, Volatile.Read(ref connects));
        }

        [Fact]
        public async Task ExplicitDisconnect_DoesNotTriggerReconnect() {
            using var server = new FakePhd2Server();
            using var client = new PHD2Client("127.0.0.1", server.Port);
            int connects = 0;
            client.ConnectionStatusChanged += (s, status) => {
                if (status == Phd2ConnectionStatus.Connected) Interlocked.Increment(ref connects);
            };
            using var manager = CreateManager(client);

            manager.Start();
            await WaitUntil(() => Volatile.Read(ref connects) == 1, "initial auto-connect");

            // An explicit Disconnect (the plugin-shutdown path) must not be treated
            // like a connection loss - even with the manager still running
            client.Disconnect();

            await Task.Delay(NO_RECONNECT_OBSERVATION_MS);
            Assert.False(client.IsConnected);
            Assert.Equal(1, Volatile.Read(ref connects));
        }

        [Fact]
        public async Task RetriesUntilSuccess_WhenServerBecomesAvailableLater() {
            int port = ReserveFreeLoopbackPort();
            using var client = new PHD2Client("127.0.0.1", port);
            int connects = 0;
            int failures = 0;
            client.ConnectionStatusChanged += (s, status) => {
                if (status == Phd2ConnectionStatus.Connected) Interlocked.Increment(ref connects);
                if (status == Phd2ConnectionStatus.ConnectionFailed) Interlocked.Increment(ref failures);
            };
            using var manager = CreateManager(client);

            // Nothing is listening on the port yet: the initial attempt and at
            // least one retry must fail before the server appears
            manager.Start();
            await WaitUntil(() => Volatile.Read(ref failures) >= 2, "at least two failed attempts while nothing is listening");
            Assert.Equal(0, Volatile.Read(ref connects));

            using var server = new FakePhd2Server(port);
            await WaitUntil(() => Volatile.Read(ref connects) == 1, "connect once the server becomes available");
            Assert.True(client.IsConnected);
        }

        [Fact]
        public async Task Stop_DuringRetryLoop_PreventsLaterConnect() {
            int port = ReserveFreeLoopbackPort();
            using var client = new PHD2Client("127.0.0.1", port);
            int connects = 0;
            int failures = 0;
            client.ConnectionStatusChanged += (s, status) => {
                if (status == Phd2ConnectionStatus.Connected) Interlocked.Increment(ref connects);
                if (status == Phd2ConnectionStatus.ConnectionFailed) Interlocked.Increment(ref failures);
            };
            using var manager = CreateManager(client);

            // Let the manager fail a couple of times while nothing is listening,
            // then stop it mid-retry-loop - the loop must halt for good
            manager.Start();
            await WaitUntil(() => Volatile.Read(ref failures) >= 2, "at least two failed attempts while nothing is listening");
            manager.Stop();

            // The server only becomes available after Stop() - a still-running
            // retry loop would have connected to it
            using var server = new FakePhd2Server(port);
            await Task.Delay(NO_RECONNECT_OBSERVATION_MS);
            Assert.False(client.IsConnected);
            Assert.Equal(0, Volatile.Read(ref connects));
        }
    }
}
