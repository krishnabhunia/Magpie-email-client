using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;

namespace Magpie.App.ViewModels;

/// <summary>One condition row in the rule editor (design B5): [field ▾] [operator ▾] [value] ✕.</summary>
public partial class EditableCondition : ObservableObject
{
    public static List<Choice<RuleField>> FieldChoices { get; } = Enum.GetValues<RuleField>().Select(f => new Choice<RuleField>(f, RuleEngine.FieldName(f))).ToList();

    private readonly Func<IReadOnlyList<string>> _accounts;

    public EditableCondition(RuleCondition c, Func<IReadOnlyList<string>> accounts)
    {
        _accounts = accounts;
        _field = c.Field;
        _op = c.Op;
        _value = c.Value;
        Refresh();
    }

    [ObservableProperty] private RuleField _field;
    [ObservableProperty] private RuleOp _op;
    [ObservableProperty] private string _value;
    [ObservableProperty] private List<Choice<RuleOp>> _opChoices = new();
    [ObservableProperty] private List<string> _valueChoices = new();
    [ObservableProperty] private bool _showOp = true;
    [ObservableProperty] private bool _showText = true;
    [ObservableProperty] private bool _showChoice;

    partial void OnFieldChanged(RuleField value)
    {
        Refresh();
        if (!OpChoices.Any(o => o.Value == Op)) Op = OpChoices.Count > 0 ? OpChoices[0].Value : RuleOp.Contains;
        Value = ShowChoice ? (ValueChoices.FirstOrDefault() ?? "") : "";
    }

    private void Refresh()
    {
        var listed = Field is RuleField.Category or RuleField.Account or RuleField.HasAttachment;
        ShowChoice = listed;
        ShowText = !listed;
        ShowOp = Field != RuleField.HasAttachment;
        OpChoices = Field is RuleField.Category or RuleField.Account
            ? new() { new(RuleOp.Is, "is"), new(RuleOp.DoesNotContain, "isn't") }
            : Enum.GetValues<RuleOp>().Select(o => new Choice<RuleOp>(o, RuleEngine.OpName(o, Field))).ToList();
        ValueChoices = Field switch
        {
            RuleField.Category => new() { "People", "Notifications", "Newsletters" },
            RuleField.Account => _accounts().ToList(),
            RuleField.HasAttachment => new() { "Yes", "No" },
            _ => new(),
        };
        if (listed && !ValueChoices.Contains(Value, StringComparer.OrdinalIgnoreCase)) Value = ValueChoices.FirstOrDefault() ?? "";
    }

    public RuleCondition ToCondition() => new() { Field = Field, Op = Op, Value = (Value ?? "").Trim() };
}

/// <summary>One action row in the rule editor: [action ▾] [target].</summary>
public partial class EditableAction : ObservableObject
{
    /// <summary>Set aside is offered from 1.2.0 (design B7).</summary>
    public static List<Choice<RuleActionKind>> KindChoices { get; } = Enum.GetValues<RuleActionKind>().Select(k => new Choice<RuleActionKind>(k, RuleEngine.ActionName(k))).ToList();

    private readonly Func<RuleActionKind, IReadOnlyList<string>> _targets;

    public EditableAction(RuleAction a, Func<RuleActionKind, IReadOnlyList<string>> targets)
    {
        _targets = targets;
        _kind = a.Kind;
        _target = a.Target;
        Refresh();
    }

    [ObservableProperty] private RuleActionKind _kind;
    [ObservableProperty] private string _target;
    [ObservableProperty] private List<string> _targetChoices = new();
    [ObservableProperty] private bool _showTarget;

    partial void OnKindChanged(RuleActionKind value)
    {
        Refresh();
        Target = TargetChoices.FirstOrDefault() ?? "";
    }

    public void Refresh()
    {
        ShowTarget = Kind is RuleActionKind.MoveToFolder or RuleActionKind.Tag or RuleActionKind.Snooze;
        var list = ShowTarget ? _targets(Kind).ToList() : new List<string>();
        if (ShowTarget && Target.Length > 0 && !list.Contains(Target, StringComparer.OrdinalIgnoreCase)) list.Insert(0, Target);
        TargetChoices = list;
    }

    public RuleAction ToAction() => new() { Kind = Kind, Target = ShowTarget ? (Target ?? "").Trim() : "" };
}

