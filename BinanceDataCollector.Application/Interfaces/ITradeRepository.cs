using BinanceDataCollector.Domain.Entities;
using System.Diagnostics;

namespace BinanceDataCollector.Application.Interfaces;

public interface ITradeRepository
{
    Task<Trade?> GetByIdAsync(long tradeId, string symbol);
    Task<IEnumerable<Trade>> GetLatestTradesAsync(string symbol, int count);

    // Самый важный метод для быстрой вставки множества записей
    Task BulkInsertAsync(IEnumerable<Trade> trades);}
