# 🛡️ Vigilante - Qdrant Cluster Guardian

Monitoring and management service for Qdrant vector database clusters with real-time health monitoring, automatic recovery, and shard replication management.

## Features

- 🔍 Cluster health monitoring
- 🔄 Automatic recovery from cluster issues
- 📊 Collection metrics and shard management
- 📸 **S3-compatible snapshot storage** (AWS S3, MinIO, etc.)
- 💾 Local snapshot management
- 📈 Prometheus metrics export
- 🌐 REST API and web dashboard
- ☸️ Kubernetes integration (pod management, StatefulSet operations)

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

### Basic Configuration

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

### S3 Snapshot Storage (Optional)

Vigilante supports S3-compatible storage for snapshots.

**Configuration:**
- **Secret data** (EndpointUrl, AccessKey, SecretKey): Kubernetes Secret (recommended) or appsettings.json
- **Other settings** (BucketName, Region, UsePathStyle): appsettings.json only

**Create Kubernetes Secret** (recommended for production):
```bash
kubectl create secret generic qdrant-s3-credentials \
  --from-literal=endpoint-url='https://s3.ae-rus.net' \
  --from-literal=access-key='your-access-key' \
  --from-literal=secret-key='your-secret-key' \
  -n qdrant
```

**Configure appsettings.json:**
```json
{
  "Qdrant": {
    "S3": {
      "BucketName": "snapshots",
      "Region": "us-east-1",
      "UsePathStyle": true
    }
  }
}
```

**For local development** (without Kubernetes), you can specify all settings in appsettings:
```json
{
  "Qdrant": {
    "S3": {
      "EndpointUrl": "https://s3.amazonaws.com",
      "AccessKey": "your-access-key",
      "SecretKey": "your-secret-key",
      "BucketName": "snapshots",
      "Region": "us-east-1",
      "UsePathStyle": true
    }
  }
}
```


## API

### Cluster Operations
- `GET /api/v1/cluster/status` - Cluster health and status
- `GET /api/v1/collections/info` - Collection metrics
- `POST /api/v1/cluster/replicate-shards` - Shard replication

### Snapshot Management
- `GET /api/v1/snapshots/info` - List snapshots (S3 → Local → API priority)
- `POST /api/v1/snapshots/{collectionName}` - Create snapshot
- `POST /api/v1/snapshots/download` - Download snapshot
- `POST /api/v1/snapshots/delete-from-disk` - Delete snapshot

### Kubernetes Operations
- `POST /api/v1/kubernetes/delete-pod` - Delete pod (triggers restart)
- `POST /api/v1/kubernetes/manage-statefulset` - Rollout/Scale StatefulSet

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

