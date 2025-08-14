# --- Этап 1: Сборка (Build) ---
# Используем официальный образ .NET 8 SDK. Даем этому этапу имя 'build'.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Копируем .sln и все .csproj файлы, чтобы оптимизировать кэширование NuGet-пакетов
COPY ["BinanceDataCollector.sln", "./"]
COPY ["src/BinanceDataCollector.Domain/BinanceDataCollector.Domain.csproj", "src/BinanceDataCollector.Domain/"]
COPY ["src/BinanceDataCollector.Application/BinanceDataCollector.Application.csproj", "src/BinanceDataCollector.Application/"]
COPY ["src/BinanceDataCollector.Infrastructure/BinanceDataCollector.Infrastructure.csproj", "src/BinanceDataCollector.Infrastructure/"]
COPY ["src/BinanceDataCollector.Worker/BinanceDataCollector.Worker.csproj", "src/BinanceDataCollector.Worker/"]
# Если у вас есть тестовые проекты, их .csproj тоже нужно скопировать
COPY ["src/BinanceDataCollector.Application.Tests/BinanceDataCollector.Application.Tests.csproj", "src/BinanceDataCollector.Application.Tests/"]
COPY ["src/BinanceDataCollector.Domain.Tests/BinanceDataCollector.Domain.Tests.csproj", "src/BinanceDataCollector.Domain.Tests/"]
COPY ["src/BinanceDataCollector.Infrastructure.Tests/BinanceDataCollector.Infrastructure.Tests.csproj", "src/BinanceDataCollector.Infrastructure.Tests/"]


# Восстанавливаем NuGet-пакеты
RUN dotnet restore "BinanceDataCollector.sln"

# Копируем весь остальной исходный код
COPY . .

# Публикуем приложение, создавая оптимизированную версию для запуска
RUN dotnet publish "src/BinanceDataCollector.Worker/BinanceDataCollector.Worker.csproj" -c Release -o /app/publish --no-restore


# --- Этап 2: Финальный образ (Final) ---
# Используем легкий образ .NET 8 Runtime на базе Alpine Linux.
FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine AS final
WORKDIR /app

# Копируем только опубликованные файлы из этапа 'build'
COPY --from=build /app/publish .

# Указываем команду, которая будет запущена при старте контейнера
ENTRYPOINT ["dotnet", "BinanceDataCollector.Worker.dll"]