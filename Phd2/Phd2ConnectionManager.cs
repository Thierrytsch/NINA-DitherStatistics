using NINA.Core.Utility;
using System;
using System.Threading.Tasks;

namespace DitherStatistics.Plugin {
    /// <summary>
    /// Owns the PHD2 auto-connect/reconnect policy, moved out of the VM with
    /// identical timing: 2 s initial delay after Start(), 10 s retry while not
    /// connected, 5 s delay before reconnecting after a lost/closed connection.
    /// Also owns the deduplicated connection-status logging that shared the
    /// hasLoggedConnectionFailure flag with the retry loop in the VM.
    /// </summary>
    public class Phd2ConnectionManager {
        private const int INITIAL_DELAY_MS = 2000;
        private const int RETRY_DELAY_MS = 10000;
        private const int RECONNECT_DELAY_MS = 5000;

        private readonly PHD2Client client;
        private bool hasLoggedConnectionFailure = false;

        public Phd2ConnectionManager(PHD2Client client) {
            this.client = client;
            client.ConnectionStatusChanged += OnConnectionStatusChanged;
        }

        /// <summary>
        /// Kick off the initial auto-connect (2 s delay, then the retry loop)
        /// </summary>
        public void Start() {
            Task.Run(async () => {
                await Task.Delay(INITIAL_DELAY_MS);
                await ConnectLoop();
            });
        }

        /// <summary>
        /// Try to connect until it succeeds, retrying every 10 s. Failures are only
        /// logged the first time until a connection succeeds again.
        /// </summary>
        private async Task ConnectLoop() {
            try {
                while (true) {
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
                    await Task.Delay(RETRY_DELAY_MS);
                }
            } catch (Exception ex) {
                Logger.Error($"Error connecting to PHD2: {ex.Message}");
            }
        }

        private void OnConnectionStatusChanged(object sender, string status) {
            // Only log connection status changes if it's a successful connection or the first failure
            if (status.Contains("Connected") && !status.Contains("failed")) {
                Logger.Info($"PHD2 Connection Status: {status}");
                hasLoggedConnectionFailure = false; // Reset flag on successful connection
            } else if (!hasLoggedConnectionFailure && (status.Contains("Connection failed") || status.Contains("Connection lost"))) {
                Logger.Info($"PHD2 Connection Status: {status}");
                hasLoggedConnectionFailure = true;
            }

            // If disconnected, try to reconnect after delay
            if (status.Contains("Connection lost") || status.Contains("Disconnected")) {
                Task.Run(async () => {
                    await Task.Delay(RECONNECT_DELAY_MS);
                    await ConnectLoop();
                });
            }
        }
    }
}
