﻿using Hangfire;
using KiteConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradeMaster6000.Server.DataHelpers;
using TradeMaster6000.Server.Extensions;
using TradeMaster6000.Server.Helpers;
using TradeMaster6000.Server.Models;
using TradeMaster6000.Shared;

namespace TradeMaster6000.Server.Services
{
    public class TickerService : ITickerService
    {
        private readonly ILogger<TickerService> logger;
        private IConfiguration Configuration { get; }
        private readonly IKiteService kiteService;
        private readonly IInstrumentHelper instrumentHelper;
        private readonly ITimeHelper timeHelper;
        private readonly ICandleDbHelper candleHelper;
        private readonly ITickDbHelper tickDbHelper;
        private readonly IOrderUpdatesDbHelper updatesHelper;
        private readonly IBackgroundJobClient backgroundJob;
        private readonly IContextExtension contextExtension;
        private readonly ITradeOrderHelper tradeOrderHelper;
        private readonly IZoneService zoneService;
        private readonly IProtectionService protectionService;

        private static readonly ConcurrentDictionary<string, Ticker> Tickers = new ConcurrentDictionary<string, Ticker>();
        private static readonly ConcurrentQueue<SomeLog> TickerLogs = new ConcurrentQueue<SomeLog>();
        private static readonly ConcurrentQueue<MyTick> MyTicks = new ConcurrentQueue<MyTick>();
        private static readonly ConcurrentQueue<Candle> Candles = new ConcurrentQueue<Candle>();
        private static readonly ConcurrentQueue<OrderUpdate> OrderUpdates = new ConcurrentQueue<OrderUpdate>();

        private static readonly CancellationGod CandleCancel = new CancellationGod();
        private static readonly CancellationGod FlushingCancel = new CancellationGod();
        private static readonly CancellationGod TickManagerCancel = new CancellationGod();
        private static readonly CancellationGod CandleManagerCancel = new CancellationGod();
        private static readonly CancellationGod OrderUpdatesCancel = new CancellationGod();

        private static bool Flushing { get; set; }
        private static bool TickManagerOn { get; set; }
        private static bool CandleManagerOn { get; set; }
        private static bool CandlesRunning { get; set; }
        private static bool OrderUpdatesOn { get; set; }
        private static bool IsTickerRunning = false;

        public TickerService(IProtectionService protectionService, IConfiguration configuration, IKiteService kiteService, IInstrumentHelper instrumentHelper, ITimeHelper timeHelper, ICandleDbHelper candleHelper, ITickDbHelper tickDbHelper, IOrderUpdatesDbHelper orderUpdatesDbHelper, IBackgroundJobClient backgroundJob, IContextExtension contextExtension, ILogger<TickerService> logger , ITradeOrderHelper tradeOrderHelper, IZoneService zoneService)
        {
            this.protectionService = protectionService;
            this.kiteService = kiteService;
            Configuration = configuration;
            this.instrumentHelper = instrumentHelper;
            this.timeHelper = timeHelper;
            this.candleHelper = candleHelper;
            this.tickDbHelper = tickDbHelper;
            this.updatesHelper = orderUpdatesDbHelper;
            this.backgroundJob = backgroundJob;
            this.contextExtension = contextExtension;
            this.logger = logger;
            this.tradeOrderHelper = tradeOrderHelper;
            this.zoneService = zoneService;
        }

        public void TickerForMaster(ApplicationUser user)
        {
            IsTickerRunning = true;
            Ticker ticker = new Ticker(protectionService.UnprotectApiKey(user.ApiKey), kiteService.GetAccessToken(user.UserName));

            ticker.OnTick += OnTick;
            ticker.OnOrderUpdate += OnOrderUpdate;
            ticker.OnNoReconnect += OnNoReconnect;
            ticker.OnError += OnError;
            ticker.OnReconnect += OnReconnect;
            ticker.OnClose += OnClose;
            ticker.OnConnect += OnConnect;

            ticker.EnableReconnect(Interval: 5, Retries: 50);
            ticker.Connect();

            var instruments = instrumentHelper.GetTradeInstruments().Result;
            foreach(var instrument in instruments)
            {
                ticker.Subscribe(Tokens: new UInt32[] { instrument.Token });
                ticker.SetMode(Tokens: new UInt32[] { instrument.Token }, Mode: Constants.MODE_LTP);
            }

            Tickers.TryAdd(user.UserName, ticker);

            TickManagerCancel.Source = new CancellationTokenSource();
            FlushingCancel.Source = new CancellationTokenSource();

            TickManagerCancel.HangfireId = backgroundJob.Enqueue(() => TickManager(TickManagerCancel.Source.Token));
            FlushingCancel.HangfireId = backgroundJob.Enqueue(() => StartFlushing(FlushingCancel.Source.Token));
        }

