namespace SRdeckPlugin.OneSeg.Models;

internal static class OneSegProgramComparison
{
    public static bool Equivalent(OneSegProgramInfo left, OneSegProgramInfo right) =>
        ReferenceEquals(left, right) ||
        (left.ServiceId == right.ServiceId && left.ServiceName == right.ServiceName &&
         left.ProviderName == right.ProviderName && left.PmtPid == right.PmtPid &&
         left.PcrPid == right.PcrPid && left.Streams.SequenceEqual(right.Streams));
}
