# ================================================
# Ступень 1: Общая база для зависимостей и кода
# ================================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS base-build
WORKDIR /src

# Копируем .csproj и .sln для восстановления зависимостей
COPY ["BinanceDataCollector.sln", "./"]
COPY "src/BinanceDataCollector.Application/BinanceDataCollector.Application.csproj" "src/BinanceDataCollector.Application/"
COPY "src/BinanceDataCollector.Domain/BinanceDataCollector.Domain.csproj" "src/BinanceDataCollector.Domain/"
COPY "src/BinanceDataCollector.Infrastructure/BinanceDataCollector.Infrastructure.csproj" "src/BinanceDataCollector.Infrastructure/"
COPY "src/BinanceDataCollector.Worker/BinanceDataCollector.Worker.csproj" "src/BinanceDataCollector.Worker/"
COPY "src/BinanceDataCollector.DataManager/BinanceDataCollector.DataManager.csproj" "src/BinanceDataCollector.DataManager/"

COPY "tests/BinanceDataCollector.Application.Tests/BinanceDataCollector.Application.Tests.csproj" "tests/BinanceDataCollector.Application.Tests/"
COPY "tests/BinanceDataCollector.Domain.Tests/BinanceDataCollector.Domain.Tests.csproj" "tests/BinanceDataCollector.Domain.Tests/"
COPY "tests/BinanceDataCollector.Infrastructure.Tests/BinanceDataCollector.Infrastructure.Tests.csproj" "tests/BinanceDataCollector.Infrastructure.Tests/"
COPY "tests/BinanceDataCollector.Worker.Tests/BinanceDataCollector.Worker.Tests.csproj" "tests/BinanceDataCollector.Worker.Tests/"

RUN dotnet restore "BinanceDataCollector.sln"

# Копируем остальной код
COPY . .

# ================================================
# Ступень 2: Публикация Worker
# ================================================
FROM base-build AS build-worker
RUN dotnet publish "src/BinanceDataCollector.Worker/BinanceDataCollector.Worker.csproj" -c Release -o /app/publish

# ================================================
# Ступень 3: Публикация DataManager
# ================================================
FROM base-build AS build-datamanager
RUN dotnet publish "src/BinanceDataCollector.DataManager/BinanceDataCollector.DataManager.csproj" -c Release -o /app/publish

# ================================================
# Ступень 4: Финальный образ для Worker
# ================================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS worker
WORKDIR /app
COPY --from=build-worker /app/publish .

# V-- ЯВНО КОПИРУЕМ НУЖНЫЕ КОНФИГИ --V
COPY src/BinanceDataCollector.Worker/appsettings.json .
#COPY src/BinanceDataCollector.Worker/appsettings.Production.json .
# ^-- ЯВНО КОПИРУЕМ НУЖНЫЕ КОНФИГИ --^

ENTRYPOINT ["dotnet", "BinanceDataCollector.Worker.dll"]

# ================================================
# Ступень 5: Финальный образ для DataManager
# ================================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS datamanager
WORKDIR /app
COPY --from=build-datamanager /app/publish .

# V-- ЯВНО КОПИРУЕМ НУЖНЫЕ КОНФИГИ --V
COPY src/BinanceDataCollector.DataManager/appsettings.json .
#COPY src/BinanceDataCollector.DataManager/appsettings.Production.json .
# ^-- ЯВНО КОПИРУЕМ НУЖНЫЕ КОНФИГИ --^

ENTRYPOINT ["dotnet", "BinanceDataCollector.DataManager.dll"]