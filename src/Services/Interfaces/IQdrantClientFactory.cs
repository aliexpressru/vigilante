using Aer.QdrantClient.Http.Abstractions;

namespace Vigilante.Services.Interfaces;

public interface IQdrantClientFactory
{
    IQdrantHttpClient CreateClient(string host, int port, string? apiKey = null);
}
