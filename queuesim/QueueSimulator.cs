using System.Diagnostics;
using Microsoft.Helix.QueueScale;

namespace QueueSim;

class SimulationStep(DateTime time, List<WorkItem> completed)
{
    public DateTime Time { get; set; } = time;
    public TimeSpan Duration { get; set; }
    public int Queued { get; set; }
    public int QueueDepth { get; set; }
    public int Machines { get; set; }
    public int SpinningUp { get; set; }
    public List<WorkItem> Completed { get; set; } = completed;
    public double TotalMachineMinutes { get; set; }
    public double WastedMachineMinutes { get; set; }
    public int SpinUps { get; set; }
    public int SpinDowns { get; set; }
}

class WorkItem
{
    public DateTime Queued { get; set; }
    public DateTime Started { get; set; }
    public DateTime Finished { get; set; }
    public double Duration { get; set; }
}

class QueueSimulator
{
    private TimeSpan MinTimestamp { get; } = TimeSpan.FromSeconds(15);
    private TimeSpan WorkItemCleanup { get; } = TimeSpan.FromMinutes(4);
    private TimeSpan MachineRecheckTime { get; } = TimeSpan.FromMinutes(3);
    private TimeSpan ReportInterval { get; } = TimeSpan.FromMinutes(5);

    public IEnumerable<SimulationStep> Simulate(IQueueScaler scaler, QueueHistory[] history)
    {
        bool windows = history.Any(h => h.QueueName.Contains("windows"));
        TimeSpan spinupTime = windows ? TimeSpan.FromMinutes(18) : TimeSpan.FromMinutes(10);
        Queue<WorkItem> work = [];
        List<WorkItem?> machines = [];
        List<DateTime> spinup = [];
        Queue<QueueHistory> workHistory = new(history);
        int spindown = 0;

        SimQueueInfo infoProvider = new(work, machines, spinup, spinupTime);
        DateTime time = history.First().Timestamp;
        infoProvider.CurrentTime = time;
        DateTime nextWorkerCheck = time.Add(MachineRecheckTime);
        DateTime nextReport = time.Add(ReportInterval);

        SimulationStep report = new(time, []);

        while (workHistory.Count != 0 || work.Count != 0 || machines.Any(r => r != null))
        {
            infoProvider.CurrentTime = time;

            // Push history into the work queue
            int queued = 0;
            while (workHistory.TryPeek(out QueueHistory? item) && item.Timestamp <= time)
            {
                item = workHistory.Dequeue();
                foreach (double duration in item.Durations)
                {
                    work.Enqueue(new WorkItem { Queued = item.Timestamp, Duration = duration });
                    queued++;
                }
            }

            // Did any machine finish spinning up?
            for (int i = 0; i < spinup.Count; i++)
            {
                if (spinup[i] <= time)
                {
                    machines.Add(null);
                    spinup.RemoveAt(i--);
                    report.SpinUps++;
                }
            }

            // update each machine
            double wastedMachineFractional = 0;
            int wastedMachines = 0;
            int totalMachinesBeforeSpindown = machines.Count;
            for (int i = 0; i < machines.Count; i++)
            {
                var machine = machines[i];
                if (machine is null)
                {
                    wastedMachines++;
                }
                else if (machine.Finished <= time)
                {
                    wastedMachineFractional += time.Subtract(machine.Finished).TotalMinutes;
                    infoProvider.AddCompleted(machine);
                    report.Completed.Add(machine);
                    machines[i] = null;
                }

                if (machines[i] == null)
                {
                    if (spindown > 0)
                    {
                        machines.RemoveAt(i--);
                        spindown--;
                        report.SpinDowns++;
                    }
                    else if (work.Count > 0)
                    {
                        WorkItem workItem = work.Dequeue();
                        workItem.Started = time;
                        workItem.Finished = time + TimeSpan.FromMinutes(workItem.Duration) + WorkItemCleanup;
                        machines[i] = workItem;
                    }
                }
            }

            // Did we need to check the worker count?
            if (time >= nextWorkerCheck)
            {
                nextWorkerCheck = time.Add(MachineRecheckTime);
                int newWorkerCount = scaler.GetQueueNeededCapacityAsync(infoProvider, CancellationToken.None).Result;
                if (newWorkerCount < machines.Count)
                {
                    spindown = machines.Count - newWorkerCount;
                }
                else if (newWorkerCount > machines.Count)
                {
                    int toAdd = newWorkerCount - machines.Count;
                    while (spinup.Count < toAdd)
                        spinup.Add(time + spinupTime);
                }
            }

            TimeSpan step = MachineRecheckTime;
            if (workHistory.TryPeek(out QueueHistory? next))
            {
                var diff = next.Timestamp - time;
                if (diff > TimeSpan.Zero && diff < step)
                    step = diff;
            }

            var minMachine = machines.Where(r => r != null).OrderBy(r => r!.Finished).FirstOrDefault();
            if (minMachine != null)
            {
                var diff = minMachine.Finished - time;
                if (diff > TimeSpan.Zero && diff < step)
                    step = diff;
            }

            if (step < MinTimestamp)
                step = MinTimestamp;

            time += step;

            report.QueueDepth = Math.Max(work.Count, report.QueueDepth);
            report.Queued += queued;
            report.WastedMachineMinutes += wastedMachineFractional + wastedMachines * step.TotalMinutes;
            report.TotalMachineMinutes += totalMachinesBeforeSpindown * step.TotalMinutes;
            if (report.Machines <= machines.Count)
            {
                report.Machines = machines.Count;
                report.SpinningUp = spinup.Count;
            }

            if (time >= nextReport)
            {

                report.Duration = time - report.Time;
                yield return report;

                nextReport = time.Add(ReportInterval);
                report = new(time, []);
            }
        }
    }
}
