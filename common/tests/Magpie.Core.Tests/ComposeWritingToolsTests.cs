using System.Text.Json;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;
using Xunit;

namespace Magpie.Core.Tests;

public sealed class ComposeWritingToolsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Legacy_body_only_template_never_changes_an_existing_subject()
    {
        var template = JsonSerializer.Deserialize<QuickTemplate>("""{"Name":"Old reply","Body":"Hello {{first_name}}"}""")!;
        Assert.Equal("", template.Subject);
        var expanded = ComposeTemplates.Expand(template, null, "Jane Example <jane@example.test>", "Keep this", Now);
        Assert.Equal("Hello Jane", expanded.Body);
        Assert.False(expanded.WouldReplaceSubject("Keep this"));
        Assert.Equal("Keep this", expanded.SubjectFor("Keep this", replaceExisting: true));
    }

    [Fact]
    public void Template_subject_and_body_survive_settings_round_trip()
    {
        var settings = new AppSettings
        {
            Templates = new() { new QuickTemplate { Name = "Agenda", Subject = "Agenda for {{date}}", Body = "Hi {{first_name}},\nHere is the agenda." } },
        };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, AppSettings.Json), AppSettings.Json)!;
        Assert.Equal(settings.Templates[0].Subject, restored.Templates[0].Subject);
        Assert.Equal(settings.Templates[0].Body, restored.Templates[0].Body);
    }

    [Fact]
    public void Subject_and_body_expand_against_the_same_original_subject()
    {
        var sender = new Account { Email = "me@example.test", DisplayName = "Sender" };
        var template = new QuickTemplate { Subject = "Follow-up: {{subject}}", Body = "Hello {{first_name}}, about {{subject}} — {{sender_name}}" };
        var expanded = ComposeTemplates.Expand(template, sender, "Jane Example <jane@example.test>", "Original", Now);
        Assert.Equal("Follow-up: Original", expanded.Subject);
        Assert.Equal("Hello Jane, about Original — Sender", expanded.Body);
        Assert.Equal("Original", expanded.SubjectFor("Original"));
        Assert.True(expanded.WouldReplaceSubject("Original"));
        Assert.Equal("Follow-up: Original", expanded.SubjectFor("Original", replaceExisting: true));
    }

    [Fact]
    public void Template_fills_only_blank_subjects_without_confirmation()
    {
        var expanded = new ExpandedQuickTemplate("Saved subject", "Saved text");
        Assert.Equal("Saved subject", expanded.SubjectFor(""));
        Assert.Equal("Saved subject", expanded.SubjectFor("   "));
        Assert.False(expanded.WouldReplaceSubject("Saved subject"));
        Assert.Equal("Newer subject", expanded.SubjectFor("Newer subject"));
    }

    [Fact]
    public void Subject_expansion_is_single_line_and_unknown_placeholders_stay_visible()
    {
        var expanded = ComposeTemplates.Expand(new QuickTemplate
        {
            Subject = "Hello\r\nBcc: not-a-header\n{{unknown}}",
            Body = "{{first_name}} {{missing}}",
        }, null, "", "", Now);
        Assert.Equal("Hello Bcc: not-a-header {{unknown}}", expanded.Subject);
        Assert.Equal("{{first_name}} {{missing}}", expanded.Body);
        Assert.DoesNotContain('\n', expanded.Subject);
        Assert.DoesNotContain('\r', expanded.Subject);
    }

    [Fact]
    public void Null_template_fields_from_older_or_external_json_are_safe()
    {
        var template = JsonSerializer.Deserialize<QuickTemplate>("""{"Name":"Nullable","Subject":null,"Body":null}""")!;
        var expanded = ComposeTemplates.Expand(template, null, "", "Keep", Now);
        Assert.Equal("", expanded.Body);
        Assert.Equal("Keep", expanded.SubjectFor("Keep", true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    public void Empty_writing_has_zero_words_characters_and_reading_time(string? text)
    {
        var statistics = ComposeWritingStatistics.Measure(text);
        Assert.Equal(0, statistics.Words);
        Assert.Equal(0, statistics.Characters);
        Assert.Equal(0, statistics.ReadingMinutes);
        Assert.Contains("0 min read", statistics.Summary);
    }

    [Fact]
    public void Counts_normalize_line_endings_and_keep_internal_spaces()
    {
        var statistics = ComposeWritingStatistics.Measure("  Hello world!\r\nNew line.  ");
        Assert.Equal(4, statistics.Words);
        Assert.Equal("Hello world!\nNew line.".Length, statistics.Characters);
        Assert.Equal(1, statistics.ReadingMinutes);
    }

    [Fact]
    public void Character_counts_use_graphemes_not_utf16_units()
    {
        var statistics = ComposeWritingStatistics.Measure("A e\u0301 👩‍💻");
        Assert.Equal(5, statistics.Characters);
        Assert.Equal(2, statistics.Words);
        Assert.Equal(2, ComposeWritingStatistics.Measure("नमस्ते भारत").Words);
    }

    [Fact]
    public void Reading_time_uses_200_words_per_minute_with_round_up()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 201));
        var statistics = ComposeWritingStatistics.Measure(text);
        Assert.Equal(201, statistics.Words);
        Assert.Equal(2, statistics.ReadingMinutes);
        Assert.Equal(1, ComposeWritingStatistics.Measure(string.Join(' ', Enumerable.Repeat("word", 200))).ReadingMinutes);
        Assert.Contains("1 word · 1 character", ComposeWritingStatistics.Measure("I").Summary);
    }
}
