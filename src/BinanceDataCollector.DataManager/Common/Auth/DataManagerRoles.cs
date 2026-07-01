namespace BinanceDataCollector.DataManager.Common.Auth;

public static class DataManagerRoles
{
    public const string Viewer = "Viewer";
    public const string Operator = "Operator";
    public const string Admin = "Admin";

    public static readonly string[] All = [Viewer, Operator, Admin];
}
