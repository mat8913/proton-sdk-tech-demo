namespace Proton.Sdk;

public abstract class EventChannelBase<TEventId>
    where TEventId : struct
{
    private IEventPoller? _currentPoller;

    private protected interface IEventPoller : IAsyncDisposable
    {
        void Start();
    }

    public TEventId? BaselineEventId { get; private set; }

    public void Start()
    {
        if (_currentPoller != null)
        {
            return;
        }

        var newPoller = CreateEventPoller();
        if (Interlocked.CompareExchange(ref _currentPoller, newPoller, null) is not null)
        {
            return;
        }

        newPoller.Start();
    }

    public async Task StopAsync()
    {
        var previousPoller = Interlocked.Exchange(ref _currentPoller, null);
        if (previousPoller is null)
        {
            return;
        }

        await previousPoller.DisposeAsync().ConfigureAwait(false);
    }

    private protected abstract IEventPoller CreateEventPoller();

    private protected abstract class EventPollerBase<TEvents> : IEventPoller
    {
        private readonly TimeSpan _pollingInterval = JitterGenerator.ApplyJitter(ProtonApiDefaults.DefaultPollingInterval, 0.2);
        private readonly Timer _timer;
        private readonly CancellationTokenSource _cancellationTokenSource = new();

        private Task _pollingTask = Task.CompletedTask;

        protected EventPollerBase()
        {
            _timer = new Timer(OnTimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        protected abstract EventChannelBase<TEventId> Owner { get; }

        public void Start()
        {
            _timer.Change(TimeSpan.Zero, _pollingInterval);
        }

        public async ValueTask DisposeAsync()
        {
            using (_cancellationTokenSource)
            {
                await using (_timer)
                {
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
                }

                await _cancellationTokenSource.CancelAsync().ConfigureAwait(false);

                await _pollingTask.ConfigureAwait(false);
            }
        }

        protected abstract ValueTask<TEventId> GetLatestEventIdAsync(CancellationToken cancellationToken);

        protected abstract ValueTask<(TEvents Events, bool MoreEntriesExist, TEventId LastEventId)> GetEventsAsync(
            TEventId baselineEventId,
            CancellationToken cancellationToken);

        protected abstract ValueTask DispatchEventsAsync(TEvents events, CancellationToken cancellationToken);

        private void OnTimerTick(object? state)
        {
            _pollingTask = PollAsync(_cancellationTokenSource.Token);
        }

        private async Task PollAsync(CancellationToken cancellationToken)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);

            try
            {
                var baselineEventId = Owner.BaselineEventId;
                if (baselineEventId is null)
                {
                    Owner.BaselineEventId = await GetLatestEventIdAsync(cancellationToken).ConfigureAwait(false);

                    _timer.Change(_pollingInterval, _pollingInterval);
                    return;
                }

                var moreEntriesExist = true;

                while (moreEntriesExist)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    (var events, moreEntriesExist, var lastEventId) = await GetEventsAsync(baselineEventId.Value, cancellationToken).ConfigureAwait(false);

                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    Owner.BaselineEventId = lastEventId;

                    await DispatchEventsAsync(events, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // TODO: decide what to do here
            }

            _timer.Change(_pollingInterval, _pollingInterval);
        }
    }
}
