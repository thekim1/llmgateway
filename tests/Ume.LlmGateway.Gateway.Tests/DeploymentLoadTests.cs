using Ume.LlmGateway.Gateway.Pipeline;

namespace Ume.LlmGateway.Gateway.Tests;

public sealed class DeploymentLoadTests
{
    [Fact]
    public void Unused_deployment_scores_best()
    {
        var load = new DeploymentLoad();
        var used = Guid.NewGuid();
        using (var lease = load.Start(used))
        {
            lease.Complete(healthy: true, TimeSpan.FromMilliseconds(200));
        }

        load.Score(Guid.NewGuid()).ShouldBe(1);
        load.Score(used).ShouldBeLessThan(1);
    }

    [Fact]
    public void Slow_failing_and_busy_deployments_score_lower()
    {
        var load = new DeploymentLoad();
        var (fast, slow, failing) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        for (var i = 0; i < 5; i++)
        {
            Record(load, fast, healthy: true, ms: 100);
            Record(load, slow, healthy: true, ms: 2000);
            Record(load, failing, healthy: false, ms: 100);
        }

        load.Score(fast).ShouldBeGreaterThan(load.Score(slow));
        load.Score(fast).ShouldBeGreaterThan(load.Score(failing));

        var idle = load.Score(fast);
        using var a = load.Start(fast);
        using var b = load.Start(fast);
        load.Score(fast).ShouldBeLessThan(idle); // requests in flight count against it
    }

    [Fact]
    public void Lease_counts_once_and_ends_once()
    {
        var load = new DeploymentLoad();
        var id = Guid.NewGuid();
        Record(load, id, healthy: true, ms: 1000);
        var settled = load.Score(id);

        var lease = load.Start(id);
        lease.Complete(healthy: true, TimeSpan.FromMilliseconds(1000));
        lease.Complete(healthy: false, TimeSpan.FromSeconds(60)); // ignored
        lease.Dispose();
        lease.Dispose(); // in flight does not go negative

        load.Score(id).ShouldBe(settled, tolerance: 1e-9);
    }

    private static void Record(DeploymentLoad load, Guid id, bool healthy, int ms)
    {
        using var lease = load.Start(id);
        lease.Complete(healthy, TimeSpan.FromMilliseconds(ms));
    }
}
