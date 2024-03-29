﻿using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace TradeMaster6000.Shared
{
    public class TradeInstrument
    {
        [Key]
        public int Id { get; set; }
        public uint Token { get; set; }
        public string TradingSymbol { get; set; }
        public string Exchange { get; set; }
    }
}
