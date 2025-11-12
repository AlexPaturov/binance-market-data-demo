# ================================================
# Ступень 1: Общая база для сборки (base-build)
# ================================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS base-build
WORKDIR /src

# Копируем все .csproj и восстанавливаем зависимости для кэширования
COPY *.sln .
COPY src/BinanceDataCollector.Application/BinanceDataCollector.Application.csproj src/BinanceDataCollector.Application/
COPY src/BinanceDataCollector.Domain/BinanceDataCollector.Domain.csproj src/BinanceDataCollector.Domain/
COPY src/BinanceDataCollector.Infrastructure/BinanceDataCollector.Infrastructure.csproj src/BinanceDataCollector.Infrastructure/
COPY src/BinanceDataCollector.Worker/BinanceDataCollector.Worker.csproj src/BinanceDataCollector.Worker/
COPY src/BinanceDataCollector.DataManager/BinanceDataCollector.DataManager.csproj src/BinanceDataCollector.DataManager/
RUN dotnet restore

# Копируем весь остальной исходный код
COPY . .

# ================================================
# Ступень 2: Сборка Worker
# ================================================
FROM base-build AS build-worker
WORKDIR /src/BinanceDataCollector.Worker
RUN dotnet publish "BinanceDataCollector.Worker.csproj" -c Release -o /app/publish

# ================================================
# Ступень 3: Сборка DataManager
# ================================================
FROM base-build AS build-datamanager
WORKDIR /src/BinanceDataCollector.DataManager
RUN dotnet publish "BinanceDataCollector.DataManager.csproj" -c Release -o /app/publish

# ================================================
# Ступень 4: Финальный образ для Worker
# ================================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS worker
WORKDIR /app
COPY --from=build-worker /app/publish .
ENTRYPOINT ["dotnet", "BinanceDataCollector.Worker.dll"]

# ================================================
# Ступень 5: Финальный образ для DataManager
# ================================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS datamanager
WORKDIR /app
COPY --from=build-datamanager /app/publish .
ENTRYPOINT ["dotnet", "BinanceDataCollector.DataManager.dll"]