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

        private static Ticker Ticker { get; set; }
        private ConcurrentQueue<SomeLog> TickerLogs { get; set; }
        private List<MyTick> MyTicks { get; set; }
        private List<Candle> Candles { get; set; }

        private static CancellationTokenSource CandleSource { get; set; }
        private static CancellationTokenSource FlushingSource { get; set; }
        private static CancellationTokenSource TickManagerSource { get; set; }
        private static CancellationTokenSource CandleManagerSource { get; set; }
        private static bool Flushing { get; set; }
        private static bool TickManagerOn { get; set; }
        private static bool CandleManagerOn { get; set; }
        private static bool CandlesRunning { get; set; }
        private static object myTicksKey;
        private static object candlesKey;

        public TickerService(IConfiguration configuration, IKiteService kiteService, IInstrumentHelper instrumentHelper, ITimeHelper timeHelper, ICandleDbHelper candleHelper, ITickDbHelper tickDbHelper, IOrderUpdatesDbHelper orderUpdatesDbHelper, IBackgroundJobClient backgroundJob, IContextExtension contextExtension, ILogger<TickerService> logger)
        {
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
            TickerLogs = new ();
            MyTicks = new();
            Candles = new List<Candle>();
            myTicksKey = new object();
            candlesKey = new object();
        }

        public void Start()
        {
            if (Ticker == null)
            {
                var accessToken = kiteService.GetAccessToken();
                Ticker = new Ticker(Configuration.GetValue<string>("APIKey"), accessToken);

                Ticker.OnTick += OnTick;
                Ticker.OnOrderUpdate += OnOrderUpdate;
                Ticker.OnNoReconnect += OnNoReconnect;
                Ticker.OnError += OnError;
                Ticker.OnReconnect += OnReconnect;
                Ticker.OnClose += OnClose;
                Ticker.OnConnect += OnConnect;

                Ticker.EnableReconnect(Interval: 5, Retries: 50);
                Ticker.Connect();

                FlushingSource = new CancellationTokenSource();
                TickManagerSource = new CancellationTokenSource();

                backgroundJob.Enqueue(() => TickManager(TickManagerSource.Token));
                backgroundJob.Enqueue(() => StartFlushing(FlushingSource.Token));
            }
        }

        [AutomaticRetry(Attempts = 0)]
        public async Task TickManager(CancellationToken token)
        {
            TickManagerOn = true;
            while (!token.IsCancellationRequested)
            {
                List<MyTick> myTicks = MyTicks.ToList();
                if (myTicks.Count > 0)
                {
                    await tickDbHelper.Add(myTicks);

                    for (int i = 0; i < myTicks.Count; i++)
                    {
                        MyTicks.RemoveAt(i);
                    }
                }

                await Task.Delay(10000);
            }
            TickManagerOn = false;
        }

        [AutomaticRetry(Attempts = 0)]
        public async Task CandleManager(CancellationToken token)
        {
            CandleManagerOn = true;
            while (!token.IsCancellationRequested)
            {
                List<Candle> myCandles = Candles;
                if (myCandles.Count > 0)
                {
                    await candleHelper.Add(myCandles);

                    for (int i = 0; i < myCandles.Count; i++)
                    {
                        Candles.RemoveAt(i);
                    }
                }

                await Task.Delay(75000);
            }
            CandleManagerOn = false;
        }

        [AutomaticRetry(Attempts = 0)]
        public async Task StartFlushing(CancellationToken token)
        {
            Flushing = true;
            while(!token.IsCancellationRequested)
            {
                await tickDbHelper.Flush();
                await Task.Delay(10000);
            }
            Flushing = false;
        }


        public void StopCandles()
        {
            CandleSource.Cancel();
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

        public async Task RunCandles()
        {
            CandlesRunning = true;
            await candleHelper.Flush();

            CandleManagerSource = new CancellationTokenSource();
            backgroundJob.Enqueue(() => CandleManager(CandleManagerSource.Token));
            backgroundJob.Enqueue(() => AnalyzeCandles());
        }

        [AutomaticRetry(Attempts = 0)]
        public async Task AnalyzeCandles()
        {
            CandleSource = new CancellationTokenSource();

            while (!timeHelper.IsPreMarketOpen() && !CandleSource.Token.IsCancellationRequested)
            {
                await Task.Delay(10000, CandleSource.Token);
            }

            while (!timeHelper.IsMarketOpen() && !CandleSource.Token.IsCancellationRequested)
            {
                await Task.Delay(1000, CandleSource.Token);
            }

            var instruments = await instrumentHelper.GetTradeInstruments();
            List<Task> tasks = new List<Task>();
            for (int i = 0; i < 30; i++)
            {
                Subscribe(instruments[i].Token);
                tasks.Add(Analyze(instruments[i], CandleSource.Token));
            }

            await Task.WhenAll(tasks);

            CandlesRunning = false;
            CandleManagerSource.Cancel();
        }

        public async Task Analyze(TradeInstrument instrument, CancellationToken token)
        {
            DateTime waittime = timeHelper.OpeningTime();
            DateTime current = timeHelper.CurrentTime();
            DateTime candleTime = new DateTime();
            TimeSpan duration = new TimeSpan();
            List<MyTick> ticks = new List<MyTick>();
            Candle previousCandle = new Candle();

            if (DateTime.Compare(waittime, current) < 0)
            {
                int hour = current.Hour;
                int minute = current.Minute;
                int second = current.Second;
                if(minute == 59)
                {
                    hour++;
                    minute = 0;
                }
                else
                {
                    minute++;
                }

                if (second > 50)
                {
                    minute++;
                }
                waittime = new DateTime(current.Year, current.Month, current.Day, hour, minute, 00);
            }

            while (!token.IsCancellationRequested && !await tickDbHelper.Any(instrument.Token))
            {
                await Task.Delay(1000, CancellationToken.None);
            }

            try
            {
                while (!timeHelper.IsMarketEnded() && !token.IsCancellationRequested)
                {
                    current = timeHelper.CurrentTime();
                    duration = timeHelper.GetDuration(waittime, current).Add(TimeSpan.FromSeconds(11));
                    candleTime = waittime.Subtract(TimeSpan.FromSeconds(30));
                    candleTime = new DateTime(candleTime.Year, candleTime.Month, candleTime.Day, candleTime.Hour, candleTime.Minute, 00);

                    await Task.Delay(duration, CancellationToken.None);

                    ticks = await tickDbHelper.Get(instrument.Token, candleTime);
                    Candle candle = new Candle() { InstrumentToken = instrument.Token, Timestamp = candleTime, Kill = waittime.AddDays(2), TicksCount = ticks.Count };
                    if (ticks.Count > 0)
                    {
                        candle.High = ticks[0].LTP;
                        candle.Low = ticks[0].LTP;
                        for (int i = 0; i < ticks.Count; i++)
                        {
                            if (candle.High < ticks[i].LTP)
                            {
                                candle.High = ticks[i].LTP;
                            }
                            if (candle.Low > ticks[i].LTP)
                            {
                                candle.Low = ticks[i].LTP;
                            }
                        }
                        candle.Open = ticks[0].LTP;
                        candle.Close = ticks[^1].LTP;
                    }
                    else
                    {
                        candle.High = previousCandle.Close;
                        candle.Low = previousCandle.Close;
                        candle.Open = previousCandle.Close;
                        candle.Close = previousCandle.Close;
                    }

                    lock (candlesKey)
                    {
                        Candles.Add(candle);
                    }

                    previousCandle = candle;
                    waittime = waittime.AddMinutes(1);
                }

                UnSubscribe(instrument.Token);
            }
            catch (Exception e)
            {
                logger.LogInformation(e.Message);
            }

        }

        public async Task<OrderUpdate> GetOrder(string id)
        {
            var update = await updatesHelper.Get(id);
            if (update != null)
            {
                return update;
            }

            try
            {
                var order = kiteService.GetKite().GetOrderHistory(id)[^1];
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

                    await updatesHelper.AddOrUpdate(newOrderUpdate);


                return newOrderUpdate;
            }
            catch (KiteException e)
            {
                AddLog(e.Message, LogType.Exception);
                return default;
            };
        }

        public void SetTicker(Ticker ticker)
        {
            Ticker = ticker;
        }

        public List<SomeLog> GetSomeLogs()
        {
            return TickerLogs.ToList();
        }

        public void Stop()
        {
            Ticker.DisableReconnect();
            Ticker.Close();
            Ticker = null;
            FlushingSource.Cancel();
            TickManagerSource.Cancel();
        }

        public void Subscribe(uint token)
        {
            Ticker.Subscribe(Tokens: new UInt32[] { token });
            Ticker.SetMode(Tokens: new UInt32[] { token }, Mode: Constants.MODE_LTP);
        }

        public void UnSubscribe(uint token)
        {
            Ticker.UnSubscribe(Tokens: new UInt32[] { token });
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
            lock (myTicksKey)
            {
                MyTicks.Add(new MyTick
                {
                    InstrumentToken = tickData.InstrumentToken,
                    LTP = tickData.LastPrice,
                    Timestamp = time,
                    Flushtime = time.AddMinutes(2).AddSeconds(30)
                });
            }
        }

        private async void OnOrderUpdate(Order orderData)
        {
            await updatesHelper.AddOrUpdate(new OrderUpdate
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
        Task<OrderUpdate> GetOrder(string id);
        void Subscribe(uint token);
        void UnSubscribe(uint token);
        void Start();
        void Stop();
        List<SomeLog> GetSomeLogs();
        void SetTicker(Ticker ticker);
        Task StartFlushing(CancellationToken token);
        Task RunCandles();
        Task Analyze(TradeInstrument instrument, CancellationToken token);
        Task AnalyzeCandles();
        void StopCandles();
        bool IsCandlesRunning();
        bool IsFlushing();
        bool IsTickManagerOn();
        bool IsCandleManagerOn();
    }
}
