# 🛡️ Vigilante - Qdrant Cluster Guardian

Monitoring and management service for Qdrant vector database clusters with real-time health monitoring, automatic recovery, and shard replication management.

## Features

- 🔍 Cluster health monitoring
- 🔄 Automatic recovery from cluster issues
- 📊 Collection metrics and shard management
- 📈 Prometheus metrics export
- 🌐 REST API and web dashboard

## Quick Start

### Docker

```bash
docker build -t vigilante:latest .

docker run -p 8080:8080 \
  -e Qdrant__Nodes__0__Host=qdrant-node-1 \
  -e Qdrant__Nodes__0__Port=6333 \
  -e Qdrant__ApiKey=your-api-key \
  vigilante:latest
```

### Kubernetes

See [k8s/README.md](k8s/README.md) for deployment instructions.

```bash
kubectl apply -f k8s/
```

## Configuration

```json
{
  "Qdrant": {
    "MonitoringIntervalSeconds": 30,
    "EnableAutoRecovery": true,
    "ApiKey": "your-api-key",
    "Nodes": [
      { "Host": "localhost", "Port": 6333 }
    ]
  }
}
```

## API

- `GET /api/v1/cluster/status` - Cluster health and status
- `GET /api/v1/cluster/collections-info` - Collection metrics
- `POST /api/v1/cluster/replicate-shards` - Shard replication

Swagger UI: http://localhost:8080/swagger

## Development

```bash
dotnet restore
dotnet build
dotnet run --project src/Aer.Vigilante.csproj
```

Dashboard: http://localhost:8080  
Metrics: http://localhost:8080/metrics

## License

See [LICENSE](LICENSE) file for details.

