using System;
using System.Diagnostics;
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
    }
}
