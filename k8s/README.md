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

### 1. Switch kubectl context
```bash
kubectl config use-context <context-name>
```

### 2. Choose access method and deploy
The deploy.sh script performs a complete redeployment of the application:
- Removes existing deployment
- Recreates all resources from scratch
- Ensures clean state for each deployment

You have two options to access Vigilante after deployment:

#### Option 1: Direct Pod IP access (default)
Just run the deployment script without any additional parameters:
```bash
cd k8s/dev  # or k8s/prod
../../deploy.sh
```
The script will output Pod IP addresses that you can use to access Vigilante directly.

#### Option 2: Ingress access
If you want to access Vigilante through Ingress, provide your cluster domain:
```bash
cd k8s/dev  # or k8s/prod
CLUSTER_DOMAIN=your-cluster-domain.com ../../deploy.sh
```
This will create an Ingress resource and make Vigilante available at:
`http://vigilante-<namespace>.<context>.<your-cluster-domain>`

Example:
```bash
CLUSTER_DOMAIN=k8s.company.com ./deploy.sh
# Will be available at: http://vigilante-qdrant.dev1.k8s.company.com
#                                    ^      ^    ^
#                                namespace ctx  domain
```

### 3. Access the application
Depending on your deployment method, Vigilante will be accessible either:
- Via Pod IP: `http://<pod-ip>:8080` (when deployed without CLUSTER_DOMAIN)
- Via Ingress: `http://vigilante-<namespace>.<context>.<cluster-domain>` (when deployed with CLUSTER_DOMAIN)

## 🎯 How It Works

### Deployment Process
The `deploy.sh` script performs these steps:
1. **Clean up**: Removes existing deployment to ensure clean state
2. **Configure**: Prepares environment-specific settings
3. **Deploy**: Creates new resources (deployment, service, etc.)
4. **Wait**: Ensures new pods are ready
5. **Access**: Provides appropriate URL based on access method

### Unified System
- **Single script** (`deploy.sh`) for all environments
- **Clean deployment** - removes existing resources before creating new ones
- **Automatic environment detection** based on current folder (dev/prod)
- **Context-aware** - uses current kubectl context
- **Smart configuration** - automatically adapts for prod (more resources, replicas)
- **Flexible access** - supports both direct Pod IP and Ingress access methods
- **Auto domain detection** - can detect cluster domain from existing ingresses

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
./get-url.sh

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
kubectl delete deployment vigilante -n qdrant

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
