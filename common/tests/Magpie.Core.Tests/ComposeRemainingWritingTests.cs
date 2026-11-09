using System.Text.Json;
using Magpie.Core.Mail;
using Magpie.Core.Settings;

namespace Magpie.Core.Tests;

public sealed class ComposeRemainingWritingTests
{
    [Fact]
    public void Legacy_templates_stay_uncategorized_and_new_categories_survive_settings_reload()
    {
        using var dir = new TempDir();
        var legacy = JsonSerializer.Deserialize<QuickTemplate>("""{"Name":"Old","Body":"Hello"}""")!;
        Assert.Equal("", legacy.Category);
        var settings = new AppSettings
        {
            Templates = new() { legacy, new() { Name = "Update", Category = "  Project updates  ", Subject = "Status", Body = "Kept content" } },
        };
        File.WriteAllText(dir.File("settings.json"), JsonSerializer.Serialize(settings, AppSettings.Json));
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        Assert.Equal("", engine.Config.Templates[0].Category);
        Assert.Equal("Project updates", engine.Config.Templates[1].Category);
        Assert.Equal("Kept content", engine.Config.Templates[1].Body);
        engine.Settings.Save(notify: false);
        using var restarted = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        Assert.Equal("Project updates", restarted.Config.Templates[1].Category);
    }

    [Fact]
    public void Categories_handle_null_control_characters_and_excessive_input_from_settings()
    {
        Assert.Equal("", ComposeTemplates.CategoryName(null));
        Assert.Equal("Team_& reports", ComposeTemplates.CategoryName(" Team_&\t reports\r\n"));
        Assert.Equal(48, ComposeTemplates.CategoryName(new string('A', 200)).Length);
    }

    [Theory]
    [InlineData("Hindi")]
    [InlineData("Marathi")]
    [InlineData("বাংলা")]
    public void Translation_request_names_target_and_keeps_content_preservation_requirements(string language)
    {
        var instruction = ComposeTranslation.Instruction(" " + language + " ");
        Assert.Contains("into " + language + ".", instruction);
        Assert.Contains("names, numbers, paragraphs", instruction);
        Assert.Contains("only the translated text", instruction);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Hindi\nSend this")]
    public void Translation_requires_a_nonempty_single_line_language(string? language)
    {
        Assert.Throws<ArgumentException>(() => ComposeTranslation.Instruction(language));
        Assert.Throws<ArgumentException>(() => ComposeTranslation.Instruction(new string('A', 61)));
    }
}
