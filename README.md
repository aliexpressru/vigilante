# 🛡️ Vigilante - Qdrant Cluster Guardian

Monitoring and management service for Qdrant vector database clusters with real-time health monitoring, automatic recovery, and shard replication management.

## Features

- 🔍 **Cluster Health Monitoring** - Real-time health checks and status tracking
- 🔄 **Automatic Recovery** - Self-healing from cluster issues
- 📊 **Collection Metrics** - Storage usage, shard distribution, replication status
- 📸 **Flexible Snapshot Storage** - S3-compatible storage (AWS S3, MinIO) or Kubernetes volumes
- 🔀 **Storage Backend Switching** - Toggle between S3 and Kubernetes storage via ConfigMap
- 🔗 **Presigned URLs** - Secure, time-limited download links for S3 snapshots
- 📈 **Prometheus Metrics** - Built-in metrics export for monitoring
- 🌐 **Web Dashboard** - Modern UI for cluster management
- ☸️ **Kubernetes Native** - Pod management, StatefulSet operations, RBAC support
- 🔐 **Secure Credentials** - Kubernetes Secrets integration for S3 credentials

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
    "HttpTimeoutSeconds": 5,
    "EnableAutoRecovery": true,
    "ApiKey": "your-api-key",
    "Nodes": [
      { "Host": "localhost", "Port": 6333 }
    ],
    "S3": {
      "Enabled": false,
      "BucketName": "snapshots",
      "Region": "default"
    }
  }
}
```

**Key settings:**
- `MonitoringIntervalSeconds`: How often to check cluster health (default: 30)
- `EnableAutoRecovery`: Enable automatic recovery from cluster issues (default: true)
- `S3.Enabled`: Enable S3 storage for snapshots (default: true, set to false to use Kubernetes storage)

### S3 Snapshot Storage (Optional)

Vigilante supports S3-compatible storage for snapshots with flexible storage backend selection.

**Storage Priority:**
1. S3 storage (if `Enabled: true` and credentials configured)
2. Kubernetes storage (pod volumes)
3. Qdrant API (fallback)

**Configuration Parameters:**

| Parameter | Source | Description | Required |
|-----------|--------|-------------|----------|
| `Enabled` | ConfigMap | Enable/disable S3 storage | No (default: true) |
| `EndpointUrl` | Secret → appsettings | S3 endpoint URL | Yes (when Enabled=true) |
| `AccessKey` | Secret → appsettings | S3 access key | Yes (when Enabled=true) |
| `SecretKey` | Secret → appsettings | S3 secret key | Yes (when Enabled=true) |
| `BucketName` | ConfigMap only | S3 bucket name | Yes (when Enabled=true) |
| `Region` | ConfigMap only | S3 region | No (default: "default") |

**Priority:** Credentials from Environment Variables (Kubernetes Secret) override appsettings.json

#### Switching Between S3 and Kubernetes Storage

**To use Kubernetes storage** (default for dev environment):

Edit ConfigMap (`k8s/dev/configmap.yaml`):
```json
{
  "Qdrant": {
    "MonitoringIntervalSeconds": 120,
    "HttpTimeoutSeconds": 3,
    "EnableAutoRecovery": true,
    "ApiKey": "test",
    "S3": {
      "Enabled": false,
      "BucketName": "snapshots",
      "Region": "default"
    }
  }
}
```

**To use S3 storage** (default for production):

Edit ConfigMap (`k8s/prod/configmap.yaml`):
```json
{
  "Qdrant": {
    "MonitoringIntervalSeconds": 30,
    "HttpTimeoutSeconds": 5,
    "EnableAutoRecovery": true,
    "ApiKey": "your-api-key",
    "S3": {
      "Enabled": true,
      "BucketName": "snapshots",
      "Region": "us-east-1"
    }
  }
}
```

**Quick switch in Kubernetes:**
1. Edit ConfigMap: `kubectl edit configmap vigilante-config -n qdrant`
2. Find the `"S3"` section inside `"Qdrant"` and change `"Enabled": false` to `"Enabled": true` (or vice versa)
3. Restart deployment: `kubectl rollout restart deployment/vigilante -n qdrant`
4. Verify: `kubectl logs -n qdrant -l app=vigilante --tail=50 | grep -E "S3 storage is disabled|S3 storage is available"`

#### Setup S3 Credentials

**Kubernetes (Production - Recommended):**

Credentials are automatically loaded from environment variables (Kubernetes Secret):

```bash
# 1. Create secret with S3 credentials
kubectl create secret generic qdrant-s3-credentials \
  --from-literal=endpoint-url='https://s3.amazonaws.com' \
  --from-literal=access-key='your-access-key' \
  --from-literal=secret-key='your-secret-key' \
  -n qdrant

