# --- Этап 1: Сборка (Build) ---
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# --- УПРОЩЕННЫЙ ПОДХОД ---
# Шаг 1: Копируем АБСОЛЮТНО ВСЕ файлы проекта в контейнер
COPY . .

# Шаг 2: Восстанавливаем зависимости для всего решения
RUN dotnet restore "BinanceDataCollector.sln"

# Шаг 3: Публикуем наше рабочее приложение
RUN dotnet publish "BinanceDataCollector.Worker/BinanceDataCollector.Worker.csproj" -c Release -o /app/publish --no-restore


# --- Этап 2: Финальный образ (Final) ---
FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "BinanceDataCollector.Worker.dll"]