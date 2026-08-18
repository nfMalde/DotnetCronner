using DotnetCronner;

namespace CronTestApp.Configuration;

/// <summary>Which <c>ICronnerStore</c> implementation backs the scheduler.</summary>
public enum StoreKind
{
    /// <summary>The built-in in-memory store (no external service needed).</summary>
    Memory,

    /// <summary>The Redis store from DotnetCronner.Stores.Redis.</summary>
    Redis,

    /// <summary>The EF Core store using the package's ready-made <c>CronnerDbContext</c>.</summary>
    EfPostgres,

    /// <summary>The EF Core store using this app's own <c>AppDbContext : ICronnerDbContext</c>.</summary>
    EfPostgresAppContext,

    /// <summary>This app's hand-written <c>ICronnerStore</c> (JSON file), registered with <c>UseStore&lt;T&gt;()</c>.</summary>
    Custom,

    /// <summary>
    /// A store holding one open SQLite connection with no internal locking, registered
    /// <c>Scoped</c> — each scheduler operation gets its own instance. The supported way to back the
    /// scheduler with a scoped, non-concurrent resource.
    /// </summary>
    Scoped,

    /// <summary>
    /// The SAME store registered <c>Singleton</c>, so overlapping scheduler operations share one
    /// connection. Present to demonstrate the failure the scoped lifetime exists to prevent — expect
    /// concurrent-access errors under load. Not a configuration to copy.
    /// </summary>
    ScopedBroken,
}

/// <summary>Whether a second-level cache sits in front of the store.</summary>
public enum CacheKind
{
    /// <summary>No second-level cache.</summary>
    None,

    /// <summary>Redis second-level cache (<c>UseSecondLevelCache(c =&gt; c.UseRedisCacheProvider(...))</c>).</summary>
    Redis,
}

/// <summary>How <c>[CronnerTask]</c> methods are discovered.</summary>
public enum DiscoveryMode
{
    /// <summary>Entry assembly (the default) plus the external jobs assembly.</summary>
    Auto,

    /// <summary>Same assemblies, but restricted to types implementing <c>IScheduledJob</c>.</summary>
    Filtered,

    /// <summary>No scanning at all — only the <c>Sched(...)</c> lambdas are registered.</summary>
    Off,
}

/// <summary>Which service provider jobs are resolved from.</summary>
public enum JobServicesMode
{
    /// <summary>The application's provider (a fresh scope per run).</summary>
    App,

    /// <summary>An isolated container built with <c>WithDedicatedDI(...)</c>.</summary>
    Dedicated,
}

/// <summary>
/// Everything the .env file steers, parsed once at startup. Keys are read from the environment (and
/// therefore also from <c>.env</c>, which <see cref="DotEnvFile"/> loads before the host is built).
/// </summary>
public sealed class TestAppOptions
{
    /// <summary>The store implementation to use.</summary>
    public required StoreKind Store { get; init; }

    /// <summary>The second-level cache to put in front of the store.</summary>
    public required CacheKind Cache { get; init; }

    /// <summary>How attribute tasks are discovered.</summary>
    public required DiscoveryMode Discovery { get; init; }

    /// <summary>Where job dependencies are resolved from.</summary>
    public required JobServicesMode JobServices { get; init; }

    /// <summary>Time zone cron expressions are evaluated in.</summary>
    public required TimeZoneInfo TimeZone { get; init; }

    /// <summary>How often the store is polled for due tasks.</summary>
    public required TimeSpan PollingInterval { get; init; }

    /// <summary>Global concurrent execution limit.</summary>
    public required int MaxConcurrentTasks { get; init; }

    /// <summary>How long an execution claim is held before a stalled task may be reclaimed.</summary>
    public required TimeSpan LockTtl { get; init; }

    /// <summary>Automatic retries after a failed execution.</summary>
    public required int MaxRetries { get; init; }

    /// <summary>Delay between retries.</summary>
    public required TimeSpan RetryDelay { get; init; }

    /// <summary>StackExchange.Redis connection string.</summary>
    public required string RedisConnection { get; init; }

    /// <summary>Key prefix for everything DotnetCronner writes to Redis.</summary>
    public required string RedisKeyPrefix { get; init; }

    /// <summary>Optional expiry for second-level cache entries.</summary>
    public TimeSpan? RedisCacheTtl { get; init; }

    /// <summary>Npgsql connection string for the EF Core stores.</summary>
    public required string PostgresConnection { get; init; }

    /// <summary>File the custom JSON store persists to.</summary>
    public required string CustomStoreFile { get; init; }

