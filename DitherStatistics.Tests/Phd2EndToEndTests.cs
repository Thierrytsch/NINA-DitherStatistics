using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DitherStatistics.Plugin;
using Xunit;

namespace DitherStatistics.Tests {
    /// <summary>
    /// In-process end-to-end tests: a real PHD2Client connected over a real loopback
    /// socket to FakePhd2Server, wired to a DitherOptimizerService exactly like the
    /// VM does it. Covers the layer the Handle*-level DitherOptimizerServiceTests
    /// cannot: TCP connect/read-loop, JSON-RPC round-trips and event parsing.
    /// Events are stamped with DateTime.Now inside PHD2Client, so time-dependent
    /// scenarios (15-min reference window, 120-s cap) stay in the Handle*-level tests.
    /// </summary>
    public class Phd2EndToEndTests : IDisposable {
        private readonly string tempDir;

        public Phd2EndToEndTests() {
            tempDir = Path.Combine(Path.GetTempPath(), "Phd2EndToEndTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose() {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }

        /// <summary>Poll until the condition holds; fail the test on timeout.</summary>
        private static async Task WaitUntil(Func<bool> condition, string description, int timeoutMs = 10000) {
            var sw = Stopwatch.StartNew();
            while (!condition()) {
                Assert.True(sw.ElapsedMilliseconds < timeoutMs, $"Timeout after {timeoutMs} ms waiting for: {description}");
                await Task.Delay(25);
            }
        }

        /// <summary>
        /// Connect a real client to the fake server and wait until the delayed
        /// get_exposure/get_pixel_scale queries (1 s after connect) are answered -
        /// before that, PHD2Client drops the first GuideStep as timing reference.
        /// </summary>
        private static async Task<PHD2Client> ConnectAndWaitReady(FakePhd2Server server) {
            var client = new PHD2Client("127.0.0.1", server.Port);
            Assert.True(await client.ConnectAsync(), "ConnectAsync should succeed against the fake server");
            await WaitUntil(() => client.CurrentGuideExposure > 0 && client.GuiderPixelScaleArcsec.HasValue,
                "initial get_exposure/get_pixel_scale round-trip");
            return client;
        }

        [Fact]
        public async Task Connect_ReceivesGreeting_AndQueriesExposureAndPixelScale() {
            using var server = new FakePhd2Server();
            server.ExposureMs = 1500;
            server.PixelScaleArcsec = 3.25;

            using var client = new PHD2Client("127.0.0.1", server.Port);
            var statuses = new ConcurrentQueue<string>();
            client.ConnectionStatusChanged += (s, status) => statuses.Enqueue(status);

            Assert.True(await client.ConnectAsync());
            Assert.True(client.IsConnected);
            Assert.Contains("Connected", statuses);

            // The client queries 1 s after connect; both answers must land in the properties
            await WaitUntil(() => client.CurrentGuideExposure > 0 && client.GuiderPixelScaleArcsec.HasValue,
                "initial RPC round-trip");
            Assert.Equal(1.5, client.CurrentGuideExposure, precision: 10);   // 1500 ms -> seconds
            Assert.Equal(3.25, client.GuiderPixelScaleArcsec.Value, precision: 10);
        }

        [Fact]
        public async Task FullDitherCycle_OverTheWire_ParsesEvents_AndFiresRecommendation() {
            using var server = new FakePhd2Server();
            using var client = await ConnectAndWaitReady(server);

            // Wire client -> optimizer exactly like DitherStatisticsVM does
            using var optimizer = new DitherOptimizerService(
                () => client.CurrentGuideExposure,
                () => client.GuiderPixelScaleArcsec,
                tempDir);
            client.GuidingDithered += (s, e) => optimizer.HandleGuidingDithered(e);
            client.SettleDone += (s, e) => optimizer.HandleSettleDone(e);
            client.GuideStep += (s, e) => optimizer.HandleGuideStep(e);
            client.StarLost += (s, e) => optimizer.HandleStarLost();
            client.GuidingStarted += (s, e) => optimizer.HandleGuidingStarted();

            var guideSteps = new ConcurrentQueue<PHD2GuideStepEventArgs>();
            PHD2GuidingDitheredEventArgs ditheredArgs = null;
            PHD2SettleDoneEventArgs settleArgs = null;
            bool guidingStarted = false;
            DitherSettingsRecommendation recommendation = null;
            using var recommendationFired = new ManualResetEventSlim(false);
            client.GuideStep += (s, e) => guideSteps.Enqueue(e);
            client.GuidingDithered += (s, e) => ditheredArgs = e;
            client.SettleDone += (s, e) => settleArgs = e;
            client.GuidingStarted += (s, e) => guidingStarted = true;
            optimizer.DitherRecommendationUpdated += (s, r) => { recommendation = r; recommendationFired.Set(); };

            // --- Scripted session, same shape as DitherOptimizerServiceTests ---
            server.SendStartGuiding();
            await WaitUntil(() => guidingStarted, "StartGuiding event");

            // Stable guiding fills the reference window (REFERENCE_MIN_POINTS = 20)
            for (int i = 0; i < 30; i++) {
                server.SendGuideStep(0.3 + (i % 5) * 0.05, 0.3);
            }
            await WaitUntil(() => guideSteps.Count >= 30, "30 reference guide steps");

            server.SendGuidingDithered(5.0, 5.0);
            await WaitUntil(() => ditheredArgs != null, "GuidingDithered event");
            Assert.Equal(5.0, ditheredArgs.DeltaX);
            Assert.Equal(5.0, ditheredArgs.DeltaY);

            // Settling: distances decay back below the reference level
            foreach (var d in new[] { 4.0, 2.0, 1.0, 0.3, 0.3, 0.3 }) {
                server.SendGuideStep(d, 0);
            }

            server.SendSettleDone(status: 0, totalFrames: 6, droppedFrames: 1);
            await WaitUntil(() => settleArgs != null, "SettleDone event");
            Assert.True(settleArgs.Success);
            Assert.Equal(6, settleArgs.TotalFrames);
            Assert.Equal(1, settleArgs.DroppedFrames);

            // Post-settle countdown: 10 steps close the collection window and
            // trigger the analysis + recommendation
            for (int i = 0; i < 10; i++) {
                server.SendGuideStep(0.3, 0.3);
            }

            Assert.True(recommendationFired.Wait(TimeSpan.FromSeconds(10)), "recommendation should fire after the window closes");
            Assert.NotNull(recommendation);
            Assert.Equal(1, recommendation.DitherEventsAnalyzed);
            Assert.Equal(2.0, recommendation.GuideExposure, precision: 10); // default 2000 ms from the fake server

            // The parsed guide steps carry dx/dy and the queried exposure
            Assert.All(guideSteps, e => Assert.Equal(2.0, e.Exposure, precision: 10));
            Assert.Equal(0.3, guideSteps.First().DX, precision: 10);
            Assert.Equal(0.3, guideSteps.First().DY, precision: 10);

            // The series was collected over the wire: 6 settling + 10 post-settle points
            var snapshot = optimizer.GetDitherAnalysisSnapshot();
            Assert.Equal(16, snapshot.DitherData.Count);
            var info = Assert.Single(snapshot.SeriesInfos);
            Assert.True(info.SettleReceived);
            Assert.False(info.SettleFailed);
            Assert.True(info.ThresholdP90 > 0, "thresholds should be captured from the filled reference window");
        }

        [Fact]
        public async Task MalformedJsonAndUnknownEvents_DoNotBreakTheReadLoop() {
            using var server = new FakePhd2Server();
            using var client = await ConnectAndWaitReady(server);

            var guideSteps = new ConcurrentQueue<PHD2GuideStepEventArgs>();
            client.GuideStep += (s, e) => guideSteps.Enqueue(e);

            server.SendRaw("this is not json {{{");
            server.SendRaw("{\"Event\":\"SomeFutureEvent\",\"NewField\":42}");
            server.SendRaw("{\"NoEventProperty\":true}");
            server.SendGuideStep(0.7, -0.2);

            await WaitUntil(() => guideSteps.Count >= 1, "guide step after garbage lines");
            Assert.True(client.IsConnected);
            Assert.Equal(0.7, guideSteps.First().DX, precision: 10);
            Assert.Equal(-0.2, guideSteps.First().DY, precision: 10);
        }

        [Fact]
        public async Task ServerClosesConnection_RaisesConnectionLost() {
            using var server = new FakePhd2Server();
            using var client = await ConnectAndWaitReady(server);

            var statuses = new ConcurrentQueue<string>();
            client.ConnectionStatusChanged += (s, status) => statuses.Enqueue(status);

            server.DropConnection();

            await WaitUntil(() => statuses.Contains("Connection lost"), "'Connection lost' status");
            Assert.False(client.IsConnected);
        }

        [Fact]
        public async Task ExplicitDisconnect_RaisesDisconnected_TheOptimizerAbortSignal() {
            using var server = new FakePhd2Server();
            using var client = await ConnectAndWaitReady(server);

            // Same wiring as the VM: only the explicit "Disconnected" status aborts
            // the optimizer's running collection window
            using var optimizer = new DitherOptimizerService(() => client.CurrentGuideExposure, () => client.GuiderPixelScaleArcsec, tempDir);
            client.GuidingDithered += (s, e) => optimizer.HandleGuidingDithered(e);
            client.GuideStep += (s, e) => optimizer.HandleGuideStep(e);
            var statuses = new ConcurrentQueue<string>();
            client.ConnectionStatusChanged += (s, status) => {
                statuses.Enqueue(status);
                if (status == "Disconnected") optimizer.HandleDisconnected();
            };

            // Open a collection window with some points, then disconnect mid-window
            PHD2GuidingDitheredEventArgs dithered = null;
            client.GuidingDithered += (s, e) => dithered = e;
            var steps = new ConcurrentQueue<PHD2GuideStepEventArgs>();
            client.GuideStep += (s, e) => steps.Enqueue(e);

            server.SendGuidingDithered(3.0, 3.0);
            await WaitUntil(() => dithered != null, "GuidingDithered event");
            server.SendGuideStep(2.0, 1.0);
            await WaitUntil(() => steps.Count >= 1, "in-window guide step");

            client.Disconnect();

            Assert.Contains("Disconnected", statuses);
            Assert.False(client.IsConnected);

            // The aborted window's points were discarded, no series metadata kept
            var snapshot = optimizer.GetDitherAnalysisSnapshot();
            Assert.Empty(snapshot.DitherData);
            Assert.Empty(snapshot.SeriesInfos);
        }
    }
}
