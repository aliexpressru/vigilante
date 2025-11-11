using Aer.QdrantClient.Http;
using System.Collections.Concurrent;
using Aer.QdrantClient.Http.Abstractions;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

/// <summary>
/// Factory for creating and caching Qdrant HTTP clients for dynamically discovered nodes.
/// Clients are cached per node (host:port:apiKey) to avoid recreation overhead.
/// </summary>
public class DefaultQdrantClientFactory : IQdrantClientFactory
{
    private readonly ConcurrentDictionary<string, IQdrantHttpClient> _clientCache = new();

    public IQdrantHttpClient CreateClient(string host, int port, string? apiKey = null)
    {
        var key = $"{host}:{port}:{apiKey ?? "no-key"}";
        
        return _clientCache.GetOrAdd(key, string.IsNullOrEmpty(apiKey)
            ? new QdrantHttpClient(host, port)
            : new QdrantHttpClient(host, port, apiKey: apiKey));
    }

    public IQdrantHttpClient CreateClientWithInfiniteTimeout(string host, int port, string? apiKey = null)
    {
        // Use separate cache key with :infinite suffix for clients with infinite timeout
        var key = $"{host}:{port}:{apiKey ?? "no-key"}:infinite";
        
        return _clientCache.GetOrAdd(key, string.IsNullOrEmpty(apiKey)
            ? new QdrantHttpClient(host, port, httpClientTimeout: Timeout.InfiniteTimeSpan)
            : new QdrantHttpClient(host, port, apiKey: apiKey, httpClientTimeout: Timeout.InfiniteTimeSpan));
    }
}
