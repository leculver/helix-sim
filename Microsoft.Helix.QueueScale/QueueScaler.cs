namespace Microsoft.Helix.QueueScale;


public class QueueScaler : IQueueScaler
{
    private readonly LinkedList<(DateTime When, int Machines)> _spindownTimes = [];
    private readonly List<QueueState> _history = [];
    private const double SlaScale = 0.8;
    private const int WorkItemPercentile = 90;
    private static readonly TimeSpan SpinDownDelay = TimeSpan.FromMinutes(12);

    public async Task<int> GetQueueNeededCapacityAsync(IQueueInformationProvider provider, CancellationToken ct)
    {
        // Get the estimated duration of a work item, but ensure it's at least 2 minutes to account for cleanup
        // and just oddly short work items.
        double workItemDuration = await provider.GetWorkItemNthPercentileAsync(WorkItemPercentile, ct);
        workItemDuration = Math.Max(workItemDuration, 2);

        int waitingWorkItems = await provider.GetActiveMessagesAsync(ct);

        QueueSettings settings = await provider.GetQueueSettingsAsync(ct);
        QueueMachines machines = await provider.GetQueueMachinesAsync(ct);
        TimeSpan machineCreationTime = await provider.GetMachineCreationTimeAsync(ct);

        int diff = GetQueueCapacityDiffAsync(settings, machines, workItemDuration, waitingWorkItems,
                                             machineCreationTime, provider.UtcNow, ct);

        // Scale up based on the multiplier.
        if (diff > 0 && settings.Multiplier > 0)
            diff = (int)Math.Ceiling(diff * settings.Multiplier);

        int newCapacity = machines.Current + diff;
        newCapacity = Math.Min(newCapacity, settings.MaxCapacity);
        newCapacity = Math.Max(newCapacity, settings.MinCapacity);

        UpdateHistory(waitingWorkItems, machines, diff, provider.UtcNow);

        return newCapacity;
    }

    private void UpdateHistory(int waitingWorkItems, QueueMachines machines, int diff, DateTime utcNow)
    {
        int spinningUp = machines.SpinningUp;
        if (diff > 0)
            spinningUp += diff;

        int willSpinDown = _spindownTimes.Sum(st => st.Machines);
        int machineCount = machines.Current;
        if (diff < 0)
            machineCount += -diff;
        QueueState current = new(utcNow, waitingWorkItems, machineCount, spinningUp, willSpinDown);
        _history.Add(current);
    }

