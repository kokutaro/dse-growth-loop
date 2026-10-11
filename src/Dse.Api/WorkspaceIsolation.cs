using Dse.Domain;

// Employee collaboration uses the allowlisted workspace API. Legacy validation responses
// expose internal records and must remain restricted to operators in deployed environments.
public sealed class LegacyWorkspaceEndpoint { }

public static class WorkspaceIsolation
{
    public static void Require(ActorAccess actor, string path, bool demo, bool legacyEndpoint = false)
    {
        if (demo || actor.HasRole("operator")) return;
        path = path.TrimEnd('/');
        if (legacyEndpoint || Segment(path, "/api/search") || Segment(path, "/api/v2/search") || Segment(path, "/api/v2/retrieval-runs") ||
            Segment(path, "/api/v1/conversation-context") || Segment(path, "/api/v1/challenges") ||
            Segment(path, "/api/v1/sessions") || Segment(path, "/api/hubs/conversations"))
            throw new DomainException(403, "LEGACY_WORKSPACE_RESTRICTED");
    }

    private static bool Segment(string path, string prefix) => path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
}
