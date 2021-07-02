﻿using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradeMaster6000.Server.Data;
using TradeMaster6000.Shared;

namespace TradeMaster6000.Server.DataHelpers
{
    public class ZoneDbHelper : IZoneDbHelper
    {
        private IDbContextFactory<TradeDbContext> ContextFactory { get; }
        public ZoneDbHelper(IDbContextFactory<TradeDbContext> ContextFactory)
        {
            this.ContextFactory = ContextFactory;
        }

        public async Task<List<Zone>> GetZones()
        {
            using (var context = ContextFactory.CreateDbContext())
            {
                return await context.Zones.Where(x => x.Tested == false).ToListAsync();
            }
        }

        public async Task Add(List<Zone> zones)
        {
            using (var context = ContextFactory.CreateDbContext())
            {
                foreach(var zone in zones)
                {
                    await context.Zones.AddAsync(zone);
                }

                await context.SaveChangesAsync();
            }
        }

        public async Task Update(List<Zone> zones)
        {
            using (var context = ContextFactory.CreateDbContext())
            {
                foreach (var zone in zones)
                {
                    context.Zones.Update(zone);
                }

                await context.SaveChangesAsync();
            }
        }

        public DateTime LastZoneEndTime(List<Zone> zones)
        {
            zones = zones.OrderBy(x => x.To).ToList();
            return zones[^1].To;
        }
    }
    public interface IZoneDbHelper
    {
        Task<List<Zone>> GetZones();
        Task Add(List<Zone> zones);
        DateTime LastZoneEndTime(List<Zone> zones);
        Task Update(List<Zone> zones);
    }
}
