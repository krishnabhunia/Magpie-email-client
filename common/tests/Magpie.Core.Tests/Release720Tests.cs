namespace Magpie.Core.Tests;

/// <summary>7.2.0: the repo layout / one-workflow release (plan PX1 part A) — portable copies are named Magpie_x.y.z.exe.</summary>
public class Release720Tests
{
    [Fact]
    public void The_zips_portable_exe_runs_portable_and_installed_or_plain_copies_do_not()
    {
        using var dir = new TempDir();
        Assert.True(AppPaths.IsPortableCopy(dir.Path, "Magpie_7.2.0.exe"));          // portable/Magpie_7.2.0.exe from the zip
        Assert.True(AppPaths.IsPortableCopy(dir.Path, "magpie_7.2.0-beta.5.EXE"));   // case and test versions
        Assert.False(AppPaths.IsPortableCopy(dir.Path, "Magpie.exe"));               // the release's loose Magpie.exe
        Assert.False(AppPaths.IsPortableCopy(dir.Path, null));
        Assert.False(AppPaths.IsPortableCopy(dir.Path, "Magpie_7.2.0.dll"));
        Assert.True(AppPaths.IsPortableCopy(dir.Path, "Magpie_7.2.0 (1).exe"));     // downloaded twice
        Assert.False(AppPaths.IsPortableCopy(dir.Path, "Magpie_backup.exe"));       // a renamed copy keeps its profile data
        Assert.False(AppPaths.IsPortableCopy(dir.Path, "Magpie_7.2.exe"));
        File.WriteAllText(Path.Combine(dir.Path, AppPaths.InstallerUninstaller), "");
        Assert.False(AppPaths.IsPortableCopy(dir.Path, "Magpie_7.2.0.exe"));         // the installer put it there
        File.WriteAllText(Path.Combine(dir.Path, AppPaths.PortableMarker), "portable");
        Assert.True(AppPaths.IsPortableCopy(dir.Path, "Magpie.exe"));                // an older portable copy keeps working
    }

    [Fact]
    public void A_portable_copy_keeps_its_data_next_to_the_exe()
    {
        using var dir = new TempDir();
        var p = AppPaths.For(dir.Path, "Magpie_7.2.0.exe");
        Assert.True(p.IsPortable);
        Assert.Equal(Path.Combine(dir.Path, AppPaths.PortableFolder), p.Root);
        Assert.False(AppPaths.For(dir.Path, "Magpie.exe").IsPortable);
    }
}
