using Aer.QdrantClient.Http.Abstractions;
using Aer.QdrantClient.Http.DependencyInjection;
using FluentValidation;
using FluentValidation.AspNetCore;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.OpenApi;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Vigilante.Configuration;
using Vigilante.Middleware;
using Vigilante.Services;
using Vigilante.Services.Interfaces;
using Vigilante.Validators;

namespace Vigilante;

public partial class Startup(IConfiguration configuration)
{
    public void ConfigureServices(IServiceCollection services)
    {
        // Configuration
        services.Configure<QdrantOptions>(configuration.GetSection("Qdrant"));
        services.Configure<VigilanteOptions>(configuration.GetSection("Vigilante"));
        services.AddDataProtection();

        // Kubernetes client - only available when running in cluster
        services.AddSingleton<k8s.IKubernetes>(sp => 
        {
            try
            {
                return new k8s.Kubernetes(k8s.KubernetesClientConfiguration.InClusterConfig());
            }
            catch
            {
                // Not running in Kubernetes cluster - return null
                // KubernetesManager will handle null check
                return null!;
            }
        });
        services.AddSingleton<IKubernetesManager, KubernetesManager>();
        services.AddSingleton<IPodCommandExecutor, PodCommandExecutor>();
        services.AddSingleton<IDynamicConfigService, DynamicConfigService>();

        // Add health checks
        services.AddHealthChecks();

        // Metrics service
        services.AddSingleton<IMeterService, MeterService>();
        
        // Qdrant client setup
        services.AddSingleton<IQdrantNodesProvider, QdrantNodesProvider>();
        services.AddQdrantClientFactory();
        
        // Core services
        services.AddSingleton<IJobRegistry, JobRegistry>();
        services.AddSingleton<ISnapshotAutomationStatus, SnapshotAutomationStatus>();
        services.AddSingleton<IRestoreReplicationFactorJobService, RestoreReplicationFactorJobService>();
        services.AddSingleton<ICollectionService, CollectionService>();
        services.AddSingleton<ISnapshotService, SnapshotService>();
        services.AddSingleton<SnapshotOrphanedState>();
        services.AddSingleton<IClusterManager, ClusterManager>();
        services.AddSingleton<ILogReader, LogReader>();
        
        // Test data provider for local development
        services.AddSingleton<TestDataProvider>();
        
        // S3 services
        services.AddSingleton<IS3ConfigurationProvider, S3ConfigurationProvider>();
        services.AddSingleton<IS3SnapshotService, S3SnapshotService>();
        
        services.AddHostedService<QdrantMonitorService>();

        // OpenTelemetry with Prometheus exporter
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: MeterService.MeterName,
                    serviceInstanceId: Environment.MachineName)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["host.name"] = Environment.MachineName
                }))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter(MeterService.MeterName)
                .AddMeter("Microsoft.AspNetCore.Hosting")
                .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
                .AddMeter("System.Net.Http")
                .AddPrometheusExporter());

        // API
        services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            });
        services.AddFluentValidationAutoValidation()
            .AddFluentValidationClientsideAdapters()
            .AddValidatorsFromAssemblyContaining<V1ReplicateShardsRequestValidator>();

        // API Documentation
        services.AddEndpointsApiExplorer();
        var dashboardPassword = configuration["Vigilante:DashboardPassword"];
        services.AddSwaggerGen(options =>
        {
            options.CustomSchemaIds(type =>
                type.FullName?.Replace("+", ".", StringComparison.Ordinal) ?? type.Name);

            if (!string.IsNullOrWhiteSpace(dashboardPassword))
            {
                options.AddSecurityDefinition("basic", new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "basic",
                    Description = "Any username; password is Vigilante:DashboardPassword."
                });
                options.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("basic")] = []
                });
            }
        });
    }

    public void Configure(WebApplication app, IWebHostEnvironment env)
    {
        // Exception handling
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        else
        {
            app.UseExceptionHandler(options => {
                options.Run(async context =>
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        status = "Error",
                        message = "An unexpected error occurred",
                        code = context.Response.StatusCode
                    });
                });
            });
        }

        app.UseDashboardBasicAuth();

        // API Documentation
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "Vigilante API v1");
            c.DocumentTitle = "Vigilante - Qdrant Cluster Guardian";
            c.UseRequestInterceptor(
                "function (request) { request.credentials = 'include'; return request; }");
        });

        // Basic security
        app.Use(async (context, next) =>
        {
            context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
            await next();
        });

        if (ShouldUseHttpsRedirection(app.Configuration))
        {
            app.UseHttpsRedirection();
        }

        var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        var dashboardAssetVersion = ComputeDashboardAssetVersion(webRoot);

        // Serve index.html with cache-busting query params on CSS/JS (avoids stale assets after deploy)
        app.Use(async (context, next) =>
        {
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                await next();
                return;
            }

            var path = context.Request.Path.Value;
            if (path is not ("/" or "/index.html"))
            {
                await next();
                return;
            }

            var indexPath = Path.Combine(webRoot, "index.html");
            if (!File.Exists(indexPath))
            {
                await next();
                return;
            }

            try
            {
                var v = env.IsDevelopment()
                    ? ComputeDashboardAssetVersion(webRoot)
                    : dashboardAssetVersion;
                var ve = Uri.EscapeDataString(v);
                var html = await File.ReadAllTextAsync(indexPath);
                html = StylesCssHrefRegex().Replace(html, $"href=\"styles.css?v={ve}\"");
                html = DashboardJsSrcRegex().Replace(html, $"src=\"dashboard.js?v={ve}\"");
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                context.Response.Headers.Pragma = "no-cache";
                await context.Response.WriteAsync(html);
            }
            catch
            {
                await next();
            }
        });
        
        // Static files for dashboard - order is important!
        app.UseDefaultFiles(); // This must come before UseStaticFiles
        
        // Configure static files with cache control
        var staticFileOptions = new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                // In development, disable caching
                if (env.IsDevelopment())
                {
                    ctx.Context.Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate");
                    ctx.Context.Response.Headers.Append("Pragma", "no-cache");
                    ctx.Context.Response.Headers.Append("Expires", "0");
                }
                else
                {
                    // In production, cache for 1 hour but require revalidation
                    ctx.Context.Response.Headers.Append("Cache-Control", "public, max-age=3600, must-revalidate");
                }
            }
        };
        app.UseStaticFiles(staticFileOptions);
        
        app.UseRouting();
        
        // Simple CORS policy for all environments
        app.UseCors(policy => policy
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader());

        // Prometheus metrics endpoint
        app.MapPrometheusScrapingEndpoint();

        // Add health check endpoint
        app.MapHealthChecks("/health");

        app.MapControllers();

        // Graceful shutdown logging
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Register(() => 
            app.Logger.LogInformation("Vigilante is shutting down gracefully..."));
        lifetime.ApplicationStopped.Register(() => 
            app.Logger.LogInformation("Vigilante has stopped. Cluster is no longer monitored."));
    }

    /// <summary>
    /// Enables HTTPS redirection only when the app is configured with an HTTPS URL or HTTPS port
    /// (avoids "Failed to determine the https port" on HTTP-only runs e.g. local Docker).
    /// </summary>
    private static bool ShouldUseHttpsRedirection(IConfiguration configuration)
    {
        var urls = configuration["ASPNETCORE_URLS"] ?? configuration["urls"] ?? "";
        if (urls.Contains("https://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var httpsPorts = configuration["HTTPS_PORTS"] ?? configuration["ASPNETCORE_HTTPS_PORTS"] ?? "";
        return !string.IsNullOrWhiteSpace(httpsPorts);
    }

    private static string ComputeDashboardAssetVersion(string webRoot)
    {
        long xor = 0;
        foreach (var name in new[] { "styles.css", "dashboard.js" })
        {
            var p = Path.Combine(webRoot, name);
            if (File.Exists(p))
                xor ^= File.GetLastWriteTimeUtc(p).Ticks;
        }

        return xor == 0 ? "0" : (xor & 0xFFFFFFFFFFFFL).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"href\s*=\s*[""']styles\.css(\?[^""']*)?[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StylesCssHrefRegex();

    [GeneratedRegex(@"src\s*=\s*[""']dashboard\.js(\?[^""']*)?[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DashboardJsSrcRegex();
}
