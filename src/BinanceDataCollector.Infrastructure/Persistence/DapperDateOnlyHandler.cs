using System.Data;
using System.Runtime.CompilerServices;
using Dapper;

namespace BinanceDataCollector.Infrastructure.Persistence;

/// <summary>
/// Dapper этой версии не умеет `DateOnly` как параметр/результат. Хендлер маппит его на
/// PostgreSQL `date`. Регистрируется автоматически при загрузке сборки Infrastructure —
/// одинаково в приложении и в тестах, без ручного вызова в каждом хосте.
/// </summary>
public sealed class DapperDateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
{
    public override DateOnly Parse(object value) => DateOnly.FromDateTime((DateTime)value);

    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value = value.ToDateTime(TimeOnly.MinValue);
    }
}

internal static class DapperConfiguration
{
    [ModuleInitializer]
    internal static void Register() => SqlMapper.AddTypeHandler(new DapperDateOnlyHandler());
}
