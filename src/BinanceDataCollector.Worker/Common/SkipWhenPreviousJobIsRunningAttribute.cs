using Hangfire.Client;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;

namespace BinanceDataCollector.Worker.Common;

/// <summary>
/// Не ставит периодическую задачу в очередь, пока идёт её предыдущий запуск.
///
/// `DisableConcurrentExecution` не даёт двум запускам выполняться одновременно, но не мешает
/// им НАКАПЛИВАТЬСЯ: расписание ставит новую копию каждую минуту, она занимает воркера и висит
/// на распределённой блокировке. Если проход затянулся (агрегация разбирает хвост импорта),
/// очередь растёт линейно и выедает воркеров сервера — 13.07.2026 так и вышло.
///
/// Здесь копия просто не создаётся: расписание отработает снова через минуту, а если
/// предыдущий запуск к тому времени закончится — подхватит работу с того же места.
/// Работа не теряется: и агрегатор, и расчёт фич идут от состояния строк, а не от времени запуска.
///
/// Признак «идёт» — отметка времени старта в хеше recurring-джобы (`recurring-job:{id}`,
/// поле `Running`), видимая всем серверам Hangfire. Отметка ПРОТУХАЕТ через <see cref="Ttl"/>:
/// если воркер убит посреди выполнения (деплой), некому перевести джобу в конечное состояние
/// и снять флаг — без TTL расписание стояло бы до вмешательства сторожа Hangfire
/// (проверено на проде 13.07.2026: рестарт деплоем заморозил агрегацию). Протухший флаг
/// безопасен: настоящую одновременность страхует `DisableConcurrentExecution`.
/// </summary>
public sealed class SkipWhenPreviousJobIsRunningAttribute : JobFilterAttribute, IClientFilter, IApplyStateFilter
{
    private const string RunningField = "Running";
    private const string NotRunning = "0";

    /// <summary>
    /// Дольше этого один запуск честно идти не может: команда агрегации ограничена
    /// 600-секундным таймаутом, после которого джоба падает и флаг снимается штатно.
    /// Всё, что старше, — осиротевший флаг убитого процесса.
    /// </summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    public void OnCreating(CreatingContext context)
    {
        // Хранилища без JobStorageConnection не умеют читать хеш — тогда просто пропускаем задачу.
        if (context.Connection is not JobStorageConnection connection)
            return;

        var recurringJobId = context.GetJobParameter<string>("RecurringJobId");
        if (string.IsNullOrWhiteSpace(recurringJobId))
            return;   // разовая задача, ставилась руками — накопления не будет

        var running = connection.GetValueFromHash(HashKey(recurringJobId), RunningField);

        if (StartedRecently(running))
            context.Canceled = true;
    }

    public void OnCreated(CreatedContext context)
    {
    }

    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        var recurringJobId = GetRecurringJobId(context);
        if (string.IsNullOrWhiteSpace(recurringJobId))
            return;

        // Флаг поднят ровно пока задача выполняется. Любое другое состояние его снимает —
        // в том числе Failed: он в Hangfire НЕ финальный (`IsFinal == false`, задача считается
        // кандидатом на повтор), а у агрегатора повторы отключены. Проверяй мы `IsFinal`,
        // один упавший запуск навсегда заблокировал бы расписание.
        //
        // Состояние сверяем по имени: конструктор ProcessingState внутренний, по типу это
        // поведение не покрыть тестом.
        var running = context.NewState.Name == ProcessingState.StateName
            ? DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()
            : NotRunning;

        SetRunning(transaction, recurringJobId, running);
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
    }

    /// <summary>Свежая отметка старта — задача идёт. Протухшая, пустая или нечисловая — нет.</summary>
    private static bool StartedRecently(string? runningValue)
    {
        if (!long.TryParse(runningValue, out var startedAtUnix) || startedAtUnix <= 0)
            return false;

        var startedAt = DateTimeOffset.FromUnixTimeSeconds(startedAtUnix);

        return DateTimeOffset.UtcNow - startedAt < Ttl;
    }

    private static string? GetRecurringJobId(ApplyStateContext context)
    {
        var raw = context.Connection.GetJobParameter(context.BackgroundJob.Id, "RecurringJobId");

        return string.IsNullOrWhiteSpace(raw)
            ? null
            : SerializationHelper.Deserialize<string>(raw);
    }

    private static void SetRunning(IWriteOnlyTransaction transaction, string recurringJobId, string value) =>
        transaction.SetRangeInHash(
            HashKey(recurringJobId),
            new[] { new KeyValuePair<string, string>(RunningField, value) });

    private static string HashKey(string recurringJobId) => $"recurring-job:{recurringJobId}";
}
