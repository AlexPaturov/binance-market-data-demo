using BinanceDataCollector.Domain.Entities;

namespace BinanceDataCollector.Application.Archives.Models;

public class InspectArchiveContentResult
{
    public List<Trade> Trades { get; set; } = new();
    public int TotalCount { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);   
}