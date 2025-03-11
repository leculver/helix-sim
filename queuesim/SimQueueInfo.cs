using System.Collections;
using Microsoft.Helix.QueueScale;

namespace QueueSim;

class SimQueueInfo(ICollection work, ICollection machines, ICollection spinup, TimeSpan spinupTime)
            : IQueueInformationProvider
{
    private ICollection _work = work;
    private ICollection _machines = machines;
    private ICollection _spinup = spinup;
    private readonly List<WorkItem> _completed = [];
    private TimeSpan SpinupTime { get; } = spinupTime;


    public DateTime CurrentTime { get; set; }

    public DateTime UtcNow => CurrentTime;

    public void AddCompleted(WorkItem item)
    {
        _completed.Add(item);
    }

    public Task<double> EstimateWorkItemDurationAsync(CancellationToken ct)
    {
        if (_completed.Count == 0)
            return Task.FromResult(10.0);

        // sort descending
        if (_completed.Count > 100)
        {
            _completed.Sort((x, y) => y.Queued.CompareTo(x.Queued));
            _completed.RemoveRange(100, _completed.Count - 100);
        }

        return Task.FromResult(_completed.Average(r => r.Duration));
    }

    public Task<double> GetWorkItemNthPercentileAsync(int percentile, CancellationToken ct)
    {
        if (_completed.Count == 0)
            return Task.FromResult(10.0);

        // sort descending
        if (_completed.Count > 100)
        {
            _completed.Sort((x, y) => y.Queued.CompareTo(x.Queued));
            _completed.RemoveRange(100, _completed.Count - 100);
        }

        int index = (int)(_completed.Count * (percentile / 100.0));
        if (index >= _completed.Count)
            index = _completed.Count - 1;

        _completed.Sort((x, y) => x.Duration.CompareTo(y.Duration));
        return Task.FromResult(_completed[index].Duration);
    }

    public Task<int> GetActiveMessagesAsync(CancellationToken ct)
    {
        return Task.FromResult(_work.Count);
    }

    public Task<TimeSpan> GetMachineCreationTimeAsync(CancellationToken ct)
    {
        return Task.FromResult(SpinupTime);
    }

    public Task<QueueMachines> GetQueueMachinesAsync(CancellationToken ct)
    {
        return Task.FromResult(new QueueMachines(_machines.Count, _spinup.Count));
    }

    public Task<QueueSettings> GetQueueSettingsAsync(CancellationToken ct)
    {
        return Task.FromResult(new QueueSettings(0, int.MaxValue, 1, TimeSpan.FromMinutes(30)));
    }
}
