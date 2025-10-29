# Build image
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0.101 AS build
ARG TARGETPLATFORM
ARG BUILDPLATFORM
WORKDIR /src

# Configure NuGet to use faster package sources
ENV NUGET_XMLDOC_MODE=skip
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# Copy csproj and restore dependencies separately to leverage Docker layer caching
COPY src/Aer.Vigilante.csproj ./src/
RUN dotnet restore src/Aer.Vigilante.csproj --verbosity minimal

# Copy source code and build
COPY . .
WORKDIR /src/src
RUN dotnet publish -c Release -o /app --no-restore

# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:9.0.1
WORKDIR /app

# Create non-root user
RUN useradd -r -u 1001 appuser
USER appuser

# Copy app
COPY --from=build --chown=appuser:appuser /app .

# Set the entry point to use dotnet with the correct DLL name
ENTRYPOINT ["dotnet", "Vigilante.dll"]
