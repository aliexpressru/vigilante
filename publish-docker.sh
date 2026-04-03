#!/bin/bash

# Publish Vigilante Docker Image to Docker Hub

echo "🐳 Publishing Vigilante Docker Image to Docker Hub..."

# Check if Docker Hub username is provided
if [ -z "$DOCKER_HUB_USERNAME" ]; then
    echo "❌ Please set DOCKER_HUB_USERNAME environment variable"
    echo "Example: export DOCKER_HUB_USERNAME=your-dockerhub-username"
    exit 1
fi

# Get version tag (default to 'latest' if not provided)
VERSION_TAG=${VERSION_TAG:-latest}
IMAGE_NAME="$DOCKER_HUB_USERNAME/vigilante:$VERSION_TAG"

echo "📋 Build Information:"
echo "   Docker Hub Username: $DOCKER_HUB_USERNAME"
echo "   Image Name: $IMAGE_NAME"
echo "   Version Tag: $VERSION_TAG"
echo ""

# Multi-arch target platforms
PLATFORMS="${PLATFORMS:-linux/amd64,linux/arm64}"
echo "   Platforms: $PLATFORMS"
echo ""

# Ensure buildx is available
if ! docker buildx version >/dev/null 2>&1; then
    echo "❌ Docker Buildx is required for multi-arch publishing"
    echo "💡 Update Docker Desktop or install buildx plugin"
    exit 1
fi

# Build and push multi-arch image in a single step
echo "📦 Building and pushing multi-arch image: $IMAGE_NAME"
if docker buildx build \
    --platform "$PLATFORMS" \
    --tag "$IMAGE_NAME" \
    --push \
    .; then
    echo "✅ Multi-arch Docker image built and pushed successfully"
else
    echo "❌ Failed to build/push multi-arch Docker image"
    exit 1
fi

echo ""
echo "🔐 Docker Hub Authentication Check:"
echo "   The next command will verify your authentication by attempting to push the image."
echo "   If you see 'denied: requested access to the resource is denied', you need to:"
echo "   1. Run: docker login"
echo "   2. Make sure the repository '$DOCKER_HUB_USERNAME/vigilante' exists on Docker Hub"
echo "   3. Or create it automatically by pushing (if you have the rights)"
echo ""

echo "🌐 Image URL: https://hub.docker.com/r/$DOCKER_HUB_USERNAME/vigilante"
echo "📋 To use this image: docker pull $IMAGE_NAME"

echo ""
echo "🎉 Docker image publication completed successfully!"
