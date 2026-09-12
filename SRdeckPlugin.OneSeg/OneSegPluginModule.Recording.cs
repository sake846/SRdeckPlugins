using System.IO;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.OneSeg.Media;
using SRdeckPlugin.OneSeg.Models;

namespace SRdeckPlugin.OneSeg;

public sealed partial class OneSegPluginModule
{
    private void WriteRecordingPacket(byte[] packet) =>
        recording?.SubmitOwnedPacket(packet, DateTimeOffset.UtcNow);

    private void SetRecording(bool enabled)
    {
        lock (processingGate)
        {
            if (State == PluginLifecycleState.Streaming && enabled) OpenRecording();
            else if (!enabled)
            {
                _ = CloseRecording("user-stop");
                Dispatch(() => { viewModel.SetRecordingState(false); viewModel.RecordingStatus = "TS保存終了中…"; });
            }
        }
        PersistSettings();
    }

    private void OpenRecording()
    {
        IPluginHostContext? context = host;
        if (context is null || recording?.IsAccepting == true) return;
        int generation = Interlocked.Increment(ref recordingGeneration);
        OneSegRecordingSnapshot snapshot = CreateRecordingSnapshot(context);
        Dispatch(() => { viewModel.SetRecordingState(true); viewModel.RecordingStatus = "TS保存開始中…"; });
        recording = new OneSegRecordingWriter(snapshot, status =>
        {
            if (status.Error is not null)
                context.Logger.Log(PluginLogLevel.Warning, "oneseg.recording.write", status.Message, status.Error);
            context.Dispatcher.Post(() =>
            {
                if (IsDisposed || generation != Volatile.Read(ref recordingGeneration)) return;
                if (status.Active && Volatile.Read(ref recording)?.IsAccepting != true) return;
                viewModel.SetRecordingState(status.Active);
                viewModel.RecordingStatus = status.Message;
            });
        }, recordingTail);
        recordingTail = recording.Completion;
    }

    // Must be called under processingGate. Completion is awaited outside it.
    private Task CloseRecording(string reason)
    {
        OneSegRecordingWriter? current = recording;
        recording = null;
        if (current is not null) recordingTail = current.StopAsync(reason);
        return recordingTail;
    }

    private void RefreshRecordingSnapshot()
    {
        lock (processingGate)
        {
            if (recording is not null && host is IPluginHostContext context)
                recording.UpdateSnapshot(CreateRecordingSnapshot(context));
        }
    }

    private OneSegRecordingSnapshot CreateRecordingSnapshot(IPluginHostContext context)
    {
        OneSegSettings current = settings;
        // During tuning the requested channel can lead the actual IQ. Attribute
        // each queued packet to its received channel, including transition tails.
        OneSegChannel? received = OneSegChannel.JapaneseUhfChannels.FirstOrDefault(channel =>
            channel.CenterFrequencyHz == lastReceivedCenterFrequencyHz);
        if (received is not null) current = current with { Channel = received.Number };
        OneSegStation? selected = viewModel.SelectedStation;
        OneSegStation? station = selected?.ChannelNumber == current.Channel &&
            (!current.LastServiceId.HasValue || selected.ServiceId == current.LastServiceId) ? selected :
            (current.ScannedStations ?? []).FirstOrDefault(value => value.ChannelNumber == current.Channel &&
                (!current.LastServiceId.HasValue || value.ServiceId == current.LastServiceId));
        return new(current.RecordingDirectory ?? Path.Combine(context.Settings.DataDirectory, "recordings"),
            current, station?.DisplayName, station?.ServiceName, viewModel.SelectedAudioTrack?.DisplayName);
    }
}
