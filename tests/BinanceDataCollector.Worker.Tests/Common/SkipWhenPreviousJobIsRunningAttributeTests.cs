using BinanceDataCollector.Worker.Common;
using Hangfire;
using Hangfire.Client;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Moq;

namespace BinanceDataCollector.Worker.Tests.Common;

public class SkipWhenPreviousJobIsRunningAttributeTests
{
    private const string RecurringJobId = "ohlcv-aggregator";
    private const string HashKey = "recurring-job:ohlcv-aggregator";

    private readonly Mock<JobStorageConnection> _connection = new();
    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<IWriteOnlyTransaction> _transaction = new();
    private readonly SkipWhenPreviousJobIsRunningAttribute _filter = new();

    private static readonly Job SampleJob = Job.FromExpression(() => Console.WriteLine());

    [Fact]
    public void OnCreating_CancelsTheCopy_WhenPreviousRunIsStillRunning()
    {
        _connection.Setup(c => c.GetValueFromHash(HashKey, "Running")).Returns("yes");

        var context = CreatingContext(RecurringJobId);

        _filter.OnCreating(context);

        Assert.True(context.Canceled);
    }

    [Theory]
    [InlineData("no")]
    [InlineData(null)]   // джоба ещё ни разу не выполнялась — хеша нет
    public void OnCreating_LetsTheJobThrough_WhenNothingIsRunning(string? running)
    {
        _connection.Setup(c => c.GetValueFromHash(HashKey, "Running")).Returns(running!);

        var context = CreatingContext(RecurringJobId);

        _filter.OnCreating(context);

        Assert.False(context.Canceled);
    }

    [Fact]
    public void OnCreating_LetsTheJobThrough_WhenItIsNotRecurring()
    {
        // Разовая задача (запуск руками из DataManager) не имеет RecurringJobId
        // и накапливаться не может — её пропускаем, не заглядывая в хеш.
        var context = CreatingContext(recurringJobId: null);

        _filter.OnCreating(context);

        Assert.False(context.Canceled);
        _connection.Verify(c => c.GetValueFromHash(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OnStateApplied_RaisesTheFlag_WhenTheJobStartsProcessing()
    {
        // ProcessingState напрямую не создать — конструктор внутренний, поэтому состояние
        // подменяем заглушкой с тем же именем: фильтр сверяет именно имя.
        _filter.OnStateApplied(ApplyStateContext(State(ProcessingState.StateName)), _transaction.Object);

        VerifyFlagSetTo("yes");
    }

    [Fact]
    public void OnStateApplied_ClearsTheFlag_WhenTheJobSucceeds()
    {
        _filter.OnStateApplied(ApplyStateContext(new SucceededState(null, 0, 0)), _transaction.Object);

        VerifyFlagSetTo("no");
    }

    [Fact]
    public void OnStateApplied_ClearsTheFlag_WhenTheJobFails()
    {
        // Failed в Hangfire не финальное состояние (IsFinal == false), а повторы у агрегатора
        // отключены. Не сними мы флаг здесь — один упавший запуск заблокировал бы расписание навсегда.
        var failed = new FailedState(new InvalidOperationException("тест"));
        Assert.False(failed.IsFinal);   // фиксируем допущение, на котором держится фильтр

        _filter.OnStateApplied(ApplyStateContext(failed), _transaction.Object);

        VerifyFlagSetTo("no");
    }

    [Fact]
    public void OnStateApplied_ClearsTheFlag_WhenTheJobIsEnqueued()
    {
        // Задача стоит в очереди — значит, она не выполняется.
        _filter.OnStateApplied(ApplyStateContext(new EnqueuedState()), _transaction.Object);

        VerifyFlagSetTo("no");
    }

    [Fact]
    public void OnStateApplied_IgnoresJobsThatAreNotRecurring()
    {
        var context = new ApplyStateContext(
            _storage.Object,
            _connection.Object,
            _transaction.Object,
            new BackgroundJob("77", SampleJob, DateTime.UtcNow),
            new SucceededState(null, 0, 0),
            oldStateName: null);

        _filter.OnStateApplied(context, _transaction.Object);

        _transaction.Verify(
            t => t.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()),
            Times.Never);
    }

    private static IState State(string name)
    {
        var state = new Mock<IState>();
        state.SetupGet(s => s.Name).Returns(name);
        state.SetupGet(s => s.IsFinal).Returns(false);

        return state.Object;
    }

    private void VerifyFlagSetTo(string expected) =>
        _transaction.Verify(t => t.SetRangeInHash(
            HashKey,
            It.Is<IEnumerable<KeyValuePair<string, string>>>(pairs =>
                pairs.Single().Key == "Running" && pairs.Single().Value == expected)),
            Times.Once);

    private CreatingContext CreatingContext(string? recurringJobId)
    {
        var context = new CreateContext(
            _storage.Object, _connection.Object, SampleJob, new EnqueuedState());

        if (recurringJobId is not null)
            context.Parameters["RecurringJobId"] = recurringJobId;

        return new CreatingContext(context);
    }

    private ApplyStateContext ApplyStateContext(IState newState)
    {
        var backgroundJob = new BackgroundJob("42", SampleJob, DateTime.UtcNow);

        _connection
            .Setup(c => c.GetJobParameter("42", "RecurringJobId"))
            .Returns(SerializationHelper.Serialize(RecurringJobId));

        return new ApplyStateContext(
            _storage.Object,
            _connection.Object,
            _transaction.Object,
            backgroundJob,
            newState,
            oldStateName: null);
    }
}
