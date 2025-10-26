using BinanceDataCollector.Application.Interfaces;
using BinanceDataCollector.Domain.Events;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using System.Text.Json;

namespace BinanceDataCollector.Infrastructure.Messaging;

public class RabbitMQStatusNotifier : IStatusNotifier, IAsyncDisposable
{
    private const string ExchangeName = "status_updates_exchange";
    private readonly IConnection _connection;
    private readonly IChannel _channel;

    private RabbitMQStatusNotifier(IConnection connection, IChannel channel)
    {
        _connection = connection;
        _channel = channel;
    }

    public static async Task<RabbitMQStatusNotifier> CreateAsync(IConfiguration configuration)
    {
        var factory = new ConnectionFactory() 
        { 
            HostName = configuration["RabbitMQ:HostName"] 
        };
        var connection = await factory.CreateConnectionAsync();
        var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(exchange: ExchangeName, type: ExchangeType.Fanout);
        return new RabbitMQStatusNotifier(connection, channel);
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