    private int GetQueueCapacityDiffAsync(QueueSettings settings, QueueMachines machines, double workItemDuration,
                                          int waitingWorkItems, TimeSpan machineCreationTime, DateTime utcNow, CancellationToken ct)
    {
        // From the original code:  This scales down our SLA by a factor to account for cleanup overhead, machines
        // taking longer to spin up, and work taking longer than expected.  We assume a slightly better scaleup than
        // the original code since the code below assumes we always pay the full spinup time regardless of how long
        // ago we actually starting spinning up a machines.
        TimeSpan sla = TimeSpan.FromTicks((long)(settings.Sla.Ticks * SlaScale));

        // How long we have left in our SLA after spinning up a machine
        TimeSpan remainingSlaAfterSpinup = sla - machineCreationTime;
        if (remainingSlaAfterSpinup < TimeSpan.Zero)
            remainingSlaAfterSpinup = TimeSpan.Zero;

        // First, calculate how many work items we will process in our SLA with the current capacity.
        // Note that the number of active messages will not included locked messages already being processed, so we
        // subtact the number of workitems we think we will process by the number of active machines since they are
        // already processing an uncounted message.
        int spindownCount = _spindownTimes.Sum(st => st.Machines);
        int machinesAvailable = spindownCount == 0 ? machines.Current - 1 : machines.Current - spindownCount - 1;
        machinesAvailable = Math.Max(machinesAvailable, 0);
        int willProcess = (int)Math.Floor(sla.TotalMinutes / workItemDuration * machinesAvailable);

        // If our current plan for spinning down machines would cause us to miss our SLA, cancel spindown and
        // recalculate our items to process.  We do this regardless of spinning up machines since we will need to
        // cancel and recalculate anyway.
        if (spindownCount > 0 && willProcess < waitingWorkItems)
        {
            _spindownTimes.Clear();
            spindownCount = 0;
            machinesAvailable = machines.Current - 1;
            willProcess = (int)Math.Floor(sla.TotalMinutes / workItemDuration * machinesAvailable);
        }

        // Now add the expected number of work items that will be processed by the spinning up machines.
        if (machines.SpinningUp > 0)
        {
            int curr = (int)Math.Floor(remainingSlaAfterSpinup.TotalMinutes / workItemDuration * machines.SpinningUp);

            // We assume each machine spinning up will account for at least one work item.  This prevents us from
            // infinitely spinning up machines if the SLA is close to spinup time.
            willProcess += Math.Max(machines.SpinningUp, curr);
        }

        int machineDiff = 0;

        // Calculate the total number of work items we expect to be out of SLA, and if there are any, spin up machines.
        int workItemsOutOfSla = Math.Max(waitingWorkItems - willProcess, 0);
        if (workItemsOutOfSla > 0)
        {
            // Calculate how many work items a machine can process in the remaining SLA window.
            double workItemsPerMachine = remainingSlaAfterSpinup.TotalMinutes / workItemDuration;
            workItemsPerMachine = Math.Max(workItemsPerMachine, 1);
            int machinesToAdd = (int)Math.Ceiling(workItemsOutOfSla / workItemsPerMachine);
            machinesToAdd = Math.Min(machinesToAdd, workItemsOutOfSla);

            machineDiff = machinesToAdd;
        }
        else if (waitingWorkItems < machines.Current)
        {
            // If we have less work items than machines, scale down.
            DateTime now = utcNow;

            int spindownNeeded = machines.Current - waitingWorkItems;
            if (spindownNeeded > spindownCount)
            {
                _spindownTimes.AddLast((now + SpinDownDelay, spindownNeeded - spindownCount));
            }
            else if (spindownCount < spindownNeeded)
            {
                // We previously overestimated the number of machines to spin down, so we need to remove some from
                // the spindown queue.  We will remove the machines that were scheduled to spin down the earliest since
                // if we understimated before...we want to delay a little longer.
                while (spindownNeeded > 0 && _spindownTimes.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var (spindownTime, spindownMachines) = _spindownTimes.First!.Value;
                    if (spindownMachines <= spindownNeeded)
                    {
                        spindownNeeded -= spindownMachines;
                        _spindownTimes.RemoveFirst();
                    }
                    else
                    {
                        _spindownTimes.RemoveFirst();
                        _spindownTimes.AddFirst((spindownTime, spindownMachines - spindownNeeded));
                        spindownNeeded = 0;
                    }
                }
            }

            int machinesToRemove = 0;
            while (_spindownTimes.Count > 0 && _spindownTimes.First!.Value.When <= now)
            {
                ct.ThrowIfCancellationRequested();
                machinesToRemove += _spindownTimes.First.Value.Machines;
                _spindownTimes.RemoveFirst();
            }

            machineDiff = -machinesToRemove;
        }

        //Console.WriteLine($"  {utcNow}: QueueDepth: {waitingWorkItems}, OutOfSla: {workItemsOutOfSla} WorkItemDuration: {workItemDuration:n2}, Machines: {machines.Current}, SpinningUp: {machines.SpinningUp}, WillSpinDown: {spindownCount}, MachineDiff: {machineDiff}");

        // Clear any planned spindown if we need to spin up.
        if (machineDiff > 0)
            _spindownTimes.Clear();

        return machineDiff;
    }

    class QueueState(DateTime time, int workItems, int machines, int spinningUp, int willSpinDown)
    {
        public DateTime Time { get; } = time;
        public int WorkItems { get; } = workItems;
        public int Machines { get; } = machines;
        public int SpinningUp { get; } = spinningUp;
        public int WillSpinDown { get; } = willSpinDown;
    }
}
