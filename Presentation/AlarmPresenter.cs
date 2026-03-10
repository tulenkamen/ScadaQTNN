using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ScadaQTNN.Data;
using ScadaQTNN.Models;

namespace ScadaQTNN.Presentation
{
    public sealed class AlarmPresenter : IDisposable
    {
        private readonly IAlarmView _view;
        private readonly IAlarmRepository _repository;
        private readonly Dictionary<int, string> _wellNameMap;
        private CancellationTokenSource _cts;
        private Task _pollTask;
        private HashSet<int> _knownIds;
        private bool _initialLoadDone;
        private bool _disposed;

        private const int PollIntervalMs = 5000;

        public AlarmPresenter(
            IAlarmView view,
            IAlarmRepository repository,
            Dictionary<int, string> wellNameMap)
        {
            _view = view ?? throw new ArgumentNullException(nameof(view));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _wellNameMap = wellNameMap ?? throw new ArgumentNullException(nameof(wellNameMap));
            _knownIds = new HashSet<int>();
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException) { }
        }

        private async Task PollLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await PollOnceAsync(ct).ConfigureAwait(false);
                    await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Error(ex, "AlarmPresenter.PollLoopAsync");
            }
        }

        private async Task PollOnceAsync(CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                var alarms = await _repository.GetLatestAsync().ConfigureAwait(false);

                foreach (var alarm in alarms)
                {
                    if (_wellNameMap.TryGetValue(alarm.WellId, out var name))
                        alarm.WellName = name;
                    else
                        alarm.WellName = $"#{alarm.WellId}";
                }

                if (!_initialLoadDone)
                {
                    _initialLoadDone = true;
                    _knownIds = new HashSet<int>(alarms.Select(a => a.Id));
                    _view.ReplaceAll(alarms);
                }
                else
                {
                    foreach (var alarm in alarms)
                    {
                        _knownIds.Add(alarm.Id);
                        _view.Upsert(alarm);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "AlarmPresenter.PollOnceAsync");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Stop();
                _cts?.Dispose();
                _cts = null;
            }
        }
    }
}
