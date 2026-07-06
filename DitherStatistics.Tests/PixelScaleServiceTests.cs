using Xunit;

namespace DitherStatistics.Plugin.Tests {
    public class PixelScaleServiceTests {
        [Fact]
        public void Calculate_OverrideSet_WinsOverEverythingElse() {
            var result = PixelScaleService.Calculate(
                overrideRatio: 2.5,
                ninaGuiderPixelScale: 3.0,
                phd2GuiderPixelScale: 3.0,
                pixelSizeMicron: 4.0,
                focalLengthMm: 600);

            Assert.Equal("manual", result.Source);
            Assert.Equal(2.5, result.Ratio);
            Assert.Null(result.FallbackReason);
            Assert.Null(result.ImplausibleWarning);
        }

        [Fact]
        public void Calculate_NinaGuiderScaleAvailable_PrefersNinaOverPhd2() {
            // guiderScale=2.0, pixelSize=4.0, focalLength=600 -> mainScale=206.265*4/600=1.3751
            // ratio = 2.0 / 1.3751 = 1.4544...
            var result = PixelScaleService.Calculate(
                overrideRatio: 0,
                ninaGuiderPixelScale: 2.0,
                phd2GuiderPixelScale: 9.0, // should be ignored since NINA value is present
                pixelSizeMicron: 4.0,
                focalLengthMm: 600);

            Assert.Equal("auto/NINA", result.Source);
            Assert.Equal(1.4544, result.Ratio, 3);
        }

        [Fact]
        public void Calculate_OnlyPhd2ScaleAvailable_FallsBackToPhd2Source() {
            var result = PixelScaleService.Calculate(
                overrideRatio: 0,
                ninaGuiderPixelScale: 0,
                phd2GuiderPixelScale: 2.0,
                pixelSizeMicron: 4.0,
                focalLengthMm: 600);

            Assert.Equal("auto/PHD2", result.Source);
            Assert.True(result.Ratio > 0);
        }

        [Fact]
        public void Calculate_MissingInputs_ReturnsFallbackWithReason() {
            var result = PixelScaleService.Calculate(
                overrideRatio: 0,
                ninaGuiderPixelScale: 0,
                phd2GuiderPixelScale: 0,
                pixelSizeMicron: 0,
                focalLengthMm: 0);

            Assert.Equal("fallback", result.Source);
            Assert.Equal(1.0, result.Ratio);
            Assert.NotNull(result.FallbackReason);
            Assert.Null(result.ImplausibleWarning);
        }

        [Fact]
        public void Calculate_ImplausibleRatio_ReturnsFallbackWithWarning() {
            // guiderScale=1000, pixelSize=4, focalLength=600 -> mainScale=1.3751, ratio=727.2 (>100, implausible)
            var result = PixelScaleService.Calculate(
                overrideRatio: 0,
                ninaGuiderPixelScale: 1000,
                phd2GuiderPixelScale: 0,
                pixelSizeMicron: 4.0,
                focalLengthMm: 600);

            Assert.Equal("fallback", result.Source);
            Assert.Equal(1.0, result.Ratio);
            Assert.NotNull(result.ImplausibleWarning);
            Assert.Null(result.FallbackReason);
        }
    }
}
