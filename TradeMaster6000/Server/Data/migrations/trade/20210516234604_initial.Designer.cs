﻿// <auto-generated />
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TradeMaster6000.Server.Data;

namespace TradeMaster6000.Server.data.migrations.trade
{
    [DbContext(typeof(TradeDbContext))]
    [Migration("20210516234604_initial")]
    partial class initial
    {
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("Relational:MaxIdentifierLength", 64)
                .HasAnnotation("ProductVersion", "5.0.6");

            modelBuilder.Entity("TradeMaster6000.Shared.TradeInstrument", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("int");

                    b.Property<string>("Exchange")
                        .HasColumnType("longtext");

                    b.Property<uint>("Token")
                        .HasColumnType("int unsigned");

                    b.Property<string>("TradingSymbol")
                        .HasColumnType("longtext");

                    b.HasKey("Id");

                    b.ToTable("TradeInstruments");
                });

            modelBuilder.Entity("TradeMaster6000.Shared.TradeLog", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("int");

                    b.Property<string>("Log")
                        .HasColumnType("longtext");

                    b.Property<DateTime>("Timestamp")
                        .HasColumnType("datetime(6)");

                    b.Property<int>("TradeOrderId")
                        .HasColumnType("int");

                    b.HasKey("Id");

                    b.HasIndex("TradeOrderId");

                    b.ToTable("TradeLogs");
                });

            modelBuilder.Entity("TradeMaster6000.Shared.TradeOrder", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("int");

                    b.Property<decimal>("Entry")
                        .HasColumnType("decimal(65,30)");

                    b.Property<bool>("EntryHit")
                        .HasColumnType("tinyint(1)");

                    b.Property<int>("Quantity")
                        .HasColumnType("int");

                    b.Property<int>("QuantityFilled")
                        .HasColumnType("int");

                    b.Property<decimal>("Risk")
                        .HasColumnType("decimal(65,30)");

                    b.Property<int>("RxR")
                        .HasColumnType("int");

                    b.Property<bool>("SLMHit")
                        .HasColumnType("tinyint(1)");

                    b.Property<int>("Status")
                        .HasColumnType("int");

                    b.Property<decimal>("StopLoss")
                        .HasColumnType("decimal(65,30)");

                    b.Property<bool>("TargetHit")
                        .HasColumnType("tinyint(1)");

                    b.Property<string>("TradingSymbol")
                        .IsRequired()
                        .HasColumnType("longtext");

                    b.Property<int>("TransactionType")
                        .HasColumnType("int");

                    b.HasKey("Id");

                    b.ToTable("TradeOrders");
                });

            modelBuilder.Entity("TradeMaster6000.Shared.TradeLog", b =>
                {
                    b.HasOne("TradeMaster6000.Shared.TradeOrder", "TradeOrder")
                        .WithMany("TradeLogs")
                        .HasForeignKey("TradeOrderId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("TradeOrder");
                });

            modelBuilder.Entity("TradeMaster6000.Shared.TradeOrder", b =>
                {
                    b.Navigation("TradeLogs");
                });
#pragma warning restore 612, 618
        }
    }
}
