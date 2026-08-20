using System.Net;
using System.Net.Sockets;
using ModbusTcpSimulator.Api.Services;
using ModbusTcpSimulator.Core.Conversion;
using ModbusTcpSimulator.Core.Persistence;
using ModbusTcpSimulator.Core.State;

namespace ModbusTcpSimulator.Api.Endpoints;

public static class SimulatorEndpoints
{
    public static void MapSimulatorEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/simulator");

        grp.MapGet("/status", async (
            SimulationWorker sim,
            AnomalyEngine anomalyEngine,
            WebSocketBroadcaster broadcaster,
            IUnitRepository unitRepo,
            IRegisterRepository regRepo,
            IAnomalyRepository anomalyRepo,
            IConfiguration config) =>
        {
            var units = await unitRepo.GetAllAsync();
            var regs = await regRepo.GetAllAsync();
            var anomalies = await anomalyRepo.GetAllAsync();

            // Determine the best local IP address for Modbus client connections
            var configHost = config["Modbus:Host"] ?? "0.0.0.0";
            var localIp = configHost;
            if (configHost == "0.0.0.0" || configHost == "::")
            {
                localIp = GetLocalIPAddress();
            }

            return Results.Ok(new
            {
                isRunning = sim.IsRunning,
                modbusHost = configHost,
                modbusPort = config["Modbus:Port"] ?? "502",
                localIp,
                unitCount = units.Count(),
                registerCount = regs.Sum(r =>
                {
                    int stride = Math.Max(1, DataTypeConverter.RegisterCount(r.DataType));
                    return (r.EndAddress - r.StartAddress) / stride + 1;
                }),
                activeAnomalyCount = anomalyEngine.ActiveAnomalies.Count,
                totalAnomalyCount = anomalies.Count(),
                webSocketClients = broadcaster.ClientCount
            });
        });

        grp.MapPost("/start", (SimulationWorker worker) =>
        {
            // The simulation auto-starts as a BackgroundService
            // This endpoint exists for UI start action
            return Results.Ok(new { started = true, message = "Simulator is running" });
        });

        grp.MapPost("/stop", (SimulationWorker worker) =>
        {
            // In a full production version, this would stop the background service
            return Results.Ok(new { stopped = false, message = "Use application restart to stop the simulator" });
        });
    }

    private static string GetLocalIPAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80);
            if (socket.LocalEndPoint is IPEndPoint endPoint)
            {
                if (!IsDockerSubnetIp(endPoint.Address))
                    return endPoint.Address.ToString();
            }
        }
        catch { }

        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip) && !IsDockerSubnetIp(ip))
                    return ip.ToString();
            }
        }
        catch { }

        return "127.0.0.1";
    }

    private static bool IsDockerSubnetIp(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 4)
        {
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return true;
            if (bytes[0] == 10 && bytes[1] == 255)
                return true;
        }
        return false;
    }
}
