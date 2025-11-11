using Aer.QdrantClient.Http.Abstractions;

namespace Vigilante.Services.Interfaces;

public interface IQdrantClientFactory
{
    IQdrantHttpClient CreateClient(string host, int port, string? apiKey = null);
    
    /// <summary>
    /// Creates a client with infinite timeout for long-running operations like uploading large snapshots
    /// </summary>
    IQdrantHttpClient CreateClientWithInfiniteTimeout(string host, int port, string? apiKey = null);
}
