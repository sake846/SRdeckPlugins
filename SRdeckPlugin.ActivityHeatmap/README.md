# Frequency activity heatmap

Plugin ID: `activity-heatmap`. Monitors the main spectrum's visible frequency range or the full input band as 256 cells. The newest interval appears at the top, matching the waterfall. It can run alongside another receiver without requesting tuning changes.

Color represents the fraction of observations whose strongest FFT bin in that cell reaches the threshold. Gray means unobserved, distinct from measured 0%. Hover or use arrow keys for frequency, time, occupancy, observation count and the dBm threshold. The newest row is incomplete.

The threshold defaults to −100 dBm per FFT bin and can be changed from −140 to −20 dBm in 10 dB steps. Detection shares the main spectrum/waterfall FFT and applies the same receiver calibration. An unavailable shared spectrum leaves intervals unobserved and displays a status message.

Choose a 1-, 6- or 24-hour view (default: 6 hours). The 1- and 6-hour views use one-minute rows; the 24-hour view uses four-minute rows. One-minute counts are retained for up to 24 hours, so changing the view preserves history. Aggregated activity is weighted by observation count. Dropped samples are excluded from observations, and previously accumulated history is retained. The view updates approximately once per second.

The default frequency scope follows spectrum zooming and panning. Select the full-input option to monitor independently of the viewport. Cells outside the captured input band remain unobserved. Changing the scope or following a new viewport resets history so old and new frequency columns are not mixed.

No independent FFT is run. Data comes from the general `IPluginSpectrumService`, using the host's frequency resolution, window, normalization and gain correction. Each frequency cell takes the maximum displayed level over its frequency range. Repeated copies of the same FFT frame for the same range are not counted again. Signals shorter than the FFT update interval can be missed; this is sampled activity rather than exact continuous occupancy. Absolute dBm accuracy depends on receiver calibration.

Clear, threshold changes, stream restarts, source/generation changes, retuning, input rate changes or a backwards sample position reset history. Stopping retains the last history and allows view-period changes. Settings reset on application restart. No disk history or export is provided.

Windows x64 / .NET 10 WPF; no additional native dependencies. GPL-3.0-only. See [Japanese documentation](README.ja.md).
