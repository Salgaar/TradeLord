﻿using KiteConnect;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradeMaster6000.Server.DataHelpers;
using TradeMaster6000.Server.Services;
using TradeMaster6000.Shared;

namespace TradeMaster6000.Server.Helpers
{
    public class TradeHelper : ITradeHelper
    {
        private ITradeLogHelper LogHelper { get; set; }
        private ITickerService TickService { get; set; }
        private Kite Kite { get; set; }
        public TradeHelper(ITradeLogHelper tradeLogHelper, ITickerService tickService, IKiteService kiteService)
        {
            LogHelper = tradeLogHelper;
            TickService = tickService;
            Kite = kiteService.GetKite();
        }

        public string GetCurrentVariety()
        {

            string variety = null;

            DateTime GST1 = DateTime.Now;
            DateTime IST1 = GST1.AddHours(5).AddMinutes(30);
            DateTime opening1 = new DateTime(IST1.Year, IST1.Month, IST1.Day, 9, 15, 0);
            DateTime closing1 = opening1.AddHours(6).AddMinutes(15);

            if (DateTime.Compare(IST1, opening1) < 0)
            {
                variety = "amo";
            }
            else if (DateTime.Compare(IST1, opening1) >= 0)
            {
                variety = "regular";
            }
            if (DateTime.Compare(IST1, closing1) >= 0)
            {
                variety = "amo";
            }

            return variety;

        }

        public async Task CancelEntry(string orderId_ent, int orderId)
        {
            var variety = await Task.Run(()=>GetCurrentVariety());
            var entryTask = Task.Run(() => TickService.GetOrder(orderId_ent));
            var entry = await entryTask;

            if (entry.Status != "COMPLETE" && entry.Status != "REJECTED")
            {
                try
                {
                    Kite.CancelOrder(orderId_ent, variety);
                    await LogHelper.AddLog(orderId, $"entry order cancelled...").ConfigureAwait(false);
                }
                catch (KiteException e)
                {
                    await LogHelper.AddLog(orderId, $"kite error: {e.Message}...").ConfigureAwait(false);
                }
            }
        }

        public async Task CancelStopLoss(string orderId_slm, bool is_pre_slm_cancelled, bool regularSLMplaced, int orderId)
        {
            var variety = await Task.Run(()=>GetCurrentVariety());
            var slmTask = Task.Run(() => TickService.GetOrder(orderId_slm));
            var slm = await slmTask;

            if (!is_pre_slm_cancelled)
            {
                if (slm.Status != "COMPLETE" && slm.Status != "REJECTED")
                {
                    try
                    {
                        Kite.CancelOrder(orderId_slm, variety);
                        await LogHelper.AddLog(orderId, $"slm order cancelled...").ConfigureAwait(false);
                    }
                    catch (KiteException e)
                    {
                        await LogHelper.AddLog(orderId, $"kite error: {e.Message}...").ConfigureAwait(false);
                    }
                }
            }
            else if (regularSLMplaced)
            {
                if (slm.Status != "COMPLETE" && slm.Status != "REJECTED")
                {
                    try
                    {
                        Kite.CancelOrder(orderId_slm, variety);
                        await LogHelper.AddLog(orderId, $"slm order cancelled...").ConfigureAwait(false);
                    }
                    catch (KiteException e)
                    {
                        await LogHelper.AddLog(orderId, $"kite error: {e.Message}...").ConfigureAwait(false);
                    }
                }
            }
        }

        public async Task CancelTarget(string orderId_tar, bool targetplaced, int orderId)
        {
            var targetTask = Task.Run(() => TickService.GetOrder(orderId_tar));
            var variety = await Task.Run(() => GetCurrentVariety());
            var targetO = await targetTask;

            if (targetplaced)
            {
                if (targetO.Status != "COMPLETE" && targetO.Status != "REJECTED")
                {
                    try
                    {
                        Kite.CancelOrder(orderId_tar, variety);
                        await LogHelper.AddLog(orderId, $"target order cancelled...").ConfigureAwait(false);
                    }
                    catch (KiteException e)
                    {
                        await LogHelper.AddLog(orderId, $"kite error: {e.Message}...").ConfigureAwait(false);
                    }
                }
            }
        }
        public async Task SquareOff(TradeOrder order, string orderId_ent, string exitTransactionType)
        {
            var entryTask = Task.Run(() => TickService.GetOrder(orderId_ent));
            var entry = await entryTask;
            if (entry.FilledQuantity > 0)
            {
                Dictionary<string, dynamic> placeOrderResponse = Kite.PlaceOrder(
                     Exchange: order.Instrument.Exchange,
                     TradingSymbol: order.Instrument.TradingSymbol,
                     TransactionType: exitTransactionType,
                     Quantity: entry.FilledQuantity,
                     Product: Constants.PRODUCT_MIS,
                     OrderType: Constants.ORDER_TYPE_MARKET,
                     Validity: Constants.VALIDITY_DAY,
                     Variety: Constants.VARIETY_REGULAR
                );
            }
        }

