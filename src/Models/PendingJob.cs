using Vigilante.Services.Interfaces;

namespace Vigilante.Models;

/// <summary>
/// A pending job (held by ClusterManager, advanced by monitor each tick). Abstraction-agnostic: holds IJob and cancellation.
/// </summary>
public sealed record PendingJob(
    IJob Job,
    CancellationTokenSource CancellationTokenSource
);
