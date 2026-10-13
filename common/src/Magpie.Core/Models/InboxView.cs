namespace Magpie.Core.Models;

/// <summary>A local saved query. Inbox views stay in receiving folders; saved searches span mail folders.</summary>
public sealed record InboxView(long Id, string Name, string Query, string? AccountId, bool InboxOnly);
