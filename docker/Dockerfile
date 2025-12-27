# ================================================
# Ступень 1: Общая база для зависимостей и кода
# ================================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS base-build
WORKDIR /src

# Копируем .csproj и .sln для восстановления зависимостей
COPY BinanceDataCollector.Prod.sln ./
COPY src/BinanceDataCollector.Application/BinanceDataCollector.Application.csproj src/BinanceDataCollector.Application/
COPY src/BinanceDataCollector.DataManager/BinanceDataCollector.DataManager.csproj src/BinanceDataCollector.DataManager/
COPY src/BinanceDataCollector.Domain/BinanceDataCollector.Domain.csproj src/BinanceDataCollector.Domain/
COPY src/BinanceDataCollector.Infrastructure/BinanceDataCollector.Infrastructure.csproj src/BinanceDataCollector.Infrastructure/
COPY src/BinanceDataCollector.MarketScreenService/BinanceDataCollector.MarketScreenService.csproj src/BinanceDataCollector.MarketScreenService/
COPY src/BinanceDataCollector.Symbols/BinanceDataCollector.Symbols.csproj src/BinanceDataCollector.Symbols/
COPY src/BinanceDataCollector.Worker/BinanceDataCollector.Worker.csproj src/BinanceDataCollector.Worker/

RUN dotnet restore BinanceDataCollector.Prod.sln

# Копируем остальной код по папкам
COPY src/BinanceDataCollector.Application/ ./src/BinanceDataCollector.Application/
COPY src/BinanceDataCollector.Domain/ ./src/BinanceDataCollector.Domain/
COPY src/BinanceDataCollector.Infrastructure/ ./src/BinanceDataCollector.Infrastructure/
COPY src/BinanceDataCollector.Worker/ ./src/BinanceDataCollector.Worker/
COPY src/BinanceDataCollector.DataManager/ ./src/BinanceDataCollector.DataManager/
COPY src/BinanceDataCollector.MarketScreenService/ ./src/BinanceDataCollector.MarketScreenService/
COPY src/BinanceDataCollector.Symbols/ ./src/BinanceDataCollector.Symbols/

# ================================================
# Ступень 2: Публикация Worker
# ================================================
FROM base-build AS build-worker
WORKDIR /src
# Публикуем сразу в уникальную папку для Worker
RUN dotnet publish "src/BinanceDataCollector.Worker/BinanceDataCollector.Worker.csproj" \
    -c Release \
    -o /app/worker \
    --no-restore

# ================================================
# Ступень 3: Публикация DataManager
# ================================================
FROM base-build AS build-datamanager
WORKDIR /src
# Публикуем сразу в уникальную папку для DataManager
RUN dotnet publish "src/BinanceDataCollector.DataManager/BinanceDataCollector.DataManager.csproj" \
    -c Release \
    -o /app/datamanager \
    --no-restore

# ================================================
# Ступень 4: Финальный образ для Worker
# ================================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS worker
WORKDIR /app
COPY --from=build-worker /app/worker .
USER app 
ENTRYPOINT ["dotnet", "BinanceDataCollector.Worker.dll"]

# ================================================
# Ступень 5: Финальный образ для DataManager
# ================================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS datamanager
WORKDIR /app
COPY --from=build-datamanager /app/datamanager .
USER app 
ENTRYPOINT ["dotnet", "BinanceDataCollector.DataManager.dll"]

# ================================================
# Dev-образ для Worker (dotnet watch)
# ================================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS worker-dev
WORKDIR /app
ENV DOTNET_USE_POLLING_FILE_WATCHER=1 
# Копируем проект целиком (в dev в любом случае будут volume из compose)
COPY BinanceDataCollector.Prod.sln ./
COPY src ./src

ENTRYPOINT ["dotnet", "watch", "run", "--project", "src/BinanceDataCollector.Worker/BinanceDataCollector.Worker.csproj", "--urls=http://0.0.0.0:7001", "--diagnostic-port", "5600"]

# ================================================
# Dev-образ для DataManager (dotnet watch)
# ================================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS datamanager-dev
WORKDIR /app
ENV DOTNET_USE_POLLING_FILE_WATCHER=1 
COPY BinanceDataCollector.Prod.sln ./
COPY src ./src

ENTRYPOINT ["dotnet", "watch", "run", "--project", "src/BinanceDataCollector.DataManager/BinanceDataCollector.DataManager.csproj", "--urls=http://0.0.0.0:7002", "--diagnostic-port", "5601"]
