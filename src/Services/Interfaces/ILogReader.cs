using Vigilante.Services.Models;

namespace Vigilante.Services.Interfaces;

public interface ILogReader
{
    Task<LogPage> GetQdrantPodLogsAsync(string podName, LogQuery query, CancellationToken cancellationToken);

    Task<LogPage> GetServiceLogsAsync(LogQuery query, CancellationToken cancellationToken);
}