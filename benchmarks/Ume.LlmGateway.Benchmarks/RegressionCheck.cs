using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using StackExchange.Redis.Profiling;
using Ume.LlmGateway.Gateway.Authentication;
using Ume.LlmGateway.Gateway.Routing;
using Ume.LlmGateway.Gateway.Usage;

namespace Ume.LlmGateway.Benchmarks;

/// <summary>
/// Pre-release regression guard: measures what one request costs in allocations and in external calls and compares it
/// with <c>regression-baseline.json</c>. Time is not gated (it is noisy on shared machines; the same benchmark measured
/// 479 and 766 µs in two runs of one day), allocations and call counts are stable. A scenario fails when its allocated
/// bytes per request grow by more than 20 %, when a request makes a Postgres statement or Redis command the baseline
/// did not, or when a scenario is missing. <c>check --update</c> rewrites the baseline after an intended change.
/// Run with <c>dotnet run -c Release --project benchmarks/Ume.LlmGateway.Benchmarks -- check</c>.
/// </summary>
public static class RegressionCheck
{
    private const string RequestActivity = "Microsoft.AspNetCore.Hosting.HttpRequestIn";
    private const double AllocationTolerance = 0.20;
    private const double CallTolerance = 0.02;
    private const int Warmup = 30;
    private const int Requests = 300;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public sealed record Measurement(double AllocatedBytes, double PostgresOnRequestPath, double RedisCommands);

    public static async Task<int> RunAsync(string baselinePath, bool update)
    {
        Console.WriteLine("Starting Postgres + Redis containers and the gateway…");
        var results = new SortedDictionary<string, Measurement>(StringComparer.Ordinal);
        await using (var host = await GatewayHost.StartAsync(StoreKind.Redis))
        {
            var keys = host.Services.GetRequiredService<KeyAuthenticator>();
            var catalog = host.Services.GetRequiredService<GatewayCatalog>();
            var writer = host.Services.GetRequiredService<UsageWriter>();
            var redis = host.Services.GetRequiredService<IConnectionMultiplexer>();
            var chat = Encoding.UTF8.GetBytes(Payloads.ChatRequest(GatewayHost.ChatAlias));
            var stream = Encoding.UTF8.GetBytes(Payloads.ChatRequest(GatewayHost.ChatAlias, stream: true));
            var embeddings = Encoding.UTF8.GetBytes(Payloads.EmbeddingsRequest(GatewayHost.EmbeddingsAlias));
            var unknownKey = "ume-sk-" + new string('Q', host.Key.Length - "ume-sk-".Length);
            (string Name, string Path, byte[] Body, bool Cold, string? Key, int? Status)[] scenarios =
            [
                ("chat (warm caches)", "/v1/chat/completions", chat, false, null, null),
                ("chat stream (warm caches)", "/v1/chat/completions", stream, false, null, null),
                ("embeddings (warm caches)", "/v1/embeddings", embeddings, false, null, null),
                ("chat (key + catalog cache miss)", "/v1/chat/completions", chat, true, null, null),
                ("chat, unknown key (401)", "/v1/chat/completions", chat, false, unknownKey, 401),
            ];

            async Task<int> Send((string Name, string Path, byte[] Body, bool Cold, string? Key, int? Status) s)
            {
                if (s.Cold)
                {
                    keys.InvalidateAll();
                    catalog.Invalidate();
                }

                return await host.SendAsync(s.Path, s.Body, s.Key, s.Status);
            }

            // Pass 1, nothing attached: allocations. The usage writer's work is part of a request's cost, so flush it inside.
            var allocated = new Dictionary<string, double>();
            foreach (var scenario in scenarios)
            {
                for (var i = 0; i < Warmup; i++)
                {
                    await Send(scenario);
                }

                await writer.FlushAsync(CancellationToken.None);
                GC.Collect();
                var before = GC.GetTotalAllocatedBytes(precise: true);
                for (var i = 0; i < Requests; i++)
                {
                    await Send(scenario);
                }

                await writer.FlushAsync(CancellationToken.None);
                allocated[scenario.Name] = (GC.GetTotalAllocatedBytes(precise: true) - before) / (double)Requests;
            }

            // Pass 2, profilers attached: Postgres statements on the request path, Redis commands.
            var onRequestPath = 0;
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

                    for (var parent = activity.Parent; parent is not null; parent = parent.Parent)
                    {
                        if (parent.OperationName == RequestActivity)
                        {
                            Interlocked.Increment(ref onRequestPath);
                            return;
                        }
                    }
                },
            };
            ActivitySource.AddActivityListener(listener);
            var session = new ProfilingSession();
            redis.RegisterProfiler(() => session);

