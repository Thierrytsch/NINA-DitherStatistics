using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;

namespace DitherStatistics.Plugin.Tests {
    public class ExportServiceTests {
        [Fact]
        public void ExportDitherEventsCsv_UnderDeDeCulture_ProducesInvariantNineColumnRows() {
            var originalCulture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            string path = null;
            try {
                var events = new List<DitherEvent> {
                    new DitherEvent {
                        StartTime = new DateTime(2026, 1, 1, 0, 0, 0),
                        EndTime = new DateTime(2026, 1, 1, 0, 0, 5),
                        PixelShiftX = 1.23456,
                        PixelShiftY = -2.5,
                        CumulativeX = 3.14159,
                        CumulativeY = 6.7891,
                        SettleTime = 4.5,
                        Success = true
                    }
                };

                path = ExportService.ExportDitherEventsCsv(events);
                var lines = File.ReadAllLines(path);

                Assert.Equal(2, lines.Length);
                var fields = lines[1].Split(',');
                Assert.Equal(9, fields.Length);
                Assert.Equal("3.1416", fields[5]);
                Assert.Equal("6.7891", fields[6]);
            } finally {
                CultureInfo.CurrentCulture = originalCulture;
                if (path != null && File.Exists(path)) {
                    File.Delete(path);
                }
            }
        }
    }
}
