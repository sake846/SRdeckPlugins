namespace SRdeckPlugin.Acars.Protocols;

internal readonly record struct AcarsInterpretationInput(string Label, string Text);

internal delegate bool AcarsInterpretationRule(
    AcarsInterpretationInput input,
    out string summary);

internal sealed record AcarsInterpretationRuleDefinition(
    string Name,
    AcarsInterpretationCategory Category,
    AcarsInterpretationRule Handler,
    bool IsInterpreted = true);

internal enum AcarsInterpretationCategory
{
    Arinc620622CpdlcAtis,
    OooiPositionWeatherFlightPlan,
    AcmsTelemetryAirlineSpecific,
    CommonFallback
}

/// <summary>
/// Runs ACARS interpretation rules in their declared order.
/// The first matching rule wins; the order is part of the compatibility contract.
/// </summary>
internal sealed class AcarsInterpretationPipeline
{
    private readonly IReadOnlyList<AcarsInterpretationRuleDefinition> rules;

    public AcarsInterpretationPipeline(
        IReadOnlyList<AcarsInterpretationRuleDefinition> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        this.rules = rules;
    }

    public bool TryInterpret(AcarsInterpretationInput input, out string summary)
    {
        bool matched = TryInterpret(input, out summary, out bool isInterpreted);
        return matched && isInterpreted;
    }

    public bool TryInterpret(
        AcarsInterpretationInput input,
        out string summary,
        out bool isInterpreted)
    {
        foreach (AcarsInterpretationRuleDefinition rule in rules)
            if (rule.Handler(input, out summary))
            {
                isInterpreted = rule.IsInterpreted;
                return true;
            }

        summary = string.Empty;
        isInterpreted = false;
        return false;
    }

    internal IReadOnlyList<AcarsInterpretationRuleDefinition> Rules => rules;
}
