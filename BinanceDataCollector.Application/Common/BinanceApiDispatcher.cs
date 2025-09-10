using Microsoft.Extensions.Logging;

namespace BinanceDataCollector.Application.Common;


/// <summary>
/// Singleton
/// Управление приоритетом доступа к api binance
/// </summary>
public class BinanceApiDispatcher
{
    // Один семафор, который представляет собой "разрешение" на выход в сеть.
    // Начинаем с 1 "разрешения".
    private readonly SemaphoreSlim _networkAccessSemaphore = new(1, 1);

    // Блокирующий примитив для синхронизации доступа к внутренней логике.
    private readonly object _lock = new();

    // Очереди для каждого приоритета.
    private readonly Queue<TaskCompletionSource<bool>>[] _waitQueues;

    private readonly ILogger<BinanceApiDispatcher> _logger;

    public BinanceApiDispatcher(ILogger<BinanceApiDispatcher> logger)
    {
        _logger = logger;
        _waitQueues = new Queue<TaskCompletionSource<bool>>[Enum.GetNames(typeof(ApiRequestPriority)).Length];
        for (int i = 0; i < _waitQueues.Length; i++)
        {
            _waitQueues[i] = new Queue<TaskCompletionSource<bool>>();
        }
    }

    public async Task<IDisposable> AquireAccessAsync(ApiRequestPriority priority, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> tcs;

        lock (_lock)
        {
            // Ставим задачу в очередь своего приоритета
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waitQueues[(int)priority].Enqueue(tcs);

            _logger.LogTrace("Запрос с приоритетом {Priority} поставлен в очередь.", priority);
        }

        // Пытаемся запустить обработку очереди.
        // Если кто-то уже обрабатывает, этот вызов ничего не сделает.
        TryProcessQueue();

        try
        {
            // Асинхронно ждем, пока наша задача будет выполнена
            await tcs.Task.WaitAsync(cancellationToken);

            _logger.LogTrace("Запрос с приоритетом {Priority} получил разрешение.", priority);

            // Возвращаем объект, который освободит семафор при своем Dispose()
            return new ApiAccessLease(_networkAccessSemaphore, this); // Передаем ссылку на себя
        }
        catch (OperationCanceledException)
        {
            // Этот блок сработает, если ожидание было прервано извне.
            _logger.LogWarning("Запрос с приоритетом {Priority} был ОТКЛОНЕН (отменен).", priority);

            // Важно! Нам нужно попытаться удалить этот tcs из очереди, чтобы она не засорялась "мертвыми" задачами.
            lock (_lock)
            {
                // Это сложная операция, самый простой способ - пересоздать очередь без этого элемента.
                var queue = _waitQueues[(int)priority];
                var newQueue = new Queue<TaskCompletionSource<bool>>(queue.Where(x => x != tcs));
                _waitQueues[(int)priority] = newQueue;
            }

            // Выбрасываем исключение дальше, чтобы вызывающий код тоже знал об отмене.
            throw;
            // ===================================
        }
    }

    private void TryProcessQueue()
    {
        // Неблокирующая попытка "захватить" семафор.
        // Если он свободен, значит, можно выдать разрешение следующему в очереди.
        if (_networkAccessSemaphore.Wait(0))
        {
            Task.Run(() => {
                lock (_lock)
                {
                    // Проходим по очередям от самого высокого приоритета к самому низкому
                    for (int i = 0; i < _waitQueues.Length; i++)
                    {
                        if (_waitQueues[i].Count > 0)
                        {
                            var tcs = _waitQueues[i].Dequeue();
                            // Выдаем "пропуск" задаче с самым высоким приоритетом
                            tcs.TrySetResult(true);
                            return; // Выходим, так как пропуск выдан
                        }
                    }

                    // Если все очереди пусты, просто освобождаем семафор
                    _networkAccessSemaphore.Release();
                }
            });
        }
    }

    // Приватный класс, который автоматически освобождает семафор
    private class ApiAccessLease : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly BinanceApiDispatcher _dispatcher;

        public ApiAccessLease(SemaphoreSlim semaphore, BinanceApiDispatcher dispatcher) 
        {
            _semaphore = semaphore;
            _dispatcher = dispatcher;
        } 

        public void Dispose()
        {
            _semaphore.Release();

            // После того, как мы "освободили пропуск", сразу же пытаемся выдать его следующему в очереди.
            _dispatcher.TryProcessQueue();
        }
    }
}
