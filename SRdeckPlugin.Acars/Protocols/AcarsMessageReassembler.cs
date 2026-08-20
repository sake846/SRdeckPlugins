using System.Text;
using SRdeckPlugin.Acars.Models;

namespace SRdeckPlugin.Acars.Protocols;

/// <summary>
/// Reassembles ARINC 618 messages terminated by ETB. Incomplete messages are
/// released after a short timeout so a lost final block does not hide data.
/// </summary>
public sealed class AcarsMessageReassembler
{
    private static readonly TimeSpan FragmentTimeout = TimeSpan.FromSeconds(30);
    private readonly Dictionary<Key, PendingMessage> pending = [];

    public IReadOnlyList<ReassembledMessage> Process(AcarsFrame frame, AcarsMessage message)
    {
        List<ReassembledMessage> output = FlushExpired(frame.ReceivedAt);
        var key = new Key(frame.StreamId, frame.FrequencyHz, message.Mode,
            message.AircraftRegistration, message.Label);

        if (pending.TryGetValue(key, out PendingMessage? existing) &&
            !IsNextBlock(existing.LastBlockId, message.BlockId))
        {
            output.Add(existing.ToIncompleteResult());
            pending.Remove(key);
            existing = null;
        }

        if (message.IsContinuationBlock)
        {
            if (existing is null)
                pending[key] = new(frame, message);
            else
                existing.Append(frame, message);
            return output;
        }

        if (existing is null)
        {
            output.Add(new(frame, message));
            return output;
        }

        existing.Append(frame, message);
        pending.Remove(key);
        output.Add(existing.ToCompleteResult());
        return output;
    }

    public IReadOnlyList<ReassembledMessage> Drain()
    {
        ReassembledMessage[] output = pending.Values
            .Select(item => item.ToIncompleteResult())
            .ToArray();
        pending.Clear();
        return output;
    }

    private List<ReassembledMessage> FlushExpired(DateTimeOffset now)
    {
        List<ReassembledMessage> output = [];
        foreach ((Key key, PendingMessage value) in pending.ToArray())
        {
            if (now - value.LastReceivedAt <= FragmentTimeout) continue;
            output.Add(value.ToIncompleteResult());
            pending.Remove(key);
        }
        return output;
    }

    private static bool IsNextBlock(string previous, string current)
    {
        if (previous.Length != 1 || current.Length != 1) return true;
        char expected = previous[0] switch
        {
            >= '0' and < '9' => (char)(previous[0] + 1),
            '9' => '0',
            >= 'A' and < 'Z' => (char)(previous[0] + 1),
            'Z' => 'A',
            _ => current[0]
        };
        return char.ToUpperInvariant(current[0]) == expected;
    }

    private readonly record struct Key(Guid StreamId, long FrequencyHz, string Mode, string Aircraft, string Label);

    private sealed class PendingMessage(AcarsFrame firstFrame, AcarsMessage firstMessage)
    {
        private readonly StringBuilder text = new(firstMessage.Text);
        private readonly string firstBlockId = firstMessage.BlockId;
        private AcarsFrame latestFrame = firstFrame;
        private AcarsMessage latestMessage = firstMessage;

        public string LastBlockId => latestMessage.BlockId;
        public DateTimeOffset LastReceivedAt => latestFrame.ReceivedAt;

        public void Append(AcarsFrame frame, AcarsMessage message)
        {
            text.Append(message.Text);
            latestFrame = frame;
            latestMessage = message;
        }

        public ReassembledMessage ToCompleteResult() => CreateResult(false);
        public ReassembledMessage ToIncompleteResult() => CreateResult(true);

        private ReassembledMessage CreateResult(bool incomplete)
        {
            string combinedText = incomplete
                ? $"{text}\n[ACARS複数ブロック: 最終ブロック未受信]"
                : text.ToString();
            string blockId = firstBlockId == latestMessage.BlockId
                ? firstBlockId
                : $"{firstBlockId}-{latestMessage.BlockId}";
            AcarsMessage combined = latestMessage with
            {
                BlockId = blockId,
                Text = combinedText,
                IsContinuationBlock = false
            };
            return new(latestFrame, combined);
        }
    }
}

public sealed record ReassembledMessage(AcarsFrame Frame, AcarsMessage Message);
