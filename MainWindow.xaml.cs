
// ### START MainWindow.xaml.cs ### 

using Bybit.Net;
using Bybit.Net.Clients;
using Bybit.Net.Enums;
using Bybit.Net.Objects.Models.V5;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Xml.Linq;
using Telegram.Bot;
using Telegram.Bot.Extensions;
using Telegram.Bot.Types.Enums;
using static System.Net.Mime.MediaTypeNames;
using CryptoExchange.Net.Authentication;

namespace Sniper
{
    public partial class MainWindow : Window
    {
        // === КОНФИГУРАЦИЯ ЧЕРЕЗ ВНЕШНИЙ ФАЙЛ ===
        private BotConfig _config = new();
        private const string ConfigFilePath = "config.json";

        // 🔸 ПРОМПТ №1: МУЛЬТИМОНЕТНОСТЬ + ИНДИКАТОРЫ
        // ========================================
        // 1. БЛОК ДИНАМИЧЕСКИХ НАСТРОЕК СТРАТЕГИИ
        // =========================================
        private int currentSymbolIndex = 0;
        // Замени старую строку private KlineInterval Timeframe ... на этот парсер:
        private KlineInterval Timeframe
        {
            get
            {
                if (Enum.TryParse<Bybit.Net.Enums.KlineInterval>(_config.Timeframe, out var parsedInterval))
                    return parsedInterval;
                return Bybit.Net.Enums.KlineInterval.ThirtyMinutes; // Дефолт, если в json ошибка
            }
        }


        private string openPositionSymbol = "";
        // Виртуальный трекер позиции
        private bool isPositionOpen = false;
        private OrderSide currentPositionSide;
        private decimal entryPrice = 0m;
        private decimal initialQuantity = 0m;
        private decimal currentRemainingQuantity = 0m;
        private bool isTp1Executed = false;
        private decimal trailingStopPrice = 0m;
        // Клиент и цикл
        private BybitRestClient _restClient;
        private bool _isLoopRunning = false;
        // Статистика
        private int DailySignalsCount = 0;
        private int DailyTradesOpened = 0;
        private int DailyTradesClosedProfit = 0;
        private int DailyTradesClosedStop = 0;
        private int lastSentReportHour = -1;
        private DateTime lastOptimizerRunTime = DateTime.Now;


        public MainWindow()
        {
            InitializeComponent();
            LoadConfig(); // Конфигурация загружается первой, заполняя _config

            _restClient = new BybitRestClient(options =>
            {
                options.ApiCredentials = new BybitCredentials(
                    _config.BybitApiKey,
                    _config.BybitApiSecret
                );
                options.RequestTimeout = TimeSpan.FromSeconds(60);
                options.Environment = BybitEnvironment.Live;
            });

            InitBot();
        }


        private void InitBot()
        {
            LogToUI($"[INIT] Система готова. Стратегия: Sniper(КОНТР-ТРЕНД) [{_config.Timeframe}]. Нажмите кнопку 'Запустить'.");
            // Бэктест оставляем на автозапуске, он не мешает реальной торговле
            _ = Task.Run(() => RunBacktestAsync());
        }


        private void LoadConfig()
        {
            if (!File.Exists(ConfigFilePath))
            {
                SaveConfig();
                LogToUI("[CONFIG] Файл config.json создан.");
                return;
            }

            try
            {
                string json = File.ReadAllText(ConfigFilePath);
                var loaded = JsonSerializer.Deserialize<BotConfig>(json);
                _config = loaded ?? new BotConfig();
                LogToUI("[CONFIG] Настройки загружены из config.json");
            }
            catch (Exception ex)
            {
                _config = new BotConfig();
                LogToUI($"[CONFIG ERROR] {ex.Message}. Используются значения по умолчанию.");
            }
        }

        private void SaveConfig()
        {
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(ConfigFilePath, JsonSerializer.Serialize(_config, opts));
            }
            catch (Exception ex)
            {
                LogToUI($"[CONFIG SAVE ERROR] {ex.Message}");
            }
        }

