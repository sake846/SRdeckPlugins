using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using SRdeckPlugin.Meshtastic.ViewModels;

namespace SRdeckPlugin.Meshtastic.Services;

/// <summary>One active write and one replaceable pending snapshot; writes never overlap.</summary>
internal sealed class MeshtasticStateWriter(string path,
    Func<string, MeshtasticPersistedState, Task>? write = null) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Func<string, MeshtasticPersistedState, Task> writeState = write ?? WriteFileAsync;
    private MeshtasticPersistedState? pending;
    private Task worker = Task.CompletedTask;
    private bool running;
    private bool disposed;
    private Exception? lastFailure;
    internal event Action<Exception>? SaveFailed;

    internal void Enqueue(MeshtasticPersistedState snapshot)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pending = snapshot;
            if (running) return;
            running = true;
            worker = Task.Run(ProcessAsync);
        }
    }

    private async Task ProcessAsync()
    {
        while (true)
        {
            MeshtasticPersistedState snapshot;
            lock (gate)
            {
                if (pending is null) { running = false; return; }
                snapshot = pending;
                pending = null;
            }
            try
            {
                await writeState(path, snapshot).ConfigureAwait(false);
                lock (gate) lastFailure = null;
            }
            catch (Exception exception)
            {
                lock (gate) lastFailure = exception;
                // An error reporter must not stop subsequent saves or disposal.
                try { SaveFailed?.Invoke(exception); }
                catch (Exception) { }
            }
        }
    }

    internal async Task FlushAsync()
    {
        Task current;
        lock (gate) current = worker;
        await current.ConfigureAwait(false);
        lock (gate)
            if (lastFailure is not null)
                throw new IOException("Meshtastic node state could not be saved.", lastFailure);
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate) disposed = true;
        await FlushAsync().ConfigureAwait(false);
    }

    private static async Task WriteFileAsync(string path, MeshtasticPersistedState state)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, json).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            // Preserve the original write error if cleanup also fails.
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
