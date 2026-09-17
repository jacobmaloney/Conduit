using Conduit.Sync.Connectors;

namespace Conduit.Sync.Orchestration;

public sealed record PersonCreationOutcome(bool Created, bool Skipped, string? Error);

/// <summary>The Create step's execution boundary: incomplete or unresolved prior probes cannot dispatch a write.</summary>
public static class PersonCreationDispatch
{
    public static async Task<PersonCreationOutcome> ExecuteAsync(IConnectorSink sink, ConnectorObject obj,
        IReadOnlyDictionary<string, PersonMatchResult>? priorMatches, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (priorMatches != null)
        {
            if (!priorMatches.TryGetValue(obj.SourceId, out var match))
                return new(false, false, "No matching result was returned for this account. Creation was blocked.");
            if (!string.IsNullOrWhiteSpace(match.ErrorMessage)) return new(false, false, match.ErrorMessage);
            if (!string.IsNullOrWhiteSpace(match.MatchedIdentityId)) return new(false, true, null);
            if (!match.CanCreate || match.Outcome != "Unmatched")
                return new(false, false, $"Matching outcome '{match.Outcome}' does not authorize creation.");
        }
        // An explicitly configured standalone Create step retains its existing behavior.
        try
        {
            var result = await sink.CreatePersonAsync(obj, ct);
            return !string.IsNullOrWhiteSpace(result.CreatedIdentityId) ? new(true, false, null) :
                new(false, false, result.ErrorMessage ?? "The sink returned no created person ID.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(false, false, ex.Message); }
    }
}
