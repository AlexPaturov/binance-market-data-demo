protected override IHost CreateHost(IHostBuilder builder)
{
    builder.ConfigureServices(services =>
    {
        // Удаляем настоящую регистрацию
        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IBinanceService));
        if (descriptor != null) services.Remove(descriptor);

        // Добавляем наш мок
        var mockBinanceService = new Mock<IBinanceService>();
        
        // Настраиваем мок, чтобы он возвращал "недостающую" сделку
        var missingTrade = new Trade { TradeId = 1002, /* ... */ };
        var fetchResult = FetchResult.SuccessResult(new List<Trade> { missingTrade });
        
        mockBinanceService
            .Setup(s => s.GetHistoricalRawTradesAsync("BTCUSDT", 1002, It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(fetchResult);
            
        // Возвращаем пустой список для всех других запросов, чтобы цикл остановился
        mockBinanceService
            .Setup(s => s.GetHistoricalRawTradesAsync("BTCUSDT", It.IsNotIn(1002L), ...))
            .ReturnsAsync(FetchResult.SuccessResult(new List<Trade>()));

        services.AddScoped<IBinanceService>(_ => mockBinanceService.Object);
    });
    
    return base.CreateHost(builder);
}

