using SRdeckPlugin.Acars.Models;
using SRdeckPlugin.Acars.Protocols;

namespace SRdeckPlugin.Acars;

/// <summary>
/// Application-facing ACARS message interpretation boundary.
/// </summary>
public sealed class AcarsMessageInterpretationService
{
    public AcarsInterpretation Interpret(AcarsMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return AcarsMessageInterpreter.InterpretDetailed(message.Label, message.Text);
    }
}
