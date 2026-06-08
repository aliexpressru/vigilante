#!/usr/bin/env pwsh

# Universal Vigilante Deployment Script
# Deploys to current kubectl context using configmap from current directory (dev or prod)

$ErrorActionPreference = 'Stop'

# Set custom label for resource ownership/team tracking
$env:OWNER_LABEL_NAME = "owner"   # Label name (e.g., "owner", "team", "managed-by")
$env:OWNER_LABEL_VALUE = "YOUR_NAME_HERE"  # Replace with actual owner/team name

# Set cluster domain for ingress access
$env:CLUSTER_DOMAIN = "your-cluster-domain.com"  # Replace with actual cluster domain
# =======================================================

Write-Host "🚀 Starting Vigilante deployment..."

# Get current kubernetes context and namespace
$KUBE_CONTEXT = kubectl config current-context
$KUBE_NAMESPACE = kubectl config view --minify -o jsonpath='{..namespace}'

if ([string]::IsNullOrEmpty($KUBE_NAMESPACE)) {
    $KUBE_NAMESPACE = "default"
}

Write-Host "🧹 Cleaning up old deployment..."
kubectl delete deployment vigilante -n $KUBE_NAMESPACE --ignore-not-found=true

# Determine environment based on current directory
$CURRENT_DIR = Split-Path -Leaf (Get-Location)
if ($CURRENT_DIR -eq "dev") {
    $AppEnv = "Development"
    $ENV_NAME = "dev"
} elseif ($CURRENT_DIR -eq "stg") {
    $AppEnv = "Staging"
    $ENV_NAME = "stg"
} elseif ($CURRENT_DIR -eq "prod") {
    $AppEnv = "Production"
    $ENV_NAME = "prod"
} else {
    Write-Host "❌ Please run this script from k8s/dev, k8s/stg or k8s/prod directory"
    Write-Host "💡 Current directory: $(Get-Location)"
    Write-Host "💡 Expected to be in: .../k8s/dev, .../k8s/stg or .../k8s/prod"
    exit 1
}

Write-Host "📦 Deploying to context: ${KUBE_CONTEXT}, namespace: ${KUBE_NAMESPACE}..."
if (-not [string]::IsNullOrEmpty($env:CLUSTER_DOMAIN)) {
    Write-Host "📦 Using cluster domain: $($env:CLUSTER_DOMAIN)"
}

# Get current kubectl context
$CURRENT_CONTEXT = kubectl config current-context 2>$null
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrEmpty($CURRENT_CONTEXT)) { $CURRENT_CONTEXT = "unknown" }

$CURRENT_NAMESPACE = kubectl config view --minify -o jsonpath='{.contexts[0].context.namespace}' 2>$null
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrEmpty($CURRENT_NAMESPACE)) { $CURRENT_NAMESPACE = "default" }

Write-Host "🎯 Environment: $AppEnv ($ENV_NAME)"
Write-Host "🎯 Context: $CURRENT_CONTEXT"
Write-Host "🎯 Namespace: $CURRENT_NAMESPACE"
Write-Host "📁 Config source: $(Get-Location)/configmap.yaml"
Write-Host ""

# Validate files exist
if (-not (Test-Path "configmap.yaml")) {
    Write-Host "❌ configmap.yaml not found in current directory"
    Write-Host "💡 Make sure you're in k8s/dev, k8s/stg or k8s/prod directory"
    exit 1
}

if (-not (Test-Path "dynamic-configmap.yaml")) {
    Write-Host "❌ dynamic-configmap.yaml not found in current directory"
    Write-Host "💡 Make sure you're in k8s/dev, k8s/stg or k8s/prod directory"
    exit 1
}

if (-not (Test-Path "../deployment.yaml")) {
    Write-Host "❌ ../deployment.yaml not found"
    Write-Host "💡 Make sure k8s/deployment.yaml exists"
    exit 1
}

