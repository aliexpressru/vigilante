using FluentValidation;
using FluentValidation.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Vigilante.Configuration;
using Vigilante.Services;
using Vigilante.Services.Interfaces;
using Vigilante.Validators;

namespace Vigilante;

public class Startup(IConfiguration configuration)
{
    public void ConfigureServices(IServiceCollection services)
    {
        // Configuration
        services.AddMemoryCache();
        services.Configure<QdrantOptions>(configuration.GetSection("Qdrant"));
        services.AddSingleton<IQdrantNodesProvider, QdrantNodesProvider>();
        services.AddSingleton<IQdrantClientFactory, DefaultQdrantClientFactory>();

        // Add health checks
        services.AddHealthChecks();

        // Core services
        services.AddSingleton<ClusterManager>();
        services.AddHostedService<QdrantMonitorService>();
        services.AddSingleton<CollectionService>();
        
        // Metrics service
        services.AddSingleton<IMeterService, MeterService>();

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
        services.AddControllers();
        services.AddFluentValidationAutoValidation()
            .AddFluentValidationClientsideAdapters()
            .AddValidatorsFromAssemblyContaining<V1ReplicateShardsRequestValidator>();

        // API Documentation
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();
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

        // API Documentation
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "Vigilante API v1");
            c.DocumentTitle = "🛡️ Vigilante - Qdrant Cluster Guardian";
        });

        // Basic security
        app.Use(async (context, next) =>
        {
            context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
            await next();
        });

        app.UseHttpsRedirection();
        
        // Static files for dashboard - order is important!
        app.UseDefaultFiles(); // This must come before UseStaticFiles
        app.UseStaticFiles();
        
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
            app.Logger.LogInformation("🛡️ Vigilante is shutting down gracefully..."));
        lifetime.ApplicationStopped.Register(() => 
            app.Logger.LogInformation("🛡️ Vigilante has stopped. Cluster is no longer monitored."));
    }
}
