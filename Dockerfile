# --- Этап 1: Сборка (Build) ---
# Используем образ SDK для компиляции
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Копируем АБСОЛЮТНО ВСЕ файлы проекта в контейнер.
# Это самый надежный, хоть и не самый быстрый способ.
COPY . .

# --- Публикуем НАШ КОНКРЕТНЫЙ ПРОЕКТ ---
# Команда 'publish' сама вызовет 'restore' только для нужных зависимостей.
# Мы больше не работаем с .sln файлом, а напрямую с .csproj.
RUN dotnet publish "BinanceDataCollector.Worker/BinanceDataCollector.Worker.csproj" -c Release -o /app/publish


# --- Этап 2: Финальный образ (Final) ---
# Используем легкий образ Runtime для запуска
FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine AS final
WORKDIR /app

# Копируем только скомпилированное приложение из этапа 'build'
COPY --from=build /app/publish .

# Указываем точку входа
ENTRYPOINT ["dotnet", "BinanceDataCollector.Worker.dll"]