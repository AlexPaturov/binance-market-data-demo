
using BinanceDataCollector.DataManager.Hubs;
using BinanceDataCollector.Domain.Events;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text.Json;

namespace BinanceDataCollector.DataManager.Messaging;

public class RabbitMQListenerService : BackgroundService
{
    private readonly ILogger<RabbitMQListenerService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHubContext<ArchiveStatusHub> _hubContext;

    public RabbitMQListenerService(
        ILogger<RabbitMQListenerService> logger,
        IConfiguration configuration,
        IHubContext<ArchiveStatusHub> hubContext)
    {
        _logger = logger;
        _configuration = configuration;
        _hubContext = hubContext;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RabbitMQ Listener запускается.");

        var factory = new ConnectionFactory()
        {
            HostName = _configuration["RabbitMQ:HostName"],
            Port = Int32.Parse(_configuration["RabbitMQ:Port"]),
            UserName = _configuration["RabbitMQ:UserName"],
            Password = _configuration["RabbitMQ:Password"],
            AutomaticRecoveryEnabled = true, // Автоматическое восстановление
            NetworkRecoveryInterval = TimeSpan.FromSeconds(10)
        };

        try
        {
            using var connection = await factory.CreateConnectionAsync(stoppingToken);
            using var channel = await connection.CreateChannelAsync(options: null, stoppingToken);

            await channel.ExchangeDeclareAsync(exchange: "status_updates_exchange", type: ExchangeType.Fanout, cancellationToken: stoppingToken);

            // Создаем временную, не-эксклюзивную, авто-удаляемую очередь. Имя будет сгенерировано RabbitMQ.
            var queueName = (await channel.QueueDeclareAsync(
                queue: "", 
                durable: false, 
                exclusive: false, 
                autoDelete: true, 
                arguments: null, 
                cancellationToken: stoppingToken)
                ).QueueName;

            _logger.LogInformation("Создана временная очередь: {QueueName}", queueName);

            await channel.QueueBindAsync(queue: queueName,
                                       exchange: "status_updates_exchange",
                                       routingKey: "",
                                       cancellationToken: stoppingToken);

            var consumer = new AsyncEventingBasicConsumer(channel);

            consumer.ReceivedAsync += async (model, ea) => {
                var body = ea.Body.ToArray();
                try
                {
                    var statusEvent = JsonSerializer.Deserialize<StatusUpdateEvent>(body);
                    if (statusEvent?.ConnectionId != null)
                    {
                        _logger.LogDebug("Получено сообщение для ConnectionId {ConnectionId}", statusEvent.ConnectionId);
                        await _hubContext.Clients.Group(statusEvent.ConnectionId)
                            .SendAsync("ReceiveStatusUpdate", statusEvent.Message, stoppingToken);
                    }
                }
                catch (JsonException jsonEx)
                {
                    _logger.LogError(jsonEx, "Ошибка десериализации сообщения из RabbitMQ.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка при обработке сообщения из RabbitMQ.");
                }
            };

            await channel.BasicConsumeAsync(
                queue: queueName, 
                autoAck: true, 
                consumer: consumer, 
                cancellationToken: stoppingToken);

            // Держим сервис "живым", пока не придет команда на остановку
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Это нормальное исключение при остановке сервиса. Игнорируем.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "RabbitMQ Listener упал с критической ошибкой.");
        }

        _logger.LogInformation("RabbitMQ Listener останавливается.");
    }
}