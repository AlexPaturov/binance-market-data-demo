using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Events;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using System.Text.Json;

namespace BinanceDataCollector.Infrastructure.Messaging;

public class RabbitMqStatusNotifier : IStatusNotifier, IAsyncDisposable
{
    private const string ExchangeName = "status_updates_exchange";
    private readonly IConnection _connection;
    private readonly IChannel _channel;

    private RabbitMqStatusNotifier(IConnection connection, IChannel channel)
    {
        _connection = connection;
        _channel = channel;
    }

    // TODO: [Refactor] переделать на polly CreateConnectionAsync() + политику на случай обрыва связи
    // См. Issue #1
    public static async Task<RabbitMqStatusNotifier> CreateAsync(IConfiguration configuration)
    {
        var factory = new ConnectionFactory() 
        { 
            HostName = configuration["RabbitMQ:HostName"],
            UserName = configuration["RabbitMQ:UserName"],
            Password = configuration["RabbitMQ:Password"],
            Port = int.Parse(configuration["RabbitMQ:Port"] ?? "5672")
        };
        var connection = await factory.CreateConnectionAsync();
        var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(exchange: ExchangeName, type: ExchangeType.Fanout);
        return new RabbitMqStatusNotifier(connection, channel);
    }

    public async Task SendStatusUpdateAsync(string connectionId, string message)
    {
        var statusEvent = new StatusUpdateEvent { ConnectionId = connectionId, Message = message };
        var body = JsonSerializer.SerializeToUtf8Bytes(statusEvent);
        await _channel.BasicPublishAsync(exchange: ExchangeName, routingKey: "", body: body);
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel != null) await _channel.CloseAsync();
        if (_connection != null) await _connection.CloseAsync();
    }

}
