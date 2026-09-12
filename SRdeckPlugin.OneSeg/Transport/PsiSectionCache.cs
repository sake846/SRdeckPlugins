using System.Buffers.Binary;

namespace SRdeckPlugin.OneSeg.Transport;

// Only validated, successfully parsed PSI/SI is remembered. One entry per
// section identity replaces old versions, and FIFO eviction bounds retention
// to 512 * 4096 bytes. Eviction causes a reparse, never loss of an update.
internal sealed class PsiSectionCache
{
    private const int Capacity = 512;
    private readonly Dictionary<(ushort Pid, byte Table, ushort Extension, byte Section), byte[]> sections = [];
    private readonly Queue<(ushort Pid, byte Table, ushort Extension, byte Section)> order = [];

    public bool Contains(ushort pid, byte[] section) =>
        sections.TryGetValue(Key(pid, section), out byte[]? previous) && section.AsSpan().SequenceEqual(previous);

    public void Remember(ushort pid, byte[] section)
    {
        var key = Key(pid, section);
        if (!sections.ContainsKey(key))
        {
            if (sections.Count == Capacity) sections.Remove(order.Dequeue());
            order.Enqueue(key);
        }
        // SectionAssembler transfers its own array; it is never reused/mutated.
        sections[key] = section;
    }

    public void Clear() { sections.Clear(); order.Clear(); }

    private static (ushort, byte, ushort, byte) Key(ushort pid, byte[] section) =>
        (pid, section[0], BinaryPrimitives.ReadUInt16BigEndian(section.AsSpan(3)), section[6]);
}