        private void LogToUI(string message)
        {
            bool isSignal = message.Contains("[СИГНАЛ]") || message.Contains("ЛОНГ") || message.Contains("ШОРТ");
            if (isSignal) System.Media.SystemSounds.Exclamation.Play();
            Dispatcher.Invoke(() =>
            {
                string formattedMessage = message;
                if (message.Contains("ЛОНГ"))
                    formattedMessage = $"🟩🟩🟩 {DateTime.Now:HH:mm:ss} {message} 🟩🟩🟩";
                else if (message.Contains("ШОРТ"))
                    formattedMessage = $"🟥🟥🟥 {DateTime.Now:HH:mm:ss} {message} 🟥🟥🟥";
                else
                    formattedMessage = $"[{DateTime.Now:HH:mm:ss}] {message}";
                LogTextBox.AppendText(formattedMessage + Environment.NewLine);
                LogTextBox.ScrollToEnd();
            });
        }



        private async Task StartMonitoringLoopAsync()
        {
            LogToUI("[LOOP] Цикл запущен. Интервал: ~90 сек (+ jitter).");
            var random = new Random();
            while (_isLoopRunning)
            {
                try
                {
                    await ExecuteStrategyAsync();
                    await CheckAndSendScheduledReportAsync();
                }
                catch (Exception ex)
                {
                    LogToUI($"[CRITICAL] {ex.Message}");
                }
                int delay = 90 + random.Next(0, 6);
                LogToUI($"[SLEEP] Следующая проверка через {delay} сек.");
                await Task.Delay(TimeSpan.FromSeconds(delay));
            }
        }

        private async Task CheckAndSendScheduledReportAsync()
        {
            int currentHour = DateTime.Now.Hour;
            if ((currentHour == 9 || currentHour == 15 || currentHour == 21) && currentHour != lastSentReportHour)
            {
                lastSentReportHour = currentHour;
                await SendEnterpriseTelegramReportAsync();
            }
        }

        private async Task ExecuteStrategyAsync()
        {
            string currentSymbol = isPositionOpen ? openPositionSymbol : _config.Symbols[currentSymbolIndex];
            if (string.IsNullOrEmpty(currentSymbol)) { if (!isPositionOpen) MoveToNextSymbol(); return; }

            LogToUI($"[MONITORING] Сканирование: {currentSymbol}");
            List<BybitKline> candles = null;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                var klinesResult = await _restClient.V5Api.ExchangeData.GetKlinesAsync(Category.Linear, currentSymbol, Timeframe, limit: 60);
                if (klinesResult.Success && klinesResult.Data?.List != null)
                {
                    candles = klinesResult.Data.List.OrderBy(c => c.StartTime).ToList();
                    break;
                }
                await Task.Delay(2000);
            }

            if (candles == null || candles.Count < 30) { if (!isPositionOpen) MoveToNextSymbol(); return; }

            List<decimal> closePrices = candles.Select(c => c.ClosePrice).ToList();
            var bbResults = CalculateAllBollingerBands(closePrices, _config.BBPeriod, _config.BBDeviation);
            var rsiResults = CalculateAllRSI(closePrices, _config.RSIPeriod);

            if (rsiResults.Count < 3 || bbResults.Count < 3) { if (!isPositionOpen) MoveToNextSymbol(); return; }

            decimal currentRealtimePrice = closePrices.Last();
            var closedCandle = candles[candles.Count - 2];
            decimal closedBarPrice = closePrices[closePrices.Count - 2];
            decimal closedBarRsi = rsiResults[rsiResults.Count - 2];
            var closedBarBB = bbResults[bbResults.Count - 2];
            decimal bbMiddle = closedBarBB.Middle;

            if (isPositionOpen)
            {
                await MonitorOpenPositionAsync(currentRealtimePrice, bbMiddle, currentSymbol);
                if (!isPositionOpen) { openPositionSymbol = ""; MoveToNextSymbol(); }
                return;
            }

            OrderSide? signal = null;
            string filterLog = "";

            // НАЧАЛО Sniper (КОНТР-ТРЕНДОВОЙ) ЛОГИКИ
            bool lowPierced = closedCandle.LowPrice < closedBarBB.Lower;
            bool highPierced = closedCandle.HighPrice > closedBarBB.Upper;
            bool inside = (closedBarPrice > closedBarBB.Lower && closedBarPrice < closedBarBB.Upper);

