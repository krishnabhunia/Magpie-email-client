namespace Magpie.Core.Models;

/// <summary>A lightweight entry in a draft's history on this PC. Content is loaded only when restoring.</summary>
public sealed record DraftRevision
{
    public long Id { get; init; }
    public string AccountId { get; init; } = "";
    public string MessageId { get; init; } = "";
    public DateTimeOffset Created { get; init; }
    public string Subject { get; init; } = "";
    public string ToText { get; init; } = "";
    public string Preview { get; init; } = "";
    /// <summary>Combined MIME and editable-field snapshot bytes (not SQLite page or index overhead).</summary>
    public long SizeBytes { get; init; }
}
