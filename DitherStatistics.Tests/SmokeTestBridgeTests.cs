using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DitherStatistics.Plugin;
using Xunit;

namespace DitherStatistics.Tests {
    /// <summary>
    /// Loopback-socket tests for the SmokeTest diagnostic bridge, in the spirit of
    /// FakePhd2Server/Phd2EndToEndTests: a real client over a real socket against the
    /// bridge with a fake adapter. Covers the protocol/socket layer (get-state
    /// roundtrip, command dispatch, error handling, the disabled flag, disposal) - no
    /// VM/NINA/UI thread involved (Application.Current is null in tests, so the bridge
    /// runs the adapter inline).
    /// </summary>
    public class SmokeTestBridgeTests {
        /// <summary>
        /// Records every call so tests can assert dispatch, and returns a fixed state
        /// snapshot / prior-toggle values.
        /// </summary>
        private sealed class FakeAdapter : ISmokeTestBridgeAdapter {
            public readonly List<string> Invoked = new List<string>();
            public readonly List<(string Name, bool Value)> Toggles = new List<(string, bool)>();
            public readonly List<string> Created = new List<string>();
            public readonly List<string> Selected = new List<string>();
            public readonly List<string> Deleted = new List<string>();
            public IDictionary<string, object> State = new Dictionary<string, object> {
                ["TotalDithers"] = 7,
                ["SuccessRate"] = 85.5,
                ["SelectedProfileName"] = "Default"
            };
            public bool PriorToggle = true;
            public bool ThrowOnInvoke;

            public IDictionary<string, object> GetState() => State;

            public void Invoke(string name) {
                if (ThrowOnInvoke) throw new InvalidOperationException("boom");
                Invoked.Add(name);
            }

            public bool SetToggle(string name, bool value) {
                Toggles.Add((name, value));
                return PriorToggle;
            }

            public void CreateProfile(string name) => Created.Add(name);
            public void SelectProfile(string name) => Selected.Add(name);
            public void DeleteProfile(string name) => Deleted.Add(name);
        }

        /// <summary>A tiny synchronous line client for the bridge.</summary>
        private sealed class BridgeConn : IDisposable {
            private readonly TcpClient client;
            private readonly StreamReader reader;
            private readonly StreamWriter writer;

            public BridgeConn(int port) {
                client = new TcpClient();
                client.Connect("127.0.0.1", port);
                var stream = client.GetStream();
                reader = new StreamReader(stream, new UTF8Encoding(false));
                writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            }

            public JsonElement Send(string json) {
                writer.WriteLine(json);
                string line = reader.ReadLine();
                Assert.NotNull(line);
                return JsonDocument.Parse(line).RootElement.Clone();
            }

            public void Dispose() {
                try { reader.Dispose(); } catch { }
                try { writer.Dispose(); } catch { }
                try { client.Close(); } catch { }
            }
        }

        private static SmokeTestBridge StartBridge(ISmokeTestBridgeAdapter adapter) {
            // Port 0 -> OS-assigned free port, exposed via bridge.Port
            var bridge = new SmokeTestBridge(adapter, enabled: true, port: 0);
            bridge.Start();
            Assert.True(bridge.IsListening);
            return bridge;
        }

        [Fact]
        public void GetState_RoundTrips_TheAdapterSnapshot() {
            var adapter = new FakeAdapter();
            using var bridge = StartBridge(adapter);
            using var conn = new BridgeConn(bridge.Port);

            var response = conn.Send("{\"cmd\":\"get-state\"}");

            Assert.True(response.GetProperty("ok").GetBoolean());
            var state = response.GetProperty("state");
            Assert.Equal(7, state.GetProperty("TotalDithers").GetInt32());
            Assert.Equal(85.5, state.GetProperty("SuccessRate").GetDouble(), precision: 10);
            Assert.Equal("Default", state.GetProperty("SelectedProfileName").GetString());
        }

        [Fact]
        public void Invoke_DispatchesTheNamedCommand() {
            var adapter = new FakeAdapter();
            using var bridge = StartBridge(adapter);
            using var conn = new BridgeConn(bridge.Port);

            var response = conn.Send("{\"cmd\":\"invoke\",\"name\":\"Recalc\"}");

            Assert.True(response.GetProperty("ok").GetBoolean());
            Assert.Equal(new[] { "Recalc" }, adapter.Invoked);
        }

