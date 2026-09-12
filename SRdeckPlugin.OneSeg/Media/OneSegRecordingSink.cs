using System.IO;
using System.Text.Json;

namespace SRdeckPlugin.OneSeg.Media;

// All methods run on the recording worker. No receiver lock, view-model access,
// or mutable plugin settings are used during disk operations.
internal sealed class OneSegRecordingSink : IOneSegRecordingSink
{
    private readonly Action<OneSegRecordingStatus> report;
    private OneSegRecordingSnapshot snapshot;
    private FileStream? stream;
    private string? path;
    private DateTimeOffset openedAt;
    private int segmentIndex;
    private long packetCount;

    public OneSegRecordingSink(OneSegRecordingSnapshot snapshot, DateTimeOffset started,
        Action<OneSegRecordingStatus> report)
    {
        this.snapshot = snapshot;
        this.report = report;
        Open(started);
    }

    public void Write(OneSegRecordedPacket packet)
    {
        if (stream is null) throw new IOException("録画ファイルが開かれていません。");
        var settings = packet.Snapshot.Settings;
        bool channelChanged = settings.Channel != snapshot.Settings.Channel ||
            settings.PrefectureId != snapshot.Settings.PrefectureId || packet.Snapshot.Directory != snapshot.Directory;
        bool segmentByTime = settings.RecordingSegmentMinutes > 0 &&
            packet.ReceivedAt - openedAt >= TimeSpan.FromMinutes(settings.RecordingSegmentMinutes);
        bool segmentBySize = settings.RecordingMaxBytes > 0 && stream.Length > 0 &&
            stream.Length + packet.Data.Length > settings.RecordingMaxBytes;
        if (channelChanged || segmentByTime || segmentBySize)
        {
            Finish(channelChanged ? "channel-change" : "segment");
            snapshot = packet.Snapshot;
            segmentIndex++;
            Open(packet.ReceivedAt);
        }
        else snapshot = packet.Snapshot;
        if (settings.RecordingStorageLimitBytes > 0 &&
            stream!.Length + packet.Data.Length > settings.RecordingStorageLimitBytes)
            throw new OneSegRecordingCapacityException();
        stream!.Write(packet.Data);
        packetCount++;
    }

    private void Open(DateTimeOffset started)
    {
        string directory = snapshot.Directory;
        Directory.CreateDirectory(directory);
        string root = Path.GetPathRoot(Path.GetFullPath(directory)) ?? directory;
        if (new DriveInfo(root).AvailableFreeSpace < 64L * 1024 * 1024)
            throw new IOException("録画先の空き容量が安全しきい値未満です。");
        EnforceStorageLimit(directory);
        string station = SanitizeFilePart(snapshot.ServiceName ?? snapshot.SelectedStation ?? "station");
        string suffix = segmentIndex == 0 ? "" : $"-{segmentIndex:D3}";
        path = Path.Combine(directory, $"oneseg-ch{snapshot.Settings.Channel}-{station}-{started.LocalDateTime:yyyyMMdd-HHmmss}{suffix}.ts");
        while (File.Exists(path))
            path = Path.Combine(directory, $"oneseg-ch{snapshot.Settings.Channel}-{started.LocalDateTime:yyyyMMdd-HHmmss}-{segmentIndex:D3}-{Guid.NewGuid():N}.ts");
        stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        openedAt = started;
        packetCount = 0;
        try { TryWriteMetadata(null, "in-progress"); }
        catch { stream.Dispose(); stream = null; throw; }
        report(new(true, $"TS保存中: {path}"));
    }

    public void Finish(string reason)
    {
        if (stream is null) return;
        Exception? failure = null;
        try { stream.Flush(true); }
        catch (Exception exception) { failure = exception; reason = "io-error"; }
        try { stream.Dispose(); }
        catch (Exception exception) { failure ??= exception; reason = "io-error"; }
        stream = null;
        TryWriteMetadata(DateTimeOffset.UtcNow, reason);
        if (failure is not null) throw new IOException("録画ファイルの終了処理に失敗しました。", failure);
    }

    private void WriteMetadata(DateTimeOffset? endedAt, string reason)
    {
        if (!snapshot.Settings.RecordingSidecarEnabled || path is null) return;
        var settings = snapshot.Settings;
        var metadata = new
        {
            Schema = "SRdeck OneSeg transport stream recording v1",
            StartedAtUtc = openedAt, EndedAtUtc = endedAt,
            RawTsFile = Path.GetFileName(path), settings.PrefectureId, settings.Channel,
            snapshot.SelectedStation, settings.LastServiceId, snapshot.ServiceName,
            snapshot.AudioTrack, settings.CaptionsEnabled, TsPacketCount = packetCount,
            EndReason = reason
        };
        string sidecar = Path.ChangeExtension(path, ".json"), temporary = sidecar + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, sidecar, true);
    }

    private void TryWriteMetadata(DateTimeOffset? endedAt, string reason)
    {
        try { WriteMetadata(endedAt, reason); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The sidecar is optional. Preserve TS recording if only its JSON
            // cannot be saved, while making the failure visible and logged.
            report(new(stream is not null, $"TSメタデータ保存失敗: {exception.Message}", "metadata-error", exception));
        }
    }

    private void EnforceStorageLimit(string directory)
    {
        long limit = snapshot.Settings.RecordingStorageLimitBytes;
        if (limit <= 0) return;
        var files = Directory.EnumerateFiles(directory, "oneseg-*.ts")
            .Select(name => new FileInfo(name)).OrderBy(file => file.LastWriteTimeUtc).ToList();
        long Size(FileInfo file)
        {
            string sidecar = Path.ChangeExtension(file.FullName, ".json");
            return (file.Exists ? file.Length : 0) + (File.Exists(sidecar) ? new FileInfo(sidecar).Length : 0);
        }
        long total = files.Sum(Size);
        foreach (FileInfo file in files)
        {
            if (total <= limit) break;
            long bytes = Size(file);
            file.Delete();
            File.Delete(Path.ChangeExtension(file.FullName, ".json"));
            total -= bytes;
        }
    }

    private static string SanitizeFilePart(string value)
    {
        string sanitized = string.Concat(value.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return string.IsNullOrWhiteSpace(sanitized) ? "station" : sanitized[..Math.Min(32, sanitized.Length)];
    }

    public void Dispose() => Finish("stream-stop");
}
