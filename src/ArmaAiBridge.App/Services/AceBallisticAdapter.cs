using System.Text.Json;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public interface IAceBallisticAdapter
{
    Task<string> CalculateAsync(
        JsonElement arguments,
        StateBallisticProfile profile,
        CancellationToken cancellationToken);
}

public sealed class ArmaAceBallisticAdapter : IAceBallisticAdapter
{
    private readonly ArmaQueryCoordinator _queries;

    public ArmaAceBallisticAdapter(ArmaQueryCoordinator queries)
        => _queries = queries ?? throw new ArgumentNullException(nameof(queries));

    public Task<string> CalculateAsync(
        JsonElement arguments,
        StateBallisticProfile profile,
        CancellationToken cancellationToken)
        => _queries.CalculateAceFiringSolutionAsync(arguments, profile, cancellationToken);
}
