using Hangfire;
using Hangfire.MySql;
using KiteConnect;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Logging;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using System;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Transactions;
using TradeMaster6000.Server.Data;
using TradeMaster6000.Server.DataHelpers;
using TradeMaster6000.Server.Extensions;
using TradeMaster6000.Server.Helpers;
using TradeMaster6000.Server.Hubs;
using TradeMaster6000.Server.Models;
using TradeMaster6000.Server.Services;
using TradeMaster6000.Server.Tasks;

namespace TradeMaster6000.Server
{
    public class Startup
    {
        public Startup(IConfiguration configuration, IWebHostEnvironment webHostEnvironment)
        {
            Configuration = configuration;
            Environment = webHostEnvironment;
        }

        public IWebHostEnvironment Environment { get; }
        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            string connectionString = Configuration.GetConnectionString("DefaultConnection");
            string keyConnection = Configuration.GetConnectionString("KeyConnection");
            string tradeConnection = Configuration.GetConnectionString("TradeConnection");
            string hangfireConnection = Configuration.GetConnectionString("HangfireConnection");

            services.AddDbContext<MyKeysContext>(options =>
                options.UseMySql(keyConnection, ServerVersion.AutoDetect(keyConnection), (e) => e.EnableRetryOnFailure(5)));

            if (Environment.IsDevelopment())
            {
                services.AddDataProtection()
                    .PersistKeysToDbContext<MyKeysContext>()
                    .SetApplicationName("TradeMaster6000")
                    .ProtectKeysWithCertificate(new X509Certificate2("certificate.pfx", Configuration["Thumbprint"]));
            }
            else
            {
                services.AddDataProtection()
                    .PersistKeysToDbContext<MyKeysContext>()
                    .SetApplicationName("TradeMaster6000")
                    .ProtectKeysWithCertificate(new X509Certificate2("/etc/ssl/letsencrypt/certificate.pfx", Configuration["Thumbprint"]));
            }

            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString), (e)=> e.EnableRetryOnFailure(5)));

            services.AddDbContextFactory<TradeDbContext>(options =>
            {
                options.UseMySql(tradeConnection, ServerVersion.AutoDetect(tradeConnection), (e) => e.EnableRetryOnFailure(5));
                options.EnableSensitiveDataLogging(true);
                options.EnableDetailedErrors(true);
                options.ConfigureWarnings(options => options.Default(WarningBehavior.Log));
            });

            services.AddDefaultIdentity<ApplicationUser>(options => options.SignIn.RequireConfirmedAccount = true)
                .AddRoles<IdentityRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>();

            var builder = services.AddIdentityServer()
                .AddApiAuthorization<ApplicationUser, ApplicationDbContext>(options => {
                    options.IdentityResources["openid"].UserClaims.Add("name");
                    options.ApiResources.Single().UserClaims.Add("name");
                    options.IdentityResources["openid"].UserClaims.Add("role");
                    options.ApiResources.Single().UserClaims.Add("role");
                });

            JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Remove("role");

            services.Configure<IdentityOptions>(options =>
            {
                options.ClaimsIdentity.UserIdClaimType = ClaimTypes.NameIdentifier;
                options.ClaimsIdentity.UserIdClaimType = ClaimTypes.Name;
                options.ClaimsIdentity.UserIdClaimType = ClaimTypes.Role;
            }); 

            services.AddAuthentication()
                .AddIdentityServerJwt();

            services.AddHttpContextAccessor();

            services.AddHangfire(config => config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
                .UseColouredConsoleLogProvider()
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseStorage(new MySqlStorage(hangfireConnection, new MySqlStorageOptions
                {
                    TransactionIsolationLevel = IsolationLevel.ReadCommitted,
                    QueuePollInterval = TimeSpan.FromSeconds(15),
                    JobExpirationCheckInterval = TimeSpan.FromHours(1),
                    CountersAggregateInterval = TimeSpan.FromMinutes(5),
                    PrepareSchemaIfNecessary = true,
                    DashboardJobListLimit = 50000,
                    TransactionTimeout = TimeSpan.FromMinutes(1),
                    TablesPrefix = "HF",
                    InvisibilityTimeout = TimeSpan.FromDays(30)
                })));

            services.AddHangfireServer(options => 
            { 
                options.CancellationCheckInterval = TimeSpan.FromSeconds(5); 
                options.WorkerCount = 10;
            });

            services.TryAddTransient<IContextExtension, ContextExtension>();
            //-------------------
            services.TryAddSingleton<ITimeHelper, TimeHelper>();
            services.TryAddSingleton<IProtectionService, ProtectionService>();
            services.TryAddSingleton<IZoneDbHelper, ZoneDbHelper>();
            services.TryAddSingleton<ITradeLogHelper, TradeLogHelper>();
            services.TryAddSingleton<ICandleDbHelper, CandleDbHelper>();
            services.TryAddSingleton<IInstrumentService, InstrumentService>();
            services.TryAddSingleton<ITradeOrderHelper, TradeOrderHelper>();
            services.TryAddSingleton<IOrderUpdatesDbHelper, OrderUpdatesDbHelper>();
            services.TryAddSingleton<ITickDbHelper, TickDbHelper>();
            services.TryAddSingleton<IKiteService, KiteService>();
            //-------------------
            services.TryAddSingleton<IInstrumentHelper, InstrumentHelper>();
            services.TryAddSingleton<ITradeHelper, TradeHelper>();
            services.TryAddSingleton<ITargetHelper, TargetHelper>();
            //-------------------
            services.TryAddSingleton<ITradeabilityService, TradeabilityService>();
            services.TryAddSingleton<IZoneService, ZoneService>();
            services.TryAddSingleton<ITickerService, TickerService>();
            services.TryAddSingleton<ISLMHelper, SLMHelper>();
            services.TryAddSingleton<IWatchingTargetHelper, WatchingTargetHelper>();
            services.TryAddSingleton<IOrderManagerService, OrderManagerService>();
            //-------------------

            services.AddSignalR(options =>
            {
                options.MaximumParallelInvocationsPerClient = 50;
                options.EnableDetailedErrors = true;
                options.ClientTimeoutInterval = TimeSpan.FromMinutes(10);
                options.HandshakeTimeout = TimeSpan.FromMinutes(5);
                options.MaximumReceiveMessageSize = null;
                options.StreamBufferCapacity = 10;
            });

            services.AddControllersWithViews();
            services.AddRazorPages();

            services.AddResponseCompression(opts =>
            {
                opts.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
                    new[] { "application/octet-stream" });
            });

            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.All;
            });

            services.Configure<IdentityOptions>(options =>
            {
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = false;
                options.Password.RequiredLength = 1;
                options.Password.RequiredUniqueChars = 1;

                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.AllowedForNewUsers = true;

                options.User.AllowedUserNameCharacters =
                "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+";
                options.User.RequireUniqueEmail = false;
            });

            services.ConfigureApplicationCookie(options =>
            {
                options.AccessDeniedPath = "/Identity/Account/AccessDenied";
                options.Cookie.Name = "Tradelord-cookie";
                options.Cookie.HttpOnly = true;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(60);
                options.LoginPath = "/Identity/Account/Login";
                options.ReturnUrlParameter = CookieAuthenticationDefaults.ReturnUrlParameter;
                options.SlidingExpiration = true;
            });

            services.Configure<CookieOptions>(options =>
            {
                options.Expires = new DateTimeOffset(new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.AddDays(1).Day, 00, 00, 01));
                options.Secure = true;
                options.MaxAge = new TimeSpan(20, 00, 00);
            });

            services.Configure<PasswordHasherOptions>(option =>
            {
                option.IterationCount = 12000;
            });
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IRecurringJobManager recurringJobManager, IKiteService kiteService, IInstrumentHelper instrumentHelper, IInstrumentService instrumentService, IOrderManagerService orderManagerService)
        {

            app.UseForwardedHeaders();
            app.UseResponseCompression();

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseWebAssemblyDebugging();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }
            ServicePointManager.DefaultConnectionLimit = 50;

            app.UseCertificateForwarding();
            app.UseHttpsRedirection();

            app.UseBlazorFrameworkFiles();
            app.UseStaticFiles();

            app.UseRouting();

            app.UseIdentityServer();
            app.UseAuthentication();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapRazorPages();
                endpoints.MapControllers();
                endpoints.MapHub<OrderHub>("/orderhub");
                endpoints.MapHangfireDashboard(new DashboardOptions
                {
                    Authorization = new[] { new HangfireAutherization() }
                });
                endpoints.MapFallbackToFile("index.html");
            });

            instrumentHelper.LoadInstruments(instrumentService.GetInstruments());
            recurringJobManager.AddOrUpdate("Invalidate-Kite",() => kiteService.InvalidateAll(), Cron.Daily());
            recurringJobManager.AddOrUpdate("Flush-TradeQueue", () => orderManagerService.FlushTradeQueue(), Cron.Daily());
        }
    }
}
