using Vigilante;
using Serilog;

// Configure global SocketsHttpHandler settings to prevent native memory leaks
// These settings apply to ALL HttpClient instances created in the application
AppContext.SetSwitch("System.Net.SocketsHttpHandler.Http2Support", true);
AppContext.SetSwitch("System.Net.SocketsHttpHandler.Http2UnencryptedSupport", true);

// Set connection pool limits (applied to SocketsHttpHandler)
AppContext.SetData("System.Net.SocketsHttpHandler.MaxConnectionsPerServer", 10);

// Note: PooledConnectionLifetime and PooledConnectionIdleTimeout are set per-handler
// QdrantHttpClient will use default values, which are reasonable
// Default PooledConnectionLifetime = Infinite (connections reused)
// Default PooledConnectionIdleTimeout = 2 minutes

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

builder.Host.UseSerilog();

try
{
    Log.Information("🛡️ Starting Vigilante - Qdrant Cluster Guardian");
    
    var startup = new Startup(builder.Configuration);
    startup.ConfigureServices(builder.Services);
    var app = builder.Build();
    startup.Configure(app, app.Environment);
    
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "❌ Vigilante service terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}