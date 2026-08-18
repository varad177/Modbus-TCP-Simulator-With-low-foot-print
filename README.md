# Modbus TCP Simulator

A lightweight, low-footprint Modbus TCP simulator that generates realistic register values and supports live anomaly injection. Built with ASP.NET Core 10, SQLite, NModbus, and vanilla JavaScript — zero frontend dependencies.

**GitHub:** [varad177/Modbus-TCP-Simulator-With-low-foot-print](https://github.com/varad177/Modbus-TCP-Simulator-With-low-foot-print)

---

## Features

| Category | Capabilities |
|----------|-------------|
| **Modbus TCP Slave** | Full Modbus TCP server on port 502 — any Modbus client (mbpoll, ModRSsim, SCADA, PLC) can read simulated registers in real time |
| **Value Generation** | Constant, Random, Increment, Decrement, Sine wave — with configurable min/max range, step size, update interval, and scatterness |
| **Data Types** | Bool, UInt16, Int16, UInt32, Int32, Float32, UInt64, Int64, Float64 — with BigEndian, LittleEndian, WordSwap byte order |
| **Register Types** | Coil, DiscreteInput, HoldingRegister, InputRegister — all 4 Modbus register types supported |
| **Anomaly Injection** | Trigger anomalies on any register range — InstantSpike, GradualRamp, Oscillation patterns with Immediate or Gradual recovery |
| **Scheduled Anomalies** | Auto-trigger anomalies on a timer interval — run continuously in the background |
| **Live Dashboard** | WebSocket-powered real-time register values with sparkline trend charts |
| **Quick Inject** | One-click anomaly creation + auto-trigger directly from the live register table |
| **Export / Import** | Full configuration backup/restore as JSON — merge without duplicating on import |
| **Dark Mode** | Automatic theme detection (`prefers-color-scheme`) with manual toggle, persisted in localStorage |
| **Mobile Responsive** | Collapsible sidebar, responsive tables — works on phones and tablets |
| **Health Check** | `GET /health` endpoint for monitoring |
| **Docker Ready** | Multi-stage Dockerfile + docker-compose — ~100MB production image |

---

## Quick Start

### Option 1: Docker (Recommended)

```bash
git clone https://github.com/varad177/Modbus-TCP-Simulator-With-low-foot-print.git
cd Modbus-TCP-Simulator-With-low-foot-print
docker compose up -d
```

- **Web UI:** http://localhost:8090/index.html
- **Modbus TCP:** YourLocalIp:502

### Option 2: Run from Source

**Prerequisites:** [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

```bash
git clone https://github.com/varad177/Modbus-TCP-Simulator-With-low-foot-print.git
cd Modbus-TCP-Simulator-With-low-foot-print
dotnet run --project ModbusTcpSimulator.Api
```

- **Web UI:** http://localhost:5175
- **Modbus TCP:** localhost:502 (port 502 requires admin/root on Windows)

> **Windows (non-admin):** Change the Modbus port in `appsettings.json` to `5020` or any port above 1024.

---

## How It Works

```
┌─────────────────────────────────────────────────────────────┐
│                       Web Browser                           │
│  Vanilla JS (app.js) ◄── WebSocket ──► Live Register Table │
│  Configure units, registers, anomalies via REST API         │
└──────────┬──────────────────────┬───────────────────────────┘
           │ REST API             │ WebSocket
           ▼                     ▼
┌─────────────────────────────────────────────────────────────┐
│                  ASP.NET Core Backend                       │
│                                                             │
│  ┌─────────────┐  ┌──────────────┐  ┌──────────────────┐   │
│  │ Simulation  │  │   Anomaly    │  │    WebSocket     │   │
│  │   Worker    │  │   Engine     │  │   Broadcaster    │   │
│  │ (100ms tick)│  │ (50ms tick)  │  │  (250ms batch)   │   │
│  └──────┬──────┘  └──────┬───────┘  └────────┬─────────┘   │
│         │                │                    │             │
│         ▼                ▼                    │             │
│  ┌──────────────────────────────┐             │             │
│  │     SimulatorState           │◄────────────┘             │
│  │  (ConcurrentDictionary)      │                           │
│  │  Sparse register storage     │                           │
│  └──────────────┬───────────────┘                           │
│                 │                                           │
│  ┌──────────────▼───────────────┐  ┌────────────────────┐  │
│  │   Modbus TCP Server          │  │   SQLite (WAL)     │  │
│  │   (NModbus, port 502)       │  │   Dapper ORM       │  │
│  └──────────────────────────────┘  └────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

### Core Loop

1. **SimulationWorker** ticks every 100ms. Each register entry has its own `NextDue` timestamp — registers with `UpdateIntervalMs: 1000` update once per second, those with `200` update 5 times per second. No thread-per-register.

2. **AnomalyEngine** ticks every 50ms. It checks for expired anomalies, processes scheduled triggers, and applies active anomaly patterns (spike, ramp, oscillation) to register values. Supports immediate and gradual recovery after anomaly expiry.

3. **WebSocketBroadcaster** ticks every 250ms. It drains only the changed registers from `SimulatorState`, serializes them as JSON, and pushes to all connected browsers. Only changed values are sent — never the full register set.

4. **ModbusTcpServerService** runs a persistent TCP listener. Every Modbus read request is served directly from `SimulatorState` — no disk I/O, no database queries in the hot path.

---

## Low Footprint Design

This simulator is designed to run on minimal hardware — Raspberry Pi, old laptops, CI/CD containers, or shared dev machines.

### Backend Efficiency

| Technique | Why It Matters |
|-----------|---------------|
| **Single 100ms master ticker** | One `PeriodicTimer` manages all registers. Each entry tracks its own `NextDue` — no thread-per-register, no timer pool exhaustion. |
| **Volatile snapshot swap** | Register entry list is a `volatile` reference replaced atomically on config reload. The hot loop never takes a lock. |
| **Sparse register storage** | `ConcurrentDictionary<ushort, ushort[]>` per unit per register type — only configured addresses consume memory, not a full 65536-entry array. |
| **Drain-only WebSocket** | `DrainPendingChanges()` atomically removes and returns only modified registers. The broadcaster never iterates the full state. |
| **No EF Core / No heavy ORM** | Dapper micro-ORM for direct SQL mapping. No change tracking, no migration pipeline, no expression trees compiled at runtime. |
| **Short-lived SQLite connections** | Each DB call opens and disposes its own `SqliteConnection`. No connection pool management, no leaked connections. |
| **SQLite WAL mode** | Write-ahead logging allows concurrent reads during writes — the simulation loop never blocks on database access. |
| **BackgroundService pattern** | All 4 services (`SimulationWorker`, `AnomalyEngine`, `WebSocketBroadcaster`, `ModbusTcpServerService`) use cooperative `CancellationToken` with zero thread pool pressure. |
| **No external dependencies** | No Redis, no RabbitMQ, no message broker. Everything is in-memory via `SimulatorState` singleton. |

### Frontend Efficiency

| Technique | Why It Matters |
|-----------|---------------|
| **Zero runtime dependencies** | Vanilla JavaScript — no React, no Angular, no webpack, no node_modules. Single `app.js` file. |
| **Incremental DOM updates** | WebSocket updates patch individual cells — the full table is only rebuilt on structural changes (new registers). |
| **Debounced rendering** | `scheduleRenderLiveTable()` uses 80ms debounce — rapid WS updates coalesce into a single DOM pass. |
| **Canvas sparklines** | Trend charts use raw `<canvas>` — no charting library, no SVG overhead. |
| **In-place anomaly updates** | `updateAnomalyCellsInPlace()` patches only the controls column — no full row recreation. |

### Docker Efficiency

- Multi-stage build: SDK image for compilation, minimal ASP.NET runtime for production
- Runs as non-root user
- ~100MB final image size
- Named volume for SQLite persistence

---

## Configuration

### appsettings.json

```json
{
  "Database": {
    "ConnectionString": "Data Source=simulator.db"
  },
  "Modbus": {
    "Host": "0.0.0.0",
    "Port": 502
  },
  "WebSocket": {
    "BroadcastIntervalMs": 250
  }
}
```

### Environment Variables (Docker)

| Variable | Default | Description |
|----------|---------|-------------|
| `ASPNETCORE_URLS` | `http://+:8080` | Web UI listen address |
| `Modbus__Host` | `0.0.0.0` | Modbus TCP bind address |
| `Modbus__Port` | `502` | Modbus TCP port |
| `Database__ConnectionString` | `Data Source=simulator.db` | SQLite path |
| `WebSocket__BroadcastIntervalMs` | `250` | WebSocket broadcast interval |

---

## API Reference

### Units

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/units` | List all units |
| `POST` | `/api/units` | Create unit (UnitId 1-247) |
| `PUT` | `/api/units/{id}` | Update unit |
| `DELETE` | `/api/units/{id}` | Delete unit + cascade |

### Register Configurations

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/register-configurations` | List all registers |
| `POST` | `/api/units/{unitId}/registers` | Create register (overlap validation) |
| `PUT` | `/api/register-configurations/{id}` | Update register |
| `DELETE` | `/api/register-configurations/{id}` | Delete register |
| `POST` | `/api/register-configurations/{id}/split` | Split address range into individual registers |

### Anomalies

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/anomalies` | List all anomalies (with live `isActive` status) |
| `POST` | `/api/anomalies` | Create anomaly |
| `PUT` | `/api/anomalies/{id}` | Update anomaly |
| `DELETE` | `/api/anomalies/{id}` | Delete anomaly |
| `POST` | `/api/anomalies/{id}/trigger` | Trigger anomaly manually |
| `POST` | `/api/anomalies/{id}/stop` | Stop active anomaly |
| `POST` | `/api/anomalies/{id}/enable` | Enable schedule |
| `POST` | `/api/anomalies/{id}/disable` | Disable schedule |

### Simulator

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/simulator/status` | System status (units, registers, active anomalies, WS clients) |
| `POST` | `/api/simulator/start` | Start simulation |
| `POST` | `/api/simulator/stop` | Stop simulation |

### Export / Import

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/export` | Export full configuration as JSON |
| `POST` | `/api/import` | Import configuration (merge, no duplicates) |

### Other

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/health` | Health check (`{ status: "healthy", timestamp }`) |
| `WS` | `/ws` | WebSocket for live register updates |

---

## Modbus TCP Usage

The simulator acts as a **Modbus TCP slave**. Any Modbus client can connect and read registers.

### Example with mbpoll

```bash
# Read 10 holding registers starting at address 0 from unit 1
mbpoll -m tcp -a 1 -r 1 -c 10 -t 3 -i 1000 localhost

# Read 5 coils from unit 1
mbpoll -m tcp -a 1 -r 1 -c 5 -t 0 -i 1000 localhost

# Read a single float32 holding register at address 100
mbpoll -m tcp -a 1 -r 101 -c 1 -t 4 -i 1000 localhost
```

### Register Addressing

The simulator uses **zero-based addressing** internally:

| Register Type | Modbus Protocol Address | Modicon 4x/3x Address |
|---------------|------------------------|-----------------------|
| HoldingRegister | 0 | 40001 |
| InputRegister | 0 | 30001 |
| Coil | 0 | 1 |
| DiscreteInput | 0 | 10001 |

---

## Anomaly System

An anomalies simulate real-world faults — sensor spikes, gradual drift, oscillation.

### Patterns

| Pattern | Behavior |
|---------|----------|
| **InstantSpike** | Immediately jumps to target value |
| **GradualRamp** | Linearly ramps from base to target over the duration |
| **Oscillation** | Sinusoidal oscillation between base and target |

### Recovery Types

| Type | Behavior |
|------|----------|
| **Immediate** | Registers snap back to fresh simulation values instantly |
| **Gradual** | Registers smoothly interpolate from anomaly values back to normal over the same duration |

### Directions

| Direction | Behavior |
|-----------|----------|
| **Increase** | Value increases by N% from midpoint |
| **Decrease** | Value decreases by N% from midpoint |
| **CustomValue** | Set a specific constant or per-register random range |

---

## Value Generation Types

| Type | Description |
|------|-------------|
| **Constant** | Fixed value (e.g., 42.0) |
| **Random** | Uniform random between Min and Max |
| **Increment** | Adds `IncrementStep` each update, wraps to Min when exceeding Max |
| **Decrement** | Subtracts `IncrementStep` each update, wraps to Max when going below Min |
| **Sine** | Sinusoidal wave: `midpoint + amplitude * sin(phase)` with configurable period |

Each register type also supports **scatterness** — adding random noise to values:
- **Percentage:** Noise proportional to current value
- **Absolute:** Fixed noise amplitude

---

## Frontend Guide

### Live Values Page
- Registers grouped by Unit ID with expand/collapse
- Real-time value updates via WebSocket
- Inline sparkline trend charts (rolling 20-point history)
- Inline anomaly controls (trigger / stop / schedule toggle)
- Quick Inject button for one-click anomaly creation
- Search/filter across all registers
- Copy mbpoll command to clipboard

### Anomalies Page
- Full CRUD for anomaly configurations
- Active anomalies table with live countdown timers
- Manual trigger / schedule enable/disable
- Pattern, recovery type, and direction configuration

### Units Page
- Unit management (Modbus Unit ID 1-247)
- Register configuration with data type, byte order, generation type
- Address range with automatic overlap validation
- Split range into individual registers

### Dashboard
- System overview: running status, Modbus connection info, unit/register counts
- Active anomaly count, WebSocket client count

---

## Project Structure

```
ModbusTcpSimulator.slnx
├── ModbusTcpSimulator.Api/          # ASP.NET Core Web API
│   ├── Endpoints/                    # REST API route handlers
│   │   ├── UnitEndpoints.cs
│   │   ├── RegisterEndpoints.cs
│   │   ├── AnomalyEndpoints.cs
│   │   ├── SimulatorEndpoints.cs
│   │   └── ExportImportEndpoints.cs
│   ├── Services/                     # Background services
│   │   ├── SimulationWorker.cs       # Value generation loop (100ms tick)
│   │   ├── AnomalyEngine.cs          # Anomaly lifecycle (50ms tick)
│   │   ├── WebSocketBroadcaster.cs   # Live data push (250ms batch)
│   │   └── ModbusTcpServerService.cs # Modbus TCP slave (NModbus)
│   ├── wwwroot/                      # Frontend (vanilla JS)
│   │   ├── index.html
│   │   ├── app.js
│   │   └── app.css
│   └── Program.cs                    # App entry point, DI, middleware
│
└── ModbusTcpSimulator.Core/          # Domain + Infrastructure
    ├── Models/Domain.cs              # All entities and enums
    ├── Conversion/DataTypeConverter.cs # Data type encode/decode
    ├── Generation/ValueGenerator.cs  # Stateful value generation
    ├── Persistence/
    │   ├── DatabaseInitializer.cs    # SQLite schema creation
    │   └── Repositories.cs           # Dapper-based repositories
    └── State/SimulatorState.cs       # Thread-safe register state
```

---

## Tech Stack

| Layer | Technology |
|-------|-----------|
| **Runtime** | .NET 10 (ASP.NET Core) |
| **Database** | SQLite (WAL mode) |
| **ORM** | Dapper (micro-ORM) |
| **Modbus** | NModbus 3.0.83 |
| **Logging** | Serilog (Console sink) |
| **Frontend** | Vanilla JavaScript (zero dependencies) |
| **Realtime** | WebSocket (System.Net.WebSockets) |
| **Container** | Docker (multi-stage, non-root) |

---

## License

MIT
