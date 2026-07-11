namespace DitherStatistics.Plugin {
    /// <summary>
    /// Display-model for one Dither Settings Optimizer profile column
    /// (Strict / Standard / Fast). Regroups the already-formatted recommendation
    /// strings the VM exposes so the three columns can share a single DataTemplate
    /// instead of three copy-pasted XAML blocks. Pure presentation container — no
    /// math, no persisted state.
    /// </summary>
    public class OptimizerProfileDisplay {
        public string Name { get; set; }          // "Strict" / "Standard" / "Fast"
        public string Quantile { get; set; }      // "P90" / "P95" / "P99"
        public string Tagline { get; set; }       // "slow" / "balanced" / "recommended"
        public string SettlePixel { get; set; }   // formatted settle tolerance (px)
        public string Arcsec { get; set; }        // tolerance in arcsec, or empty
        public string ExpectedSettle { get; set; }// median settle duration (s)
        public string Timeout { get; set; }       // suggested settle timeout (s)
        public bool IsRecommended { get; set; }   // true for the Fast column (accent)
        public string ToolTip { get; set; }
    }
}
