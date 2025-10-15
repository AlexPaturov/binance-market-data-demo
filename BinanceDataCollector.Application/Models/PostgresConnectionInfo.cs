namespace BinanceDataCollector.Application.Models;

/// <summary>
/// Connection name 
/// </summary>
public class PostgresConnectionInfo
{
    public string DatabaseName { get; set; }
    public string UserName { get; set; }
    public string ApplicationName { get; set; }
    public string State { get; set; }
    public int ConnectionCount { get; set; }
}