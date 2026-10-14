using BenchmarkDotNet.Running;

namespace Ume.LlmGateway.Benchmarks;

/// <summary>
/// <c>dotnet run -c Release -- --filter *</c> runs BenchmarkDotNet (see <c>--help</c>); <c>audit</c> counts Postgres and
/// Redis round trips per request and runs a short load test. Both need Docker for the Postgres/Redis containers.
/// </summary>
public static class BenchmarkProgram
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["audit", ..])
        {
            var requests = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 500;
            await InteractionAudit.RunAsync(requests, loadDuration: TimeSpan.FromSeconds(15), concurrency: 64);
            return 0;
        }

        if (args is ["stream-probe", var target, var baseUrl, var vus, var seconds, var outPath, ..])
        {
            return await StreamProbe.RunAsync(target, baseUrl, int.Parse(vus, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture), outPath, args.Length > 6 ? args[6] : null);
        }

        if (args is ["compare-seed", var connectionString, var pepper, var upstream])
        {
            return await CompareSeed.RunAsync(connectionString, pepper, upstream);
        }

        BenchmarkSwitcher.FromAssembly(typeof(BenchmarkProgram).Assembly).Run(args);
        return 0;
    }
}
