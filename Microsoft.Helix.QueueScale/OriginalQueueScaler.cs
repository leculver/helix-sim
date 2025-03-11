namespace Microsoft.Helix.QueueScale;

/// <summary>
/// Implementation of the IQueueScaler interface that encapsulates the scaling algorithm.
/// </summary>
public class OriginalQueueScaler : IQueueScaler
{
    public async Task<int> GetQueueNeededCapacityAsync(IQueueInformationProvider provider, CancellationToken ct)
    {
        // Retrieve the work item duration (in minutes) and SLA, then compute the number of work items a machine can process.
        Task<double> workItemDurationTask = provider.EstimateWorkItemDurationAsync(ct);
        QueueSettings queueSettings = await provider.GetQueueSettingsAsync(ct);
        QueueMachines queueMachines = await provider.GetQueueMachinesAsync(ct);
        TimeSpan sla = queueSettings.Sla;

        long activeMessages = await provider.GetActiveMessagesAsync(ct);
        TimeSpan machineCreationTime = await provider.GetMachineCreationTimeAsync(ct);
        int currentCapacity = queueMachines.Current + queueMachines.SpinningUp;

        // Calculate the raw needed capacity using the original algorithm.
        int workItemsPerMachine = (int)Math.Ceiling(sla.TotalMinutes / await workItemDurationTask);
        int neededCapacity = CalculateNeededCapacity(workItemsPerMachine, activeMessages, machineCreationTime, sla, currentCapacity);

        // Ensure the desired capacity does not exceed the maximum allowed capacity.
        neededCapacity = Math.Min(neededCapacity, queueSettings.MaxCapacity);
        neededCapacity = Math.Max(neededCapacity, queueSettings.MinCapacity);
        return neededCapacity;
    }

    /// <summary>
    /// Computes the needed capacity given the current queue and machine metrics.
    /// </summary>
    private static int CalculateNeededCapacity(
        int machineProcessingSpeed,
        long activeMessages,
        TimeSpan machineCreationTime,
        TimeSpan sla,
        int currentCapacity)
    {
        // Scale down SLA to 75%, as the original code does:
        sla = TimeSpan.FromTicks((long)(sla.Ticks * 0.75));

        int totalProcessingSpeed = machineProcessingSpeed * currentCapacity;
        int neededMachines = currentCapacity;

        // If current capacity is insufficient to process active messages, scale up.
        if (totalProcessingSpeed < activeMessages)
        {
            double toBeCreatedMachinesSpeed = 1;
            if (machineCreationTime < sla)
            {
                // Estimate how many work items a new machine can process in the remaining SLA window.
                toBeCreatedMachinesSpeed = Math.Ceiling((sla - machineCreationTime).TotalMinutes / sla.TotalMinutes * machineProcessingSpeed);
            }
            neededMachines = currentCapacity + (int)Math.Ceiling((activeMessages - totalProcessingSpeed) / toBeCreatedMachinesSpeed);
        }
        // If there are fewer work items than machines, consider scaling down.
        else if (activeMessages < currentCapacity)
        {
            neededMachines = (int)activeMessages;
        }

        return neededMachines;
    }
}
