using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using StackExchange.Redis.Profiling;
using Ume.LlmGateway.Gateway.Pipeline;

namespace Ume.LlmGateway.Benchmarks;

/// <summary>
/// Counts what one request costs in external round trips: Postgres commands issued on the request path (vs. by the
/// background usage writer) and Redis commands, per scenario. Then a short concurrent load run reports throughput
/// and tail latency with short cache TTLs, which is where cache-expiry stalls show up.
/// Run with <c>dotnet run -c Release --project benchmarks/Ume.LlmGateway.Benchmarks -- audit</c>.
/// </summary>
public static class InteractionAudit
{
    private const string RequestActivity = "Microsoft.AspNetCore.Hosting.HttpRequestIn";

    public static async Task RunAsync(int requests, TimeSpan loadDuration, int concurrency)
    {
        var dbOnRequestPath = 0;
        var dbInBackground = 0;
        var statements = new ConcurrentDictionary<string, int>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Npgsql" or "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.Source.Name != "Npgsql")
                {
                    return;
                }

                var onRequestPath = false;
                for (var parent = activity.Parent; parent is not null; parent = parent.Parent)
                {
                    onRequestPath |= parent.OperationName == RequestActivity;
                }

                if (onRequestPath)
                {
                    Interlocked.Increment(ref dbOnRequestPath);
                    var sql = activity.GetTagItem("db.query.text") as string ?? activity.GetTagItem("db.statement") as string ?? activity.DisplayName;
                    statements.AddOrUpdate(Summarise(sql), 1, (_, n) => n + 1);
                }
                else
                {
                    Interlocked.Increment(ref dbInBackground);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        Console.WriteLine("Starting Postgres + Redis containers and the gateway…");
        await using (var host = await GatewayHost.StartAsync(StoreKind.Redis))
        {
            var redis = host.Services.GetRequiredService<IConnectionMultiplexer>();
            var session = new ProfilingSession();
            redis.RegisterProfiler(() => session);
            var writer = host.Services.GetRequiredService<UsageWriter>();
            var keys = host.Services.GetRequiredService<KeyAuthenticator>();
            var catalog = host.Services.GetRequiredService<GatewayCatalog>();

            var chat = Encoding.UTF8.GetBytes(Payloads.ChatRequest(GatewayHost.ChatAlias));
            var stream = Encoding.UTF8.GetBytes(Payloads.ChatRequest(GatewayHost.ChatAlias, stream: true));
            var embeddings = Encoding.UTF8.GetBytes(Payloads.EmbeddingsRequest(GatewayHost.EmbeddingsAlias));
            (string Name, string Path, byte[] Body, bool Cold)[] scenarios =
            [
                ("chat (warm caches)", "/v1/chat/completions", chat, false),
                ("chat stream (warm caches)", "/v1/chat/completions", stream, false),
                ("embeddings (warm caches)", "/v1/embeddings", embeddings, false),
                ("chat (key + catalog cache miss)", "/v1/chat/completions", chat, true),
            ];

            Console.WriteLine();
            Console.WriteLine($"Per-request external calls, sequential, {requests} requests per scenario:");
            Console.WriteLine();
            Console.WriteLine($"| {"Scenario",-34} | {"Mean ms",8} | {"p99 ms",8} | {"PG on request path",18} | {"PG background",13} | {"Redis cmds",10} | Redis breakdown");
            Console.WriteLine($"|{new string('-', 36)}|{new string('-', 10)}|{new string('-', 10)}|{new string('-', 20)}|{new string('-', 15)}|{new string('-', 12)}|----------------");
            var coldStatements = new Dictionary<string, int>();
            foreach (var scenario in scenarios)
            {
                for (var i = 0; i < 20; i++)
                {
                    await host.SendAsync(scenario.Path, scenario.Body);
                }

                await writer.FlushAsync(CancellationToken.None);
                session.FinishProfiling();
                Interlocked.Exchange(ref dbOnRequestPath, 0);
                Interlocked.Exchange(ref dbInBackground, 0);
                statements.Clear();

                var latencies = new double[requests];
                for (var i = 0; i < requests; i++)
                {
                    if (scenario.Cold)
                    {
                        keys.InvalidateAll();
                        catalog.Invalidate();
                    }

                    var started = Stopwatch.GetTimestamp();
                    await host.SendAsync(scenario.Path, scenario.Body);
                    latencies[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }

                await writer.FlushAsync(CancellationToken.None);
                var commands = session.FinishProfiling().ToList();
                var breakdown = string.Join(", ", commands.GroupBy(c => c.Command).OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Key} {g.Count() / (double)requests:0.##}"));
                Array.Sort(latencies);
                Console.WriteLine($"| {scenario.Name,-34} | {latencies.Average(),8:0.000} | {latencies[(int)(requests * 0.99)],8:0.000} | {dbOnRequestPath / (double)requests,18:0.##} | {dbInBackground / (double)requests,13:0.##} | {commands.Count / (double)requests,10:0.##} | {breakdown}");
                if (scenario.Cold)
                {
                    coldStatements = statements.ToDictionary();
                }
            }

            if (coldStatements.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Postgres statements per cache miss (key + catalog reload):");
                foreach (var (sql, count) in coldStatements.OrderByDescending(s => s.Value))
                {
                    Console.WriteLine($"  {count / (double)requests,5:0.##} × {sql}");
                }
            }
        }

        await RunLoadAsync(loadDuration, concurrency);
    }

    /// <summary>Concurrent load with 1 s cache TTLs so key/catalog expiry happens many times during the run.</summary>
    private static async Task RunLoadAsync(TimeSpan duration, int concurrency)
    {
        await using var host = await GatewayHost.StartAsync(StoreKind.Redis, new Dictionary<string, string>
        {
            ["Gateway:KeyCacheSeconds"] = "1",
            ["Gateway:CatalogCacheSeconds"] = "1",
        });
        var chat = Encoding.UTF8.GetBytes(Payloads.ChatRequest(GatewayHost.ChatAlias, stream: true));
        for (var i = 0; i < 200; i++)
        {
            await host.SendAsync("/v1/chat/completions", chat);
        }

        var latencies = new ConcurrentBag<double>();
        var deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        var started = Stopwatch.GetTimestamp();
        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            while (Stopwatch.GetTimestamp() < deadline)
            {
                var t = Stopwatch.GetTimestamp();
                await host.SendAsync("/v1/chat/completions", chat);
                latencies.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            }
        })));
        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var sorted = latencies.Order().ToArray();
        Console.WriteLine();
        Console.WriteLine($"Concurrent load: streaming chat, {concurrency} clients, {duration.TotalSeconds:0} s, key/catalog TTL 1 s, Redis:");
        Console.WriteLine($"  {sorted.Length / elapsed:0} req/s   p50 {sorted[sorted.Length / 2]:0.00} ms   p99 {sorted[(int)(sorted.Length * 0.99)]:0.00} ms   p99.9 {sorted[(int)(sorted.Length * 0.999)]:0.00} ms   max {sorted[^1]:0.00} ms");
    }

    private static string Summarise(string sql)
    {
        var line = sql.ReplaceLineEndings(" ");
        var from = line.IndexOf(" FROM ", StringComparison.OrdinalIgnoreCase);
        var table = from < 0 ? line : line[(from + 6)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return (line.Split(' ')[0] + " " + table).Trim();
    }
}