        public void TickerForFollower(ApplicationUser user)
        {
            Ticker ticker = new Ticker(protectionService.UnprotectApiKey(user.ApiKey), kiteService.GetAccessToken(user.UserName));

            ticker.OnOrderUpdate += OnOrderUpdate;
            ticker.OnNoReconnect += OnNoReconnect;
            ticker.OnError += OnError;
            ticker.OnReconnect += OnReconnect;
            ticker.OnClose += OnClose;
            ticker.OnConnect += OnConnect;

            ticker.EnableReconnect(Interval: 5, Retries: 50);
            ticker.Connect();

            var instruments = instrumentHelper.GetTradeInstruments().Result;
            foreach (var instrument in instruments)
            {
                ticker.Subscribe(Tokens: new UInt32[] { instrument.Token });
            }

            Tickers.TryAdd(user.UserName, ticker);
        }

        [AutomaticRetry(Attempts = 0)]
        public async Task TickManager(CancellationToken token)
        {
            List<MyTick> myTicks;
            TickManagerOn = true;
            while (true)
            {
                myTicks = new List<MyTick>();
                while (!MyTicks.IsEmpty)
                {
                    MyTicks.TryDequeue(out MyTick tick);
                    myTicks.Add(tick);
                }

                if(myTicks.Count > 0)
                {
                    await tickDbHelper.Add(myTicks);
                }

                if (token.IsCancellationRequested)
                {
                    break;
                }

                await Task.Delay(1000, CancellationToken.None);
            }
            TickManagerOn = false;
        }

        public void StartOrderUpdatesManager()
        {
            OrderUpdatesCancel.Source = new CancellationTokenSource();
            OrderUpdatesCancel.HangfireId = backgroundJob.Enqueue(() => OrderUpdatesManager(OrderUpdatesCancel.Source.Token));
        }

        public bool IsTheTickerRunning()
        {
            return IsTickerRunning;
        }

        public void StopOrderUpdatesManager()
        {
            OrderUpdatesCancel.Source.Cancel();
            backgroundJob.Delete(OrderUpdatesCancel.HangfireId);
        }

        [AutomaticRetry(Attempts = 0)]
        public async Task OrderUpdatesManager(CancellationToken token)
        {
            List<OrderUpdate> orderUpdates;
            OrderUpdatesOn = true;
            while (true)
            {
                orderUpdates = new List<OrderUpdate>();
                while (!OrderUpdates.IsEmpty)
                {
                    OrderUpdates.TryDequeue(out OrderUpdate update);
                    orderUpdates.Add(update);
                }

                if (orderUpdates.Count > 0)
                {
                    await updatesHelper.Add(orderUpdates);
                }

                if (token.IsCancellationRequested)
                {
                    break;
                }

                await Task.Delay(1000);
            }
            OrderUpdatesOn = false;
        }

        [AutomaticRetry(Attempts = 0)]
        public async Task CandleManager(CancellationToken token)
        {
            List<Candle> myCandles = new();
            CandleManagerOn = true;
            while (true)
            {
                myCandles = new ();
                while (!Candles.IsEmpty)
                {
                    Candles.TryDequeue(out Candle candle);
                    myCandles.Add(candle);
                }
                
                if (myCandles.Count > 0)
                {
                    await candleHelper.Add(myCandles);
                }

                if (token.IsCancellationRequested)
                {
                    break;
                }

                await Task.Delay(60000);
            }
            CandleManagerOn = false;
        }

        [AutomaticRetry(Attempts = 0)]
        public async Task StartFlushing(CancellationToken token)
        {
            Flushing = true;
            while(true)
            {
                await tickDbHelper.Flush();

                if (token.IsCancellationRequested)
                {
                    break;
                }

                await Task.Delay(60000);
            }
            Flushing = false;
        }

        public bool IsOrderUpdateOn()
        {
            return OrderUpdatesOn;
        }

