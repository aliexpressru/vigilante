# Vigilante

Web service and dashboard for monitoring and operating Qdrant clusters: health, collections, snapshots (S3-compatible storage, Kubernetes volumes, or Qdrant API), replication helpers, and optional Kubernetes pod/StatefulSet actions when the app runs in-cluster.

## Stack

| Component | Notes |
|-----------|--------|
| Runtime | ASP.NET Core, .NET 10 |
| UI | Static assets under `src/wwwroot` (Vanilla JS) |
| Qdrant | HTTP client `Aerx.QdrantClient.Http` |
| Kubernetes | `KubernetesClient` (optional; disabled outside cluster) |
| Observability | OpenTelemetry metrics, Prometheus scrape endpoint |
| API docs | Swagger / OpenAPI |

## Repository layout

| Path | Purpose |
|------|---------|
| `src/` | Application: `Controllers/`, `Services/`, `Models/`, `Configuration/`, `Validators/`, `wwwroot/` |
| `tests/` | Unit and integration tests (`Aer.Vigilante.Tests`) |
| `k8s/` | Kubernetes manifests, per-environment ConfigMaps (`dev/`, `stg/`, `prod/`), `deploy.sh` driver |
| `Dockerfile` | Multi-stage build, .NET 10 SDK/runtime |
| `docker-compose.yml` | Local run against registry image `aercis/vigilante` |

## Quick Start

### Option A: Full local stack via docker-compose (Vigilante + Qdrant cluster)

This starts:
- `qdrant-1`, `qdrant-2`, `qdrant-3`
- `vigilante`

Run:

```bash
docker compose pull
docker compose up -d
```

Open:
- Dashboard: `http://localhost:6360`
- Swagger: `http://localhost:6360/swagger`

Qdrant ports on host:
- `6343` -> `qdrant-1:6333`
- `6353` -> `qdrant-2:6333`
- `6363` -> `qdrant-3:6333`

Stop:

```bash
docker compose down
```

### Option B: Use Vigilante with an existing Qdrant cluster

If Qdrant is already running elsewhere, start only `vigilante` and point it to your nodes via `QDRANT_NODES`.

1. Edit `docker-compose.yml`:
   - set `QDRANT_NODES` to your endpoints, for example:
     - in same Docker network: `qdrant-1:6333;qdrant-2:6333`
     - on host machine from container: `host.docker.internal:6343;host.docker.internal:6353`
2. If S3 is needed, provide `S3__EndpointUrl`, `S3__AccessKey`, `S3__SecretKey` (via `.env` or literal env values).
3. Start only Vigilante:

```bash
docker compose up -d vigilante
```

4. Verify logs:

```bash
docker compose logs -f vigilante
```

## Configuration model

**Static configuration** (`appsettings.json`, or `Qdrant` section from Kubernetes ConfigMap `vigilante-config`):

- `Qdrant:HttpTimeoutSeconds`, `Qdrant:ApiKey`, `Qdrant:Nodes`, and optional `Qdrant:S3` secret fallback (endpoint, keys) when not provided via environment.

**Dynamic configuration** (in-cluster: ConfigMap `vigilante-dynamic-config` mounted at `/app/config/dynamic-config.json`; runtime updates via `GET`/`PUT /api/v1/config`):

- `MonitoringIntervalSeconds`, snapshot automation settings, and **non-secret** S3 fields: `S3.Enabled`, `S3.BucketName`, `S3.Region`.

**Qdrant node discovery** (order):

1. Kubernetes API (pods labeled for Qdrant), when a cluster client is available.
2. Environment variable `QDRANT_NODES` — semicolon-separated `host:port` list.
3. Configuration section `Qdrant:Nodes` (e.g. from appsettings).

**S3 credentials for the app** (used together with dynamic S3 flags):

- Environment variables `S3__EndpointUrl`, `S3__AccessKey`, `S3__SecretKey` (Kubernetes Secret in production; `.env` or compose `environment` locally).

## Local development (without Docker)

Requires .NET 10 SDK.

```bash
dotnet restore
dotnet build
dotnet run --project src/Aer.Vigilante.csproj
```

Default HTTP URL is defined in `src/Properties/launchSettings.json` (profile `http`, typically `http://localhost:5297`). Metrics: `/metrics`. Health: `/health`. Swagger: `/swagger`.

## Docker Compose (recommended for local runs)

You only need `docker-compose.yml` and optionally a `.env` file in the **same directory**. Cloning the repository is not required; you can copy `docker-compose.yml` from the default branch and run Compose from that folder.

```bash
docker compose pull
docker compose up -d
```

(Use `docker-compose` if your installation exposes the legacy CLI.)

