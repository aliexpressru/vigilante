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

# Build Docker image
echo "📦 Building Docker image: $IMAGE_NAME"
if docker build -t $IMAGE_NAME .; then
    echo "✅ Docker image built successfully"
else
    echo "❌ Failed to build Docker image"
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

# Push to Docker Hub
echo "📤 Pushing image to Docker Hub: $IMAGE_NAME"
if docker push $IMAGE_NAME; then
    echo "✅ Image pushed successfully to Docker Hub!"
    echo "🌐 Image URL: https://hub.docker.com/r/$DOCKER_HUB_USERNAME/vigilante"
    echo "📋 To use this image: docker pull $IMAGE_NAME"
else
    echo "❌ Failed to push image to Docker Hub"
    echo "💡 Common solutions:"
    echo "   1. Login to Docker Hub: docker login"
    echo "   2. Create the repository on Docker Hub: https://hub.docker.com/repository/create"
    echo "   3. Check your username is correct: $DOCKER_HUB_USERNAME"
    exit 1
fi

echo ""
echo "🎉 Docker image publication completed successfully!"
