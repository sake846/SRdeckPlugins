using System.IO;
using System.Threading.Channels;
using SRdeckPlugin.OneSeg.Models;

namespace SRdeckPlugin.OneSeg.Media;

internal sealed record OneSegRecordingSnapshot(string Directory, OneSegSettings Settings,
    string? SelectedStation, string? ServiceName, string? AudioTrack);
internal readonly record struct OneSegRecordedPacket(byte[] Data, DateTimeOffset ReceivedAt,
    OneSegRecordingSnapshot Snapshot);
internal sealed record OneSegRecordingStatus(bool Active, string Message, string Reason = "", Exception? Error = null);
internal sealed record OneSegRecordingResult(string Reason, long WrittenPackets, long DroppedPackets, Exception? Error);

internal interface IOneSegRecordingSink : IDisposable
{
    void Write(OneSegRecordedPacket packet);
    void Finish(string reason);
}

internal sealed class OneSegRecordingCapacityException() : IOException("保存先容量上限に到達しました。");

// One worker per recording session. A new session awaits the previous worker,
// so stop/start never races the old session's final flush or capacity cleanup.
internal sealed class OneSegRecordingWriter
{
    internal const int DefaultCapacityPackets = 8192; // 1.54 MB of TS payload.
    private readonly object gate = new();
    private readonly Channel<OneSegRecordedPacket> packets;
    private readonly Action<OneSegRecordingStatus> report;
    private OneSegRecordingSnapshot snapshot;
    private bool accepting = true;
    private string endReason = "normal";
    private long overflowPackets;
    public Task<OneSegRecordingResult> Completion { get; }
    public bool IsAccepting { get { lock (gate) return accepting; } }

    public OneSegRecordingWriter(OneSegRecordingSnapshot snapshot, Action<OneSegRecordingStatus> report,
        Task? previous = null, Func<OneSegRecordingSnapshot, IOneSegRecordingSink>? createSink = null,
        int capacityPackets = DefaultCapacityPackets)
    {
        this.snapshot = snapshot;
        this.report = report;
        packets = Channel.CreateBounded<OneSegRecordedPacket>(new BoundedChannelOptions(capacityPackets)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Completion = Task.Run(() => RunAsync(snapshot, previous ?? Task.CompletedTask,
            createSink ?? (options => new OneSegRecordingSink(options, started, Report))));
    }

    public void UpdateSnapshot(OneSegRecordingSnapshot value) => Volatile.Write(ref snapshot, value);

    private void Report(OneSegRecordingStatus status)
    {
        if (status.Active && !IsAccepting) return;
        report(status);
    }

    // Ownership: callers must never mutate an accepted array. The outer decoder
    // already produces an independently owned array for each TS packet.
    public bool SubmitOwnedPacket(byte[] packet, DateTimeOffset receivedAt)
    {
        lock (gate)
        {
            if (!accepting) return false;
            if (packets.Writer.TryWrite(new(packet, receivedAt, Volatile.Read(ref snapshot)))) return true;
            accepting = false;
            endReason = "queue-overflow";
            overflowPackets++;
            packets.Writer.TryComplete();
        }
        report(new(false, "TS保存停止: 保存処理が追いつきません（録画キュー上限）", "queue-overflow"));
        return false;
    }

    public Task<OneSegRecordingResult> StopAsync(string reason)
    {
        lock (gate)
        {
            if (accepting) { accepting = false; endReason = reason; }
            packets.Writer.TryComplete();
        }
        return Completion;
    }

    private async Task<OneSegRecordingResult> RunAsync(OneSegRecordingSnapshot initial, Task previous,
        Func<OneSegRecordingSnapshot, IOneSegRecordingSink> createSink)
    {
        IOneSegRecordingSink? sink = null;
        Exception? error = null;
        long written = 0, dropped = 0;
        string? failureReason = null;
        try
        {
            await previous.ConfigureAwait(false);
            sink = createSink(initial);
            // The disk sink buffers writes in 64 KiB blocks. Drain available
            // packets without creating a task or waiting for each packet.
            while (await packets.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (packets.Reader.TryRead(out OneSegRecordedPacket packet))
                {
                    try { sink.Write(packet); written++; }
                    catch { dropped++; throw; }
                }
            }
        }
        catch (Exception exception)
        {
            error = exception;
            failureReason = exception is OneSegRecordingCapacityException ? "capacity" : "io-error";
        }
        finally
        {
            lock (gate) { accepting = false; packets.Writer.TryComplete(); }
            while (packets.Reader.TryRead(out _)) dropped++;
        }
        string reason;
        lock (gate) { reason = failureReason ?? endReason; dropped += overflowPackets; }
        if (sink is not null)
        {
            try { sink.Finish(reason); }
            catch (Exception exception) { error ??= exception; reason = "io-error"; }
            finally
            {
                try { sink.Dispose(); }
                catch (Exception exception) { error ??= exception; reason = "io-error"; }
            }
        }
        string message = reason switch
        {
            "queue-overflow" => "TS保存停止: 保存処理が追いつきません（録画キュー上限）",
            "capacity" => "TS保存停止: 保存先容量上限に到達",
            "io-error" => $"TS保存失敗: {error?.Message}",
            _ => "TS保存停止"
        };
        if (dropped > 0) message += $" / 未保存 {dropped:N0}パケット";
        report(new(false, message, reason, error));
        return new(reason, written, dropped, error);
    }
}
