# ── Build Stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy and restore
COPY ModbusTcpSimulator.Core/ModbusTcpSimulator.Core.csproj ModbusTcpSimulator.Core/
COPY ModbusTcpSimulator.Api/ModbusTcpSimulator.Api.csproj ModbusTcpSimulator.Api/
RUN dotnet restore ModbusTcpSimulator.Api/ModbusTcpSimulator.Api.csproj

# Copy source and publish
COPY . .
RUN dotnet publish ModbusTcpSimulator.Api/ModbusTcpSimulator.Api.csproj \
    -c Release -o /app/publish

# ── Runtime Stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Non-root user (built into .NET 8+ runtime images)
COPY --from=build /app/publish .

# Create data directory and assign ownership to app user
USER root
RUN mkdir -p /app/data && chown -R app:app /app/data
USER app

# Data volume for SQLite
VOLUME ["/app/data"]

# Environment defaults (override via docker-compose or -e flags)
ENV ASPNETCORE_URLS=http://+:8080
ENV Modbus__Host=0.0.0.0
ENV Modbus__Port=502
ENV Database__ConnectionString="Data Source=/app/data/simulator.db"
ENV WebSocket__BroadcastIntervalMs=250
ENV Serilog__MinimumLevel__Default=Information

EXPOSE 8080
EXPOSE 502

ENTRYPOINT ["dotnet", "ModbusTcpSimulator.Api.dll"]
