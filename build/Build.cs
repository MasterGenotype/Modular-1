using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

/// <summary>
/// Cross-platform build automation for Modular.
/// Replaces the GNU Makefile with .NET-native tooling.
/// </summary>
[ShutdownDotNetAfterServerBuild]
class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Compile);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly string Configuration = IsLocalBuild ? "Debug" : "Release";

    [Parameter("Runtime identifier for publish (e.g., linux-x64, win-x64, osx-x64, osx-arm64)")]
    readonly string Runtime = "linux-x64";

    [Solution] readonly Solution? Solution;

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath TestsDirectory => RootDirectory / "tests";
    AbsolutePath ExamplesDirectory => RootDirectory / "examples";
    AbsolutePath PublishDirectory => RootDirectory / "publish";

    AbsolutePath CliProject => SourceDirectory / "Modular.Cli" / "Modular.Cli.csproj";
    AbsolutePath GuiProject => SourceDirectory / "Modular.Gui" / "Modular.Gui.csproj";
    AbsolutePath SdkProject => SourceDirectory / "Modular.Sdk" / "Modular.Sdk.csproj";
    AbsolutePath CoreProject => SourceDirectory / "Modular.Core" / "Modular.Core.csproj";
    AbsolutePath HttpProject => SourceDirectory / "Modular.FluentHttp" / "Modular.FluentHttp.csproj";
    AbsolutePath ExamplePluginProject => ExamplesDirectory / "ExamplePlugin" / "ExamplePlugin.csproj";

    string HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    AbsolutePath LocalBinDirectory => (AbsolutePath)Path.Combine(HomeDirectory, ".local", "bin");
    AbsolutePath LocalShareDirectory => (AbsolutePath)Path.Combine(HomeDirectory, ".local", "share");
    AbsolutePath ConfigDirectory => (AbsolutePath)Path.Combine(HomeDirectory, ".config", "Modular");
    AbsolutePath PluginDirectory => ConfigDirectory / "plugins";

    // ==================== Clean Targets ====================

    Target Clean => _ => _
        .Description("Clean build artifacts")
        .Executes(() =>
        {
            DotNetClean(s => s
                .SetProject(Solution));
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(x => x.DeleteDirectory());
            TestsDirectory.GlobDirectories("**/bin", "**/obj").ForEach(x => x.DeleteDirectory());
        });

    Target CleanAll => _ => _
        .Description("Clean everything including publish directory")
        .DependsOn(Clean)
        .Executes(() =>
        {
            PublishDirectory.DeleteDirectory();
            ExamplesDirectory.GlobDirectories("**/bin", "**/obj").ForEach(x => x.DeleteDirectory());
        });

    // ==================== Build Targets ====================

    Target Restore => _ => _
        .Description("Restore NuGet packages")
        .Executes(() =>
        {
            DotNetRestore(s => s
                .SetProjectFile(Solution));
        });

    Target Compile => _ => _
        .Description("Build all projects in Debug mode")
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .EnableNoRestore());
        });

    Target CompileCli => _ => _
        .Description("Build CLI only")
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(CliProject)
                .SetConfiguration(Configuration)
                .EnableNoRestore());
        });

    Target CompileGui => _ => _
        .Description("Build GUI only")
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(GuiProject)
                .SetConfiguration(Configuration)
                .EnableNoRestore());
        });

    Target CompileSdk => _ => _
        .Description("Build SDK only")
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(SdkProject)
                .SetConfiguration(Configuration)
                .EnableNoRestore());
        });

    Target CompileCore => _ => _
        .Description("Build Core library only")
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(CoreProject)
                .SetConfiguration(Configuration)
                .EnableNoRestore());
        });

    Target CompileHttp => _ => _
        .Description("Build FluentHttp library only")
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(HttpProject)
                .SetConfiguration(Configuration)
                .EnableNoRestore());
        });

    // ==================== Test Targets ====================

    Target Test => _ => _
        .Description("Run all tests")
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTest(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .EnableNoRestore()
                .EnableNoBuild());
        });

    Target TestCore => _ => _
        .Description("Run Core library tests only")
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTest(s => s
                .SetProjectFile(TestsDirectory / "Modular.Core.Tests" / "Modular.Core.Tests.csproj")
                .SetConfiguration(Configuration)
                .EnableNoRestore()
                .EnableNoBuild());
        });

    Target TestHttp => _ => _
        .Description("Run FluentHttp tests only")
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTest(s => s
                .SetProjectFile(TestsDirectory / "Modular.FluentHttp.Tests" / "Modular.FluentHttp.Tests.csproj")
                .SetConfiguration(Configuration)
                .EnableNoRestore()
                .EnableNoBuild());
        });

    // ==================== Publish Targets ====================

    Target PublishCli => _ => _
        .Description("Publish CLI as self-contained single file")
        .DependsOn(Clean)
        .Executes(() =>
        {
            DotNetPublish(s => s
                .SetProject(CliProject)
                .SetConfiguration("Release")
                .SetRuntime(Runtime)
                .SetSelfContained(true)
                .SetPublishSingleFile(true)
                .SetProperty("IncludeNativeLibrariesForSelfExtract", "true")
                .SetOutput(PublishDirectory / "cli" / Runtime));
        });

    Target PublishGui => _ => _
        .Description("Publish GUI as self-contained")
        .DependsOn(Clean)
        .Executes(() =>
        {
            DotNetPublish(s => s
                .SetProject(GuiProject)
                .SetConfiguration("Release")
                .SetRuntime(Runtime)
                .SetSelfContained(true)
                .SetOutput(PublishDirectory / "gui" / Runtime));
        });

    Target PublishLinux => _ => _
        .Description("Publish CLI and GUI for Linux")
        .Executes(() =>
        {
            const string rid = "linux-x64";
            DotNetPublish(s => s
                .SetProject(CliProject)
                .SetConfiguration("Release")
                .SetRuntime(rid)
                .SetSelfContained(true)
                .SetPublishSingleFile(true)
                .SetOutput(PublishDirectory / "linux" / "cli"));
            DotNetPublish(s => s
                .SetProject(GuiProject)
                .SetConfiguration("Release")
                .SetRuntime(rid)
                .SetSelfContained(true)
                .SetOutput(PublishDirectory / "linux" / "gui"));
            Serilog.Log.Information("Linux builds published to {Path}", PublishDirectory / "linux");
        });

    Target PublishWindows => _ => _
        .Description("Publish CLI and GUI for Windows")
        .Executes(() =>
        {
            const string rid = "win-x64";
            DotNetPublish(s => s
                .SetProject(CliProject)
                .SetConfiguration("Release")
                .SetRuntime(rid)
                .SetSelfContained(true)
                .SetPublishSingleFile(true)
                .SetOutput(PublishDirectory / "windows" / "cli"));
            DotNetPublish(s => s
                .SetProject(GuiProject)
                .SetConfiguration("Release")
                .SetRuntime(rid)
                .SetSelfContained(true)
                .SetOutput(PublishDirectory / "windows" / "gui"));
            Serilog.Log.Information("Windows builds published to {Path}", PublishDirectory / "windows");
        });

    Target PublishMacOS => _ => _
        .Description("Publish CLI and GUI for macOS (Intel)")
        .Executes(() =>
        {
            const string rid = "osx-x64";
            DotNetPublish(s => s
                .SetProject(CliProject)
                .SetConfiguration("Release")
                .SetRuntime(rid)
                .SetSelfContained(true)
                .SetPublishSingleFile(true)
                .SetOutput(PublishDirectory / "macos" / "cli"));
            DotNetPublish(s => s
                .SetProject(GuiProject)
                .SetConfiguration("Release")
                .SetRuntime(rid)
                .SetSelfContained(true)
                .SetOutput(PublishDirectory / "macos" / "gui"));
            Serilog.Log.Information("macOS builds published to {Path}", PublishDirectory / "macos");
        });

    Target PublishMacOSArm => _ => _
        .Description("Publish CLI and GUI for macOS (Apple Silicon)")
        .Executes(() =>
        {
            const string rid = "osx-arm64";
            DotNetPublish(s => s
                .SetProject(CliProject)
                .SetConfiguration("Release")
                .SetRuntime(rid)
                .SetSelfContained(true)
                .SetPublishSingleFile(true)
                .SetOutput(PublishDirectory / "macos-arm" / "cli"));
            DotNetPublish(s => s
                .SetProject(GuiProject)
                .SetConfiguration("Release")
                .SetRuntime(rid)
                .SetSelfContained(true)
                .SetOutput(PublishDirectory / "macos-arm" / "gui"));
            Serilog.Log.Information("macOS ARM builds published to {Path}", PublishDirectory / "macos-arm");
        });

    Target PublishAll => _ => _
        .Description("Publish for all platforms")
        .DependsOn(PublishLinux, PublishWindows, PublishMacOS, PublishMacOSArm)
        .Executes(() =>
        {
            Serilog.Log.Information("All platforms published to {Path}", PublishDirectory);
        });

    // ==================== Install Targets ====================

    Target InstallCli => _ => _
        .Description("Install CLI to ~/.local/bin")
        .DependsOn(PublishCli)
        .OnlyWhenStatic(() => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        .Executes(() =>
        {
            LocalBinDirectory.CreateDirectory();
            var source = PublishDirectory / "cli" / Runtime / "modular";
            var dest = LocalBinDirectory / "modular";
            File.Copy(source, dest, overwrite: true);
            // Make executable on Unix
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                ProcessTasks.StartProcess("chmod", $"+x {dest}").WaitForExit();
            }
            Serilog.Log.Information("Modular CLI installed to {Path}", dest);
            Serilog.Log.Information("Make sure {Path} is in your PATH", LocalBinDirectory);
        });

    Target InstallGui => _ => _
        .Description("Install GUI to ~/.local/share and create symlink")
        .DependsOn(PublishGui)
        .OnlyWhenStatic(() => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        .Executes(() =>
        {
            LocalBinDirectory.CreateDirectory();
            var shareDir = LocalShareDirectory / "modular-gui";
            if (shareDir.Exists())
                shareDir.DeleteDirectory();
            shareDir.CreateDirectory();
            var sourceDir = PublishDirectory / "gui" / Runtime;
            CopyDirectory(sourceDir, shareDir);

            var guiExe = shareDir / "Modular.Gui";
            var symlink = LocalBinDirectory / "modular-gui";
            if (symlink.Exists())
                symlink.DeleteFile();
            File.CreateSymbolicLink(symlink, guiExe);
            
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                ProcessTasks.StartProcess("chmod", $"+x {guiExe}").WaitForExit();
            }
            Serilog.Log.Information("Modular GUI installed to {Path}", symlink);
        });

    Target Install => _ => _
        .Description("Install CLI (alias)")
        .DependsOn(InstallCli);

    Target UninstallCli => _ => _
        .Description("Uninstall CLI from ~/.local/bin")
        .OnlyWhenStatic(() => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        .Executes(() =>
        {
            var modular = LocalBinDirectory / "modular";
            if (modular.Exists())
            {
                modular.DeleteFile();
                Serilog.Log.Information("Modular CLI uninstalled");
            }
        });

    Target UninstallGui => _ => _
        .Description("Uninstall GUI from ~/.local")
        .OnlyWhenStatic(() => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        .Executes(() =>
        {
            var symlink = LocalBinDirectory / "modular-gui";
            var shareDir = LocalShareDirectory / "modular-gui";
            if (symlink.Exists())
                symlink.DeleteFile();
            if (shareDir.Exists())
                shareDir.DeleteDirectory();
            Serilog.Log.Information("Modular GUI uninstalled");
        });

    Target Uninstall => _ => _
        .Description("Uninstall CLI (alias)")
        .DependsOn(UninstallCli);

    Target UninstallAll => _ => _
        .Description("Uninstall everything")
        .DependsOn(UninstallCli, UninstallGui);

    // ==================== Run Targets ====================

    Target Run => _ => _
        .Description("Run CLI in development mode")
        .Executes(() =>
        {
            DotNetRun(s => s
                .SetProjectFile(CliProject)
                .SetConfiguration(Configuration));
        });

    Target RunGui => _ => _
        .Description("Run GUI in development mode")
        .Executes(() =>
        {
            DotNetRun(s => s
                .SetProjectFile(GuiProject)
                .SetConfiguration(Configuration));
        });

    // ==================== Plugin Targets ====================

    Target PluginExample => _ => _
        .Description("Build example plugin")
        .Executes(() =>
        {
            if (!ExamplePluginProject.Exists())
            {
                Serilog.Log.Warning("Example plugin project not found at {Path}", ExamplePluginProject);
                return;
            }
            DotNetBuild(s => s
                .SetProjectFile(ExamplePluginProject)
                .SetConfiguration("Release"));
            Serilog.Log.Information("Example plugin built: {Path}", ExamplesDirectory / "ExamplePlugin" / "bin" / "Release" / "net8.0");
        });

    Target PluginInstall => _ => _
        .Description("Install example plugin to user plugins directory")
        .DependsOn(PluginExample)
        .Executes(() =>
        {
            var pluginOutputDir = ExamplesDirectory / "ExamplePlugin" / "bin" / "Release" / "net8.0";
            var pluginInstallDir = PluginDirectory / "ExamplePlugin";
            pluginInstallDir.CreateDirectory();
            
            var dll = pluginOutputDir / "ExamplePlugin.dll";
            if (dll.Exists())
                File.Copy(dll, pluginInstallDir / "ExamplePlugin.dll", overwrite: true);
            
            var sdk = pluginOutputDir / "Modular.Sdk.dll";
            if (sdk.Exists())
                File.Copy(sdk, pluginInstallDir / "Modular.Sdk.dll", overwrite: true);
            
            var manifest = ExamplesDirectory / "ExamplePlugin" / "plugin.json";
            if (manifest.Exists())
                File.Copy(manifest, pluginInstallDir / "plugin.json", overwrite: true);
            
            Serilog.Log.Information("Example plugin installed to {Path}", pluginInstallDir);
        });

    Target PluginClean => _ => _
        .Description("Remove installed plugins")
        .Executes(() =>
        {
            if (PluginDirectory.Exists())
            {
                PluginDirectory.GlobDirectories("*").ForEach(x => x.DeleteDirectory());
                Serilog.Log.Information("All plugins removed from {Path}", PluginDirectory);
            }
        });

    // ==================== SDK Targets ====================

    Target SdkPack => _ => _
        .Description("Pack SDK as NuGet package")
        .Executes(() =>
        {
            DotNetPack(s => s
                .SetProject(SdkProject)
                .SetConfiguration("Release")
                .SetOutputDirectory(PublishDirectory / "nuget"));
            Serilog.Log.Information("SDK package created in {Path}", PublishDirectory / "nuget");
        });

    // ==================== Helper Methods ====================

    static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
            CopyDirectory(dir, destSubDir);
        }
    }
}
