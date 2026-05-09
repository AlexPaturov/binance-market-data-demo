SELECT top(10000) [TradeId]
      ,[Symbol]
      ,[Price]
      ,[Quantity]
      ,[QuoteQuantity]
	  ,DATEADD(ms, [TradeTime] % 1000, DATEADD(second, [TradeTime] / 1000, '1970-01-01')) AS TradeDateTime_UTC
	  ,TODATETIMEOFFSET(DATEADD(ms, [TradeTime] % 1000, DATEADD(second, [TradeTime] / 1000, '1970-01-01')), '+00:00') AS TradeDateTime_Offset
      ,[TradeTime]
      ,[IsBuyerMaker]
      ,[IsBestMatch]
      ,[OrderId]
      ,[Commission]
      ,[CommissionAsset]
      ,[IsMyTrade]
  FROM [MarketAnalytics].[dbo].[Trades]
 order by 
	[TradeTime] DESC
