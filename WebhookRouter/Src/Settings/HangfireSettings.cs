namespace WebhookRouter.Settings;

public class HangfireSettings
{
    public string RedisPrefix { get; set; } = "webhookrouter:hangfire:";
    public int WorkerCount { get; set; } = 4;
    public int RetryAttempts { get; set; } = 10;
    public static readonly int[] DefaultRetryDelaysInSeconds = [10, 30, 60, 120, 300, 600, 1800, 3600, 7200, 14400];

    public int[] RetryDelaysInSeconds { get; set; } = [];

    public int[] EffectiveRetryDelaysInSeconds
        => RetryDelaysInSeconds.Length > 0 ? RetryDelaysInSeconds : DefaultRetryDelaysInSeconds;
    public int SchedulePollingSeconds { get; set; } = 5;
    public bool DashboardEnabled { get; set; }
}
