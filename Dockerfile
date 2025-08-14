# --- Этап 1: Сборка (Build) ---
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Копируем .sln и все .csproj файлы. Пути исправлены - убираем "src/".
COPY ["BinanceDataCollector.sln", "./"]
COPY ["BinanceDataCollector.Domain/BinanceDataCollector.Domain.csproj", "BinanceDataCollector.Domain/"]
COPY ["BinanceDataCollector.Application/BinanceDataCollector.Application.csproj", "BinanceDataCollector.Application/"]
COPY ["BinanceDataCollector.Infrastructure/BinanceDataCollector.Infrastructure.csproj", "BinanceDataCollector.Infrastructure/"]
COPY ["BinanceDataCollector.Worker/BinanceDataCollector.Worker.csproj", "BinanceDataCollector.Worker/"]
# Копируем .csproj тестовых проектов
COPY ["BinanceDataCollector.Application.Tests/BinanceDataCollector.Application.Tests.csproj", "BinanceDataCollector.Application.Tests/"]
COPY ["BinanceDataCollector.Domain.Tests/BinanceDataCollector.Domain.Tests.csproj", "BinanceDataCollector.Domain.Tests/"]
COPY ["BinanceDataCollector.Infrastructure.Tests/BinanceDataCollector.Infrastructure.Tests.csproj", "BinanceDataCollector.Infrastructure.Tests/"]


# Восстанавливаем NuGet-пакеты
RUN dotnet restore "BinanceDataCollector.sln"

# Копируем весь остальной исходный код
COPY . .

# Публикуем приложение. Путь к проекту исправлен.
RUN dotnet publish "BinanceDataCollector.Worker/BinanceDataCollector.Worker.csproj" -c Release -o /app/publish --no-restore


# --- Этап 2: Финальный образ (Final) ---
FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "BinanceDataCollector.Worker.dll"]