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
/// Работа не теряется: и агрегатор, и расчёт фич идут от статуса строк, а не от времени запуска.
///
/// Признак «идёт» хранится в хеше самой recurring-джобы (`recurring-job:{id}`, поле `Running`),
/// поэтому флаг виден всем серверам Hangfire, а не только тому, где задача выполняется.
/// Если процесс убить прямо во время выполнения, флаг останется поднятым до тех пор, пока
/// брошенную задачу не переподхватит watchdog Hangfire и она не придёт в конечное состояние.
/// </summary>
public sealed class SkipWhenPreviousJobIsRunningAttribute : JobFilterAttribute, IClientFilter, IApplyStateFilter
{
    private const string RunningField = "Running";
    private const string Yes = "yes";
    private const string No = "no";

    public void OnCreating(CreatingContext context)
    {
        // Хранилища без JobStorageConnection не умеют читать хеш — тогда просто пропускаем задачу.
        if (context.Connection is not JobStorageConnection connection)
            return;

        var recurringJobId = context.GetJobParameter<string>("RecurringJobId");
        if (string.IsNullOrWhiteSpace(recurringJobId))
            return;   // разовая задача, ставилась руками — накопления не будет

        var running = connection.GetValueFromHash(HashKey(recurringJobId), RunningField);

        if (string.Equals(running, Yes, StringComparison.OrdinalIgnoreCase))
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
        var running = context.NewState.Name == ProcessingState.StateName ? Yes : No;

        SetRunning(transaction, recurringJobId, running);
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
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
