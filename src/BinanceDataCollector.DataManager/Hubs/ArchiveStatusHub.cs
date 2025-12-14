using Microsoft.AspNetCore.SignalR;

namespace BinanceDataCollector.DataManager.Hubs;


public class ArchiveStatusHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    // Метод OnDisconnectedAsync для очистки
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
