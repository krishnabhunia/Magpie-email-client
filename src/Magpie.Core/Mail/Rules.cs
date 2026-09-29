using Magpie.Core.Models;

namespace Magpie.Core.Mail;

// ───────────────────────── Rules / filters (design B5) ─────────────────────────
// Stored in settings.json (AppSettings.Rules). They run top to bottom on new Inbox mail, on this PC, after the
// messages are saved and before the new-mail notification (so "Skip notification" works).

public enum RuleField { From = 0, ToCc = 1, Subject = 2, Body = 3, HasAttachment = 4, Category = 5, Account = 6 }

public enum RuleOp { Contains = 0, Is = 1, StartsWith = 2, EndsWith = 3, DoesNotContain = 4 }

public enum RuleActionKind { MoveToFolder = 0, Tag = 1, MarkRead = 2, Pin = 3, SetAside = 4, Snooze = 5, SkipNotification = 6, Delete = 7 }

public sealed class RuleCondition
{
    public RuleField Field { get; set; }
    public RuleOp Op { get; set; }
    /// <summary>Text to look for; for Has attachment "Yes"/"No"; for Category the category name; for Account the address.</summary>
    public string Value { get; set; } = "";
}

public sealed class RuleAction
{
    public RuleActionKind Kind { get; set; }
    /// <summary>Folder path (Move), tag name (Tag) or snooze choice (Snooze: "Later today", "Tomorrow", …).</summary>
    public string Target { get; set; } = "";
}

public sealed class MailRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>"" = all accounts, else the account id.</summary>
    public string AccountId { get; set; } = "";
    /// <summary>True: all conditions must match; false: any.</summary>
    public bool MatchAll { get; set; } = true;
    public List<RuleCondition> Conditions { get; set; } = new();
    public List<RuleAction> Actions { get; set; } = new();

    public MailRule Clone() => new()
    {
        Id = Id, Name = Name, Enabled = Enabled, AccountId = AccountId, MatchAll = MatchAll,
        Conditions = Conditions.Select(c => new RuleCondition { Field = c.Field, Op = c.Op, Value = c.Value }).ToList(),
        Actions = Actions.Select(a => new RuleAction { Kind = a.Kind, Target = a.Target }).ToList(),
    };
}

/// <summary>What a rule needs to know about a message beyond its row.</summary>
public sealed record RuleContext(string AccountEmail, string BodyText);

public static class RuleEngine
{
    public static readonly string[] SnoozeChoices = { "Later today", "This evening", "Tomorrow", "This weekend", "Next week" };

    /// <summary>Drops rules and rows that can't work (no conditions or no actions still load, so the user can finish them).</summary>
    public static List<MailRule> Normalise(List<MailRule>? rules)
    {
        var list = (rules ?? new()).Where(r => r != null).ToList();
        var seen = new HashSet<string>();
        foreach (var r in list)
        {
            if (string.IsNullOrWhiteSpace(r.Id) || !seen.Add(r.Id)) { r.Id = Guid.NewGuid().ToString("N"); seen.Add(r.Id); }
            r.Name ??= "";
            r.AccountId ??= "";
            r.Conditions = (r.Conditions ?? new()).Where(c => c != null && Enum.IsDefined(c.Field) && Enum.IsDefined(c.Op)).ToList();
            foreach (var c in r.Conditions) c.Value ??= "";
            r.Actions = (r.Actions ?? new()).Where(a => a != null && Enum.IsDefined(a.Kind)).ToList();
            foreach (var a in r.Actions) a.Target ??= "";
        }
        return list;
    }

    /// <summary>A rule can run when it is on, has at least one condition with something to match and at least one action.</summary>
    public static bool IsRunnable(MailRule r) =>
        r.Enabled && r.Actions.Count > 0 && r.Conditions.Count > 0 && r.Conditions.All(c => NeedsNoText(c.Field) || c.Value.Trim().Length > 0);

    private static bool NeedsNoText(RuleField f) => f is RuleField.HasAttachment;

    public static bool AppliesToAccount(MailRule r, string accountId) => r.AccountId.Length == 0 || r.AccountId == accountId;

    public static bool Matches(MailRule rule, MessageRow m, RuleContext ctx)
    {
        if (rule.Conditions.Count == 0) return false;
        return rule.MatchAll ? rule.Conditions.All(c => Matches(c, m, ctx)) : rule.Conditions.Any(c => Matches(c, m, ctx));
    }

