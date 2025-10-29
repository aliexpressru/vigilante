using Vigilante;
using Serilog;

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