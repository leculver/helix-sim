using QueueSim;
using Microsoft.Helix.QueueScale;


string path = "queued_work.tsv";
for (int i = 0; i < 10 && !File.Exists(path); i++)
    path = Path.Combine("..", path);

Dictionary<string, QueueHistory[]> history = QueueHistory.Load(path);
using (var output = File.CreateText("result.tsv"))
{
    output.WriteLine("AutoScaler\tQueue\t50th\t75th\t80th\t90th\t95th\t99th\tMachineHours\tWastedHours\tSpinupsPerDay\tEstimatedHoursPerDay");
    foreach (string queue in history.Keys)
    {
        Console.WriteLine($"---------- {queue} ----------");
        Console.WriteLine();
        Console.WriteLine("Original queue scaler:");
        RunSimulation(output, new OriginalQueueScaler(), queue, history[queue], verbose: false);

        Console.WriteLine("New queue scaler:");
        RunSimulation(output, new QueueScaler(), queue, history[queue], verbose: false);
    }
}

static void RunSimulation(TextWriter output, IQueueScaler scaler, string queueName, QueueHistory[] history, bool verbose)
{
    QueueSimulator simulator = new();

    int spinups = 0;
    int spindowns = 0;

    TimeSpan total = TimeSpan.Zero;
    TimeSpan wasted = TimeSpan.Zero;

    List<double> queueTimes = [];

    foreach (SimulationStep step in simulator.Simulate(scaler, history))
    {
        total = total.Add(TimeSpan.FromMinutes(step.TotalMachineMinutes));
        wasted = wasted.Add(TimeSpan.FromMinutes(step.WastedMachineMinutes));

        spinups += step.SpinUps;
        spindowns += step.SpinDowns;

        double duration = step.Completed.Count > 0 ? step.Completed.Average(r => r.Duration) : 0;

        if (verbose)
            Console.WriteLine($"{step.Time}: queued:{step.Queued} depth: {step.QueueDepth}, machines: {step.Machines}, spinning up: {step.SpinningUp}, completed: {step.Completed.Count}, completed duration: {duration:n2}");

        foreach (var completed in step.Completed)
            queueTimes.Add((completed.Started - completed.Queued).TotalMinutes);
    }

    queueTimes.Sort();

    List<double> percentiles = [];
    foreach (int percentile in (int[])[50, 75, 80, 90, 95, 99])
    {
        percentiles.Add(GetPercentile(queueTimes, percentile / 100.0));
        Console.WriteLine($"{percentile}th percentile: {percentiles.Last():n0} minutes");
    }

    bool windows = history.Any(h => h.QueueName.Contains("windows"));
    TimeSpan spinupTime = windows ? TimeSpan.FromMinutes(18) : TimeSpan.FromMinutes(10);
    double spinupTimeHours = spinupTime.TotalHours * 0.8; // not all of spinup time is paid
    double totalDays = (history.Max(h => h.Timestamp) - history.Min(h => h.Timestamp)).TotalDays;
    double machineHours = total.TotalHours / totalDays;
    Console.WriteLine($"Machine hours per day: {machineHours:n0} ({100 * wasted.TotalHours / total.TotalHours:n1}% wasted)");
    Console.WriteLine($"Spinups per day: {spinups / totalDays:n0}");
    Console.WriteLine($"Estimated hours per day (with spinups): {machineHours + spinups * spinupTimeHours:n0}");
    Console.WriteLine();

    output.Write($"{scaler.GetType().Name}\t");
    output.Write($"{queueName}\t");
    foreach (double percentile in percentiles)
        output.Write($"{percentile}\t");
    output.Write($"{machineHours}\t");
    output.Write($"{wasted.TotalHours / totalDays}\t");
    output.Write($"{spinups / totalDays}\t");
    output.Write($"{machineHours + spinups * spinupTimeHours}\t");
    output.WriteLine();
}

static double GetPercentile(List<double> values, double percentile)
{
    double index_parts = values.Count * percentile;
    int index = (int)index_parts;
    double fraction = index_parts - index;
    if (index == values.Count - 1)
        return values[index];

    return values[index] + fraction * (values[index + 1] - values[index]);
}
