# --- Этап 1: Сборка (Build) ---
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore "BinanceDataCollector.sln"
RUN dotnet publish "BinanceDataCollector.Worker/BinanceDataCollector.Worker.csproj" -c Release -o /app/publish --no-restore

# --- Этап 2: Финальный образ (Final) ---
FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "BinanceDataCollector.Worker.dll"]