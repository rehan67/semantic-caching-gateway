namespace SemanticCachingGateway.Services;

public interface IXFetchService
{
    bool ShouldRefresh(DateTime expiryUtc, double computationDurationSeconds, double beta = 1.0, double? testRandom = null);
}

public class XFetchService : IXFetchService
{
    private static readonly Random _rng = new();

    public bool ShouldRefresh(DateTime expiryUtc, double computationDurationSeconds, double beta = 1.0, double? testRandom = null)
    {
        double rand = testRandom ?? _rng.NextDouble();
        // Prevent log(0)
        rand = Math.Clamp(rand, 1e-9, 1.0);

        // delta_t = - beta * delta * ln(rand)
        double deltaTSeconds = -1.0 * beta * computationDurationSeconds * Math.Log(rand);

        DateTime now = DateTime.UtcNow;
        DateTime projectedTime = now.AddSeconds(deltaTSeconds);

        return projectedTime >= expiryUtc;
    }
}