            foreach (var scenario in scenarios)
            {
                for (var i = 0; i < Warmup; i++)
                {
                    await Send(scenario);
                }

                await writer.FlushAsync(CancellationToken.None);
                session.FinishProfiling();
                Interlocked.Exchange(ref onRequestPath, 0);
                for (var i = 0; i < Requests; i++)
                {
                    await Send(scenario);
                }

                await writer.FlushAsync(CancellationToken.None);
                var commands = session.FinishProfiling().Count();
                results[scenario.Name] = new Measurement(allocated[scenario.Name], onRequestPath / (double)Requests, commands / (double)Requests);
            }
        }

        Print(results);
        if (update)
        {
            await File.WriteAllTextAsync(baselinePath, JsonSerializer.Serialize(results, Json) + Environment.NewLine);
            Console.WriteLine($"Baseline written to {baselinePath}.");
            return 0;
        }

        if (!File.Exists(baselinePath))
        {
            Console.Error.WriteLine($"No baseline at {baselinePath}; run with --update to create it.");
            return 2;
        }

        var baseline = JsonSerializer.Deserialize<Dictionary<string, Measurement>>(await File.ReadAllTextAsync(baselinePath)) ?? [];
        var failures = Compare(baseline, results);
        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("No regression against the baseline.");
            return 0;
        }

        foreach (var failure in failures)
        {
            Console.Error.WriteLine("REGRESSION: " + failure);
        }

        return 1;
    }

    public static List<string> Compare(IReadOnlyDictionary<string, Measurement> baseline, IReadOnlyDictionary<string, Measurement> current)
    {
        var failures = new List<string>();
        foreach (var (name, expected) in baseline)
        {
            if (!current.TryGetValue(name, out var actual))
            {
                failures.Add($"{name}: scenario is no longer measured");
                continue;
            }

            if (actual.AllocatedBytes > expected.AllocatedBytes * (1 + AllocationTolerance))
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{name}: {actual.AllocatedBytes / 1024:0.0} KB allocated per request, baseline {expected.AllocatedBytes / 1024:0.0} KB (+{(actual.AllocatedBytes / expected.AllocatedBytes - 1) * 100:0} %, limit {AllocationTolerance * 100:0} %)"));
            }

            if (actual.PostgresOnRequestPath > expected.PostgresOnRequestPath + CallTolerance)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{name}: {actual.PostgresOnRequestPath:0.##} Postgres statements per request, baseline {expected.PostgresOnRequestPath:0.##}"));
            }

            if (actual.RedisCommands > expected.RedisCommands + CallTolerance)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{name}: {actual.RedisCommands:0.##} Redis commands per request, baseline {expected.RedisCommands:0.##}"));
            }
        }

        foreach (var name in current.Keys.Where(n => !baseline.ContainsKey(n)))
        {
            failures.Add($"{name}: not in the baseline; run check --update");
        }

        return failures;
    }

    private static void Print(IReadOnlyDictionary<string, Measurement> results)
    {
        Console.WriteLine();
        Console.WriteLine($"| {"Scenario",-34} | {"KB allocated",12} | {"PG on request path",18} | {"Redis cmds",10} |");
        Console.WriteLine($"|{new string('-', 36)}|{new string('-', 13)}:|{new string('-', 19)}:|{new string('-', 11)}:|");
        foreach (var (name, m) in results)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {name,-34} | {m.AllocatedBytes / 1024,12:0.0} | {m.PostgresOnRequestPath,18:0.##} | {m.RedisCommands,10:0.##} |"));
        }
    }
}