/// <summary>A rule in the list (toggle, name, summary) and in the editor.</summary>
public partial class EditableRule : ObservableObject
{
    public string Id { get; }
    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _accountId;
    [ObservableProperty] private bool _matchAll;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isSelected;
    public ObservableCollection<EditableCondition> Conditions { get; } = new();
    public ObservableCollection<EditableAction> Actions { get; } = new();

    public EditableRule(MailRule r)
    {
        Id = r.Id;
        _name = r.Name;
        _enabled = r.Enabled;
        _accountId = r.AccountId;
        _matchAll = r.MatchAll;
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Untitled rule" : Name;
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DisplayName));

    public MailRule ToRule() => new()
    {
        Id = Id, Name = (Name ?? "").Trim(), Enabled = Enabled, AccountId = AccountId ?? "", MatchAll = MatchAll,
        Conditions = Conditions.Select(c => c.ToCondition()).ToList(),
        Actions = Actions.Select(a => a.ToAction()).ToList(),
    };

    public void UpdateSummary() => Summary = RuleEngine.Describe(ToRule());
}

public sealed record RuleMatchLine(string Sender, string Subject, string When);

/// <summary>One row of Settings → Rules → Auto-delete (design AD4).</summary>
public sealed class AutoDeleteRow
{
    public AutoDeleteRule Rule { get; init; } = new();
    public string From => Rule.Pattern.StartsWith("*@", StringComparison.Ordinal) ? "Anyone at " + Rule.Pattern[2..] : Rule.Pattern;
    public string After => Rule.Otp ? "OTP · 24 hours" : AutoDelete.After(Rule.Amount, Rule.Unit);
    public string Accounts { get; init; } = "";
    public string Waiting { get; init; } = "";
    public string NextDelete { get; init; } = "";
    public bool Paused => Rule.Paused;
    public string PauseLabel => Rule.Paused ? "Resume" : "Pause";
    public string StateText => Rule.Paused ? "Paused" : "";
}

