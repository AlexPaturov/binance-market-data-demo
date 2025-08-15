# --- Этап 1: Сборка (Build) ---
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# --- Копируем все файлы проектов (.csproj) и файл решения (.sln) ---
# Это позволяет Docker кэшировать слой с NuGet-пакетами
COPY ["BinanceDataCollector.sln", "./"]
COPY ["BinanceDataCollector.Domain/BinanceDataCollector.Domain.csproj", "BinanceDataCollector.Domain/"]
COPY ["BinanceDataCollector.Application/BinanceDataCollector.Application.csproj", "BinanceDataCollector.Application/"]
COPY ["BinanceDataCollector.Infrastructure/BinanceDataCollector.Infrastructure.csproj", "BinanceDataCollector.Infrastructure/"]
COPY ["BinanceDataCollector.Worker/BinanceDataCollector.Worker.csproj", "BinanceDataCollector.Worker/"]

# --- Копируем .csproj для ТЕСТОВЫХ проектов ---
# Скорее всего, ошибка была здесь
COPY ["BinanceDataCollector.Domain.Tests/BinanceDataCollector.Domain.Tests.csproj", "BinanceDataCollector.Domain.Tests/"]
COPY ["BinanceDataCollector.Application.Tests/BinanceDataCollector.Application.Tests.csproj", "BinanceDataCollector.Application.Tests/"]
COPY ["BinanceDataCollector.Infrastructure.Tests/BinanceDataCollector.Infrastructure.Tests.csproj", "BinanceDataCollector.Infrastructure.Tests/"]


# --- Восстанавливаем зависимости ---
# Теперь dotnet restore найдет все проекты, описанные в .sln
RUN dotnet restore "BinanceDataCollector.sln"

# --- Копируем весь остальной исходный код ---
# Теперь, когда пакеты восстановлены, копируем все .cs файлы и т.д.
COPY . .

# --- Публикуем основное приложение ---
# Мы указываем конкретный проект для публикации.
RUN dotnet publish "BinanceDataCollector.Worker/BinanceDataCollector.Worker.csproj" -c Release -o /app/publish --no-restore


# --- Этап 2: Финальный образ (Final) ---
FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "BinanceDataCollector.Worker.dll"]