if (-not (Test-Path "../service-monitor.yaml")) {
    Write-Host "❌ ../service-monitor.yaml not found"
    Write-Host "💡 Make sure k8s/service-monitor.yaml exists"
    exit 1
}

# Use IMAGE_NAME from environment or default
if ([string]::IsNullOrEmpty($env:IMAGE_NAME)) {
    $IMAGE_NAME = "aercis/vigilante:latest"
} else {
    $IMAGE_NAME = $env:IMAGE_NAME
}
Write-Host "📝 Using image: $IMAGE_NAME"
Write-Host "💡 Make sure the image is already built and pushed via GitHub Actions"
Write-Host ""

# Create temporary deployment file with environment-specific settings
$TEMP_DEPLOYMENT = Join-Path ([System.IO.Path]::GetTempPath()) "vigilante-deployment-${ENV_NAME}.yaml"
Copy-Item "../deployment.yaml" $TEMP_DEPLOYMENT

# Update deployment with environment-specific settings
$content = Get-Content $TEMP_DEPLOYMENT -Raw
$content = $content -replace 'image: .*', "image: $IMAGE_NAME"
$content = $content -replace 'value: "Production"', "value: `"$AppEnv`""
$content = $content -replace 'value: "Development"', "value: `"$AppEnv`""
Set-Content $TEMP_DEPLOYMENT $content -NoNewline

# Replace owner label placeholder with actual values if set
if (-not [string]::IsNullOrEmpty($env:OWNER_LABEL_NAME) -and
    -not [string]::IsNullOrEmpty($env:OWNER_LABEL_VALUE) -and
    $env:OWNER_LABEL_VALUE -ne "YOUR_NAME_HERE") {
    Write-Host "🏷️  Setting owner label: $($env:OWNER_LABEL_NAME)=$($env:OWNER_LABEL_VALUE)"
    $content = Get-Content $TEMP_DEPLOYMENT -Raw
    $content = $content -replace 'owner: "OWNER_PLACEHOLDER"', "$($env:OWNER_LABEL_NAME): `"$($env:OWNER_LABEL_VALUE)`""
    Set-Content $TEMP_DEPLOYMENT $content -NoNewline
} else {
    $content = Get-Content $TEMP_DEPLOYMENT
    $content = $content | Where-Object { $_ -notmatch 'owner: "OWNER_PLACEHOLDER"' }
    Set-Content $TEMP_DEPLOYMENT $content
}

# For production, update resources and health check timings
if ($ENV_NAME -eq "prod") {
    $content = Get-Content $TEMP_DEPLOYMENT -Raw
    $content = $content -replace 'memory: "128Mi"', 'memory: "256Mi"'
    $content = $content -replace 'memory: "256Mi"', 'memory: "512Mi"'
    $content = $content -replace 'cpu: "100m"', 'cpu: "250m"'
    $content = $content -replace 'cpu: "250m"', 'cpu: "500m"'
    $content = $content -replace 'initialDelaySeconds: 15', 'initialDelaySeconds: 30'
    Set-Content $TEMP_DEPLOYMENT $content -NoNewline
}

# Clean up any existing resources to avoid duplicates
Write-Host "🧹 Cleaning up old resources..."
kubectl delete service vigilante vigilante-service --ignore-not-found=true

# If CLUSTER_DOMAIN is set, prepare ingress file
if (-not [string]::IsNullOrEmpty($env:CLUSTER_DOMAIN)) {
    Write-Host "🔄 Creating ingress with domain $($env:CLUSTER_DOMAIN)..."
    kubectl delete ingress vigilante-ingress --ignore-not-found=true

    $TEMP_INGRESS = Join-Path ([System.IO.Path]::GetTempPath()) "vigilante-ingress-${ENV_NAME}.yaml"
    Copy-Item "../ingress.yaml" $TEMP_INGRESS

    $content = Get-Content $TEMP_INGRESS -Raw
    $content = $content -replace '\$\{KUBE_CONTEXT\}', $KUBE_CONTEXT
    $content = $content -replace '\$\{KUBE_NAMESPACE\}', $KUBE_NAMESPACE
    $content = $content -replace '\$\{CLUSTER_DOMAIN\}', $env:CLUSTER_DOMAIN
    Set-Content $TEMP_INGRESS $content -NoNewline
}

# Apply Kubernetes configurations
Write-Host "☸️  Applying Kubernetes configurations..."
kubectl apply -f ../rbac.yaml
kubectl apply -f configmap.yaml

# Bootstrap dynamic configuration ConfigMap if it doesn't exist or needs update
Write-Host "🔧 Bootstrapping dynamic configuration..."
kubectl get configmap vigilante-dynamic-config -n $KUBE_NAMESPACE 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Host "✓ Dynamic config ConfigMap already exists"
    $CURRENT_CONFIG = kubectl get configmap vigilante-dynamic-config -n $KUBE_NAMESPACE `
        -o jsonpath='{.data.dynamic-config\.json}' 2>$null
    if ([string]::IsNullOrEmpty($CURRENT_CONFIG)) {
        Write-Host "⚠️  ConfigMap exists but has no config data, applying from file..."
        kubectl apply -f dynamic-configmap.yaml
        Write-Host "✓ Dynamic config initialized from file"
    } else {
        Write-Host "✓ Current config: $CURRENT_CONFIG"
        Write-Host "💡 ConfigMap not updated to preserve runtime changes. To reset, delete and redeploy."
    }
} else {
    Write-Host "Creating dynamic config ConfigMap from file..."
    kubectl apply -f dynamic-configmap.yaml
    Write-Host "✓ Dynamic config ConfigMap created from: $(Get-Location)/dynamic-configmap.yaml"
}