        public void StopCandles()
        {
            CandleCancel.Source.Cancel();
        }

        public bool IsFlushing()
        {
            return Flushing;
        }

        public bool IsCandlesRunning()
        {
            return CandlesRunning;
        }

        public bool IsTickManagerOn()
        {
            return TickManagerOn;
        }

        public bool IsCandleManagerOn()
        {
            return CandleManagerOn;
        }

        public async Task RunCandles(ApplicationUser user)
        {
            CandlesRunning = true;
            if (!IsTickerRunning)
            {
                TickerForMaster(user);
            }
            await candleHelper.Flush();
            CandleManagerCancel.Source = new CancellationTokenSource();

            CandleManagerCancel.HangfireId = backgroundJob.Enqueue(() => CandleManager(CandleManagerCancel.Source.Token));
            backgroundJob.Enqueue(() => AnalyzeCandles()); // HUSK LIGE DET MED at analyzecandles ikke har cancellation token med i backgroundJob, men det virker med at cancel token. det gør det ikke med de andre.
        }

        [AutomaticRetry(Attempts = 0)]
        public async Task AnalyzeCandles()
        {
            CandleCancel.Source = new CancellationTokenSource();

            var instruments = await instrumentHelper.GetTradeInstruments();
            List<Task> tasks = new List<Task>();
            for (int i = 0, n = instruments.Count; i < n; i++)
            {
                tasks.Add(Analyze(instruments[i], CandleCancel.Source.Token));
            }

            await Task.WhenAll(tasks);

            StopMasterTicker();

            CandlesRunning = false;

            CandleManagerCancel.Source.Cancel();
            backgroundJob.Delete(CandleManagerCancel.HangfireId);
        }

        public async Task Analyze(TradeInstrument instrument, CancellationToken token)
        {
            DateTime current = timeHelper.CurrentTime();
            DateTime waittime = timeHelper.GetWaittime(current);
            DateTime candleTime = new DateTime();
            TimeSpan duration = new TimeSpan();
            List<MyTick> ticks = new List<MyTick>();
            Candle previousCandle = new Candle();
            Candle candle = new Candle();

            try
            {
                while (!timeHelper.IsMarketEnded())
                {
                    current = timeHelper.CurrentTime();
                    duration = timeHelper.GetDuration(waittime, current).Add(TimeSpan.FromSeconds(11));
                    candleTime = waittime.Subtract(TimeSpan.FromSeconds(30));
                    candleTime = new DateTime(candleTime.Year, candleTime.Month, candleTime.Day, candleTime.Hour, candleTime.Minute, 00);

                    await Task.Delay(duration, CancellationToken.None);
                    ticks = await tickDbHelper.Get(instrument.Token, candleTime);
                    await Task.Run(() =>
                    {
                        candle = new Candle() { InstrumentToken = instrument.Token, Timestamp = candleTime, Kill = waittime.AddDays(14), TicksCount = ticks.Count, Timeframe = 1 };
                        if (ticks.Count > 0)
                        {
                            candle.Low = ticks[0].LTP;
                            candle.High = ticks[0].LTP;
                            for (int i = 0; i < ticks.Count; i++)
                            {
                                if (candle.Low > ticks[i].LTP)
                                {
                                    candle.Low = ticks[i].LTP;
                                }
                                if (candle.High < ticks[i].LTP)
                                {
                                    candle.High = ticks[i].LTP;
                                }
                            }
                            candle.Open = ticks[0].LTP;
                            candle.Close = ticks[^1].LTP;
                        }
                        else
                        {
                            candle.Low = previousCandle.Close;
                            candle.High = previousCandle.Close;
                            candle.Open = previousCandle.Close;
                            candle.Close = previousCandle.Close;
                        }

                        Candles.Enqueue(candle);

                        previousCandle = candle;
                        waittime = waittime.AddMinutes(1);
                    });

                    if (token.IsCancellationRequested)
                    {
                        break;
                    }
                }

            }
            catch (Exception e)
            {
                logger.LogInformation(e.Message);
            }
        }

