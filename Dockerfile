# ================================================
# Ступень 1: Общая база для зависимостей и кода
# ================================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source

# Копируем .sln и .csproj, сохраняя структуру, и восстанавливаем зависимости
COPY *.sln .
COPY src/ ./src/
COPY tests/ ./tests/
RUN dotnet restore

# Копируем остальной код
COPY . .

# ================================================
# Ступень 2: Публикация Worker
# ================================================
FROM build AS build-worker
RUN dotnet publish /source/src/BinanceDataCollector.Worker/BinanceDataCollector.Worker.csproj \
    -c Release \
    -o /app/publish/worker \
    --no-restore 

# ================================================
# Ступень 3: Публикация DataManager
# ================================================
FROM build AS build-datamanager
RUN dotnet publish /source/src/BinanceDataCollector.DataManager/BinanceDataCollector.DataManager.csproj \
    -c Release \
    -o /app/publish/datamanager \
    --no-restore 

# ================================================
# Финальные ступени
# ================================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS worker
WORKDIR /app
COPY --from=build-worker /app/publish/worker .
ENTRYPOINT ["dotnet", "BinanceDataCollector.Worker.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS datamanager
WORKDIR /app
COPY --from=build-datamanager /app/publish/datamanager .
ENTRYPOINT ["dotnet", "BinanceDataCollector.DataManager.dll"]