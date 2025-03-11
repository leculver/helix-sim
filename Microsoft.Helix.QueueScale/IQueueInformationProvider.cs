namespace Microsoft.Helix.QueueScale;

public record QueueSettings(int MinCapacity, int MaxCapacity, double Multiplier, TimeSpan Sla);
public record QueueMachines(int Current, int SpinningUp);

/// <summary>
/// Provides all necessary queue and machine information asynchronously.
/// </summary>
public interface IQueueInformationProvider
{
    DateTime UtcNow { get; }

    /// <summary>
    /// Returns a metric of the total time (in minutes) it takes to process a work item.
    /// </summary>
    Task<double> EstimateWorkItemDurationAsync(CancellationToken ct);

    Task<double> GetWorkItemNthPercentileAsync(int percentile, CancellationToken ct);

    /// <summary>
    /// Gets the current number of active messages in the queue.
    /// </summary>
    Task<int> GetActiveMessagesAsync(CancellationToken ct);

    /// <summary>
    /// Gets the time it takes for a new machine to be created.
    /// </summary>
    Task<TimeSpan> GetMachineCreationTimeAsync(CancellationToken ct);

    Task<QueueSettings> GetQueueSettingsAsync(CancellationToken ct);
    Task<QueueMachines> GetQueueMachinesAsync(CancellationToken ct);
}