        public async Task<OrderUpdate> GetOrder(string id, string username)
        {
            var update = await updatesHelper.Get(id);
            if (update != null)
            {
                return update;
            }

            try
            {
                var order = kiteService.GetKite(username).GetOrderHistory(id)[^1];
                var newOrderUpdate = new OrderUpdate
                {
                    AveragePrice = order.AveragePrice,
                    FilledQuantity = order.FilledQuantity,
                    InstrumentToken = order.InstrumentToken,
                    OrderId = order.OrderId,
                    Price = order.Price,
                    Quantity = order.Quantity,
                    Status = order.Status,
                    Timestamp = DateTime.Now,
                    TriggerPrice = order.TriggerPrice
                };

                if (!OrderUpdates.Contains(newOrderUpdate))
                {
                    OrderUpdates.Enqueue(newOrderUpdate);
                }

                return newOrderUpdate;
            }
            catch (KiteException e)
            {
                AddLog(e.Message, LogType.Exception);
                return default;
            };
        }

        public List<SomeLog> GetSomeLogs()
        {
            return TickerLogs.ToList();
        }

        public void StopMasterTicker()
        {
            IsTickerRunning = false;
            Ticker ticker = Tickers.FirstOrDefault(x => x.Key == "God").Value;
            ticker.DisableReconnect();
            ticker.Close();
            Tickers.TryRemove("God", out _);

            if (OrderUpdatesOn)
            {
                StopOrderUpdatesManager();
            }

            FlushingCancel.Source.Cancel();
            backgroundJob.Delete(FlushingCancel.HangfireId);

            TickManagerCancel.Source.Cancel();
            backgroundJob.Delete(TickManagerCancel.HangfireId);

            zoneService.CancelToken();
        }

        public void StopFollowerTicker(string username)
        {
            Ticker ticker = Tickers.FirstOrDefault(x => x.Key == username).Value;
            ticker.DisableReconnect();
            ticker.Close();
            Tickers.TryRemove(username, out _);
        }

        private void AddLog(string log, LogType logType)
        {
            TickerLogs.Enqueue(new()
            {
                Log = log,
                Timestamp = DateTime.Now,
                LogType = logType
            });
        }

        // ------------------------------------------- TICKER EVENTS -------------------------------------------------

        private void OnTick(Tick tickData)
        {
            DateTime time = timeHelper.CurrentTime();
            MyTicks.Enqueue(new MyTick
            {
                InstrumentToken = tickData.InstrumentToken,
                LTP = tickData.LastPrice,
                Timestamp = time,
                Flushtime = time.AddMinutes(2).AddSeconds(30)
            });
        }

        private void OnOrderUpdate(Order orderData) // check orderdata timestamp
        {
            OrderUpdates.Enqueue(new OrderUpdate
            {
                AveragePrice = orderData.AveragePrice,
                FilledQuantity = orderData.FilledQuantity,
                InstrumentToken = orderData.InstrumentToken,
                OrderId = orderData.OrderId,
                Price = orderData.Price,
                Quantity = orderData.Quantity,
                Status = orderData.Status,
                Timestamp = DateTime.Now,
                TriggerPrice = orderData.TriggerPrice
            });
        }

        private void OnError(string message)
        {
            AddLog(message, LogType.Error);
        }

        private void OnClose()
        {
            AddLog("ticker connection closed...", LogType.Close);
        }

        private void OnReconnect()
        {
            AddLog("ticker connection reconnected...", LogType.Reconnect);
        }

        private void OnNoReconnect()
        {
            AddLog("ticker connection failed to reconnect...", LogType.NoReconnect);
        }

        private void OnConnect()
        {
            AddLog("ticker connected...", LogType.Connect);
        }
    }
    public interface ITickerService
    {
        Task<OrderUpdate> GetOrder(string id, string username);
        void TickerForMaster(ApplicationUser user);
        void TickerForFollower(ApplicationUser user);
        void StopMasterTicker();
        void StopFollowerTicker(string username);
        List<SomeLog> GetSomeLogs();
        Task StartFlushing(CancellationToken token);
        Task RunCandles(ApplicationUser user);
        Task Analyze(TradeInstrument instrument, CancellationToken token);
        Task AnalyzeCandles();
        void StopCandles();
        bool IsCandlesRunning();
        bool IsFlushing();
        bool IsTickManagerOn();
        bool IsCandleManagerOn();
        bool IsOrderUpdateOn();
        Task OrderUpdatesManager(CancellationToken token);
        void StopOrderUpdatesManager();
        void StartOrderUpdatesManager();
        bool IsTheTickerRunning();
    }
}
