using System.Text.Json.Serialization;

namespace Magpie.Core.Models;

/// <summary>The conversation selected for a reply, independent of its outgoing sender. Never written to message headers.</summary>
public sealed record ReplyArchiveSource(string AccountId, string ThreadKey)
{
    [JsonIgnore]
    public bool IsValid => !string.IsNullOrWhiteSpace(AccountId) && AccountId.Length <= 256
        && !AccountId.Any(char.IsControl) && !string.IsNullOrWhiteSpace(ThreadKey) && ThreadKey.Length <= 2048
        && !ThreadKey.Any(char.IsControl);
}