        public async Task<string> PlaceEntry(TradeOrder order)
        {
            dynamic value1;
            try
            {
                var variety = await Task.Run(() => GetCurrentVariety());
                // place entry limit order
                Dictionary<string, dynamic> response = Kite.PlaceOrder(
                     Exchange: order.Instrument.Exchange,
                     TradingSymbol: order.Instrument.TradingSymbol,
                     TransactionType: order.TransactionType.ToString(),
                     Quantity: order.Quantity,
                     Price: order.Entry,
                     Product: Constants.PRODUCT_MIS,
                     OrderType: Constants.ORDER_TYPE_LIMIT,
                     Validity: Constants.VALIDITY_DAY,
                     Variety: variety
                 );

                // get order id from place order response
                response.TryGetValue("data", out dynamic value);
                Dictionary<string, dynamic> data = value;
                data.TryGetValue("order_id", out value1);

                await LogHelper.AddLog(order.Id, $"entry order placed...").ConfigureAwait(false);
            }
            catch (KiteException e)
            {
                await LogHelper.AddLog(order.Id, $"kite error: {e.Message}...").ConfigureAwait(false);
                return null;
            }
    
            return value1;
        }

        public async Task<string> PlacePreSLM(TradeOrder order, string exitTransactionType, string orderId_ent)
        {
            var lastPrice = TickService.LastTick(order.Instrument.Token).LastPrice;
            if (exitTransactionType == "SELL")
            {
                // if last price is more than stop loss then place slm
                if (lastPrice > order.StopLoss)
                {
                    try
                    {
                        var variety = await Task.Run(() => GetCurrentVariety());
                        // place slm order
                        Dictionary<string, dynamic> responseS = Kite.PlaceOrder(
                             Exchange: order.Instrument.Exchange,
                             TradingSymbol: order.Instrument.TradingSymbol,
                             TransactionType: exitTransactionType,
                             Quantity: order.Quantity,
                             TriggerPrice: order.StopLoss,
                             Product: Constants.PRODUCT_MIS,
                             OrderType: Constants.ORDER_TYPE_SLM,
                             Validity: Constants.VALIDITY_DAY,
                             Variety: variety
                        );

                        // set id
                        responseS.TryGetValue("data", out dynamic valueS);
                        Dictionary<string, dynamic> dateS = valueS;
                        dateS.TryGetValue("order_id", out dynamic value1S);

                        await LogHelper.AddLog(order.Id, $"slm order placed...").ConfigureAwait(false);

                        return value1S;
                    }
                    catch (KiteException e)
                    {
                        await LogHelper.AddLog(order.Id, $"kite error: {e.Message}...").ConfigureAwait(false);
                        await CancelEntry(orderId_ent, order.Id);
                        return "cancelled";
                    }
                }
                // else tell app that slm order was cancelled, which means it has to find a new one after 1 min
                else
                {
                    await LogHelper.AddLog(order.Id, $"slm was cancelled...").ConfigureAwait(false);
                    return "cancelled";
                }
            }
            else
            {
                // if last price is more than stop loss then place slm
                if (lastPrice < order.StopLoss)
                {
                    try
                    {
                        var variety = await Task.Run(() => GetCurrentVariety());
                        // place slm order
                        Dictionary<string, dynamic> responseS = Kite.PlaceOrder(
                             Exchange: order.Instrument.Exchange,
                             TradingSymbol: order.Instrument.TradingSymbol,
                             TransactionType: exitTransactionType,
                             Quantity: order.Quantity,
                             TriggerPrice: order.StopLoss,
                             Product: Constants.PRODUCT_MIS,
                             OrderType: Constants.ORDER_TYPE_SLM,
                             Validity: Constants.VALIDITY_DAY,
                             Variety: variety
                        );

                        // set id
                        responseS.TryGetValue("data", out dynamic valueS);
                        Dictionary<string, dynamic> dateS = valueS;
                        dateS.TryGetValue("order_id", out dynamic value1S);

                        await LogHelper.AddLog(order.Id, $"slm order placed...").ConfigureAwait(false);

                        return value1S;
                    }
                    catch (KiteException e)
                    {
                        await LogHelper.AddLog(order.Id, $"kite error: {e.Message}...").ConfigureAwait(false);
                        await CancelEntry(orderId_ent, order.Id);
                        return "cancelled";
                    }
                }
                // else tell app that slm order was cancelled, which means it has to find a new one after 1 min
                else
                {
                    await LogHelper.AddLog(order.Id, $"slm was cancelled...").ConfigureAwait(false);
                    return "cancelled";
                }
            }
        }
    }
    public interface ITradeHelper
    {
        Task<string> PlacePreSLM(TradeOrder order, string exitTransactionType, string orderId_ent);
        Task<string> PlaceEntry(TradeOrder order);
        Task SquareOff(TradeOrder order, string orderId_ent, string exitTransactionType);
        Task CancelTarget(string orderId_tar, bool targetplaced, int orderId);
        Task CancelStopLoss(string orderId_slm, bool is_pre_slm_cancelled, bool regularSLMplaced, int orderId);
        Task CancelEntry(string orderId_ent, int orderId);
        string GetCurrentVariety();
    }
}
