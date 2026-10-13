namespace Magpie.Core.Tests;

public sealed class GitHubPermissionProbeTests
{
    [Fact]
    public void PullRequestFixtureIsAvailableInTheTestOutput()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "github-pr-probe.txt");

        Assert.True(File.Exists(path), $"The PR fixture was not copied to the test output: {path}");
        Assert.Equal("Magpie GitHub PR probe", File.ReadAllText(path).Trim());
    }
}