    /// <summary>Keep only the newest N finished one-off (enqueued) instances per definition; 0 keeps them all.</summary>
    public int OneOffRetention { get; init; }

    /// <summary>Record execution history, keeping the newest N runs per task; 0 (the default) disables it.</summary>
    public int ExecutionHistory { get; init; }

    /// <summary>Which DI scope the terminal lifecycle hooks run in: shared (job's scope) or isolated.</summary>
    public CronnerHookScope HookScope { get; init; }

    /// <summary>What to do about a cron that parses but never fires: mark the task Failed, or throw at startup.</summary>
    public CronnerInvalidScheduleBehavior OnInvalidSchedule { get; init; }

    /// <summary>Pins the keepalive / <c>OnKeepAlive</c> cadence, or <c>null</c> to use the default <c>LockTtl</c>/2.</summary>
    public TimeSpan? KeepAliveInterval { get; init; }

    /// <summary>
    /// How long the deliberately slow <c>OnKeepAlive</c> hook blocks, or <see cref="TimeSpan.Zero"/> to
    /// leave it out. Set it above <c>LockTtl</c>/2 to prove that a slow hook cannot stretch the renewal
    /// cadence (the scheduler fires keepalive hooks detached from the renewal loop).
    /// </summary>
    public TimeSpan SlowKeepAliveDelay { get; init; }

    /// <summary>True when the selected store needs PostgreSQL.</summary>
    public bool UsesPostgres => Store is StoreKind.EfPostgres or StoreKind.EfPostgresAppContext;

    /// <summary>True when Redis is needed either as the store or as the cache.</summary>
    public bool UsesRedis => Store is StoreKind.Redis || Cache is CacheKind.Redis;

