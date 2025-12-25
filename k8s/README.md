# Vigilante Kubernetes Deployment

This directory contains configurations for deploying Vigilante in Kubernetes clusters.

## 📁 Structure

```
k8s/
├── deployment.yaml        # Main deployment configuration
├── service.yaml          # Service configuration
├── ingress.yaml         # Ingress template (optional, used only with CLUSTER_DOMAIN)
├── rbac.yaml            # ServiceAccount and K8s API access permissions
├── service-monitor.yaml # Prometheus ServiceMonitor
├── README.md            # This file
├── dev/
│   └── configmap.yaml   # Configuration for development environment
└── prod/
    └── configmap.yaml   # Configuration for production environment
```

## 🚀 Quick Start

### 1. Configure deployment script (optional)
Before deploying, you can customize the deployment by editing `deploy.sh`:

```bash
# Edit these variables at the top of deploy.sh
export OWNER_LABEL_NAME="owner"                     # Custom label name for resource tracking
export OWNER_LABEL_VALUE="YOUR_NAME_HERE"           # Your team/owner name
export CLUSTER_DOMAIN="your-cluster-domain.com"     # Your cluster domain for Ingress
```

### 2. Switch kubectl context
```bash
kubectl config use-context <context-name>
```

### 3. Apply RBAC permissions (first time or after updates)
Vigilante requires specific Kubernetes permissions to manage pods and StatefulSets:

```bash
kubectl apply -f rbac.yaml -n qdrant
```

**Required permissions:**
- `pods`: `list`, `get`, `watch`, `delete` - for pod management and monitoring
- `pods/exec`: `create`, `get`, `watch` - for executing commands in pods
- `pods/log`: `get`, `list` - for reading pod logs
- `events` (CoreV1): `list`, `get`, `watch` - for fetching Kubernetes warning events
- `statefulsets`: `get`, `list`, `patch`, `update` - for StatefulSet operations (rollout, scale)

> **Note:** The `events` permission is required for displaying Kubernetes warning events on the dashboard when the cluster is degraded. If you have access to grant `events.k8s.io` API permissions, you can add them for compatibility with newer Kubernetes versions.

### 4. Deploy to your environment
The deploy.sh script performs a complete redeployment of the application:
- Removes existing deployment
- Recreates all resources from scratch
- Ensures clean state for each deployment

Simply run:
```bash
cd k8s/dev  # or k8s/prod
../../deploy.sh
```

The script will:
- 🏷️  Apply custom owner labels (if configured)
- 🌐 Create Ingress (if CLUSTER_DOMAIN is set)
- 📦 Deploy with environment-specific settings
- ⏳ Wait for pods to be ready
- 🔗 Display access URLs

### 4. Access the application
Depending on your deployment configuration, Vigilante will be accessible:
- **Via Ingress** (if CLUSTER_DOMAIN is set): `http://vigilante-<namespace>.<context>.<cluster-domain>`
- **Via Pod IP** (if CLUSTER_DOMAIN is not set): `http://<pod-ip>:8080`

The script automatically displays the appropriate URL after deployment.

## 🏷️ Kubernetes Labels

All deployed resources include standard Kubernetes labels for better tracking and monitoring:

### Standard Labels
```yaml
app: vigilante
app.kubernetes.io/name: vigilante
app.kubernetes.io/instance: vigilante
app.kubernetes.io/component: monitoring
```

### Custom Owner Label
You can add a custom label for resource ownership tracking by configuring `deploy.sh`:

```bash
export OWNER_LABEL_NAME="team"              # Your label name
export OWNER_LABEL_VALUE="platform-team"    # Your team/owner
```

This label will be added to:
- Deployment metadata
- Pod template metadata

**Use case**: Track resources by team/owner in Prometheus metrics via `kube_pod_labels`

### Prometheus Metrics
After deployment, your pods will be visible in Prometheus with labels:
```promql
kube_pod_labels{
  label_app="vigilante",
  label_app_kubernetes_io_name="vigilante",
  label_app_kubernetes_io_instance="vigilante",
  label_app_kubernetes_io_component="monitoring",
  label_team="platform_team"  # Your custom label (if configured)
}
```

**Note**: Dots and slashes in label names are converted to underscores in Prometheus.

## 🎯 How It Works

