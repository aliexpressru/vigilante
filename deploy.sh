#!/bin/bash

# Universal Vigilante Deployment Script
# Deploys to current kubectl context using configmap from current directory (dev or prod)

set -e

# Set custom label for resource ownership/team tracking
export OWNER_LABEL_NAME="owner"  # Label name (e.g., "owner", "team", "managed-by")
export OWNER_LABEL_VALUE="YOUR_NAME_HERE"  # Replace with actual owner/team name

# Set cluster domain for ingress access
export CLUSTER_DOMAIN="your-cluster-domain.com"  # Replace with actual cluster domain
# =======================================================

echo "🚀 Starting Vigilante deployment..."

# Get current kubernetes context and namespace
KUBE_CONTEXT=$(kubectl config current-context)
KUBE_NAMESPACE=$(kubectl config view --minify -o jsonpath='{..namespace}')

# If namespace is empty, set it to default
if [ -z "$KUBE_NAMESPACE" ]; then
    KUBE_NAMESPACE="default"
fi

echo "🧹 Cleaning up old deployment..."
kubectl delete deployment vigilante -n $KUBE_NAMESPACE --ignore-not-found=true

# Determine environment based on current directory
CURRENT_DIR=$(basename "$PWD")
if [ "$CURRENT_DIR" = "dev" ]; then
    ENV="Development"
    ENV_NAME="dev"
elif [ "$CURRENT_DIR" = "prod" ]; then
    ENV="Production" 
    ENV_NAME="prod"
else
    echo "❌ Please run this script from k8s/dev or k8s/prod directory"
    echo "💡 Current directory: $PWD"
    echo "💡 Expected to be in: .../k8s/dev or .../k8s/prod"
    exit 1
fi

echo "📦 Deploying to context: ${KUBE_CONTEXT}, namespace: ${KUBE_NAMESPACE}..."
if [ ! -z "$CLUSTER_DOMAIN" ]; then
    echo "📦 Using cluster domain: ${CLUSTER_DOMAIN}"
fi

# Get current kubectl context
CURRENT_CONTEXT=$(kubectl config current-context 2>/dev/null || echo "unknown")
CURRENT_NAMESPACE=$(kubectl config view --minify -o jsonpath='{.contexts[0].context.namespace}' 2>/dev/null || echo "default")

echo "🎯 Environment: $ENV ($ENV_NAME)"
echo "🎯 Context: $CURRENT_CONTEXT"
echo "🎯 Namespace: $CURRENT_NAMESPACE"
echo "📁 Config source: $PWD/configmap.yaml"
echo ""

# Validate files exist
if [ ! -f "configmap.yaml" ]; then
    echo "❌ configmap.yaml not found in current directory"
    echo "💡 Make sure you're in k8s/dev or k8s/prod directory"
    exit 1
fi

if [ ! -f "../deployment.yaml" ]; then
    echo "❌ ../deployment.yaml not found"
    echo "💡 Make sure k8s/deployment.yaml exists"
    exit 1
fi

if [ ! -f "../service-monitor.yaml" ]; then
    echo "❌ ../service-monitor.yaml not found"
    echo "💡 Make sure k8s/service-monitor.yaml exists"
    exit 1
fi

IMAGE_NAME="aercis/vigilante:latest"
echo "📝 Using image: $IMAGE_NAME"
echo "💡 Make sure the image is already built and pushed via GitHub Actions"
echo ""

# Create temporary deployment file with environment-specific settings
TEMP_DEPLOYMENT="/tmp/vigilante-deployment-${ENV_NAME}.yaml"
cp ../deployment.yaml "$TEMP_DEPLOYMENT"

# Update deployment with environment-specific settings
sed -i '' "s/value: \"Development\"/value: \"$ENV\"/" "$TEMP_DEPLOYMENT" || true

# Replace owner label placeholder with actual values if set
if [ ! -z "$OWNER_LABEL_NAME" ] && [ ! -z "$OWNER_LABEL_VALUE" ] && [ "$OWNER_LABEL_VALUE" != "YOUR_NAME_HERE" ]; then
    echo "🏷️  Setting owner label: $OWNER_LABEL_NAME=$OWNER_LABEL_VALUE"
    # Replace the placeholder label name and value (using | as delimiter to avoid conflicts with /)
    sed -i '' "s|owner: \"OWNER_PLACEHOLDER\"|$OWNER_LABEL_NAME: \"$OWNER_LABEL_VALUE\"|g" "$TEMP_DEPLOYMENT"
