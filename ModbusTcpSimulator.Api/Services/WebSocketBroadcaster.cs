using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModbusTcpSimulator.Core.State;

namespace ModbusTcpSimulator.Api.Services;

/// <summary>
/// Maintains connected WebSocket clients and periodically broadcasts
/// only changed register values (batched, configurable interval).
/// </summary>
public sealed class WebSocketBroadcaster : BackgroundService
{
    private readonly SimulatorState _state;
    private readonly ILogger<WebSocketBroadcaster> _logger;
    private readonly int _broadcastIntervalMs;

    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();

    public WebSocketBroadcaster(
        SimulatorState state,
        ILogger<WebSocketBroadcaster> logger,
        IConfiguration configuration)
    {
        _state = state;
        _logger = logger;
        _broadcastIntervalMs = int.Parse(configuration["WebSocket:BroadcastIntervalMs"] ?? "250");
    }

    public void AddClient(string clientId, WebSocket ws) => _clients.TryAdd(clientId, ws);
    public void RemoveClient(string clientId) => _clients.TryRemove(clientId, out _);
    public int ClientCount => _clients.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_broadcastIntervalMs));
        try
        {
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (_clients.IsEmpty) continue;

            var changes = _state.DrainPendingChanges();
            if (changes.Count == 0) continue;

            // Group by unitId for a compact message structure
            var grouped = changes
                .GroupBy(kv => kv.Key.UnitId)
                .Select(g => new
                {
                    unitId = (int)g.Key,
                    changes = g.Select(kv => new
                    {
                        registerType = kv.Key.RegisterType.ToString(),
                        address = (int)kv.Key.Address,
                        value = Math.Round(kv.Value, 4)
                    }).ToList()
                });

            var json = JsonSerializer.Serialize(grouped, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            var bytes = Encoding.UTF8.GetBytes(json);
            var segment = new ArraySegment<byte>(bytes);

            var deadClients = new List<string>();

            foreach (var (clientId, ws) in _clients)
            {
                if (ws.State != WebSocketState.Open)
                {
                    deadClients.Add(clientId);
                    continue;
                }

                try
                {
                    await ws.SendAsync(segment, WebSocketMessageType.Text, true, stoppingToken);
                }
                catch
                {
                    deadClients.Add(clientId);
                }
            }

            foreach (var id in deadClients)
                _clients.TryRemove(id, out _);
        }
        } catch (OperationCanceledException) { /* graceful shutdown */ }
    }
}
