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
RUN useradd -r -u 1001 -m appuser

# Install dotnet diagnostic tools as appuser
# These tools are needed for memory dumps and diagnostics in production
USER appuser
ENV DOTNET_ROOT=/usr/share/dotnet
ENV PATH="${PATH}:/home/appuser/.dotnet/tools"
RUN dotnet tool install --global dotnet-dump && \
    dotnet tool install --global dotnet-gcdump && \
    dotnet tool install --global dotnet-trace && \
    dotnet tool install --global dotnet-counters

# Create directory for dumps
RUN mkdir -p /app/dumps

USER root
RUN chown appuser:appuser /app/dumps

USER appuser

# Copy app
COPY --from=build --chown=appuser:appuser /app .

# Set the entry point to use dotnet with the correct DLL name
ENTRYPOINT ["dotnet", "Vigilante.dll"]
