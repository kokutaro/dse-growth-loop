using System.Text.Json.Serialization;

namespace Dse.Domain;

// Deliberately excludes target person IDs: only the authenticated actor can consent.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IdentityDisclosureConsent([property: JsonRequired] bool Granted,
    [property: JsonRequired] string ReviewedVersion, [property: JsonRequired] string FieldSet)
{
    public const string DisplayNameFieldSet = "profile-display-name-v1";
    public void Validate()
    {
        if (FieldSet != DisplayNameFieldSet || ReviewedVersion is null || ReviewedVersion.Length != 64 ||
            ReviewedVersion.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Invalid identity consent");
    }
}
public sealed record IdentityDisclosureParticipant(string AliasId, string AliasLabel, string? SharedName);
public sealed record IdentityDisclosureView(string RoomId, string ReviewedVersion, string FieldSet,
    string Etag, bool SelfConsent, string State, IdentityDisclosureParticipant[] Participants);
