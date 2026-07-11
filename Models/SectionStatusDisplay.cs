using System;
using NINA.Core.Utility;

namespace DitherStatistics.Plugin {
    /// <summary>
    /// Display-model for the "disabled / insufficient data" info box shown in place
    /// of the Quality and Optimizer cards. Normalizes the two sections' differing
    /// property paths (title, resolved status message, enable toggle) onto one shape
    /// so a single DataTemplate can render both instead of two near-identical XAML
    /// blocks. The toggle is two-way: user clicks flow out through <c>applyToggle</c>
    /// into the owning VM property; VM-side changes flow back in via <see cref="Sync"/>.
    /// </summary>
    public class SectionStatusDisplay : BaseINPC {
        private readonly Action<bool> applyToggle;

        public SectionStatusDisplay(string title, Action<bool> applyToggle) {
            Title = title;
            this.applyToggle = applyToggle;
        }

        public string Title { get; }

        private string message = "";
        public string Message {
            get => message;
            set { message = value; RaisePropertyChanged(); }
        }

        private bool isEnabled;
        public bool IsEnabled {
            get => isEnabled;
            set {
                if (isEnabled == value) return;
                isEnabled = value;
                RaisePropertyChanged();
                applyToggle?.Invoke(value);
            }
        }

        /// <summary>
        /// Reflect the owning VM's current state without re-triggering the toggle
        /// callback (avoids feedback when the change originated in the VM).
        /// </summary>
        public void Sync(bool enabled, string message) {
            if (isEnabled != enabled) {
                isEnabled = enabled;
                RaisePropertyChanged(nameof(IsEnabled));
            }
            Message = message;
        }
    }
}
