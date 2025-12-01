using Aer.QdrantClient.Http.Abstractions;

namespace Vigilante.Extensions;

/// <summary>
/// Extension methods for IQdrantClientFactory to simplify client creation.
/// </summary>
public static class QdrantClientFactoryExtensions
{
    /// <summary>
    /// Creates a Qdrant HTTP client using host, port and optional API key.
    /// </summary>
    /// <param name="factory">The client factory.</param>
    /// <param name="host">The Qdrant host address.</param>
    /// <param name="port">The Qdrant port.</param>
    /// <param name="apiKey">Optional API key for authentication.</param>
    /// <param name="useHttps">Whether to use HTTPS. Default is false.</param>
    /// <returns>A configured Qdrant HTTP client.</returns>
    public static IQdrantHttpClient CreateClient(
        this IQdrantClientFactory factory,
        string host,
        int port,
        string? apiKey = null,
        bool useHttps = false)
    {
        var scheme = useHttps ? "https" : "http";
        var uri = new Uri($"{scheme}://{host}:{port}");
        return factory.CreateClient(uri, apiKey);
    }
}