### Deployment Process
The `deploy.sh` script performs these steps:
1. **Load configuration**: Reads OWNER_LABEL_NAME, OWNER_LABEL_VALUE, and CLUSTER_DOMAIN from script variables
2. **Clean up**: Removes existing deployment to ensure clean state
3. **Configure**: Prepares environment-specific settings and replaces label placeholders
4. **Deploy**: Creates new resources (deployment, service, ingress if needed)
5. **Wait**: Ensures new pods are ready
6. **Access**: Provides appropriate URL based on access method

### Label Configuration
The deployment.yaml contains a placeholder for the owner label:
```yaml
owner: "OWNER_PLACEHOLDER"
```

During deployment:
- If `OWNER_LABEL_NAME` and `OWNER_LABEL_VALUE` are set → placeholder is replaced with your values
- If not set → placeholder is removed from the manifest
- This allows committing the deployment.yaml to Git with a generic placeholder

### Unified System
- **Single script** (`deploy.sh`) for all environments
- **Clean deployment** - removes existing resources before creating new ones
- **Automatic environment detection** based on current folder (dev/prod)
- **Context-aware** - uses current kubectl context
- **Smart configuration** - automatically adapts for prod (more resources, replicas)
- **Flexible access** - supports both direct Pod IP and Ingress access methods
- **Customizable labels** - add custom owner/team labels for resource tracking
- **Git-friendly** - uses placeholders to avoid committing sensitive values

### Deployment script determines:
- Environment by folder: `k8s/dev` → Development, `k8s/prod` → Production
- Current kubectl context and namespace
- Applies corresponding ConfigMap from current folder
- Configures deployment parameters for the environment

### Application Access
Vigilante is accessible via **direct pod IP** without additional operations:
- No port-forward required
- No hosts file editing needed
- Simple HTTP URL for browser access

## 📋 Requirements

### Kubernetes Cluster
- Kubernetes 1.20+
- kubectl configured for target cluster

### Access Permissions
User must have permissions in target namespace to:
- Create/update: ConfigMap, Deployment, Service, ServiceAccount
- Create RBAC: Role, RoleBinding

### Docker Image
- Image `aercis/vigilante:latest` must be available in registry
- Recommended to use GitHub Actions for automated builds

## 🔧 Troubleshooting

### Pod not starting
```bash
# Check pod status
kubectl get pods -l app=vigilante

# View logs
kubectl logs -l app=vigilante --tail=50

# Check events
kubectl describe deployment vigilante
```

### No access to URL
```bash
# Get current pod IP
kubectl get pods -l app=vigilante -o wide

# Test accessibility via port-forward
kubectl port-forward svc/vigilante-service 8080:80
# Then open http://localhost:8080
```

### Vigilante cannot find Qdrant pods
```bash
# Check Qdrant pods exist
kubectl get pods -n qdrant -l app=qdrant

# Verify ServiceAccount permissions
kubectl auth can-i list pods --as=system:serviceaccount:qdrant:vigilante-sa -n qdrant
```

## 📊 Monitoring

### Health Check
```bash
curl http://POD_IP:8080/health
```

### Cluster Status API
```bash
curl http://POD_IP:8080/api/cluster/status
```

### Web Dashboard
Open `http://POD_IP:8080` in browser for web monitoring interface.

## 🔄 Updates

### Force full redeployment:
If you need to completely recreate the deployment (e.g., for major changes):
```bash
# Delete existing deployment
kubectl delete deployment vigilante

# Then redeploy
cd k8s/dev  # or k8s/prod
../../deploy.sh
```

### After configuration changes:
```bash
# Update ConfigMap only without recreating pods
kubectl apply -f k8s/dev/configmap.yaml  # or prod

# Restart pods to apply new configuration  
kubectl rollout restart deployment/vigilante
```

**Note**: This will cause temporary downtime as all pods are recreated.

## 🏗️ Architecture

```
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   Vigilante     │───▶│  Kubernetes API  │───▶│   Qdrant Pods   │
│     Pod         │    │                  │    │                 │
│                 │    │   Auto-discover  │    │ qdrant1-0       │
│ - Monitor       │    │   Pod IPs        │    │ qdrant1-1       │
│ - Health Check  │    │                  │    │ qdrant1-2       │
│ - Web UI        │    │                  │    │                 │
└─────────────────┘    └──────────────────┘    └─────────────────┘
```

### Features:
- **Auto-discovery**: Vigilante automatically finds Qdrant pods via K8s API
- **ClusterIP**: Secure access without exposed ports on nodes
- **Direct IP**: Pod IP access for simple integration
- **RBAC**: Minimal permissions for pod read-only access

