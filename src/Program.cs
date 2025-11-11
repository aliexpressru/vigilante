using Vigilante;
using Serilog;

// Global HttpClient settings to prevent native memory leaks
AppContext.SetSwitch("System.Net.SocketsHttpHandler.Http2Support", true);
AppContext.SetSwitch("System.Net.SocketsHttpHandler.Http2UnencryptedSupport", true);
AppContext.SetData("System.Net.SocketsHttpHandler.MaxConnectionsPerServer", 10);


var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel for large file uploads (no size limit)
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxRequestBodySize = null; // No limit on request body size
    serverOptions.Limits.MinRequestBodyDataRate = null; // Allow slow uploads for large files
    serverOptions.Limits.MinResponseDataRate = null; // Allow slow downloads for large files
});

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