using Microsoft.AspNetCore.SignalR;

namespace BinanceDataCollector.DataManager.Hubs;


// Do I need to delete this class ?
public class ArchiveStatusHub : Hub
{
    // Клиенты будут подключаться к группе, соответствующей их Connection ID,
    // чтобы получать только свои сообщения.
    public async Task SubscribeToStatusUpdates()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, Context.ConnectionId);
    }
}
