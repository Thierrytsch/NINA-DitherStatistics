using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace DitherStatistics.Plugin.Tests {
    /// <summary>
    /// Safety net for the refactoring: these tests freeze the exact JSON property names of the
    /// persisted-state model types. The files under %LocalAppData%\NINA\DitherStatistics\ must stay
    /// readable across plugin versions (see REFACTORING_PLAN.md, invariant "Persistenzformat").
    /// If a test here fails after a refactor, the persisted-JSON contract changed - fix the model,
    /// not the test.
    /// </summary>
    public class JsonContractTests {
        private static HashSet<string> PropertyNames(object obj) {
            string json = JsonSerializer.Serialize(obj);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        }

        [Fact]
        public void DitherEvent_JsonPropertyNames_AreUnchanged() {
            var evt = new DitherEvent {
                StartTime = DateTime.Now,
                EndTime = DateTime.Now,
                SettleTime = 1.0,
                PixelShiftX = 2.0,
                PixelShiftY = 3.0,
                Success = true,
                CumulativeX = 4.0,
                CumulativeY = 5.0
            };

            var expected = new HashSet<string> {
                "StartTime", "EndTime", "SettleTime", "PixelShiftX", "PixelShiftY",
                "Success", "CumulativeX", "CumulativeY"
            };
            Assert.Equal(expected, PropertyNames(evt));
        }

        [Fact]
        public void PixelShiftPoint_JsonPropertyNames_AreUnchanged() {
            var point = new PixelShiftPoint(1.0, 2.0, 3.0, 4.0);

            var expected = new HashSet<string> { "X", "Y", "DeltaX", "DeltaY" };
            Assert.Equal(expected, PropertyNames(point));
        }

        [Fact]
        public void DitherDataPoint_JsonPropertyNames_AreUnchanged() {
            var point = new DitherDataPoint {
                DitherSeriesId = 1,
                DX = 0.1,
                DY = 0.2,
                PairRMS = 0.3,
                Exposure = 2.0,
                Timestamp = DateTime.Now
            };

            var expected = new HashSet<string> {
                "DitherSeriesId", "DX", "DY", "PairRMS", "Exposure", "Timestamp"
            };
            Assert.Equal(expected, PropertyNames(point));
        }

        [Fact]
        public void DitherSeriesInfo_JsonPropertyNames_AreUnchanged() {
            var info = new DitherSeriesInfo {
                DitherSeriesId = 1,
                DitherEventTime = DateTime.Now,
                SettleReceived = true,
                SettleFailed = false,
                StarLost = false,
                MeasuredSettleDuration = 1.5,
                ThresholdP90 = 0.5,
                ThresholdP95 = 0.6,
                ThresholdP99 = 0.7
            };

            var expected = new HashSet<string> {
                "DitherSeriesId", "DitherEventTime", "SettleReceived", "SettleFailed", "StarLost",
                "MeasuredSettleDuration", "ThresholdP90", "ThresholdP95", "ThresholdP99"
            };
            Assert.Equal(expected, PropertyNames(info));
        }

        [Fact]
        public void DitherAnalysisSnapshot_JsonPropertyNames_AreUnchanged() {
            var snapshot = new DitherAnalysisSnapshot {
                DitherData = new List<DitherDataPoint> { new DitherDataPoint() },
                DitherSeriesCounter = 3,
                SeriesInfos = new List<DitherSeriesInfo> { new DitherSeriesInfo() }
            };

            var expected = new HashSet<string> { "DitherData", "DitherSeriesCounter", "SeriesInfos" };
            Assert.Equal(expected, PropertyNames(snapshot));
        }

        [Fact]
        public void DitherSettingsRecommendation_JsonPropertyNames_AreUnchanged() {
            var rec = new DitherSettingsRecommendation();

            // The _Quality/_Balanced/_Performance suffixes are load-bearing for persisted-JSON
            // compatibility (they predate the Strict/Standard/Fast naming) and must never change.
            var expected = new HashSet<string> {
                "SettlePixelTolerance_Quality", "SettlePixelTolerance_Balanced", "SettlePixelTolerance_Performance",
                "MinSettleTime_Quality", "MinSettleTime_Balanced", "MinSettleTime_Performance",
                "ExpectedSettleDuration_Quality", "ExpectedSettleDuration_Balanced", "ExpectedSettleDuration_Performance",
                "SettleTimeout_Quality", "SettleTimeout_Balanced", "SettleTimeout_Performance",
                "SeriesUsed_Quality", "SeriesUsed_Balanced", "SeriesUsed_Performance",
                "Unstabilized_Quality", "Unstabilized_Balanced", "Unstabilized_Performance",
                "SettleDelaySpread_Quality", "SettleDelaySpread_Balanced", "SettleDelaySpread_Performance",
                "ExcludedSeries", "GuiderPixelScaleArcsec",
                "DitherEventsAnalyzed", "CurrentRunningRMS", "CurrentRMSStdDev", "GuideExposure"
            };
            Assert.Equal(expected, PropertyNames(rec));
        }

        [Fact]
        public void PersistedStatisticsData_JsonPropertyNames_AreUnchanged() {
            var data = new PersistedStatisticsData {
                DitherEvents = new List<DitherEvent> { new DitherEvent() },
                SettleTimeValues = new List<double> { 1.0 },
                PixelShiftValues = new List<PixelShiftPoint> { new PixelShiftPoint() },
                CumulativeX = 1.0,
                CumulativeY = 2.0,
                OptimizerData = new DitherAnalysisSnapshot(),
                Recommendation = new DitherSettingsRecommendation()
            };

            var expected = new HashSet<string> {
                "DitherEvents", "SettleTimeValues", "PixelShiftValues", "CumulativeX", "CumulativeY",
                "OptimizerData", "Recommendation"
            };
            Assert.Equal(expected, PropertyNames(data));
        }

        [Fact]
        public void PersistedStatisticsData_WithNullOptimizerFields_SerializesAsNull_ForLegacyFileCompatibility() {
            // Files written before v1.4 have no OptimizerData/Recommendation; the loader must accept
            // "field absent" as well as "field null". This test locks in that null (not omitted) is
            // what the current serializer produces, so a switch to omit-nulls would be a deliberate change.
            var data = new PersistedStatisticsData();
            string json = JsonSerializer.Serialize(data);
            using var doc = JsonDocument.Parse(json);

            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("OptimizerData").ValueKind);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("Recommendation").ValueKind);
        }
    }
}
