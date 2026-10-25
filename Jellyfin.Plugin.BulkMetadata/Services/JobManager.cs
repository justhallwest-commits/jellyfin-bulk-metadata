using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BulkMetadata.Services;

/// <summary>Progress of a background bulk job.</summary>
public sealed class JobStatus
{
    /// <summary>Gets or sets the job id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets a description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the state: Running, Completed, Failed, Cancelled.</summary>
    public string State { get; set; } = "Running";

    /// <summary>Gets or sets the number of items to process.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets the number processed.</summary>
    public int Processed { get; set; }

    /// <summary>Gets or sets the number changed.</summary>
    public int Changed { get; set; }

    /// <summary>Gets or sets per-item errors (capped).</summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>Gets or sets the number of errors.</summary>
    public int ErrorCount { get; set; }

    /// <summary>Gets or sets the history entry id produced by this job.</summary>
    public string? HistoryId { get; set; }

    /// <summary>Gets or sets the start time.</summary>
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets the finish time.</summary>
    public DateTime? FinishedUtc { get; set; }

    /// <summary>Records an error.</summary>
    /// <param name="message">Message.</param>
    public void AddError(string message)
    {
        lock (Errors)
        {
            ErrorCount++;
            if (Errors.Count < 200)
            {
                Errors.Add(message);
            }
        }
    }
}

/// <summary>
/// Runs bulk jobs in the background, one at a time, and keeps their status for polling.
/// </summary>
public sealed class JobManager
{
    private readonly ConcurrentDictionary<string, (JobStatus Status, CancellationTokenSource Cts)> _jobs = new();
    private readonly ILogger<JobManager> _logger;
    private readonly object _startLock = new();

    /// <summary>Initializes a new instance of the <see cref="JobManager"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public JobManager(ILogger<JobManager> logger)
    {
        _logger = logger;
    }

    /// <summary>Gets the running job, if any.</summary>
    public JobStatus? Current => _jobs.Values.Select(j => j.Status).FirstOrDefault(s => s.State == "Running");

    /// <summary>Starts a job.</summary>
    /// <param name="description">Description.</param>
    /// <param name="total">Item count.</param>
    /// <param name="work">Work delegate.</param>
    /// <returns>Status.</returns>
    /// <exception cref="InvalidOperationException">Another job is running.</exception>
    public JobStatus Start(string description, int total, Func<JobStatus, CancellationToken, Task> work)
    {
        lock (_startLock)
        {
            if (Current is { } running)
            {
                throw new InvalidOperationException($"Another bulk job is still running ({running.Description}: {running.Processed}/{running.Total}). Wait for it to finish or cancel it.");
            }

            Prune();
            var status = new JobStatus { Id = Guid.NewGuid().ToString("N"), Description = description, Total = total };
            var cts = new CancellationTokenSource();
            _jobs[status.Id] = (status, cts);

            _ = Task.Run(async () =>
            {
                try
                {
                    await work(status, cts.Token).ConfigureAwait(false);
                    status.State = cts.IsCancellationRequested ? "Cancelled" : "Completed";
                }
                catch (OperationCanceledException)
                {
                    status.State = "Cancelled";
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Bulk metadata job {Description} failed", description);
                    status.AddError(ex.Message);
                    status.State = "Failed";
                }
                finally
                {
                    status.FinishedUtc = DateTime.UtcNow;
                }
            });

            return status;
        }
    }

    /// <summary>Gets a job.</summary>
    /// <param name="id">Id.</param>
    /// <returns>Status or null.</returns>
    public JobStatus? Get(string id) => _jobs.TryGetValue(id, out var j) ? j.Status : null;

    /// <summary>Requests cancellation.</summary>
    /// <param name="id">Id.</param>
    /// <returns>Whether the job was found.</returns>
    public bool Cancel(string id)
    {
        if (_jobs.TryGetValue(id, out var j))
        {
            j.Cts.Cancel();
            return true;
        }

        return false;
    }

    private void Prune()
    {
        foreach (var kv in _jobs.Where(k => k.Value.Status.State != "Running" && k.Value.Status.FinishedUtc < DateTime.UtcNow.AddHours(-6)).ToList())
        {
            _jobs.TryRemove(kv.Key, out _);
        }
    }
}
