
namespace SRdeckPlugin.Meshtastic.Services;

public sealed partial class MeshtasticReceiveService
{
    public void StartStream()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _streamLifecycleGate.Wait();
        try
        {
            Volatile.Write(ref _acceptingStreamData, 0);
            SpinWait.SpinUntil(() => Volatile.Read(ref _activeEnqueues) == 0);
            // A failed block records its failure before completing this drain.
            // This also covers restart immediately after StopStreamAsync returns.
            WaitForDrainAsync(Interlocked.Read(ref _enqueuedWorkItems)).GetAwaiter().GetResult();
            if (Volatile.Read(ref _workerFailure) is not null)
                _worker.GetAwaiter().GetResult();
            lock (_streamGate)
            {
                ResetStreamState();
                if (_worker.IsCompleted)
                {
                    Volatile.Write(ref _workerFailure, null);
                    _worker = StartWorker();
                }
                Volatile.Write(ref _acceptingStreamData, 1);
            }
        }
        finally
        {
            _streamLifecycleGate.Release();
        }
    }

    public async ValueTask StopStreamAsync(CancellationToken cancellationToken = default)
    {
        await _streamLifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _acceptingStreamData, 0);
            while (Volatile.Read(ref _activeEnqueues) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            long target = Interlocked.Read(ref _enqueuedWorkItems);
            Task drain = WaitForDrainAsync(target);
            if (!drain.IsCompleted)
            {
                Task completed = await Task.WhenAny(drain, _worker)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                if (completed == _worker && Interlocked.Read(ref _completedWorkItems) < target)
                    throw new InvalidOperationException(
                        "The Meshtastic decoder worker stopped before queued IQ was drained.");
                await drain.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            lock (_streamGate) ResetStreamState();
        }
        finally
        {
            _streamLifecycleGate.Release();
        }
    }

    private Task WaitForDrainAsync(long target)
    {
        lock (_drainGate)
        {
            _drainTarget = target;
            _drainCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _drainRequested, 1);
            if (Interlocked.Read(ref _completedWorkItems) >= target)
            {
                _drainCompletion = null;
                Volatile.Write(ref _drainRequested, 0);
                return Task.CompletedTask;
            }
            return _drainCompletion.Task;
        }
    }

    private void CompleteWorkItem()
    {
        long completed = Interlocked.Increment(ref _completedWorkItems);
        if (Volatile.Read(ref _drainRequested) == 0) return;

        TaskCompletionSource? completion = null;
        lock (_drainGate)
        {
            if (_drainCompletion is not null && completed >= _drainTarget)
            {
                completion = _drainCompletion;
                _drainCompletion = null;
                Volatile.Write(ref _drainRequested, 0);
            }
        }
        completion?.TrySetResult();
    }

    private void ResetStreamState()
    {
        while (_blocks.TryTake(out IqBlock? block))
        {
            block.Dispose();
            CompleteWorkItem();
        }
        foreach (ChannelReceiver receiver in _channelReceivers) receiver.Reset();
        _lastProcessedSequence = -1;
        _lastProcessedSampleEnd = -1;
        _lastProcessedConfigurationVersion = -1;
        lock (_recentPacketsGate)
        {
            _recentPackets.Clear();
            _recentPacketOrder.Clear();
        }
        Interlocked.Increment(ref _ringGeneration);
        Volatile.Write(ref _targetInPassband, 0);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _acceptingStreamData, 0);
        _streamLifecycleGate.Wait();
        try
        {
            SpinWait.SpinUntil(() => Volatile.Read(ref _activeEnqueues) == 0);
            lock (_streamGate) ResetStreamState();
            _blocks.CompleteAdding();
            _cancellation.Cancel();
            try
            {
                _worker.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException exception)
            {
                exception.Handle(e => e is OperationCanceledException);
            }
        }
        finally
        {
            _streamLifecycleGate.Release();
        }
        _blocks.Dispose();
        _cancellation.Dispose();
        _streamLifecycleGate.Dispose();
    }
}
