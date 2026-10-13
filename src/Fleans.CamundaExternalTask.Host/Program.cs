using Fleans.Plugins.CamundaExternalTask;
using Fleans.Worker.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using StackExchange.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;

// Plugin host for Fleans.Plugins.CamundaExternalTask. Joins the Fleans engine's Orleans cluster
// as a Plugin-role silo (same contract as github.com/nightBaker/fleans-custom-worker-example),
// so Orleans places the plugin's stream-handler grain only here, and serves the Camunda 7
// /engine-rest/external-task API for existing external-task workers.
//
// Joins the cluster through the engine's Redis (ConnectionStrings:orleans-redis) with the engine's
// Orleans__ClusterId / Orleans__ServiceId.

var builder = WebApplication.CreateBuilder(args);
builder.Configuration["Fleans:Role"] ??= "Plugin";

builder.AddKeyedRedisClient("orleans-redis");

// Only the Redis stream provider (the engine's default) is wired. Fail fast rather than
// silently listening on a provider the engine isn't publishing to.
var streamingProvider = builder.Configuration["Fleans:Streaming:Provider"] ?? "Redis";
if (!streamingProvider.Equals("Redis", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException(
        $"This host supports only the Redis stream provider (Fleans:Streaming:Provider='{streamingProvider}').");

// The Redis stream provider resolves a non-keyed multiplexer; alias the keyed one.
builder.Services.TryAddSingleton<IConnectionMultiplexer>(sp =>
    sp.GetRequiredKeyedService<IConnectionMultiplexer>("orleans-redis"));

// Must match the engine's stream settings — a different TotalQueueCount hashes stream ids onto
// different queues than the engine silos publish to. Same key and default as the engine, so the
// engine's Fleans__Streaming__Redis__TotalQueueCount applies here unchanged.
const string StreamProviderName = "StreamProvider";
builder.Services.AddOptions<HashRingStreamQueueMapperOptions>(StreamProviderName)
    .Configure(o => o.TotalQueueCount = builder.Configuration.GetValue("Fleans:Streaming:Redis:TotalQueueCount", 8));
builder.Services.AddOptions<SimpleQueueCacheOptions>(StreamProviderName);
builder.Services.AddOptions<RedisStreamReceiverOptions>(StreamProviderName)
    .Configure(o =>
    {
        o.MaxStreamLength = 1000;
        o.TrimTimeMinutes = 5;
    });

builder.UseOrleans(silo =>
{
    silo.AddFleansPluginHost(builder.Configuration);

    // Every silo serves a slice of the reminder ring, including engine reminders (BPMN timers);
    // use the engine's Redis reminder table so those keep their durability.
    var orleansRedis = builder.Configuration.GetConnectionString("orleans-redis")
        ?? throw new InvalidOperationException("ConnectionStrings:orleans-redis is required.");
    silo.UseRedisReminderService(o => o.ConfigurationOptions = ConfigurationOptions.Parse(orleansRedis));

    // Aspire injects Orleans__Clustering__* / Orleans__GrainStorage__PubSubStore__*; the engine's
    // compose bundle and Helm chart only pass the Redis connection string and cluster ids.
    if (string.IsNullOrEmpty(builder.Configuration["Orleans:Clustering:ProviderType"]))
    {
        silo.UseRedisClustering(orleansRedis);
        silo.AddRedisGrainStorage("PubSubStore",
            o => o.ConfigurationOptions = ConfigurationOptions.Parse(orleansRedis));
    }

    silo.AddPersistentStreams(StreamProviderName, RedisStreamFactory.Create, null);
});

// External-task table: CamundaExternalTasks:Provider = Postgres (default) | Sqlite,
// connection string "camunda-external-tasks".
var connectionString = builder.Configuration.GetConnectionString("camunda-external-tasks")
    ?? throw new InvalidOperationException("ConnectionStrings:camunda-external-tasks is required.");
var provider = builder.Configuration["CamundaExternalTasks:Provider"] ?? "Postgres";
builder.Services.AddCamundaExternalTasks(options =>
{
    if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        options.UseSqlite(connectionString);
    else if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        options.UseNpgsql(connectionString);
    else
        throw new InvalidOperationException(
            $"CamundaExternalTasks:Provider must be 'Postgres' or 'Sqlite' (got '{provider}').");
});

builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/health");
// Anonymous unless CamundaExternalTasks:BasicAuth:Username and :Password are both set.
// For anything richer, fork and add ASP.NET authentication + .RequireAuthorization().
var api = app.MapCamundaExternalTaskApi();
var basicAuthUser = app.Configuration["CamundaExternalTasks:BasicAuth:Username"];
var basicAuthPassword = app.Configuration["CamundaExternalTasks:BasicAuth:Password"];
if (!string.IsNullOrEmpty(basicAuthUser) || !string.IsNullOrEmpty(basicAuthPassword))
{
    if (string.IsNullOrEmpty(basicAuthUser) || string.IsNullOrEmpty(basicAuthPassword))
        throw new InvalidOperationException(
            "Set both CamundaExternalTasks:BasicAuth:Username and CamundaExternalTasks:BasicAuth:Password, or neither.");
    api.RequireBasicAuthentication(basicAuthUser, basicAuthPassword);
}
app.Run();