# 2. Secret is mounted as environment variables in deployment.yaml:
#    S3__EndpointUrl, S3__AccessKey, S3__SecretKey
#
# 3. Non-secret settings (BucketName, Region, Enabled) come from ConfigMap
```

**Priority for S3 settings:**
- **Credentials** (EndpointUrl, AccessKey, SecretKey): Environment variables (Secret) → appsettings.json
- **Configuration** (BucketName, Region, Enabled): ConfigMap only

**For local development** (without Kubernetes), you can specify all settings in appsettings.json:
```json
{
  "Qdrant": {
    "S3": {
      "Enabled": true,
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
- `GET /api/v1/snapshots/info` - List snapshots (S3 [if Enabled] → Kubernetes → Qdrant API)
- `POST /api/v1/snapshots/{collectionName}` - Create snapshot
- `POST /api/v1/snapshots/get-download-url` - Generate presigned S3 download URL
- `POST /api/v1/snapshots/recover` - Recover collection from snapshot
- `POST /api/v1/snapshots/download` - Download snapshot
- `DELETE /api/v1/snapshots/delete` - Delete snapshot

### Kubernetes Operations
- `POST /api/v1/kubernetes/delete-pod` - Delete pod (triggers restart)
- `POST /api/v1/kubernetes/manage-statefulset` - Rollout/Scale StatefulSet

Swagger UI: http://localhost:8080/swagger

## Troubleshooting

### Verify S3 Storage Status

```bash
# Check which storage backend is being used
kubectl logs -n qdrant -l app=vigilante --tail=50 | grep -E "S3 storage|Fetching snapshots"

# Expected outputs:
# - S3 enabled:  "S3 storage is available, fetching snapshots from S3"
# - S3 disabled: "S3 storage is disabled via configuration (Enabled=false)"
```

### S3 Not Working (When Enabled=true)

```bash
# 1. Verify Enabled flag in ConfigMap
kubectl get configmap vigilante-config -n qdrant -o jsonpath='{.data.appsettings\.json}' | jq '.Qdrant.S3.Enabled'
# Should return: true

# 2. Check S3 credentials secret exists
kubectl get secret qdrant-s3-credentials -n qdrant
kubectl get secret qdrant-s3-credentials -n qdrant -o jsonpath='{.data}' | jq 'keys'
# Should show: ["access-key", "endpoint-url", "secret-key"]

# 3. Verify environment variables are mounted in pod
kubectl exec -n qdrant -l app=vigilante -- env | grep S3__
# Should show: S3__EndpointUrl, S3__AccessKey, S3__SecretKey

# 4. Check logs for S3 configuration errors
kubectl logs -n qdrant -l app=vigilante --tail=100 | grep -i s3
```

### Switching Storage Backend

```bash
# Switch to Kubernetes storage (disable S3)
kubectl patch configmap vigilante-config -n qdrant --type merge -p '
{
  "data": {
    "appsettings.json": "{\"Qdrant\":{\"MonitoringIntervalSeconds\":120,\"HttpTimeoutSeconds\":3,\"EnableAutoRecovery\":true,\"ApiKey\":\"test\",\"S3\":{\"Enabled\":false,\"BucketName\":\"snapshots\",\"Region\":\"default\"}}}"
  }
}'
kubectl rollout restart deployment/vigilante -n qdrant

# Switch to S3 storage (enable S3)  
kubectl patch configmap vigilante-config -n qdrant --type merge -p '
{
  "data": {
    "appsettings.json": "{\"Qdrant\":{\"MonitoringIntervalSeconds\":30,\"HttpTimeoutSeconds\":5,\"EnableAutoRecovery\":true,\"ApiKey\":\"test\",\"S3\":{\"Enabled\":true,\"BucketName\":\"snapshots\",\"Region\":\"default\"}}}"
  }
}'
kubectl rollout restart deployment/vigilante -n qdrant
```

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

