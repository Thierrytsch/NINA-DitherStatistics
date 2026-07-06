using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DitherStatistics.Plugin;
using Xunit;

namespace DitherStatistics.Tests {
    /// <summary>
    /// Skips the test automatically unless a real PHD2 instance is guiding on
    /// 127.0.0.1:4400 (start it with SmokeTest\Start-Phd2Guiding.ps1). This keeps a
    /// plain "dotnet test" green on machines/CI without PHD2 while the tests run for
    /// real whenever PHD2 is up.
    /// </summary>
    public sealed class Phd2FactAttribute : FactAttribute {
        public Phd2FactAttribute() {
            string state = ProbePhd2AppState();
            if (state == null) {
                Skip = "PHD2 is not running on 127.0.0.1:4400 - run SmokeTest\\Start-Phd2Guiding.ps1 to execute this integration test";
            } else if (state != "Guiding") {
                Skip = $"PHD2 is running but in state '{state}' (needs 'Guiding') - run SmokeTest\\Start-Phd2Guiding.ps1";
            }
        }

        /// <summary>
        /// Connect briefly and read the greeting events PHD2 pushes on connect;
        /// returns the AppState, or null when PHD2 is not reachable.
        /// </summary>
        private static string ProbePhd2AppState() {
            try {
                using var tcp = new TcpClient();
                var connect = tcp.BeginConnect("127.0.0.1", 4400, null, null);
                if (!connect.AsyncWaitHandle.WaitOne(500) || !tcp.Connected) return null;
                tcp.EndConnect(connect);

                var stream = tcp.GetStream();
                stream.ReadTimeout = 2000;
                using var reader = new StreamReader(stream, Encoding.UTF8);
                for (int i = 0; i < 10; i++) {
                    string line = reader.ReadLine();
                    if (line == null) return null;
                    try {
                        using var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.TryGetProperty("Event", out var ev) && ev.GetString() == "AppState") {
                            return doc.RootElement.GetProperty("State").GetString();
                        }
                    } catch (JsonException) { }
                }
                return null;
            } catch {
                return null;
            }
        }
    }

    /// <summary>
    /// Integration tests against a real, guiding PHD2 (simulator profile): verify
    /// that PHD2Client understands the real wire format - the piece the in-process
    /// FakePhd2Server tests cannot guarantee against new PHD2 versions.
    /// </summary>
    [Trait("Category", "Integration")]
    public class Phd2IntegrationTests : IDisposable {
        private readonly string tempDir;

        public Phd2IntegrationTests() {
            tempDir = Path.Combine(Path.GetTempPath(), "Phd2IntegrationTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose() {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }

        private static async Task WaitUntil(Func<bool> condition, string description, int timeoutMs) {
            var sw = Stopwatch.StartNew();
            while (!condition()) {
                Assert.True(sw.ElapsedMilliseconds < timeoutMs, $"Timeout after {timeoutMs} ms waiting for: {description}");
                await Task.Delay(100);
            }
        }

        /// <summary>
        /// Send one JSON-RPC request to PHD2 over a second raw socket (independent of
        /// the PHD2Client under test) and wait for the matching response.
        /// </summary>
        private static void SendRawRpc(string method, object parameters) {
            using var tcp = new TcpClient();
            tcp.Connect("127.0.0.1", 4400);
            var stream = tcp.GetStream();
            stream.ReadTimeout = 15000;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

            writer.WriteLine(JsonSerializer.Serialize(new { method, @params = parameters, id = 9001 }));

            // Responses interleave with event notifications on the same socket
            for (int i = 0; i < 200; i++) {
                string line = reader.ReadLine() ?? throw new IOException("PHD2 closed the raw RPC connection");
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("Event", out _)) continue;
                if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || id.GetInt32() != 9001) continue;
                if (root.TryGetProperty("error", out var error)) {
                    throw new InvalidOperationException($"PHD2 RPC '{method}' failed: {error}");
                }
                return;
            }
            throw new TimeoutException($"No response to PHD2 RPC '{method}'");
        }

        [Phd2Fact]
        public async Task RealPhd2_Connect_QueriesExposureAndPixelScale() {
            using var client = new PHD2Client();
            Assert.True(await client.ConnectAsync(), "should connect to the real PHD2");

            // get_exposure answers immediately; get_pixel_scale needs a calibration,
            // which a guiding PHD2 by definition has
            await WaitUntil(() => client.CurrentGuideExposure > 0, "get_exposure round-trip", 15000);
            await WaitUntil(() => client.GuiderPixelScaleArcsec.HasValue, "get_pixel_scale round-trip", 15000);
            Assert.True(client.GuiderPixelScaleArcsec.Value > 0);
        }

        [Phd2Fact]
        public async Task RealPhd2_DitherRoundTrip_EventsParse_AndRecommendationFires() {
            using var client = new PHD2Client();
            Assert.True(await client.ConnectAsync(), "should connect to the real PHD2");

            using var optimizer = new DitherOptimizerService(
                () => client.CurrentGuideExposure,
                () => client.GuiderPixelScaleArcsec,
                tempDir);
            client.GuidingDithered += (s, e) => optimizer.HandleGuidingDithered(e);
            client.SettleDone += (s, e) => optimizer.HandleSettleDone(e);
            client.GuideStep += (s, e) => optimizer.HandleGuideStep(e);
            client.StarLost += (s, e) => optimizer.HandleStarLost();
            client.GuidingStarted += (s, e) => optimizer.HandleGuidingStarted();

            int guideSteps = 0;
            PHD2GuidingDitheredEventArgs dithered = null;
            PHD2SettleDoneEventArgs settle = null;
            DitherSettingsRecommendation recommendation = null;
            using var recommendationFired = new ManualResetEventSlim(false);
            client.GuideStep += (s, e) => Interlocked.Increment(ref guideSteps);
            client.GuidingDithered += (s, e) => dithered = e;
            client.SettleDone += (s, e) => settle = e;
            optimizer.DitherRecommendationUpdated += (s, r) => { recommendation = r; recommendationFired.Set(); };

            await WaitUntil(() => client.CurrentGuideExposure > 0, "get_exposure round-trip", 15000);

            // Fill the optimizer's reference window with real stable-guiding steps
            // (REFERENCE_MIN_POINTS = 20) so the recommendation can fire after the dither
            await WaitUntil(() => Volatile.Read(ref guideSteps) >= 25, "25 stable guide steps (~30 s at 1 s exposure)", 120000);

            SendRawRpc("dither", new { amount = 3.0, raOnly = false, settle = new { pixels = 1.5, time = 8, timeout = 60 } });

            await WaitUntil(() => dithered != null, "GuidingDithered from the real PHD2", 30000);
            Assert.True(dithered.DeltaX != 0 || dithered.DeltaY != 0, "dither offset should be non-zero");

            await WaitUntil(() => settle != null, "SettleDone from the real PHD2", 120000);

            // 10 post-settle guide steps close the collection window -> analysis fires
            Assert.True(recommendationFired.Wait(TimeSpan.FromSeconds(60)), "recommendation should fire after the post-settle steps");
            Assert.NotNull(recommendation);
            Assert.True(recommendation.DitherEventsAnalyzed >= 1);

            var snapshot = optimizer.GetDitherAnalysisSnapshot();
            Assert.NotEmpty(snapshot.DitherData);
            Assert.NotEmpty(snapshot.SeriesInfos);
        }
    }
}
