namespace Vigilante.Configuration;

public class QdrantNodeConfig
{
    public string Host { get; set; } = "localhost";
    
    public int Port { get; set; }
    
    public string? Namespace { get; set; }
    
    public string? PodName { get; set; }
}