kubectl apply -f $TEMP_DEPLOYMENT
kubectl apply -f ../service.yaml
kubectl apply -f ../service-monitor.yaml

# Apply ingress if domain is set
if (-not [string]::IsNullOrEmpty($env:CLUSTER_DOMAIN)) {
    kubectl apply -f $TEMP_INGRESS
    Remove-Item $TEMP_INGRESS -Force -ErrorAction SilentlyContinue
}

# Clean up temporary files
Remove-Item $TEMP_DEPLOYMENT -Force -ErrorAction SilentlyContinue

# Wait for deployment to be ready
Write-Host "⏳ Waiting for deployment to be ready..."
kubectl wait --for=condition=available --timeout=300s deployment/vigilante

# Get pod IP if no domain is set
if ([string]::IsNullOrEmpty($env:CLUSTER_DOMAIN)) {
    Write-Host ""
    Write-Host "🌐 Access Vigilante directly via Pod IP:"
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    $POD_IPS = (kubectl get pods -l app=vigilante -o jsonpath='{.items[*].status.podIP}') -split ' ' |
        Where-Object { $_ -ne '' }
    $POD_PORT = "8080"

    if ($POD_IPS.Count -eq 0) {
        Write-Host "⚠️  No pod IPs found yet. Pods may still be starting..."
    } else {
        for ($i = 0; $i -lt $POD_IPS.Count; $i++) {
            $url = "http://$($POD_IPS[$i]):$POD_PORT"
            Write-Host "🔗 Pod $($i + 1): $url"
            if ($i -eq 0) {
                Set-Clipboard -Value $url
                Write-Host "📋 First Pod URL copied to clipboard!"
            }
        }
    }
} else {
    Write-Host ""
    Write-Host "🌐 Access Vigilante via Ingress:"
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    $ingressUrl = "http://vigilante-${KUBE_NAMESPACE}.${KUBE_CONTEXT}.$($env:CLUSTER_DOMAIN)"
    Write-Host "🔗 URL: $ingressUrl"
    Set-Clipboard -Value $ingressUrl
    Write-Host "📋 URL copied to clipboard!"
}

Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
Write-Host ""
Write-Host "✅ $AppEnv deployment completed!"
