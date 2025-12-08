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

    /// <summary>
    /// Creates a Qdrant HTTP client from a URL string.
    /// Simplifies the common pattern of parsing URL and creating client.
    /// </summary>
    /// <param name="factory">The client factory.</param>
    /// <param name="nodeUrl">The full node URL (e.g., "http://host:port").</param>
    /// <param name="apiKey">Optional API key for authentication.</param>
    /// <returns>A configured Qdrant HTTP client.</returns>
    public static IQdrantHttpClient CreateClientFromUrl(
        this IQdrantClientFactory factory,
        string nodeUrl,
        string? apiKey = null)
    {
        var uri = new Uri(nodeUrl);
        return factory.CreateClient(uri.Host, uri.Port, apiKey);
    }
}