else
    # Remove the owner label placeholder if not set
    sed -i '' "/owner: \"OWNER_PLACEHOLDER\"/d" "$TEMP_DEPLOYMENT"
fi

# For production, update resources and add security context
if [ "$ENV_NAME" = "prod" ]; then
    # Update resources for production
    sed -i '' 's/memory: "128Mi"/memory: "256Mi"/' "$TEMP_DEPLOYMENT"
    sed -i '' 's/memory: "256Mi"/memory: "512Mi"/' "$TEMP_DEPLOYMENT"
    sed -i '' 's/cpu: "100m"/cpu: "250m"/' "$TEMP_DEPLOYMENT"
    sed -i '' 's/cpu: "250m"/cpu: "500m"/' "$TEMP_DEPLOYMENT"
    
    # Update health check timings for production
    sed -i '' 's/initialDelaySeconds: 15/initialDelaySeconds: 30/' "$TEMP_DEPLOYMENT"
fi

# Clean up any existing resources to avoid duplicates
echo "🧹 Cleaning up old resources..."
kubectl delete service vigilante vigilante-service --ignore-not-found=true

# If CLUSTER_DOMAIN is set, prepare and apply ingress
if [ ! -z "$CLUSTER_DOMAIN" ]; then
    echo "🔄 Creating ingress with domain ${CLUSTER_DOMAIN}..."
    kubectl delete ingress vigilante-ingress --ignore-not-found=true
    
    # Create temporary ingress file
    TEMP_INGRESS="/tmp/vigilante-ingress-${ENV_NAME}.yaml"
    cp ../ingress.yaml "$TEMP_INGRESS"
    
    # Process templates
    sed -i '' "s/\${KUBE_CONTEXT}/$KUBE_CONTEXT/g" "$TEMP_INGRESS"
    sed -i '' "s/\${KUBE_NAMESPACE}/$KUBE_NAMESPACE/g" "$TEMP_INGRESS"
    sed -i '' "s/\${CLUSTER_DOMAIN}/$CLUSTER_DOMAIN/g" "$TEMP_INGRESS"
fi

# Apply Kubernetes configurations
echo "☸️  Applying Kubernetes configurations..."
kubectl apply -f ../rbac.yaml
kubectl apply -f configmap.yaml
kubectl apply -f "$TEMP_DEPLOYMENT"
kubectl apply -f ../service.yaml
kubectl apply -f ../service-monitor.yaml

# Apply ingress if domain is set
if [ ! -z "$CLUSTER_DOMAIN" ]; then
    kubectl apply -f "$TEMP_INGRESS"
    rm -f "$TEMP_INGRESS"
fi

# Clean up temporary files
rm -f "$TEMP_DEPLOYMENT"

# Wait for deployment to be ready
echo "⏳ Waiting for deployment to be ready..."
kubectl wait --for=condition=available --timeout=300s deployment/vigilante

# Get pod IP if no domain is set
if [ -z "$CLUSTER_DOMAIN" ]; then
    echo ""
    echo "🌐 Access Vigilante directly via Pod IP:"
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    POD_IPS=($(kubectl get pods -l app=vigilante -o jsonpath='{.items[*].status.podIP}'))
    POD_PORT="8080"
    
    if [ ${#POD_IPS[@]} -eq 0 ]; then
        echo "⚠️  No pod IPs found yet. Pods may still be starting..."
    else
        for i in "${!POD_IPS[@]}"; do
            echo "🔗 Pod $((i+1)): http://${POD_IPS[$i]}:$POD_PORT"
            if [ $i -eq 0 ] && command -v pbcopy &> /dev/null; then
                echo "http://${POD_IPS[$i]}:$POD_PORT" | pbcopy
                echo "📋 First Pod URL copied to clipboard!"
            fi
        done
    fi
else
    echo ""
    echo "🌐 Access Vigilante via Ingress:"
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    echo "🔗 URL: http://vigilante-${KUBE_NAMESPACE}.${KUBE_CONTEXT}.${CLUSTER_DOMAIN}"
    if command -v pbcopy &> /dev/null; then
        echo "http://vigilante-${KUBE_NAMESPACE}.${KUBE_CONTEXT}.${CLUSTER_DOMAIN}" | pbcopy
        echo "📋 URL copied to clipboard!"
    fi
fi

echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""
echo "✅ $ENV deployment completed!"