        [Fact]
        public void SetToggle_SetsValue_AndReturnsPriorState() {
            var adapter = new FakeAdapter { PriorToggle = true };
            using var bridge = StartBridge(adapter);
            using var conn = new BridgeConn(bridge.Port);

            var response = conn.Send("{\"cmd\":\"set-toggle\",\"name\":\"Persistence\",\"value\":false}");

            Assert.True(response.GetProperty("ok").GetBoolean());
            Assert.True(response.GetProperty("prior").GetBoolean());
            Assert.Equal(("Persistence", false), Assert.Single(adapter.Toggles));
        }

        [Fact]
        public void ProfileCommands_Dispatch() {
            var adapter = new FakeAdapter();
            using var bridge = StartBridge(adapter);
            using var conn = new BridgeConn(bridge.Port);

            Assert.True(conn.Send("{\"cmd\":\"create-profile\",\"name\":\"SmokeTestB\"}").GetProperty("ok").GetBoolean());
            Assert.True(conn.Send("{\"cmd\":\"select-profile\",\"name\":\"Default\"}").GetProperty("ok").GetBoolean());
            Assert.True(conn.Send("{\"cmd\":\"delete-profile\",\"name\":\"SmokeTestB\"}").GetProperty("ok").GetBoolean());

            Assert.Equal("SmokeTestB", Assert.Single(adapter.Created));
            Assert.Equal("Default", Assert.Single(adapter.Selected));
            Assert.Equal("SmokeTestB", Assert.Single(adapter.Deleted));
        }

        [Fact]
        public void UnknownCommand_ReturnsError() {
            var adapter = new FakeAdapter();
            using var bridge = StartBridge(adapter);
            using var conn = new BridgeConn(bridge.Port);

            var response = conn.Send("{\"cmd\":\"frobnicate\"}");

            Assert.False(response.GetProperty("ok").GetBoolean());
            Assert.Contains("unknown command", response.GetProperty("error").GetString());
        }

        [Fact]
        public void MissingRequiredArg_ReturnsError() {
            var adapter = new FakeAdapter();
            using var bridge = StartBridge(adapter);
            using var conn = new BridgeConn(bridge.Port);

            var response = conn.Send("{\"cmd\":\"invoke\"}");

            Assert.False(response.GetProperty("ok").GetBoolean());
            Assert.Empty(adapter.Invoked);
        }

        [Fact]
        public void AdapterException_BecomesErrorResponse_NotACrash() {
            var adapter = new FakeAdapter { ThrowOnInvoke = true };
            using var bridge = StartBridge(adapter);
            using var conn = new BridgeConn(bridge.Port);

            var response = conn.Send("{\"cmd\":\"invoke\",\"name\":\"ClearData\"}");

            Assert.False(response.GetProperty("ok").GetBoolean());
            Assert.Contains("boom", response.GetProperty("error").GetString());
        }

        [Fact]
        public void MalformedJson_ReturnsError_AndConnectionSurvives() {
            var adapter = new FakeAdapter();
            using var bridge = StartBridge(adapter);
            using var conn = new BridgeConn(bridge.Port);

            var bad = conn.Send("this is not json {{{");
            Assert.False(bad.GetProperty("ok").GetBoolean());
            Assert.Contains("invalid JSON", bad.GetProperty("error").GetString());

            // The same connection still serves the next request
            var good = conn.Send("{\"cmd\":\"get-state\"}");
            Assert.True(good.GetProperty("ok").GetBoolean());
        }

        [Fact]
        public void Disabled_DoesNotListen() {
            var adapter = new FakeAdapter();
            using var bridge = new SmokeTestBridge(adapter, enabled: false, port: 0);
            bridge.Start();

            Assert.False(bridge.IsListening);
            // A disabled bridge has no bound port; there is nothing to connect to.
            Assert.Equal(0, bridge.Port);
        }

        [Fact]
        public void Dispose_ClosesTheListener() {
            var adapter = new FakeAdapter();
            var bridge = StartBridge(adapter);
            int port = bridge.Port;

            bridge.Dispose();
            Assert.False(bridge.IsListening);

            using var client = new TcpClient();
            Assert.ThrowsAny<SocketException>(() => client.Connect("127.0.0.1", port));
        }
    }
}