    public static bool Matches(RuleCondition c, MessageRow m, RuleContext ctx)
    {
        var v = (c.Value ?? "").Trim();
        switch (c.Field)
        {
            case RuleField.From:
                // The address and the display name both count ("is" = either equals).
                return c.Op == RuleOp.DoesNotContain
                    ? !Text(RuleOp.Contains, m.FromAddress, v) && !Text(RuleOp.Contains, m.FromName, v)
                    : Text(c.Op, m.FromAddress, v) || Text(c.Op, m.FromName, v);
            case RuleField.ToCc:
            {
                var parts = Composer.ParseAddresses(m.To + "," + m.Cc).Mailboxes.SelectMany(mb => new[] { mb.Address ?? "", mb.Name ?? "" }).Where(s => s.Length > 0).ToList();
                if (c.Op == RuleOp.Contains) return Text(RuleOp.Contains, m.To + " " + m.Cc, v);
                if (c.Op == RuleOp.DoesNotContain) return !Text(RuleOp.Contains, m.To + " " + m.Cc, v);
                return parts.Any(p => Text(c.Op, p, v));
            }
            case RuleField.Subject: return Text(c.Op, m.Subject, v);
            case RuleField.Body:
            {
                var body = ctx.BodyText.Length > 0 ? ctx.BodyText : m.Preview;
                return Text(c.Op, body, v);
            }
            case RuleField.HasAttachment:
                return m.HasAttachments == !v.Equals("No", StringComparison.OrdinalIgnoreCase);
            case RuleField.Category:
            {
                var isCat = Enum.TryParse<Category>(v, true, out var cat) && m.Category == cat;
                return c.Op == RuleOp.DoesNotContain ? !isCat : isCat;
            }
            case RuleField.Account:
            {
                var isAcc = ctx.AccountEmail.Equals(v, StringComparison.OrdinalIgnoreCase);
                return c.Op == RuleOp.DoesNotContain ? !isAcc : isAcc;
            }
        }
        return false;
    }

    private static bool Text(RuleOp op, string? hay, string needle)
    {
        hay ??= "";
        if (needle.Length == 0) return false;
        return op switch
        {
            RuleOp.Contains => hay.Contains(needle, StringComparison.OrdinalIgnoreCase),
            RuleOp.Is => hay.Trim().Equals(needle, StringComparison.OrdinalIgnoreCase),
            RuleOp.StartsWith => hay.TrimStart().StartsWith(needle, StringComparison.OrdinalIgnoreCase),
            RuleOp.EndsWith => hay.TrimEnd().EndsWith(needle, StringComparison.OrdinalIgnoreCase),
            RuleOp.DoesNotContain => !hay.Contains(needle, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>When a "Snooze" action wakes the message; unknown or passed choices fall back to tomorrow morning.</summary>
    public static DateTimeOffset SnoozeUntil(string choice, DateTime nowLocal)
    {
        var presets = TimePresets.For(nowLocal);
        var p = presets.FirstOrDefault(x => x.Label.Equals(choice, StringComparison.OrdinalIgnoreCase))
                ?? presets.First(x => x.Label.StartsWith("Tomorrow", StringComparison.OrdinalIgnoreCase));
        return p.When;
    }

    /// <summary>Plain-English summary for the rule list: "From contains amazon → Move to Receipts".</summary>
    public static string Describe(MailRule r)
    {
        if (r.Conditions.Count == 0) return "No conditions yet";
        var when = string.Join(r.MatchAll ? " and " : " or ", r.Conditions.Take(2).Select(DescribeCondition)) + (r.Conditions.Count > 2 ? " …" : "");
        var then = r.Actions.Count == 0 ? "no action yet" : string.Join(", ", r.Actions.Take(2).Select(DescribeAction)) + (r.Actions.Count > 2 ? " …" : "");
        return when + " → " + then;
    }

    public static string FieldName(RuleField f) => f switch
    {
        RuleField.From => "From", RuleField.ToCc => "To or Cc", RuleField.Subject => "Subject", RuleField.Body => "Body",
        RuleField.HasAttachment => "Has attachment", RuleField.Category => "Category", RuleField.Account => "Account", _ => f.ToString(),
    };

    public static string OpName(RuleOp o, RuleField f) => (f is RuleField.Category or RuleField.Account)
        ? (o == RuleOp.DoesNotContain ? "isn't" : "is")
        : o switch
        {
            RuleOp.Contains => "contains", RuleOp.Is => "is", RuleOp.StartsWith => "starts with", RuleOp.EndsWith => "ends with",
            RuleOp.DoesNotContain => "doesn't contain", _ => o.ToString(),
        };

    public static string ActionName(RuleActionKind k) => k switch
    {
        RuleActionKind.MoveToFolder => "Move to folder", RuleActionKind.Tag => "Tag", RuleActionKind.MarkRead => "Mark as read",
        RuleActionKind.Pin => "Pin", RuleActionKind.SetAside => "Set aside", RuleActionKind.Snooze => "Snooze",
        RuleActionKind.SkipNotification => "Skip notification", RuleActionKind.Delete => "Delete (to Trash)", _ => k.ToString(),
    };

    private static string DescribeCondition(RuleCondition c) => c.Field == RuleField.HasAttachment
        ? (c.Value.Equals("No", StringComparison.OrdinalIgnoreCase) ? "no attachment" : "has attachment")
        : $"{FieldName(c.Field)} {OpName(c.Op, c.Field)} \"{c.Value}\"";

    private static string DescribeAction(RuleAction a) => a.Kind switch
    {
        RuleActionKind.MoveToFolder => "move to " + a.Target,
        RuleActionKind.Tag => "tag " + a.Target,
        RuleActionKind.Snooze => "snooze until " + a.Target.ToLowerInvariant(),
        _ => ActionName(a.Kind).ToLowerInvariant(),
    };
}
