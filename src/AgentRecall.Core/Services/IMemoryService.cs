namespace AgentRecall.Core.Services;

/// <summary>
/// The central abstraction for storing and recalling agent memories.
/// Phase 1 defines the contract only; no persistence is implemented yet.
/// </summary>
public interface IMemoryService
{
    /// <summary>
    /// Returns a short human-readable status describing the memory subsystem.
    /// </summary>
    string Status();
}
