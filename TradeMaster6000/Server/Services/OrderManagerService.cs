﻿using Hangfire;
using Microsoft.AspNet.SignalR;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradeMaster6000.Server.DataHelpers;
using TradeMaster6000.Server.Tasks;
using TradeMaster6000.Shared;

namespace TradeMaster6000.Server.Services
{
    public class OrderManagerService : IOrderManagerService
    {
        private readonly IInstrumentHelper instrumentHelper;
        private readonly IKiteService kiteService;
        //private readonly IRunningOrderService running;
        private readonly ITradeOrderHelper tradeOrderHelper;
        private readonly ITickerService tickerService;
        private readonly IServiceProvider serviceProvider;
        private readonly ITradeLogHelper tradeLogHelper;
        private readonly IBackgroundJobClient backgroundJobs;
        private static ConcurrentDictionary<int, CancellationTokenSource> OrderTokenSources { get; set; }
        public OrderManagerService(/*IRunningOrderService runningOrderService, */IKiteService kiteService, IInstrumentHelper instrumentHelper, ITradeOrderHelper tradeOrderHelper, ITickerService tickerService, IServiceProvider serviceProvider, ITradeLogHelper tradeLogHelper, IBackgroundJobClient backgroundJobs)
        {
            this.instrumentHelper = instrumentHelper;
            this.kiteService = kiteService;
            //this.running = runningOrderService;
            this.tradeOrderHelper = tradeOrderHelper;
            this.tickerService = tickerService;
            this.serviceProvider = serviceProvider;
            this.tradeLogHelper = tradeLogHelper;
            this.backgroundJobs = backgroundJobs;
            OrderTokenSources = new ConcurrentDictionary<int, CancellationTokenSource>();
        }

        public async Task StartOrder(TradeOrder order)
        {
            if (!kiteService.IsKiteConnected())
            {
                goto Ending;
            }

            var instruments = await instrumentHelper.GetTradeInstruments();

            foreach (var instrument in instruments)
            {
                if (instrument.TradingSymbol == order.TradingSymbol)
                {
                    order.Instrument = instrument;
                    break;
                }
            }

            var tradeorder = await tradeOrderHelper.AddTradeOrder(order);
            order.Id = tradeorder.Id;
            //running.Add(order);

            tickerService.Start();

            order.JobId = RunOrder(order);

            await tradeOrderHelper.UpdateTradeOrder(order).ConfigureAwait(false);

            Ending:;
        }

        public async Task AutoOrders(int k)
        {
            if (!kiteService.IsKiteConnected())
            {
                goto Ending;
            }

            var kite = kiteService.GetKite();
            var orders = new List<TradeOrder>();
            var instruments = await instrumentHelper.GetTradeInstruments();
            Random random = new ();

            int z = 0;
            int y = 0;
            for (int i = 0; i < k; i++)
            {
                TradeOrder order = new();
                int rng = random.Next(0, instruments.Count - 1);
                order.Instrument = instruments[rng];
                var ltp = kite.GetLTP(new[] { order.Instrument.Token.ToString() })[order.Instrument.Token.ToString()].LastPrice;
                order = MakeOrder(y, order, ltp);
                orders.Add(order);
                await Task.Delay(500);

                y++;

                if (y == 5)
                {
                    y = 0;
                    z++;
                }

                if (z == 4)
                {
                    break;
                }
            }

            for (int i = 0; i < orders.Count; i++)
            {
                var tradeorder = await tradeOrderHelper.AddTradeOrder(orders[i]);
                orders[i].Id = tradeorder.Id;
            }

            tickerService.Start();

            for (int i = 0; i < orders.Count; i++)
            {
                orders[i].JobId = RunOrder(orders[i]);
            }

            foreach(var order in orders)
            {
                await tradeOrderHelper.UpdateTradeOrder(order).ConfigureAwait(false);
            }

            Ending:;
        }

        private string RunOrder(TradeOrder order)
        {
            CancellationTokenSource source = new CancellationTokenSource();
            OrderTokenSources.TryAdd(order.Id, source);
            tickerService.Subscribe(order.Instrument.Token);
            OrderInstance orderWork = new(serviceProvider);
            return backgroundJobs.Enqueue(() => orderWork.StartWork(order, source.Token));
        }

        public void CancelToken(int id)
        {
            OrderTokenSources.TryGetValue(id, out CancellationTokenSource value);
            value.Cancel();
        }

        public async Task StopOrder(TradeOrder order)
        {
            tickerService.UnSubscribe(order.Instrument.Token);
            if (!tradeOrderHelper.AnyRunning())
            {
                tickerService.Stop();
            }
            await tradeLogHelper.AddLog(order.Id, $"order stopped...").ConfigureAwait(false);
        }
        private static TradeOrder MakeOrder(int i, TradeOrder order, decimal ltp)
        {
            switch (i)
            {
                case 0:
                    order.Entry = ltp + 1;
                    order.StopLoss = ltp - 3;
                    order.Risk = 4;
                    order.RxR = 2;
                    order.TransactionType = TransactionType.BUY;
                    order.TradingSymbol = order.Instrument.TradingSymbol;
                    return order;
                case 1:
                    order.Entry = ltp - 1;
                    order.StopLoss = ltp - 4;
                    order.Risk = 3;
                    order.RxR = 2;
                    order.TransactionType = TransactionType.BUY;
                    order.TradingSymbol = order.Instrument.TradingSymbol;
                    return order;
                case 2:
                    order.Entry = ltp - 6;
                    order.StopLoss = ltp - 2;
                    order.Risk = 4;
                    order.RxR = 2;
                    order.TransactionType = TransactionType.SELL;
                    order.TradingSymbol = order.Instrument.TradingSymbol;
                    return order;
                case 3:
                    order.Entry = ltp + 6;
                    order.StopLoss = ltp + 2;
                    order.Risk = 4;
                    order.RxR = 2;
                    order.TransactionType = TransactionType.BUY;
                    order.TradingSymbol = order.Instrument.TradingSymbol;
                    return order;
                case 4:
                    order.Entry = ltp - 1;
                    order.StopLoss = ltp + 3;
                    order.Risk = 4;
                    order.RxR = 2;
                    order.TransactionType = TransactionType.SELL;
                    order.TradingSymbol = order.Instrument.TradingSymbol;
                    return order;
                default:
                    return default;
            }
        }
    }
    public interface IOrderManagerService
    {
        Task AutoOrders(int k);
        Task StartOrder(TradeOrder order);
        Task StopOrder(TradeOrder order);
        void CancelToken(int id);
    }
}