    /// <summary>Reads and validates the configuration from the environment.</summary>
    public static TestAppOptions FromConfiguration(IConfiguration configuration) => new()
    {
        Store = ReadEnum(configuration, "CRONNER_STORE", StoreKind.Memory, new Dictionary<string, StoreKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["memory"] = StoreKind.Memory,
            ["redis"] = StoreKind.Redis,
            ["ef-postgres"] = StoreKind.EfPostgres,
            ["ef-postgres-appcontext"] = StoreKind.EfPostgresAppContext,
            ["custom"] = StoreKind.Custom,
            ["scoped"] = StoreKind.Scoped,
            ["scoped-broken"] = StoreKind.ScopedBroken,
        }),
        Cache = ReadEnum(configuration, "CRONNER_CACHE", CacheKind.None, new Dictionary<string, CacheKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["none"] = CacheKind.None,
            ["redis"] = CacheKind.Redis,
        }),
        Discovery = ReadEnum(configuration, "CRONNER_DISCOVERY", DiscoveryMode.Auto, new Dictionary<string, DiscoveryMode>(StringComparer.OrdinalIgnoreCase)
        {
            ["auto"] = DiscoveryMode.Auto,
            ["filtered"] = DiscoveryMode.Filtered,
            ["off"] = DiscoveryMode.Off,
        }),
        JobServices = ReadEnum(configuration, "CRONNER_JOB_SERVICES", JobServicesMode.App, new Dictionary<string, JobServicesMode>(StringComparer.OrdinalIgnoreCase)
        {
            ["app"] = JobServicesMode.App,
            ["dedicated"] = JobServicesMode.Dedicated,
        }),
        TimeZone = ReadTimeZone(configuration, "CRONNER_TIMEZONE"),
        PollingInterval = TimeSpan.FromMilliseconds(ReadInt(configuration, "CRONNER_POLLING_MS", 1000)),
        MaxConcurrentTasks = ReadInt(configuration, "CRONNER_MAX_CONCURRENT", Environment.ProcessorCount),
        LockTtl = TimeSpan.FromSeconds(ReadInt(configuration, "CRONNER_LOCK_TTL_SECONDS", 60)),
        MaxRetries = ReadInt(configuration, "CRONNER_MAX_RETRIES", 0),
        RetryDelay = TimeSpan.FromSeconds(ReadInt(configuration, "CRONNER_RETRY_DELAY_SECONDS", 0)),
        RedisConnection = ReadString(configuration, "CRONNER_REDIS", "localhost:6379"),
        RedisKeyPrefix = ReadString(configuration, "CRONNER_REDIS_KEY_PREFIX", "cronner:"),
        RedisCacheTtl = ReadInt(configuration, "CRONNER_REDIS_CACHE_TTL_SECONDS", 0) is var ttl && ttl > 0
            ? TimeSpan.FromSeconds(ttl)
            : null,
        PostgresConnection = ReadString(
            configuration, "CRONNER_POSTGRES", "Host=localhost;Port=5432;Database=cronner;Username=cronner;Password=cronner"),
        CustomStoreFile = ReadString(configuration, "CRONNER_CUSTOM_STORE_FILE", "cronner-store.json"),
        OneOffRetention = ReadInt(configuration, "CRONNER_ONEOFF_RETENTION", 20),
        ExecutionHistory = ReadInt(configuration, "CRONNER_EXEC_HISTORY", 20),
        HookScope = ReadEnum(configuration, "CRONNER_HOOK_SCOPE", CronnerHookScope.Shared, new Dictionary<string, CronnerHookScope>(StringComparer.OrdinalIgnoreCase)
        {
            ["shared"] = CronnerHookScope.Shared,
            ["isolated"] = CronnerHookScope.Isolated,
        }),
        OnInvalidSchedule = ReadEnum(configuration, "CRONNER_INVALID_SCHEDULE", CronnerInvalidScheduleBehavior.MarkFailed, new Dictionary<string, CronnerInvalidScheduleBehavior>(StringComparer.OrdinalIgnoreCase)
        {
            ["mark-failed"] = CronnerInvalidScheduleBehavior.MarkFailed,
            ["throw"] = CronnerInvalidScheduleBehavior.Throw,
        }),
        KeepAliveInterval = ReadInt(configuration, "CRONNER_KEEPALIVE_SECONDS", 0) is var kai && kai > 0
            ? TimeSpan.FromSeconds(kai)
            : null,
        SlowKeepAliveDelay = TimeSpan.FromMilliseconds(ReadInt(configuration, "CRONNER_SLOW_KEEPALIVE_MS", 0)),
    };

    /// <summary>A JSON-friendly summary of the active configuration, served from <c>GET /config</c>.</summary>
    public object Describe() => new
    {
        store = Store.ToString(),
        secondLevelCache = Cache.ToString(),
        discovery = Discovery.ToString(),
        jobServices = JobServices.ToString(),
        timeZone = TimeZone.Id,
        pollingInterval = PollingInterval.ToString(),
        maxConcurrentTasks = MaxConcurrentTasks,
        lockTtl = LockTtl.ToString(),
        maxRetries = MaxRetries,
        retryDelay = RetryDelay.ToString(),
        keepAliveInterval = (KeepAliveInterval ?? TimeSpan.FromMilliseconds(Math.Max(1000, LockTtl.TotalMilliseconds / 2))).ToString(),
        oneOffRetention = OneOffRetention == 0 ? "keep all" : OneOffRetention.ToString(),
        executionHistory = ExecutionHistory == 0 ? "off" : $"keep newest {ExecutionHistory} per task",
        hookScope = HookScope.ToString(),
        onInvalidSchedule = OnInvalidSchedule.ToString(),
        slowKeepAliveHook = SlowKeepAliveDelay > TimeSpan.Zero ? SlowKeepAliveDelay.ToString() : "off",
        redisConnection = UsesRedis ? RedisConnection : null,
        redisKeyPrefix = UsesRedis ? RedisKeyPrefix : null,
        redisCacheTtl = Cache is CacheKind.Redis ? RedisCacheTtl?.ToString() ?? "no expiry" : null,
        postgresConnection = UsesPostgres ? Redact(PostgresConnection) : null,
        customStoreFile = Store is StoreKind.Custom ? Path.GetFullPath(CustomStoreFile) : null,
    };

    private static string Redact(string connectionString) =>
        string.Join(';', connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.StartsWith("Password", StringComparison.OrdinalIgnoreCase) ? "Password=***" : part));

    private static string ReadString(IConfiguration configuration, string key, string fallback)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return int.TryParse(value.Trim(), out var parsed)
            ? parsed
            : throw new InvalidOperationException($"{key}='{value}' is not a whole number.");
    }

    private static TEnum ReadEnum<TEnum>(
        IConfiguration configuration, string key, TEnum fallback, IReadOnlyDictionary<string, TEnum> allowed)
        where TEnum : struct, Enum
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return allowed.TryGetValue(value.Trim(), out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"{key}='{value.Trim()}' is not supported. Use one of: {string.Join(", ", allowed.Keys)}.");
    }

    private static TimeZoneInfo ReadTimeZone(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value) || value.Equals("UTC", StringComparison.OrdinalIgnoreCase))
            return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(value.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException($"{key}='{value.Trim()}' is not a known time zone id.", ex);
        }
    }
}
