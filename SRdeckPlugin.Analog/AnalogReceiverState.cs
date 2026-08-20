namespace SRdeckPlugin.Analog;

internal sealed record AnalogReceiverOptions(
    long FrequencyHz = 0,
    int StepHz = 1_000,
    int BandwidthHz = 10_000,
    bool IsReceiverEnabled = true,
    bool IsMuted = false,
    bool IsSquelchEnabled = false,
    float SquelchThresholdDbm = -80f,
    bool IsAfcEnabled = true,
    bool IsLowerSideband = false,
    bool IsStereoEnabled = true);

internal sealed record AnalogReceiverSnapshot(
    long FrequencyHz,
    int InputSampleRateHz,
    int StepHz,
    int BandwidthHz,
    bool IsReceiverEnabled,
    bool IsMuted,
    bool IsSquelchEnabled,
    float SquelchThresholdDbm,
    bool IsAfcEnabled,
    bool IsLowerSideband,
    bool IsStereoEnabled,
    bool IsStereoDetected,
    float SignalLevelDbfs,
    float SignalLevelDbm,
    bool IsSquelchOpen,
    string TuningStatus,
    bool HasCalibratedSignalLevelDbm,
    DateTimeOffset MeasuredAt,
    DateTimeOffset? LastAudioOutputAt,
    float AudioRms,
    float AudioPeak,
    double AfcCorrectionHz,
    double DemodulationSampleRateHz);
