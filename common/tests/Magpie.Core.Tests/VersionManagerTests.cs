using Magpie.Core.Updates;
using Xunit;

namespace Magpie.Core.Tests;

public sealed class VersionManagerTests
{
    [Fact]
    public void VersionHistorySeparatesNewerOlderStableAndBeta()
    {
        static (ReleaseInfo Info, bool Prerelease) Version(string version, bool beta) =>
            (new ReleaseInfo { Version = AppVersion.TryParse(version)! }, beta);
        var versions = new[]
        {
            Version("7.0.0", false),
            Version("7.1.0-beta.2", true),
            Version("7.1.0", false),
            Version("6.9.0-beta.1", true),
            Version("6.8.0", false),
        };
        var current = AppVersion.TryParse("7.0.0")!;

        Assert.Equal(new[] { "7.1.0", "7.1.0-beta.2" },
            UpdateClient.SelectVersions(versions, current, older: false, includePrerelease: true)
                .Select(x => x.Info.Version.ToString()));
        Assert.Equal(new[] { "7.1.0" },
            UpdateClient.SelectVersions(versions, current, older: false, includePrerelease: false)
                .Select(x => x.Info.Version.ToString()));
        Assert.Equal(new[] { "6.9.0-beta.1", "6.8.0" },
            UpdateClient.SelectVersions(versions, current, older: true, includePrerelease: true)
                .Select(x => x.Info.Version.ToString()));
    }
}