            if (lowPierced && inside && closedBarRsi <= _config.RsiLongThreshold)
            {
                signal = OrderSide.Buy;
                filterLog = $"Ложный пробой нижней ленты BB + RSI({closedBarRsi:F2}) <= {_config.RsiLongThreshold}";
            }
            else if (highPierced && inside && closedBarRsi >= _config.RsiShortThreshold)
            {
                signal = OrderSide.Sell;
                filterLog = $"Ложный пробой верхней ленты BB + RSI({closedBarRsi:F2}) >= {_config.RsiShortThreshold}";
            }

            if (signal != null)
            {
                string txt = signal == OrderSide.Buy ? "ЛОНГ" : "ШОРТ";
                LogToUI($"🚨 [СИГНАЛ] [{currentSymbol}] [{txt}] | RSI: {closedBarRsi:F2} | {filterLog}");
                DailySignalsCount++;

                if (await ConfirmSignalAsync(txt, currentSymbol, closedBarPrice, closedBarRsi))
                {
                    decimal balance = await GetUSDTBalanceAsync();
                    if (balance <= 0) { MoveToNextSymbol(); return; }
                    decimal targetPositionSizeInUsdt = 4.0m;
                    decimal quantity = targetPositionSizeInUsdt / closedBarPrice;
                    int qtyDecimals = (currentSymbol.Contains("DOGE") || currentSymbol.Contains("XRP") || currentSymbol.Contains("TRX") || currentSymbol.Contains("XLM")) ? 0 : 1;
                    quantity = Math.Round(quantity, qtyDecimals);
                    if (quantity <= 0) quantity = 1.0m;

                    LogToUI($"[COMPOUND] Фиксированный вход на {targetPositionSizeInUsdt} USDT. Лот = {quantity}");
                    await TryOpenPositionAsync(signal.Value, closedBarPrice, currentSymbol, quantity);
                }
            }
            MoveToNextSymbol();
        }


        private void MoveToNextSymbol()
        {
            if (!isPositionOpen)
            {
                currentSymbolIndex = (currentSymbolIndex + 1) % _config.Symbols.Count;
            }
        }

        private async Task<bool> ConfirmSignalAsync(string signalText, string symbol, decimal price, decimal rsi)
        {
            if (_config.AutoApproveMode) return true;
            var result = await Task.Run(() =>
            {
                var msgResult = MessageBox.Show(
                    $"Сигнал [{signalText}] по {symbol}\nЦена: {price:F4}\nRSI: {rsi:F2}\nПлечо: {_config.TargetLeverage}x\nОткрыть позицию?",
                    "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                return msgResult == MessageBoxResult.Yes;
            });
            return result;
        }

        private async Task<decimal> GetUSDTBalanceAsync()
        {
            var balanceResult = await _restClient.V5Api.Account.GetBalancesAsync(AccountType.Unified, asset: "USDT");
            if (!balanceResult.Success || balanceResult.Data?.List == null) return 0m;
            var usdtAsset = balanceResult.Data.List
                .SelectMany(x => x.Assets)
                .FirstOrDefault(a => a.Asset == "USDT");
            return usdtAsset?.WalletBalance ?? 0m;
        }

        private async Task MonitorOpenPositionAsync(decimal currentPrice, decimal bbMiddle, string symbol)
        {
            if (!isPositionOpen || string.IsNullOrEmpty(symbol) || symbol != openPositionSymbol) return;

            OrderSide currentSide = currentPositionSide;
            decimal slLevel = currentSide == OrderSide.Buy ? entryPrice * (1m - _config.StopLossPercent) : entryPrice * (1m + _config.StopLossPercent);
            decimal tp1Level = currentSide == OrderSide.Buy ? entryPrice * (1m + _config.TakeProfit1Percent) : entryPrice * (1m - _config.TakeProfit1Percent);
            decimal tp2Level = currentSide == OrderSide.Buy ? entryPrice * (1m + _config.TakeProfitPercent) : entryPrice * (1m - _config.TakeProfitPercent);


            LogToUI($"[MONITOR] Позиция {symbol} ({currentSide}): Вход {entryPrice:F4}, SL {slLevel:F4}, TP1 {tp1Level:F4}, TP2 {tp2Level:F4}, Текущая {currentPrice:F4}");

            if (!isTp1Executed)
            {
                bool isTp1Hit = (currentSide == OrderSide.Buy && currentPrice >= tp1Level) ||
                                (currentSide == OrderSide.Sell && currentPrice <= tp1Level);
                if (isTp1Hit)
                {
                    LogToUI($"[TP1 HIT] Уровень TP1 ({tp1Level:F4}) достигнут для {symbol}.");
                    isTp1Executed = true;
                    decimal newSlLevel = currentSide == OrderSide.Buy ? entryPrice * 1.001m : entryPrice * 0.999m;
                    trailingStopPrice = newSlLevel;
                    LogToUI($"[SL ADJUST] SL виртуально сдвинут в безубыток (~{newSlLevel:F4}) после TP1.");
                }
            }

            decimal effectiveSlLevel = isTp1Executed ? trailingStopPrice : slLevel;
            bool isSlHit = (currentSide == OrderSide.Buy && currentPrice <= effectiveSlLevel) ||
                           (currentSide == OrderSide.Sell && currentPrice >= effectiveSlLevel);
            bool isTp2Hit = (currentSide == OrderSide.Buy && currentPrice >= tp2Level) ||
                            (currentSide == OrderSide.Sell && currentPrice <= tp2Level);

            if (isTp2Hit)
            {
                LogToUI($"[TAKE PROFIT] Позиция по {symbol} закрыта по TP2 на уровне ~{currentPrice:F4}.");
                DailyTradesClosedProfit++;
                CloseVirtualPosition();
            }
            else if (isSlHit)
            {
                LogToUI($"[STOP LOSS] Позиция по {symbol} закрыта по SL на уровне ~{currentPrice:F4}.");
                DailyTradesClosedStop++;
                CloseVirtualPosition();
            }
        }

        private void CloseVirtualPosition()
        {
            isPositionOpen = false;
            entryPrice = 0m;
            initialQuantity = 0m;
            currentRemainingQuantity = 0m;
            isTp1Executed = false;
            trailingStopPrice = 0m;
            openPositionSymbol = "";
        }

        private async Task TryOpenPositionAsync(OrderSide side, decimal currentPrice, string symbol, decimal quantity)
        {
            try
            {
                decimal sl = side == OrderSide.Buy ? currentPrice * (1m - _config.StopLossPercent) : currentPrice * (1m + _config.StopLossPercent);
                decimal tp = side == OrderSide.Buy ? currentPrice * (1m + _config.TakeProfitPercent) : currentPrice * (1m - _config.TakeProfitPercent);


                int priceDecimals = (symbol.Contains("SOL") || symbol.Contains("AVAX") || symbol.Contains("LINK") || symbol.Contains("NEAR")) ? 2 : 4;
                int qtyDecimals = (symbol.Contains("DOGE") || symbol.Contains("XRP") || symbol.Contains("ADA") ||
                                   symbol.Contains("TRX") || symbol.Contains("XLM") || symbol.Contains("POL")) ? 0 : 1;

                decimal roundedQuantity = Math.Round(quantity, qtyDecimals);
                decimal roundedTp = Math.Round(tp, priceDecimals);
                decimal roundedSl = Math.Round(sl, priceDecimals);

                var leverageResult = await _restClient.V5Api.Account.SetLeverageAsync(Category.Linear, symbol, _config.TargetLeverage, _config.TargetLeverage);
                if (leverageResult.Success) LogToUI($"[LEVERAGE] Плечо {_config.TargetLeverage}x установлено для {symbol}");

                LogToUI($"[ORDER] Отправка MARKET ордера {side} по {symbol}. Лот: {roundedQuantity}");

                var orderResult = await _restClient.V5Api.Trading.PlaceOrderAsync(
                    category: Category.Linear,
                    symbol: symbol,
                    side: side,
                    type: NewOrderType.Market,
                    quantity: roundedQuantity,
                    price: null,
                    takeProfit: roundedTp,
                    stopLoss: roundedSl,
                    positionIdx: null
                );

                if (orderResult.Success)
                {
                    LogToUI($"[SUCCESS] 🚀 Ордер размещен! ID: {orderResult.Data.OrderId}");
                    isPositionOpen = true;
                    openPositionSymbol = symbol;
                    currentPositionSide = side;
                    entryPrice = currentPrice;
                    initialQuantity = roundedQuantity;
                    currentRemainingQuantity = roundedQuantity;
                    DailyTradesOpened++;
                    LogToUI($"[TRACKER] Виртуальный менеджер начал сопровождение сделки по {symbol}.");
                }
                else
                {
                    LogToUI($"[API ERROR] ❌ Bybit отклонил ордер! Код: {orderResult.Error?.Code}. Причина: {orderResult.Error?.Message}");
                }
            }
            catch (Exception ex)
            {
                LogToUI($"[CRITICAL ERROR] Исключение при отправке ордера: {ex.Message}");
            }
        }

        private async Task RunBacktestAsync()
        {
            LogToUI($"📊 [BACKTEST] Тест Sniper (Контр-Тренда) за 7 дней ({_config.Timeframe}, RSI L={_config.RsiLongThreshold}, RSI S={_config.RsiShortThreshold}, SL={_config.StopLossPercent * 100m}%, TP1={_config.TakeProfit1Percent * 100m}%, TP2={_config.TakeProfitPercent * 100m}%)");

            int totalSignals = 0; int totalWins = 0; int totalLosses = 0; int totalTimeouts = 0; int totalCoins = 0;

            foreach (string symbol in _config.Symbols)
            {
                LogToUI($"[BACKTEST] Скачивание истории для {symbol}...");
                await Task.Delay(300);
                var klinesResult = await _restClient.V5Api.ExchangeData.GetKlinesAsync(Category.Linear, symbol, Timeframe, limit: 700);

                if (!klinesResult.Success || klinesResult.Data?.List == null)
                {
                    LogToUI($"[BACKTEST ERROR] Пропуск {symbol} из-за ошибки загрузки");
                    continue;
                }

                var candles = klinesResult.Data.List.OrderBy(c => c.StartTime).ToList();
                if (candles.Count < 100) continue;

                List<decimal> closePrices = candles.Select(c => c.ClosePrice).ToList();
                var bbResults = CalculateAllBollingerBands(closePrices, _config.BBPeriod, _config.BBDeviation);
                var rsiResults = CalculateAllRSI(closePrices, _config.RSIPeriod);

                int coinSignals = 0; int coinWins = 0; int coinLosses = 0; int coinTimeouts = 0;
                int allowedIndex = 50;

                for (int i = 50; i < candles.Count - 16; i++)
                {
                    if (i < allowedIndex) continue;

                    var currentCandle = candles[i];
                    decimal closedPrice = closePrices[i];
                    var closedBB = bbResults[i];
                    decimal closedRSI = rsiResults[i];

                    bool isSignal = false; bool isLong = false;

                    bool lowPierced = currentCandle.LowPrice < closedBB.Lower;
                    bool highPierced = currentCandle.HighPrice > closedBB.Upper;
                    bool inside = (closedPrice > closedBB.Lower && closedPrice < closedBB.Upper);

                    if (lowPierced && inside && closedRSI <= _config.RsiLongThreshold) { isSignal = true; isLong = true; }
                    else if (highPierced && inside && closedRSI >= _config.RsiShortThreshold) { isSignal = true; isLong = false; }

                    if (isSignal)
                    {
                        coinSignals++; totalSignals++;
                        decimal entry = closedPrice;
                        decimal sl = isLong ? entry * (1m - _config.StopLossPercent) : entry * (1m + _config.StopLossPercent);
                        decimal tp1 = isLong ? entry * (1m + _config.TakeProfit1Percent) : entry * (1m - _config.TakeProfit1Percent);
                        decimal tp2 = isLong ? entry * (1m + _config.TakeProfitPercent) : entry * (1m - _config.TakeProfitPercent);

                        bool hitProfit = false; bool hitLoss = false; bool hitTimeout = false; bool tp1Executed = false;
                        int executionLength = 15;

                        for (int j = 1; j <= 15 && i + j < candles.Count; j++)
                        {
                            var fCandle = candles[i + j];
                            if (isLong)
                            {
                                if (fCandle.LowPrice <= sl) { hitLoss = true; executionLength = j; break; }
                                if (!tp1Executed && fCandle.HighPrice >= tp1) { tp1Executed = true; sl = entry; }
                                if (tp1Executed && fCandle.HighPrice >= tp2) { hitProfit = true; executionLength = j; break; }
                            }
                            else
                            {
                                if (fCandle.HighPrice >= sl) { hitLoss = true; executionLength = j; break; }
                                if (!tp1Executed && fCandle.LowPrice <= tp1) { tp1Executed = true; sl = entry; }
                                if (tp1Executed && fCandle.LowPrice <= tp2) { hitProfit = true; executionLength = j; break; }
                            }
                            if (j == 12 && !hitProfit && !hitLoss) { hitTimeout = true; executionLength = 12; break; }
                        }

                        allowedIndex = i + executionLength + 1;
                        if (hitLoss) { coinLosses++; totalLosses++; }
                        else if (hitProfit) { coinWins++; totalWins++; }
                        else if (hitTimeout) { coinTimeouts++; totalTimeouts++; }
                    }
                }

                if (coinSignals > 0)
                {
                    decimal coinWinRate = (coinWins * 100m / coinSignals);
                    LogToUI($"📈 {symbol}: сигналов = {coinSignals}, Побед = {coinWins}, Поражений = {coinLosses}, WinRate = {coinWinRate:F1}%");
                }
                totalCoins++;
            }

            decimal overallWinRate = totalSignals > 0 ? (totalWins * 100m / totalSignals) : 0m;
            LogToUI("==================================================");
            LogToUI($"🤖 [Sniper(КОНТР-ТРЕНД) БЭКТЕСТ ЗАВЕРШЕН]");
            LogToUI($"✅ Общее число сигналов: {totalSignals} | Успешные (TP2): {totalWins} | Провальные (SL): {totalLosses} | Таймауты: {totalTimeouts}");
            LogToUI($"🔥 Истинный WinRate: {overallWinRate:F1}%");
            LogToUI("==================================================");
        }


        private async Task SendEnterpriseTelegramReportAsync()
        {
            try
            {
                decimal balance = await GetUSDTBalanceAsync();
                // Найди строку: string modeText = _config.BybitBot_Mode ? ...
                // Замени её на:
                string modeText = $"Sniper(КОНТР-ТРЕНД) [{_config.Timeframe}]";


                string EscapeMarkdownV2(string text)
                {
                    if (string.IsNullOrEmpty(text)) return "";
                    char[] escapeChars = { '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!' };
                    var sb = new System.Text.StringBuilder();
                    foreach (char c in text)
                    {
                        if (Array.IndexOf(escapeChars, c) >= 0)
                            sb.Append('\\');
                        sb.Append(c);
                    }
                    return sb.ToString();
                }

                string escapedTime = EscapeMarkdownV2(DateTime.Now.ToString("HH:mm"));
                string escapedBalance = EscapeMarkdownV2(balance.ToString("F2"));
                string escapedSignals = EscapeMarkdownV2(DailySignalsCount.ToString());
                string escapedTrades = EscapeMarkdownV2(DailyTradesOpened.ToString());
                string escapedProfit = EscapeMarkdownV2(DailyTradesClosedProfit.ToString());
                string escapedStop = EscapeMarkdownV2(DailyTradesClosedStop.ToString());
                string escapedMode = EscapeMarkdownV2(modeText);
                string escapedSymbolsCount = EscapeMarkdownV2(_config.Symbols.Count.ToString());

                string message = $"🤖 *[SNIPER КОНТР-ТРЕНД ENTERPRISE — ОТЧЕТ]* 📈\n" +
                                 $"⏰ Время среза: {escapedTime}\n" +
                                 $"💰 *ФИНАНСЫ:*\n" +
                                 $"▪️ Текущий баланс ETA: {escapedBalance} USDT\n" +
                                 $"🎯 *СТАТИСТИКА СУТОК:*\n" +
                                 $"▪️ Найдено сигналов: {escapedSignals}\n" +
                                 $"▪️ Открыто сделок: {escapedTrades}\n" +
                                 $"✅ Закрыто по TP2: {escapedProfit}\n" +
                                 $"❌ Выбито по Стопам: {escapedStop}\n" +
                                 $"⚙️ *НАСТРОЙКИ СИСТЕМЫ:*\n" +
                                 $"▪️ Режим: {escapedMode}\n" +
                                 $"▪️ Активных монет в пуле: {escapedSymbolsCount}";

                //

                var botClient = new TelegramBotClient(_config.TelegramBotToken.Trim());
                await botClient.SendMessage(
                    chatId: _config.TelegramChatId.Trim(),
                    text: message,
                    parseMode: ParseMode.MarkdownV2
                );

                //

                LogToUI("[TELEGRAM] Периодический отчет успешно доставлен в канал!");
            }
            catch (Exception ex)
            {
                LogToUI($"❌ [TELEGRAM ERROR] Сбой отправки отчета: {ex.Message}");
            }
        }

        protected override void OnClosed(EventArgs e) { _isLoopRunning = false; base.OnClosed(e); }

        // === ИНДИКАТОРЫ ===
        private List<(decimal Upper, decimal Middle, decimal Lower, decimal BandWidth)> CalculateAllBollingerBands(List<decimal> prices, int period, decimal deviation)
        {
            var results = new List<(decimal Upper, decimal Middle, decimal Lower, decimal BandWidth)>();
            for (int i = 0; i < prices.Count; i++)
            {
                if (i < period - 1) { results.Add((0, 0, 0, 0)); continue; }
                var window = prices.Skip(i - period + 1).Take(period).ToList();
                decimal sma = window.Average();
                double sumOfSquares = window.Select(p => Math.Pow((double)(p - sma), 2)).Sum();
                decimal standardDeviation = (decimal)Math.Sqrt(sumOfSquares / period);
                decimal upper = sma + (deviation * standardDeviation);
                decimal lower = sma - (deviation * standardDeviation);
                decimal bandWidth = sma != 0 ? (upper - lower) / sma : 0m;
                results.Add((upper, sma, lower, bandWidth));
            }
            return results;
        }

        private List<decimal> CalculateAllRSI(List<decimal> prices, int period)
        {
            var rsiValues = new List<decimal>();
            if (prices.Count == 0) return rsiValues;
            decimal[] gains = new decimal[prices.Count];
            decimal[] losses = new decimal[prices.Count];
            for (int i = 1; i < prices.Count; i++)
            {
                decimal diff = prices[i] - prices[i - 1];
                gains[i] = diff > 0 ? diff : 0;
                losses[i] = diff < 0 ? Math.Abs(diff) : 0;
            }
            decimal avgGain = 0; decimal avgLoss = 0;
            for (int i = 0; i < prices.Count; i++)
            {
                if (i < period)
                {
                    rsiValues.Add(50);
                    if (i == period - 1)
                    {
                        avgGain = gains.Skip(1).Take(period).Average();
                        avgLoss = losses.Skip(1).Take(period).Average();
                    }
                    continue;
                }
                avgGain = (avgGain * (period - 1) + gains[i]) / period;
                avgLoss = (avgLoss * (period - 1) + losses[i]) / period;
                if (avgLoss == 0) rsiValues.Add(100);
                else
                {
                    decimal rs = avgGain / avgLoss;
                    rsiValues.Add(100 - (100 / (1 + rs)));
                }
            }
            return rsiValues;
        }

        // Button
        private void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoopRunning) return;
            _isLoopRunning = true;
            UpdateButtonsState(true);
            LogToUI("[SYSTEM] 🟢 Торговый цикл активирован.");
            Task.Run(async () => await StartMonitoringLoopAsync());
        }

        private void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            _isLoopRunning = false;
            UpdateButtonsState(false);
            LogToUI("[SYSTEM] 🛑 Остановка цикла.");
        }

        private async void RestartBtn_Click(object sender, RoutedEventArgs e)
        {
            LogToUI("[SYSTEM] 🔄 Перезапуск...");
            _isLoopRunning = false;
            await Task.Delay(1000);
            LoadConfig(); // Перезагрузка config.json
            _isLoopRunning = true;
            _ = Task.Run(async () => await StartMonitoringLoopAsync());
        }

        // Добавьте вспомогательный метод для удобства
        private void UpdateButtonsState(bool running)
        {
            StartBtn.IsEnabled = !running;
            StopBtn.IsEnabled = running;
            RestartBtn.IsEnabled = running;
        }


    }
}


// ### END MainWindow.xaml.cs ###