**Image:** `aercis/vigilante:latest` (multi-arch `linux/amd64` and `linux/arm64` when built with CI). **Default platform** in the compose file is `linux/arm64` (Apple Silicon). On Intel or Linux amd64 hosts:

```bash
export VIGILANTE_PLATFORM=linux/amd64
docker compose pull
docker compose up -d
```

If the container runs under QEMU (`qemu:` in logs) or .NET crashes, ensure the pulled image matches your CPU architecture; verify the manifest:

```bash
docker buildx imagetools inspect aercis/vigilante:latest
```

**Published port:** host `6360` maps to the app (`ASPNETCORE_URLS=http://+:6360`). Dashboard and Swagger: `http://localhost:6360` and `http://localhost:6360/swagger`.

**Qdrant in the same Compose file:** the sample compose starts a 3-node cluster (`qdrant-1`, `qdrant-2`, `qdrant-3`) and sets `QDRANT_NODES="qdrant-1:6333;qdrant-2:6333;qdrant-3:6333"` for Vigilante. Service names are resolved by Docker DNS on the default network.

**S3 (optional):** the registry image does not ship credentials. Set `S3__EndpointUrl`, `S3__AccessKey`, and `S3__SecretKey` via a `.env` file next to `docker-compose.yml` (Compose substitutes `${S3__...}`), or replace those entries with quoted literals in `environment:` (do not commit secrets). Bucket name and region are configured through **dynamic config** (dashboard or `PUT /api/v1/config`), not only via `.env`.

**Build image locally** (e.g. when the registry manifest lacks your architecture):

```bash
DOCKER_BUILDKIT=0 docker build -t aercis/vigilante:latest .
docker compose up -d
```

## Container image and CI

- **Registry:** `docker.io/aercis/vigilante` (tags from Git tags `v*`, plus `latest` as configured in `.github/workflows/docker-build.yml`).
- **Multi-arch build** uses Docker Buildx with QEMU in GitHub Actions.
- **Manual publish:** see `publish-docker.sh` (requires `DOCKER_HUB_USERNAME`, optional `VERSION_TAG`).

## Kubernetes

Deployment is driven from **`deploy.sh`** in the repository root. Run it **from** `k8s/dev`, `k8s/stg`, or `k8s/prod` so the correct ConfigMaps and environment are applied:

```bash
cd k8s/dev   # or stg / prod
../../deploy.sh
```

The application listens on **port 8080** inside the cluster (`deployment.yaml`). RBAC, Service, optional Ingress, and monitoring manifests live under `k8s/`. Detailed steps, labels, and troubleshooting: **[k8s/README.md](k8s/README.md)**.

Do not rely on a single `kubectl apply -f k8s/` without following the layout and `deploy.sh` workflow expected by this project.

## HTTP API (overview)

Base path: `/api/v1/...`. OpenAPI/Swagger lists request and response schemas.

| Area | Methods and paths |
|------|-------------------|
| Cluster | `GET /cluster/status`, `POST /cluster/replicate-shards`, `POST /cluster/abort-shard-transfer`, `POST /cluster/drop-shards`, `POST /cluster/start-resharding`, `POST /cluster/remove-peer` |
| Collections | `GET /collections/info`, `DELETE /collections`, `POST /collections/alias`, `POST /collections/alias/rename`, `POST /collections/alias/delete`, `POST /collections/restore-replication-factor` |
| Snapshots | `GET /snapshots/info`, `POST /snapshots` (create), `DELETE /snapshots`, `POST /snapshots/download`, `POST /snapshots/recover`, `POST /snapshots/get-download-url` |
| Kubernetes | `POST /kubernetes/delete-pod`, `POST /kubernetes/manage-statefulset` |
| Config | `GET /config`, `PUT /config`, `GET /config/environment` |
| Jobs | `GET /jobs/status`, `POST /jobs/cancel` |
| Logs | `POST /logs/qdrant`, `POST /logs/vigilante` |

## Observability

- **Prometheus:** `GET /metrics` (OpenTelemetry Prometheus exporter).
- **Health:** `GET /health`.

## Troubleshooting (short)

- **S3:** Logs such as `S3 configuration is incomplete` mean missing endpoint, keys, or bucket in dynamic config when S3 is enabled. Confirm env vars and `PUT /api/v1/config` / dynamic ConfigMap.
- **Outside Kubernetes:** Warnings that the Kubernetes client is unavailable are expected; pod/StatefulSet APIs and volume-based snapshot paths are limited.
- **Dynamic config file missing in Docker:** Without a mounted `/app/config/dynamic-config.json`, defaults apply; runtime updates stay in memory until persisted (in-cluster, ConfigMap update is attempted when RBAC allows).

## License

See [LICENSE](LICENSE).
