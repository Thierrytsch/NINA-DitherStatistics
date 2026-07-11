using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace DitherStatistics.Plugin {
    /// <summary>
    /// Decouples the diagnostic channel's socket/protocol layer from the VM: the VM
    /// supplies one of these, the bridge never references NINA/WPF view state directly.
    /// Every method is called on the WPF UI thread by <see cref="SmokeTestBridge"/> so
    /// implementations run exactly the same code paths as the panel buttons/toggles.
    /// </summary>
    public interface ISmokeTestBridgeAdapter {
        /// <summary>The raw (unformatted) values the view binds to.</summary>
        IDictionary<string, object> GetState();
        /// <summary>Execute a panel command: ClearData, ExportCsv, ExportReport, Recalc.</summary>
        void Invoke(string name);
        /// <summary>Set a toggle (Persistence, MultiProfile, Quality, Optimizer); returns the prior state.</summary>
        bool SetToggle(string name, bool value);
        void CreateProfile(string name);
        void SelectProfile(string name);
        void DeleteProfile(string name);
    }

    /// <summary>
    /// Minimal line-delimited JSON diagnostic channel over TCP, used only by the
    /// stage-3 NINA smoke test to read the exact bound values and drive the same
    /// commands/toggles the panel offers - the plugin panel is invisible to UI
    /// Automation (see SmokeTest/Spike-UiaVisibility.ps1), so this is how the test
    /// reads/interacts with the live UI.
    ///
    /// Security framing: bound to 127.0.0.1 only, disabled by default (opt-in via
    /// smoketest_settings.txt), and exposes/executes only what the panel UI already
    /// offers. Infrastructure like Phd2ConnectionManager, so Logger.* is fine here.
    ///
    /// Protocol: one JSON object per line. Request {"cmd":"..."} -> response
    /// {"ok":true,...} or {"ok":false,"error":"..."}. Every command handler marshals
    /// to the UI thread (with timeout) so it runs the real button/toggle code paths;
    /// errors become ok:false responses and never crash NINA.
    /// </summary>
    public sealed class SmokeTestBridge : IDisposable {
        public const int DefaultPort = 4406;
        private const int UiInvokeTimeoutMs = 10000;

        private readonly ISmokeTestBridgeAdapter adapter;
        private readonly bool enabled;
        private readonly int requestedPort;
        private TcpListener listener;
        private CancellationTokenSource cts;
        private Task acceptTask;
        private int boundPort;

        public bool IsListening { get; private set; }

        /// <summary>The actually bound port once listening (requested port, or the OS-assigned one when 0).</summary>
        public int Port => IsListening ? boundPort : requestedPort;

        public SmokeTestBridge(ISmokeTestBridgeAdapter adapter, bool enabled, int port = DefaultPort) {
            this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            this.enabled = enabled;
            this.requestedPort = port;
        }

        /// <summary>Bind the localhost listener and start accepting clients - a no-op when disabled.</summary>
        public void Start() {
            if (!enabled) {
                Logger.Info("SmokeTestBridge: disabled (flag off) - not listening");
                return;
            }
            try {
                cts = new CancellationTokenSource();
                listener = new TcpListener(IPAddress.Loopback, requestedPort);
                listener.Start();
                boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                IsListening = true;
                acceptTask = Task.Run(() => AcceptLoop(cts.Token));
                Logger.Info($"SmokeTestBridge: listening on 127.0.0.1:{boundPort} (diagnostic channel enabled)");
            } catch (Exception ex) {
                Logger.Error($"SmokeTestBridge: failed to start on port {requestedPort}: {ex.Message}");
                IsListening = false;
            }
        }

        private async Task AcceptLoop(CancellationToken token) {
            try {
                while (!token.IsCancellationRequested) {
                    TcpClient client = await listener.AcceptTcpClientAsync(token);
                    try {
                        await HandleClient(client, token);
                    } catch (Exception ex) {
                        Logger.Debug($"SmokeTestBridge: client handler ended: {ex.Message}");
                    } finally {
                        try { client.Close(); } catch { }
                    }
                }
            } catch (OperationCanceledException) {
            } catch (ObjectDisposedException) {
            } catch (SocketException) {
            } catch (Exception ex) {
                Logger.Error($"SmokeTestBridge: accept loop error: {ex.Message}");
            }
        }

        private async Task HandleClient(TcpClient client, CancellationToken token) {
            var stream = client.GetStream();
            // No BOM on our output; the client reads line-delimited UTF-8 JSON.
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

            while (!token.IsCancellationRequested) {
                string line = await reader.ReadLineAsync();
                if (line == null) return;       // client disconnected
                if (line.Length == 0) continue; // ignore blank lines
                string response = ProcessCommand(line);
                await writer.WriteLineAsync(response);
            }
        }

        /// <summary>Parse and dispatch one request line into a response line. Never throws.</summary>
        private string ProcessCommand(string line) {
            try {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;

                if (!root.TryGetProperty("cmd", out JsonElement cmdEl) || cmdEl.ValueKind != JsonValueKind.String) {
                    return Error("missing 'cmd'");
                }

                switch (cmdEl.GetString()) {
                    case "get-state": {
                        var state = RunOnUi(() => adapter.GetState());
                        return Ok(new Dictionary<string, object> { ["state"] = state });
                    }
                    case "invoke": {
                        string name = RequireString(root, "name");
                        RunOnUi(() => { adapter.Invoke(name); return true; });
                        return Ok(null);
                    }
                    case "set-toggle": {
                        string name = RequireString(root, "name");
                        bool value = RequireBool(root, "value");
                        bool prior = RunOnUi(() => adapter.SetToggle(name, value));
                        return Ok(new Dictionary<string, object> { ["prior"] = prior });
                    }
                    case "create-profile": {
                        string name = RequireString(root, "name");
                        RunOnUi(() => { adapter.CreateProfile(name); return true; });
                        return Ok(null);
                    }
                    case "select-profile": {
                        string name = RequireString(root, "name");
                        RunOnUi(() => { adapter.SelectProfile(name); return true; });
                        return Ok(null);
                    }
                    case "delete-profile": {
                        string name = RequireString(root, "name");
                        RunOnUi(() => { adapter.DeleteProfile(name); return true; });
                        return Ok(null);
                    }
                    default:
                        return Error($"unknown command '{cmdEl.GetString()}'");
                }
            } catch (JsonException ex) {
                return Error($"invalid JSON: {ex.Message}");
            } catch (Exception ex) {
                return Error(ex.Message);
            }
        }

        /// <summary>
        /// Run <paramref name="func"/> on the WPF UI thread (with timeout). Runs inline
        /// when already on the UI thread or when there is no WPF Application (unit tests) -
        /// the same pattern the VM uses for its background-thread state access.
        /// </summary>
        private static T RunOnUi<T>(Func<T> func) {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) {
                return func();
            }
            return dispatcher.Invoke(func, DispatcherPriority.Normal, CancellationToken.None,
                TimeSpan.FromMilliseconds(UiInvokeTimeoutMs));
        }

        private static string RequireString(JsonElement root, string prop) {
            if (!root.TryGetProperty(prop, out JsonElement el) || el.ValueKind != JsonValueKind.String) {
                throw new ArgumentException($"missing or invalid '{prop}'");
            }
            var value = el.GetString();
            if (string.IsNullOrWhiteSpace(value)) {
                throw new ArgumentException($"'{prop}' must not be empty");
            }
            return value;
        }

        private static bool RequireBool(JsonElement root, string prop) {
            if (!root.TryGetProperty(prop, out JsonElement el)) {
                throw new ArgumentException($"missing '{prop}'");
            }
            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.False) return false;
            if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out bool parsed)) return parsed;
            throw new ArgumentException($"'{prop}' must be a boolean");
        }

        private static string Ok(IDictionary<string, object> extra) {
            var obj = new Dictionary<string, object> { ["ok"] = true };
            if (extra != null) {
                foreach (var kv in extra) obj[kv.Key] = kv.Value;
            }
            return JsonSerializer.Serialize(obj);
        }

        private static string Error(string message) =>
            JsonSerializer.Serialize(new Dictionary<string, object> { ["ok"] = false, ["error"] = message });

        public void Dispose() {
            try {
                cts?.Cancel();
                try { listener?.Stop(); } catch { }
                IsListening = false;
                try { acceptTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
                cts?.Dispose();
                if (enabled) {
                    Logger.Info("SmokeTestBridge: stopped");
                }
            } catch (Exception ex) {
                Logger.Error($"SmokeTestBridge: error during dispose: {ex.Message}");
            }
        }
    }
}
