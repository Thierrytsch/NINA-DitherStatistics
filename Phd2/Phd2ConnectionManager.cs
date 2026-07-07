using NINA.Core.Utility;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DitherStatistics.Plugin {
    /// <summary>
    /// Owns the PHD2 auto-connect/reconnect policy, moved out of the VM with
    /// identical timing: 2 s initial delay after Start(), 10 s retry while not
    /// connected, 5 s delay before reconnecting after a lost connection. Only an
    /// involuntary ConnectionLost triggers a reconnect - the explicit Disconnected
    /// raised during plugin shutdown must not, otherwise the client would happily
    /// reconnect after disposal. Also owns the deduplicated connection-status
    /// logging that shared the hasLoggedConnectionFailure flag with the retry
    /// loop in the VM. The delays are injectable for tests only; production code
    /// uses the defaults.
    /// </summary>
    public class Phd2ConnectionManager : IDisposable {
        private const int INITIAL_DELAY_MS = 2000;
        private const int RETRY_DELAY_MS = 10000;
        private const int RECONNECT_DELAY_MS = 5000;

        private readonly PHD2Client client;
        private readonly int initialDelayMs;
        private readonly int retryDelayMs;
        private readonly int reconnectDelayMs;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        // Token cached up front: cts.Token would throw once Dispose() ran, and a
        // scheduled reconnect task may start only after that
        private readonly CancellationToken stopToken;
        private bool hasLoggedConnectionFailure = false;

        public Phd2ConnectionManager(PHD2Client client,
            int initialDelayMs = INITIAL_DELAY_MS,
            int retryDelayMs = RETRY_DELAY_MS,
            int reconnectDelayMs = RECONNECT_DELAY_MS) {
            this.client = client;
            this.initialDelayMs = initialDelayMs;
            this.retryDelayMs = retryDelayMs;
            this.reconnectDelayMs = reconnectDelayMs;
            stopToken = cts.Token;
            client.ConnectionStatusChanged += OnConnectionStatusChanged;
        }

        /// <summary>
        /// Kick off the initial auto-connect (2 s delay, then the retry loop)
        /// </summary>
        public void Start() {
            Task.Run(async () => {
                try {
                    await Task.Delay(initialDelayMs, stopToken);
                    await ConnectLoop(stopToken);
                } catch (OperationCanceledException) {
                    // Stop() was called before the initial delay elapsed
                }
            });
        }

        /// <summary>
        /// Stop the auto-connect/reconnect policy for good: detach from the client
        /// and cancel any pending retry/reconnect loop. Must be called before the
        /// client is disposed, so neither its final Disconnected status nor an
        /// in-flight retry can reconnect after plugin shutdown.
        /// </summary>
        public void Stop() {
            client.ConnectionStatusChanged -= OnConnectionStatusChanged;
            cts.Cancel();
        }

        public void Dispose() {
            Stop();
            cts.Dispose();
        }

        /// <summary>
        /// Try to connect until it succeeds, retrying every 10 s. Failures are only
        /// logged the first time until a connection succeeds again.
        /// </summary>
        private async Task ConnectLoop(CancellationToken token) {
            try {
                while (!token.IsCancellationRequested) {
                    // Only log connection attempt the first time
                    if (!hasLoggedConnectionFailure) {
                        Logger.Info("Attempting to connect to PHD2...");
                    }
                    bool connected = await client.ConnectAsync();

                    if (connected) {
                        Logger.Info("Successfully connected to PHD2!");
                        hasLoggedConnectionFailure = false; // Reset flag on successful connection
                        return;
                    }

                    // Only log warning the first time
                    if (!hasLoggedConnectionFailure) {
                        Logger.Warning("Failed to connect to PHD2 - will retry later");
                        hasLoggedConnectionFailure = true;
                    }

                    // Retry after 10 seconds
                    await Task.Delay(retryDelayMs, token);
                }
            } catch (OperationCanceledException) {
                // Stop() was called - end the retry loop silently
            } catch (Exception ex) {
                Logger.Error($"Error connecting to PHD2: {ex.Message}");
            }
        }

        private void OnConnectionStatusChanged(object sender, Phd2ConnectionStatus status) {
            switch (status) {
                case Phd2ConnectionStatus.Connected:
                    Logger.Info("PHD2 Connection Status: Connected");
                    hasLoggedConnectionFailure = false; // Reset flag on successful connection
                    break;

                case Phd2ConnectionStatus.ConnectionFailed:
                    if (!hasLoggedConnectionFailure) {
                        Logger.Info("PHD2 Connection Status: Connection failed");
                        hasLoggedConnectionFailure = true;
                    }
                    break;

                case Phd2ConnectionStatus.ConnectionLost:
                    if (!hasLoggedConnectionFailure) {
                        Logger.Info("PHD2 Connection Status: Connection lost");
                        hasLoggedConnectionFailure = true;
                    }
                    // Involuntary loss: reconnect after the delay
                    Task.Run(async () => {
                        try {
                            await Task.Delay(reconnectDelayMs, stopToken);
                            await ConnectLoop(stopToken);
                        } catch (OperationCanceledException) {
                            // Stop() was called during the reconnect delay
                        }
                    });
                    break;

                case Phd2ConnectionStatus.Disconnected:
                    // Explicit disconnect (plugin shutdown) - log it, never reconnect
                    Logger.Info("PHD2 Connection Status: Disconnected");
                    break;
            }
        }
    }
}
