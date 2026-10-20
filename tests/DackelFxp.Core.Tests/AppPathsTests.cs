using DackelFxp.Core.Infrastructure;

namespace DackelFxp.Core.Tests;

/// <summary>Resolution and switching of the configuration folder (portable, custom, standard).</summary>
public sealed class AppPathsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dackel-paths").FullName;
    private readonly string _previousDefault = AppPaths.DefaultRoot;

    public AppPathsTests() => AppPaths.DefaultRoot = Path.Combine(_root, "appdata");

    public void Dispose()
    {
        AppPaths.DefaultRoot = _previousDefault;
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string ProgramDir => Directory.CreateDirectory(Path.Combine(_root, "program")).FullName;

    private AppPaths Open() => new(programDirectory: ProgramDir);

    [Fact]
    public void Without_Portable_Folder_Or_Pointer_The_Standard_Folder_Is_Used()
    {
        var paths = Open();
        Assert.Equal(ConfigLocationKind.Default, paths.Kind);
        Assert.Equal(AppPaths.DefaultRoot, paths.Root);
    }

    [Fact]
    public void A_Data_Folder_Next_To_The_Program_Makes_It_Portable()
    {
        Directory.CreateDirectory(Path.Combine(ProgramDir, "data"));
        var paths = Open();
        Assert.Equal(ConfigLocationKind.Portable, paths.Kind);
        Assert.Equal(Path.Combine(ProgramDir, "data"), paths.Root);
    }

    [Fact]
    public void Explicit_Folder_Wins_Over_Portable()
    {
        Directory.CreateDirectory(Path.Combine(ProgramDir, "data"));
        var paths = new AppPaths(Path.Combine(_root, "explicit"), ProgramDir);
        Assert.Equal(ConfigLocationKind.CommandLine, paths.Kind);
        Assert.Equal(Path.Combine(_root, "explicit"), paths.Root);
    }

    [Fact]
    public void Switch_To_Portable_Copies_The_Configuration()
    {
        var paths = Open();
        File.WriteAllText(paths.SettingsFile, "{\"theme\":\"Light\"}");
        File.WriteAllText(paths.SitesFile, "{}");
        Directory.CreateDirectory(paths.LogDirectory);
        File.WriteAllText(Path.Combine(paths.LogDirectory, "a.log"), "x");

        paths.SwitchTo(ConfigLocationKind.Portable);

        var next = Open();
        Assert.Equal(ConfigLocationKind.Portable, next.Kind);
        Assert.Equal("{\"theme\":\"Light\"}", File.ReadAllText(next.SettingsFile));
        Assert.True(File.Exists(next.SitesFile));
        Assert.True(File.Exists(Path.Combine(next.LogDirectory, "a.log")));
        // The original stays as it is.
        Assert.True(File.Exists(paths.SettingsFile));
    }

    [Fact]
    public void Switch_To_Custom_Folder_Writes_A_Pointer_And_Keeps_Existing_Data_There()
    {
        var paths = Open();
        File.WriteAllText(paths.SettingsFile, "current");
        var custom = Directory.CreateDirectory(Path.Combine(_root, "cloud")).FullName;
        File.WriteAllText(Path.Combine(custom, "settings.json"), "existing");

        paths.SwitchTo(ConfigLocationKind.Custom, custom);

        var next = Open();
        Assert.Equal(ConfigLocationKind.Custom, next.Kind);
        Assert.Equal(custom, next.Root);
        Assert.Equal("existing", File.ReadAllText(next.SettingsFile));

        next.SwitchTo(ConfigLocationKind.Default);
        var back = Open();
        Assert.Equal(ConfigLocationKind.Default, back.Kind);
        Assert.Equal("current", File.ReadAllText(back.SettingsFile));
    }

    [Fact]
    public void Leaving_Portable_Mode_Renames_The_Data_Folder()
    {
        Directory.CreateDirectory(Path.Combine(ProgramDir, "data"));
        var paths = Open();
        File.WriteAllText(paths.SettingsFile, "portable");

        paths.SwitchTo(ConfigLocationKind.Default);

        var next = Open();
        Assert.Equal(ConfigLocationKind.Default, next.Kind);
        Assert.Equal("portable", File.ReadAllText(next.SettingsFile));
        Assert.False(Directory.Exists(Path.Combine(ProgramDir, "data")));
        var backup = Assert.Single(Directory.GetDirectories(ProgramDir, "data-*"));
        Assert.True(File.Exists(Path.Combine(backup, "settings.json")));
    }

    [Fact]
    public void A_Program_Folder_Without_Write_Access_Is_Detected()
    {
        // A file where the folder would have to be created.
        File.WriteAllText(Path.Combine(_root, "file.txt"), "");
        Assert.ThrowsAny<IOException>(() => AppPaths.EnsureWritable(Path.Combine(_root, "file.txt", "data")));
    }
}
