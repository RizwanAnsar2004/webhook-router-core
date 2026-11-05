using Hangfire;
using Hangfire.Redis.StackExchange;
using Microsoft.Extensions.Options;
using WebhookRouter.Middlewares;
using WebhookRouter.Services;
using WebhookRouter.Settings;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSystemd();

#region Settings
builder.Services.AddOptions<HyperPaySettings>()
    .Bind(builder.Configuration.GetSection("HyperPay"))
    .Validate(s => !s.WebhookSecretEnabled
            || (s.WebhookSecret.Trim().Length == 64 && s.WebhookSecret.Trim().All(Uri.IsHexDigit)),
        "HyperPay:WebhookSecret must be the 64-character hex webhook secret from the HyperPay dashboard (or set HyperPay:WebhookSecretEnabled to false).")
    .Validate(s => !string.IsNullOrWhiteSpace(s.IdentifierParameter), "HyperPay:IdentifierParameter is required.")
    .ValidateOnStart();

builder.Services.AddOptions<RoutingSettings>()
    .Bind(builder.Configuration.GetSection("Routing"))
    .Validate(s => Uri.TryCreate(s.DefaultUrl, UriKind.Absolute, out _),
        "Routing:DefaultUrl must be an absolute URL; unmatched webhooks are sent there.")
    .Validate(s => s.Services.All(r => !string.IsNullOrWhiteSpace(r.Identifier) && Uri.TryCreate(r.Url, UriKind.Absolute, out _)),
        "Every Routing:Services entry needs an Identifier and an absolute Url.")
    .ValidateOnStart();

var hangfireSettings = builder.Configuration.GetSection("Hangfire").Get<HangfireSettings>() ?? new HangfireSettings();
var redisConnection = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("ConnectionStrings:Redis is not configured.");
#endregion

#region Hangfire
builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseRedisStorage(redisConnection, new RedisStorageOptions
    {
        Prefix = hangfireSettings.RedisPrefix,
        InvisibilityTimeout = TimeSpan.FromMinutes(30),
        ExpiryCheckInterval = TimeSpan.FromMinutes(5),
    }));

builder.Services.AddHangfireServer(opts =>
{
    opts.ServerName = $"webhookrouter-{Environment.MachineName}";
    opts.WorkerCount = Math.Max(1, hangfireSettings.WorkerCount);
    opts.SchedulePollingInterval = TimeSpan.FromSeconds(Math.Max(1, hangfireSettings.SchedulePollingSeconds));
});

var retry = GlobalJobFilters.Filters.Select(f => f.Instance).OfType<AutomaticRetryAttribute>().Single();
retry.Attempts = hangfireSettings.RetryAttempts;
retry.DelaysInSeconds = hangfireSettings.EffectiveRetryDelaysInSeconds;
#endregion

#region Services
builder.Services.AddSingleton<HyperPayDecryptor>();
builder.Services.AddSingleton<WebhookRouteResolver>();
builder.Services.AddScoped<WebhookIntakeService>();
builder.Services.AddScoped<HyperPayWebhookJob>();
builder.Services.AddHttpClient<WebhookForwarder>((sp, client) =>
{
    var routing = sp.GetRequiredService<IOptions<RoutingSettings>>().Value;
    client.Timeout = TimeSpan.FromSeconds(Math.Max(1, routing.TimeoutSeconds));
});

builder.Services.AddControllers(o => o.Filters.Add<ResponseWrapperFilter>());
builder.Services.AddOpenApi();
#endregion

var app = builder.Build();

if (app.Environment.IsDevelopment()) app.MapOpenApi();

app.UseMiddleware<ExceptionHandlerMiddleware>();
app.MapControllers();
app.MapGet("/v1/api/health", () => new { Status = "Running", Message = "Webhook router is up!" })
    .WithSummary("Health check");

if (hangfireSettings.DashboardEnabled) app.MapHangfireDashboard("/hangfire");

app.Run();
