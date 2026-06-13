namespace AgentRecall.Core.Services;

/// <summary>
/// A small status surface for the memory subsystem.
/// </summary>
public interface IMemoryService
{
    /// <summary>
    /// Returns a short human-readable status describing the memory subsystem.
    /// </summary>
    string Status();
}
