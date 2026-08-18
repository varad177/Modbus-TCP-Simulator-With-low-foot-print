using System.Net.WebSockets;
using ModbusTcpSimulator.Api.Endpoints;
using ModbusTcpSimulator.Api.Services;
using ModbusTcpSimulator.Core.Persistence;
using ModbusTcpSimulator.Core.State;
using Serilog;

// ── Serilog Bootstrap ──────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ── Serilog ─────────────────────────────────────────────────────────────────
    builder.Host.UseSerilog((ctx, lc) => lc
        .ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console());

    // ── Configuration ────────────────────────────────────────────────────────────
    var connectionString = builder.Configuration["Database:ConnectionString"]
        ?? $"Data Source={Path.Combine(AppContext.BaseDirectory, "simulator.db")}";

    // ── Database Init ────────────────────────────────────────────────────────────
    await DatabaseInitializer.InitializeAsync(connectionString);

    // ── Repositories ─────────────────────────────────────────────────────────────
    builder.Services.AddSingleton<IUnitRepository>(_ => new UnitRepository(connectionString));
    builder.Services.AddSingleton<IRegisterRepository>(_ => new RegisterRepository(connectionString));
    builder.Services.AddSingleton<IAnomalyRepository>(_ => new AnomalyRepository(connectionString));

    // ── Simulator State (Singleton) ───────────────────────────────────────────────
    builder.Services.AddSingleton<SimulatorState>();

    // ── Background Services ───────────────────────────────────────────────────────
    builder.Services.AddSingleton<SimulationWorker>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<SimulationWorker>());

    builder.Services.AddSingleton<AnomalyEngine>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AnomalyEngine>());

    builder.Services.AddSingleton<WebSocketBroadcaster>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<WebSocketBroadcaster>());

    builder.Services.AddSingleton<ModbusTcpServerService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ModbusTcpServerService>());

    // ── CORS ──────────────────────────────────────────────────────────────────────
    builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
        p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

    // ── JSON ──────────────────────────────────────────────────────────────────────
    builder.Services.ConfigureHttpJsonOptions(o =>
    {
        o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

    var app = builder.Build();

    app.UseCors();
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

    // ── WebSocket Endpoint ────────────────────────────────────────────────────────
    app.Map("/ws", async (HttpContext ctx, WebSocketBroadcaster broadcaster) =>
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var clientId = Guid.NewGuid().ToString();
        broadcaster.AddClient(clientId, ws);
        Log.Information("WebSocket client connected: {ClientId}", clientId);

        try
        {
            // Keep the connection alive until client disconnects
            var buffer = new byte[1024];
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, ctx.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch { }
        finally
        {
            broadcaster.RemoveClient(clientId);
            Log.Information("WebSocket client disconnected: {ClientId}", clientId);
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
    });

    // ── REST Endpoints ────────────────────────────────────────────────────────────
    app.MapUnitEndpoints();
    app.MapRegisterEndpoints();
    app.MapAnomalyEndpoints();
    app.MapSimulatorEndpoints();
    app.MapExportImportEndpoints();

    // ── SPA Fallback ──────────────────────────────────────────────────────────────
    app.MapFallbackToFile("index.html");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application failed to start");
}
finally
{
    Log.CloseAndFlush();
}
