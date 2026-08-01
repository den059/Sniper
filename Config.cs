// ### START Config.cs ###


using System.Collections.Generic;

public class BotConfig
{
    public List<string> Symbols { get; set; } = new() { "XRPUSDT", "DOGEUSDT", "SUIUSDT", "XLMUSDT", "ALGOUSDT" };
    public string Timeframe { get; set; } = "ThirtyMinutes"; // Настройка таймфрейма текстом
    public bool AutoApproveMode { get; set; } = true;
    public int BBPeriod { get; set; } = 20;
    public decimal BBDeviation { get; set; } = 2.2m;
    public int RSIPeriod { get; set; } = 14;
    public int TargetLeverage { get; set; } = 3;
    public decimal StopLossPercent { get; set; } = 0.0120m;
    public decimal TakeProfit1Percent { get; set; } = 0.0100m;
    public decimal TakeProfitPercent { get; set; } = 0.0200m;
    public decimal RiskPercent { get; set; } = 5.0m;
    public decimal RsiLongThreshold { get; set; } = 28m;
    public decimal RsiShortThreshold { get; set; } = 72m;
    public decimal MaxBandWidthForSqueeze { get; set; } = 0.050m;
}



// ### END Config.cs ###