/// <summary>Settings → Rules (design B5). Rules are saved with "Save rule" (and with Save); list toggles and order save at once.</summary>
public partial class SettingsViewModel
{
    public ObservableCollection<EditableRule> RuleList { get; } = new();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelectedRule))] private EditableRule? _selectedRule;
    public bool HasSelectedRule => SelectedRule != null;
    public string RulesCount => RuleList.Count == 0 ? "" : RuleList.Count.ToString();
    public List<Choice<string>> RuleAccountChoices { get; private set; } = new();
    public List<Choice<bool>> MatchChoices { get; } = new() { new(true, "all"), new(false, "any") };
    [ObservableProperty] private bool _applyToExisting;
    [ObservableProperty] private string _applyToExistingLabel = "Also apply to the matching messages already in Inbox";
    [ObservableProperty] private string _ruleStatus = "";
    [ObservableProperty] private bool _ruleStatusIsError;
    public ObservableCollection<RuleMatchLine> RulePreview { get; } = new();
    [ObservableProperty] private string _rulePreviewTitle = "";

    /// <summary>Each rule as last saved; the list's switches and order apply to these, so unsaved edits never slip in.</summary>
    private readonly Dictionary<string, MailRule> _savedRules = new();

    // ── Auto-delete tab (design AD4) ──
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsFiltersTab), nameof(IsAutoDeleteTab))] private string _rulesTab = "Filters";
    public bool IsFiltersTab => RulesTab != "AutoDelete";
    public bool IsAutoDeleteTab => RulesTab == "AutoDelete";
    public ObservableCollection<AutoDeleteRow> AutoDeleteList { get; } = new();
    public string FiltersTabLabel => RuleList.Count == 0 ? "Filters" : $"Filters {RuleList.Count}";
    public string AutoDeleteTabLabel => AutoDeleteList.Count == 0 ? "Auto-delete" : $"Auto-delete {AutoDeleteList.Count}";

    [RelayCommand] private void ShowRulesTab(string? tab) => RulesTab = tab == "AutoDelete" ? "AutoDelete" : "Filters";

    /// <summary>Settings.Open("Rules:AutoDelete") opens a page and a tab.</summary>
    public void GoTo(string page)
    {
        var parts = page.Split(':', 2);
        SearchText = "";
        Page = parts[0];
        if (parts.Length > 1) RulesTab = parts[1];
    }

    public void LoadAutoDelete()
    {
        var stats = _e.Store.AutoDeleteStats();
        var now = DateTimeOffset.Now;
        AutoDeleteList.Clear();
        foreach (var r in _e.AutoDeleteRules())
        {
            var (waiting, next) = stats.TryGetValue(r.Id, out var st) ? st : (0, null);
            AutoDeleteList.Add(new AutoDeleteRow
            {
                Rule = r,
                Accounts = r.AccountId.Length == 0 ? "All accounts" : _e.AccountById(r.AccountId)?.Email ?? "(removed account)",
                Waiting = waiting == 0 ? "—" : waiting.ToString("#,0"),
                NextDelete = next is { } n ? (r.Paused ? "paused" : AutoDelete.TagText(n, now, r.Otp).Replace("Deletes ", "").Replace("OTP · deletes ", "")) : "—",
            });
        }
        OnPropertyChanged(nameof(AutoDeleteTabLabel));
    }

    [RelayCommand]
    private void NewAutoDelete()
    {
        if (Views.AutoDeleteDialog.Show(Ui.ActiveWindow, new AutoDeleteRule(), editing: false) != null) LoadAutoDelete();
    }

    [RelayCommand]
    private void EditAutoDelete(AutoDeleteRow? row)
    {
        if (row != null && Views.AutoDeleteDialog.Show(Ui.ActiveWindow, row.Rule, editing: true) != null) LoadAutoDelete();
    }

    [RelayCommand]
    private void PauseAutoDelete(AutoDeleteRow? row)
    {
        if (row == null) return;
        _e.PauseAutoDeleteRule(row.Rule.Id, !row.Rule.Paused);
        LoadAutoDelete();
    }

    [RelayCommand]
    private void RemoveAutoDelete(AutoDeleteRow? row)
    {
        if (row == null) return;
        var waiting = _e.Store.AutoDeleteStats().TryGetValue(row.Rule.Id, out var st) ? st.Waiting : 0;
        bool clear;
        if (waiting == 0)
        {
            if (!Ui.Confirm("Remove rule", $"Remove the auto-delete rule for {row.From}?")) return;
            clear = true;
        }
        else
        {
            var choice = Views.ChoiceDialog.Ask(Ui.ActiveWindow, "Remove rule",
                $"{waiting:#,0} email{(waiting == 1 ? " is" : "s are")} already waiting under this rule. Keep their delete dates, or clear them all?",
                "Keep their dates", "Clear them all");
            if (choice < 0) return;
            clear = choice == 1;
        }
        _e.RemoveAutoDeleteRule(row.Rule.Id, clear);
        LoadAutoDelete();
    }

    private void LoadRules()
    {
        _savedRules.Clear();
        foreach (var r in _e.Config.Rules) _savedRules[r.Id] = r.Clone();
        RuleAccountChoices = new List<Choice<string>> { new("", "All accounts") }
            .Concat(_e.Accounts.Select(a => new Choice<string>(a.Id, a.Email))).ToList();
        RuleList.Clear();
        foreach (var r in _e.Config.Rules) RuleList.Add(ToEditable(r.Clone()));
        SelectedRule = RuleList.FirstOrDefault();
        OnPropertyChanged(nameof(RulesCount));
        OnPropertyChanged(nameof(FiltersTabLabel));
        LoadAutoDelete();
    }

    private EditableRule ToEditable(MailRule r)
    {
        var e = new EditableRule(r);
        foreach (var c in r.Conditions) e.Conditions.Add(new EditableCondition(c, AccountEmails));
        foreach (var a in r.Actions) e.Actions.Add(new EditableAction(a, k => TargetsFor(e, k)));
        e.UpdateSummary();
        e.PropertyChanged += (_, a) =>
        {
            if (a.PropertyName == nameof(EditableRule.AccountId)) foreach (var x in e.Actions) x.Refresh();
        };
        return e;
    }

    private IReadOnlyList<string> AccountEmails() => _e.Accounts.Select(a => a.Email).ToList();

    /// <summary>Folders (for Move), tags (for Tag) and snooze choices, for the rule's account(s).</summary>
    private IReadOnlyList<string> TargetsFor(EditableRule rule, RuleActionKind kind) => kind switch
    {
        RuleActionKind.MoveToFolder => _e.Folders(rule.AccountId.Length == 0 ? null : rule.AccountId)
            .Where(f => f.Role is not (FolderRole.Inbox or FolderRole.All or FolderRole.Flagged or FolderRole.Important or FolderRole.Drafts or FolderRole.Sent))
            .Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
        RuleActionKind.Tag => Tags.Select(t => t.Name.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        RuleActionKind.Snooze => RuleEngine.SnoozeChoices,
        _ => Array.Empty<string>(),
    };

    partial void OnSelectedRuleChanged(EditableRule? oldValue, EditableRule? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null) newValue.IsSelected = true;
        RulePreview.Clear();
        RulePreviewTitle = "";
        RuleStatus = "";
        ApplyToExisting = false;
        ApplyToExistingLabel = "Also apply to the matching messages already in Inbox";
    }

    [RelayCommand] private void SelectRule(EditableRule? r) { if (r != null) SelectedRule = r; }

    [RelayCommand]
    private void NewRule()
    {
        var r = ToEditable(new MailRule
        {
            Name = "New rule",
            Conditions = { new RuleCondition { Field = RuleField.From, Op = RuleOp.Contains } },
            Actions = { new RuleAction { Kind = RuleActionKind.MoveToFolder } },
        });
        foreach (var a in r.Actions) { a.Refresh(); a.Target = a.TargetChoices.FirstOrDefault() ?? ""; }
        RuleList.Add(r);
        SelectedRule = r;
        OnPropertyChanged(nameof(RulesCount));
        RuleStatus = "Fill in the rule, then press Save rule.";
        RuleStatusIsError = false;
    }

    [RelayCommand]
    private void DeleteRule(EditableRule? r)
    {
        r ??= SelectedRule;
        if (r == null) return;
        if (!Ui.Confirm("Delete rule", $"Delete the rule \"{r.DisplayName}\"?\n\nMail it already moved stays where it is.")) return;
        var i = RuleList.IndexOf(r);
        RuleList.Remove(r);
        _savedRules.Remove(r.Id);
        SelectedRule = RuleList.Count == 0 ? null : RuleList[Math.Clamp(i, 0, RuleList.Count - 1)];
        PersistRules();
        OnPropertyChanged(nameof(RulesCount));
    }

    [RelayCommand]
    private void MoveRuleUp(EditableRule? r)
    {
        var i = r == null ? -1 : RuleList.IndexOf(r);
        if (i > 0) { RuleList.Move(i, i - 1); PersistRules(); }
    }

    [RelayCommand]
    private void MoveRuleDown(EditableRule? r)
    {
        var i = r == null ? -1 : RuleList.IndexOf(r);
        if (i >= 0 && i < RuleList.Count - 1) { RuleList.Move(i, i + 1); PersistRules(); }
    }

    /// <summary>The on/off switch in the list saves straight away.</summary>
    [RelayCommand]
    private void ToggleRule(EditableRule? r)
    {
        if (r == null) return;
        PersistRules();
    }

    [RelayCommand]
    private void AddCondition()
    {
        SelectedRule?.Conditions.Add(new EditableCondition(new RuleCondition { Field = RuleField.Subject, Op = RuleOp.Contains }, AccountEmails));
    }

    [RelayCommand] private void RemoveCondition(EditableCondition? c) { if (c != null) SelectedRule?.Conditions.Remove(c); }

    [RelayCommand]
    private void AddAction()
    {
        var rule = SelectedRule;
        if (rule == null) return;
        var a = new EditableAction(new RuleAction { Kind = RuleActionKind.Tag }, k => TargetsFor(rule, k));
        a.Target = a.TargetChoices.FirstOrDefault() ?? "";
        rule.Actions.Add(a);
    }

    [RelayCommand] private void RemoveAction(EditableAction? a) { if (a != null) SelectedRule?.Actions.Remove(a); }

    [RelayCommand]
    private async Task PreviewRule()
    {
        var r = SelectedRule?.ToRule();
        if (r == null) return;
        if (r.Conditions.Count == 0 || !r.Conditions.All(c => c.Field == RuleField.HasAttachment || c.Value.Length > 0))
        {
            RulePreviewTitle = "Add a condition with something to look for first.";
            RulePreview.Clear();
            return;
        }
        RulePreviewTitle = "Looking through the Inbox…";
        var matches = await Task.Run(() => _e.RuleMatchesInInbox(r));
        if (SelectedRule?.Id != r.Id) return;
        RulePreview.Clear();
        var now = DateTimeOffset.Now;
        foreach (var m in matches.Take(20)) RulePreview.Add(new RuleMatchLine(m.Sender, string.IsNullOrWhiteSpace(m.Subject) ? "(no subject)" : m.Subject, HtmlRenderer.FriendlyDate(m.Date, now)));
        RulePreviewTitle = matches.Count switch
        {
            0 => "Nothing in the Inbox matches right now. New mail that matches will be handled.",
            1 => "1 message in the Inbox matches:",
            _ => $"{matches.Count:#,0} messages in the Inbox match" + (matches.Count > 20 ? " — the newest 20:" : ":"),
        };
        ApplyToExistingLabel = $"Also apply to the {matches.Count:#,0} matching message{(matches.Count == 1 ? "" : "s")} already in Inbox";
    }

    [RelayCommand]
    private async Task SaveRule()
    {
        var rule = SelectedRule;
        if (rule == null) return;
        var r = rule.ToRule();
        string? problem = r.Conditions.Count == 0 ? "Add at least one condition."
            : r.Conditions.Any(c => c.Field != RuleField.HasAttachment && c.Value.Length == 0) ? "Every condition needs something to look for."
            : r.Actions.Count == 0 ? "Add at least one action."
            : r.Actions.Any(a => a.Kind is RuleActionKind.MoveToFolder or RuleActionKind.Tag && a.Target.Length == 0) ? "Choose a folder or tag for each action."
            : null;
        if (problem != null) { RuleStatus = problem; RuleStatusIsError = true; return; }
        if (r.Name.Length == 0) rule.Name = r.Name = "Rule " + (RuleList.IndexOf(rule) + 1);
        rule.UpdateSummary();
        PersistRules(rule);
        RuleStatusIsError = false;
        if (ApplyToExisting)
        {
            RuleStatus = "Saved. Applying to the Inbox…";
            var n = await Task.Run(() => _e.ApplyRuleToInbox(r));
            RuleStatus = n == 0 ? "Saved. Nothing in the Inbox matched." : $"Saved and applied to {n:#,0} message{(n == 1 ? "" : "s")} in the Inbox.";
            ApplyToExisting = false;
        }
        else RuleStatus = r.Enabled ? "Saved. It runs on new mail from now on." : "Saved (switched off).";
    }

    /// <summary>
    /// Writes the rules to settings.json straight away (Cancel doesn't undo a saved rule): <paramref name="commit"/>'s
    /// edits, plus every rule's on/off switch and the list order. A new rule that was never saved isn't written.
    /// </summary>
    private void PersistRules(EditableRule? commit = null)
    {
        if (commit != null) _savedRules[commit.Id] = commit.ToRule();
        var list = new List<MailRule>();
        foreach (var e in RuleList)
        {
            if (!_savedRules.TryGetValue(e.Id, out var saved)) continue;
            saved.Enabled = e.Enabled;
            list.Add(saved.Clone());
        }
        _e.Config.Rules = list;
        _e.Settings.Save();
        OnPropertyChanged(nameof(RulesCount));
        OnPropertyChanged(nameof(FiltersTabLabel));
    }

    /// <summary>The main Save button also keeps finished edits to rules (half-written ones stay unsaved).</summary>
    private void CommitRuleEdits() => _e.Config.Rules = ProjectRules(_savedRules);

    /// <summary>The rules as they would be saved now: complete edits replace the saved ones in <paramref name="saved"/>
    /// (a copy for a what-would-change check, the real dictionary when saving).</summary>
    private List<MailRule> ProjectRules(Dictionary<string, MailRule> saved)
    {
        foreach (var e in RuleList)
        {
            var now = e.ToRule();
            var complete = now.Conditions.Count > 0 && now.Actions.Count > 0
                && now.Conditions.All(c => c.Field == RuleField.HasAttachment || c.Value.Length > 0)
                && now.Actions.All(a => a.Kind is not (RuleActionKind.MoveToFolder or RuleActionKind.Tag) || a.Target.Length > 0);
            if (!complete) continue;
            if (now.Name.Length == 0) now.Name = "Rule " + (RuleList.IndexOf(e) + 1);
            saved[e.Id] = now;
        }
        return RuleList.Where(e => saved.ContainsKey(e.Id)).Select(e => { var r = saved[e.Id].Clone(); r.Enabled = e.Enabled; return r; }).ToList();
    }
}
