namespace Microsoft.Helix.QueueScale;

/// <summary>
/// Calculates the needed capacity for a queue based on current load and settings.
/// </summary>
public interface IQueueScaler
{
    /// <summary>
    /// Returns the desired machine capacity based on queue metrics and configuration.
    /// </summary>
    /// <param name="provider">A provider that returns all required queue information.</param>
    Task<int> GetQueueNeededCapacityAsync(IQueueInformationProvider provider, CancellationToken ct);
}
