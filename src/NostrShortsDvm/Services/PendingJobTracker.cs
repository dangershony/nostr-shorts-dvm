using NostrShortsDvm.Models;

namespace NostrShortsDvm.Services;

/// <summary>
/// Tracks video jobs that are waiting for user confirmation (summary approval).
/// </summary>
public class PendingJobTracker
{
    private readonly Dictionary<string, PendingJob> _pendingJobs = new();
    private readonly object _lock = new();

    /// <summary>
    /// Store a job that's waiting for user confirmation.
    /// Key is the sender's pubkey (only one pending job per user at a time).
    /// </summary>
    public void SetPending(string senderPubKeyHex, VideoJob job, string proposedSummary)
    {
        lock (_lock)
        {
            _pendingJobs[senderPubKeyHex] = new PendingJob
            {
                Job = job,
                ProposedSummary = proposedSummary,
                CreatedAt = DateTimeOffset.UtcNow
            };
        }
    }

    /// <summary>
    /// Get and remove the pending job for a user. Returns null if none exists.
    /// </summary>
    public PendingJob? TakeJob(string senderPubKeyHex)
    {
        lock (_lock)
        {
            if (_pendingJobs.TryGetValue(senderPubKeyHex, out var pending))
            {
                _pendingJobs.Remove(senderPubKeyHex);

                // Expire after 10 minutes
                if (DateTimeOffset.UtcNow - pending.CreatedAt > TimeSpan.FromMinutes(10))
                    return null;

                return pending;
            }
            return null;
        }
    }

    /// <summary>
    /// Check if a user has a pending job.
    /// </summary>
    public bool HasPending(string senderPubKeyHex)
    {
        lock (_lock)
        {
            return _pendingJobs.ContainsKey(senderPubKeyHex);
        }
    }
}

public class PendingJob
{
    public VideoJob Job { get; set; } = null!;
    public string ProposedSummary { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
