# TODO изменить в тестах получение имени сервера для seq
# TODO сделать автозапуск тестов при деплое на прод
# TODO проследить ошибки на проде с  29-08-2025  07:02:00
# TODO 29-08-2025 - добавление onChain индикаторов 
# TODO на проде выполнить новый скрипт для sp_aggregate_trades_to_ohlcv() в папке watermark
# TODO на проде выполнить новый скрипт для sp_process_features() в папке watermark
# TODO 30-08-2025
# TODO ? на проде выполнить новый скрипт для sp_claim_new_ohlcv_for_features в папке watermark
# TODO + на проде выполнить скрипт с новой версией public.sp_aggregate_trades_to_ohlcv();
# TODO + на проде выполнить sp_find_trade_gaps
# TODO + на проде выполнить скрипт с новой версией public.sp_process_features();
# TODO Включаем OhlcvAggregatorWorker на dev - 
# TODO wsl --shutdown
# TODO - 
# TODO 17-09-2025 проверка целостности 
# TODO 18-09-2025 добавил в TradeRepository на метод GetTradeIdsInWindowAsync логирование времени выполнения - падает по таймауту
# TODO 18-09-2025 OhlcvAggregatorWorker.AggregateNextBatchAsync - куча задач в очереди
# TODO LISTAUSDT-trades-2025-09-14.zip - битый архив
# TODO 
# TODO 28-09-2025 - подключаю RabbitMQ
# TODO После docker-compose up -d у тебя по адресу http://localhost:15672 (логин/пароль: user/password) появится админка RabbitMQ
# TODO
# TODO

Сделать 21.11.2025
1. Security (Критично!): Настрой Cloudflare Zero Trust (Access) для доменов hangfire и seq. Не выставляй админки в интернет без защиты.
2. Cloudflare Config: Пропиши ingress правила в конфиге туннеля, чтобы раскидать поддомены по портам контейнеров (Seq:80, App:8080).
3. Code Architecture: Переведи Program.cs на HostApplicationBuilder.
4. High Load: Реализуй System.Threading.Channels для буферизации данных между WebSocket и БД.
5. Database: Используй NpgsqlBinaryImporter (Postgres COPY) или Bulk-библиотеки вместо обычного Add/SaveChanges.
6. Resilience: Добавь .AddStandardResilienceHandler() к HttpClient (Binance API).
7. Logging: Подключи Serilog → Seq. Убедись, что не логируешь API Secret Keys.
8. Docker Resource: Пропиши limits: memory: 512M (или сколько не жалко) в compose-файле, чтобы OOM Killer не положил весь сервер.

# TODO
# TODO
# TODO
# TODO
# TODO
# TODO
# TODO
# TODO
# TODO
# TODO
# TODO
# TODO
# TODO
# TODO
# TODO
# TODO
# TODO
# TODO
# TODO
# TODO
# TODO
# TODO
# TODO
# TODO
# TODO
# TODO
# TODO
# TODO