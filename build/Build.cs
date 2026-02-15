using System.Diagnostics;

/// <summary>
/// Cross-platform build automation for Modular.
/// Uses direct dotnet CLI calls for maximum compatibility.
/// </summary>
class Build
{
    static string RootDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    static string SourceDirectory => Path.Combine(RootDirectory, "src");
    static string TestsDirectory => Path.Combine(RootDirectory, "tests");
    static string ExamplesDirectory => Path.Combine(RootDirectory, "examples");
    static string PublishDirectory => Path.Combine(RootDirectory, "publish");

    static string CliProject => Path.Combine(SourceDirectory, "Modular.Cli", "Modular.Cli.csproj");
    static string GuiProject => Path.Combine(SourceDirectory, "Modular.Gui", "Modular.Gui.csproj");
    static string SdkProject => Path.Combine(SourceDirectory, "Modular.Sdk", "Modular.Sdk.csproj");
    static string CoreProject => Path.Combine(SourceDirectory, "Modular.Core", "Modular.Core.csproj");
    static string HttpProject => Path.Combine(SourceDirectory, "Modular.FluentHttp", "Modular.FluentHttp.csproj");
    static string SolutionFile => Path.Combine(RootDirectory, "Modular.sln");

    static string HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string LocalBinDirectory => Path.Combine(HomeDirectory, ".local", "bin");
    static string LocalShareDirectory => Path.Combine(HomeDirectory, ".local", "share");
    static string ConfigDirectory => Path.Combine(HomeDirectory, ".config", "Modular");
    static string PluginDirectory => Path.Combine(ConfigDirectory, "plugins");

    static string Configuration = "Debug";
    static string Runtime = "linux-x64";

    public static int Main(string[] args)
    {
        // Parse arguments
        var targets = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--configuration" && i + 1 < args.Length)
            {
                Configuration = args[++i];
            }
            else if (args[i] == "--runtime" && i + 1 < args.Length)
            {
                Runtime = args[++i];
            }
            else if (args[i] == "--target" && i + 1 < args.Length)
            {
                targets.Add(args[++i]);
            }
            else if (args[i] == "--help" || args[i] == "-h")
            {
                PrintHelp();
                return 0;
            }
            else if (!args[i].StartsWith("-"))
            {
                targets.Add(args[i]);
            }
        }

        if (targets.Count == 0)
            targets.Add("Compile");

        foreach (var target in targets)
        {
            Console.WriteLine($"\n═══════════════════════════════════════");
            Console.WriteLine($"║ {target}");
            Console.WriteLine($"═══════════════════════════════════════\n");

            var success = RunTarget(target);
            if (!success)
            {
                Console.WriteLine($"\n❌ Target '{target}' failed");
                return 1;
            }
        }

        Console.WriteLine("\n✅ Build succeeded");
        return 0;
    }

    static void PrintHelp()
    {
        Console.WriteLine(@"
Modular Build System

USAGE: ./build.sh [targets...] [options]

TARGETS:
  Clean               Clean build artifacts
  CleanAll            Clean everything including publish directory
  Restore             Restore NuGet packages
  Compile (default)   Build all projects
  CompileCli          Build CLI only
  CompileGui          Build GUI only
  Test                Run all tests
  PublishCli          Publish CLI as self-contained single file
  PublishGui          Publish GUI as self-contained
  PublishLinux        Publish CLI and GUI for Linux
  PublishWindows      Publish CLI and GUI for Windows
  PublishMacOS        Publish CLI and GUI for macOS (Intel)
  PublishMacOSArm     Publish CLI and GUI for macOS (Apple Silicon)
  PublishAll          Publish for all platforms
  InstallCli          Install CLI to ~/.local/bin
  InstallGui          Install GUI to ~/.local/bin
  Run                 Run CLI in development mode
  RunGui              Run GUI in development mode
  SdkPack             Pack SDK as NuGet package

OPTIONS:
  --configuration <cfg>  Build configuration (Debug/Release)
  --runtime <rid>        Runtime identifier (e.g., linux-x64, win-x64)
  --help, -h             Show this help message
");
    }

    static bool RunTarget(string target) => target.ToLowerInvariant() switch
    {
        "clean" => Clean(),
        "cleanall" => CleanAll(),
        "restore" => Restore(),
        "compile" => Compile(),
        "compilecli" => CompileCli(),
        "compilegui" => CompileGui(),
        "compilesdk" => CompileSdk(),
        "compilecore" => CompileCore(),
        "compilehttp" => CompileHttp(),
        "test" => Test(),
        "testcore" => TestCore(),
        "testhttp" => TestHttp(),
        "publishcli" => PublishCli(),
        "publishgui" => PublishGui(),
        "publishlinux" => PublishLinux(),
        "publishwindows" => PublishWindows(),
        "publishmacos" => PublishMacOS(),
        "publishmacosarm" => PublishMacOSArm(),
        "publishall" => PublishAll(),
        "installcli" => InstallCli(),
        "installgui" => InstallGui(),
        "install" => InstallCli(),
        "uninstallcli" => UninstallCli(),
        "uninstallgui" => UninstallGui(),
        "uninstall" => UninstallCli(),
        "uninstallall" => UninstallAll(),
        "run" => Run(),
        "rungui" => RunGui(),
        "pluginexample" => PluginExample(),
        "plugininstall" => PluginInstall(),
        "pluginclean" => PluginClean(),
        "sdkpack" => SdkPack(),
        _ => throw new ArgumentException($"Unknown target: {target}")
    };

    // ==================== Helper Methods ====================

    static bool DotNet(string arguments)
    {
        Console.WriteLine($"> dotnet {arguments}");
        var psi = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = RootDirectory,
            UseShellExecute = false
        };
        using var process = Process.Start(psi);
        process?.WaitForExit();
        return process?.ExitCode == 0;
    }

    static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Console.WriteLine($"Deleting {path}");
            Directory.Delete(path, recursive: true);
        }
    }

    static void EnsureDirectory(string path)
    {
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);
    }

    static void CopyDirectory(string source, string dest)
    {
        EnsureDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }

    // ==================== Clean Targets ====================

    static bool Clean()
    {
        DotNet($"clean \"{SolutionFile}\"");
        foreach (var dir in Directory.GetDirectories(SourceDirectory, "bin", SearchOption.AllDirectories))
            DeleteDirectory(dir);
        foreach (var dir in Directory.GetDirectories(SourceDirectory, "obj", SearchOption.AllDirectories))
            DeleteDirectory(dir);
        foreach (var dir in Directory.GetDirectories(TestsDirectory, "bin", SearchOption.AllDirectories))
            DeleteDirectory(dir);
        foreach (var dir in Directory.GetDirectories(TestsDirectory, "obj", SearchOption.AllDirectories))
            DeleteDirectory(dir);
        return true;
    }

    static bool CleanAll()
    {
        Clean();
        DeleteDirectory(PublishDirectory);
        if (Directory.Exists(ExamplesDirectory))
        {
            foreach (var dir in Directory.GetDirectories(ExamplesDirectory, "bin", SearchOption.AllDirectories))
                DeleteDirectory(dir);
            foreach (var dir in Directory.GetDirectories(ExamplesDirectory, "obj", SearchOption.AllDirectories))
                DeleteDirectory(dir);
        }
        return true;
    }

    // ==================== Build Targets ====================

    static bool Restore() => DotNet($"restore \"{SolutionFile}\"");

    static bool Compile() => Restore() && DotNet($"build \"{SolutionFile}\" --configuration {Configuration} --no-restore");

    static bool CompileCli() => Restore() && DotNet($"build \"{CliProject}\" --configuration {Configuration} --no-restore");

    static bool CompileGui() => Restore() && DotNet($"build \"{GuiProject}\" --configuration {Configuration} --no-restore");

    static bool CompileSdk() => Restore() && DotNet($"build \"{SdkProject}\" --configuration {Configuration} --no-restore");

    static bool CompileCore() => Restore() && DotNet($"build \"{CoreProject}\" --configuration {Configuration} --no-restore");

    static bool CompileHttp() => Restore() && DotNet($"build \"{HttpProject}\" --configuration {Configuration} --no-restore");

    // ==================== Test Targets ====================

    static bool Test() => Compile() && DotNet($"test \"{SolutionFile}\" --configuration {Configuration} --no-restore --no-build");

    static bool TestCore()
    {
        var testProject = Path.Combine(TestsDirectory, "Modular.Core.Tests", "Modular.Core.Tests.csproj");
        return Compile() && DotNet($"test \"{testProject}\" --configuration {Configuration} --no-restore --no-build");
    }

    static bool TestHttp()
    {
        var testProject = Path.Combine(TestsDirectory, "Modular.FluentHttp.Tests", "Modular.FluentHttp.Tests.csproj");
        return Compile() && DotNet($"test \"{testProject}\" --configuration {Configuration} --no-restore --no-build");
    }

    // ==================== Publish Targets ====================

    static bool PublishCli()
    {
        Clean();
        var output = Path.Combine(PublishDirectory, "cli", Runtime);
        return DotNet($"publish \"{CliProject}\" --configuration Release --runtime {Runtime} --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --output \"{output}\"");
    }

    static bool PublishGui()
    {
        Clean();
        var output = Path.Combine(PublishDirectory, "gui", Runtime);
        return DotNet($"publish \"{GuiProject}\" --configuration Release --runtime {Runtime} --self-contained true --output \"{output}\"");
    }

    static bool PublishLinux()
    {
        const string rid = "linux-x64";
        var cliOutput = Path.Combine(PublishDirectory, "linux", "cli");
        var guiOutput = Path.Combine(PublishDirectory, "linux", "gui");
        return DotNet($"publish \"{CliProject}\" --configuration Release --runtime {rid} --self-contained true -p:PublishSingleFile=true --output \"{cliOutput}\"")
            && DotNet($"publish \"{GuiProject}\" --configuration Release --runtime {rid} --self-contained true --output \"{guiOutput}\"");
    }

    static bool PublishWindows()
    {
        const string rid = "win-x64";
        var cliOutput = Path.Combine(PublishDirectory, "windows", "cli");
        var guiOutput = Path.Combine(PublishDirectory, "windows", "gui");
        return DotNet($"publish \"{CliProject}\" --configuration Release --runtime {rid} --self-contained true -p:PublishSingleFile=true --output \"{cliOutput}\"")
            && DotNet($"publish \"{GuiProject}\" --configuration Release --runtime {rid} --self-contained true --output \"{guiOutput}\"");
    }

    static bool PublishMacOS()
    {
        const string rid = "osx-x64";
        var cliOutput = Path.Combine(PublishDirectory, "macos", "cli");
        var guiOutput = Path.Combine(PublishDirectory, "macos", "gui");
        return DotNet($"publish \"{CliProject}\" --configuration Release --runtime {rid} --self-contained true -p:PublishSingleFile=true --output \"{cliOutput}\"")
            && DotNet($"publish \"{GuiProject}\" --configuration Release --runtime {rid} --self-contained true --output \"{guiOutput}\"");
    }

    static bool PublishMacOSArm()
    {
        const string rid = "osx-arm64";
        var cliOutput = Path.Combine(PublishDirectory, "macos-arm", "cli");
        var guiOutput = Path.Combine(PublishDirectory, "macos-arm", "gui");
        return DotNet($"publish \"{CliProject}\" --configuration Release --runtime {rid} --self-contained true -p:PublishSingleFile=true --output \"{cliOutput}\"")
            && DotNet($"publish \"{GuiProject}\" --configuration Release --runtime {rid} --self-contained true --output \"{guiOutput}\"");
    }

    static bool PublishAll() => PublishLinux() && PublishWindows() && PublishMacOS() && PublishMacOSArm();

    // ==================== Install Targets ====================

    static bool InstallCli()
    {
        if (!PublishCli()) return false;

        EnsureDirectory(LocalBinDirectory);
        var source = Path.Combine(PublishDirectory, "cli", Runtime, "modular");
        if (OperatingSystem.IsWindows())
            source += ".exe";
        var dest = Path.Combine(LocalBinDirectory, OperatingSystem.IsWindows() ? "modular.exe" : "modular");

        File.Copy(source, dest, overwrite: true);
        
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var chmod = Process.Start("chmod", $"+x \"{dest}\"");
            chmod?.WaitForExit();
        }

        Console.WriteLine($"✅ Modular CLI installed to {dest}");
        Console.WriteLine($"   Make sure {LocalBinDirectory} is in your PATH");
        return true;
    }

    static bool InstallGui()
    {
        if (!PublishGui()) return false;

        EnsureDirectory(LocalBinDirectory);
        var shareDir = Path.Combine(LocalShareDirectory, "modular-gui");
        DeleteDirectory(shareDir);
        EnsureDirectory(shareDir);

        var sourceDir = Path.Combine(PublishDirectory, "gui", Runtime);
        CopyDirectory(sourceDir, shareDir);

        var guiExe = Path.Combine(shareDir, "Modular.Gui");
        var symlink = Path.Combine(LocalBinDirectory, "modular-gui");
        
        if (File.Exists(symlink))
            File.Delete(symlink);
        File.CreateSymbolicLink(symlink, guiExe);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var chmod = Process.Start("chmod", $"+x \"{guiExe}\"");
            chmod?.WaitForExit();
        }

        Console.WriteLine($"✅ Modular GUI installed to {symlink}");
        return true;
    }

    static bool UninstallCli()
    {
        var modular = Path.Combine(LocalBinDirectory, OperatingSystem.IsWindows() ? "modular.exe" : "modular");
        if (File.Exists(modular))
        {
            File.Delete(modular);
            Console.WriteLine("✅ Modular CLI uninstalled");
        }
        return true;
    }

    static bool UninstallGui()
    {
        var symlink = Path.Combine(LocalBinDirectory, "modular-gui");
        var shareDir = Path.Combine(LocalShareDirectory, "modular-gui");
        if (File.Exists(symlink))
            File.Delete(symlink);
        DeleteDirectory(shareDir);
        Console.WriteLine("✅ Modular GUI uninstalled");
        return true;
    }

    static bool UninstallAll() => UninstallCli() && UninstallGui();

    // ==================== Run Targets ====================

    static bool Run() => DotNet($"run --project \"{CliProject}\" --configuration {Configuration}");

    static bool RunGui() => DotNet($"run --project \"{GuiProject}\" --configuration {Configuration}");

    // ==================== Plugin Targets ====================

    static bool PluginExample()
    {
        var exampleProject = Path.Combine(ExamplesDirectory, "ExamplePlugin", "ExamplePlugin.csproj");
        if (!File.Exists(exampleProject))
        {
            Console.WriteLine($"⚠️ Example plugin project not found at {exampleProject}");
            return true;
        }
        return DotNet($"build \"{exampleProject}\" --configuration Release");
    }

    static bool PluginInstall()
    {
        if (!PluginExample()) return false;

        var pluginOutputDir = Path.Combine(ExamplesDirectory, "ExamplePlugin", "bin", "Release", "net8.0");
        var pluginInstallDir = Path.Combine(PluginDirectory, "ExamplePlugin");
        EnsureDirectory(pluginInstallDir);

        var dll = Path.Combine(pluginOutputDir, "ExamplePlugin.dll");
        if (File.Exists(dll))
            File.Copy(dll, Path.Combine(pluginInstallDir, "ExamplePlugin.dll"), overwrite: true);

        var sdk = Path.Combine(pluginOutputDir, "Modular.Sdk.dll");
        if (File.Exists(sdk))
            File.Copy(sdk, Path.Combine(pluginInstallDir, "Modular.Sdk.dll"), overwrite: true);

        var manifest = Path.Combine(ExamplesDirectory, "ExamplePlugin", "plugin.json");
        if (File.Exists(manifest))
            File.Copy(manifest, Path.Combine(pluginInstallDir, "plugin.json"), overwrite: true);

        Console.WriteLine($"✅ Example plugin installed to {pluginInstallDir}");
        return true;
    }

    static bool PluginClean()
    {
        if (Directory.Exists(PluginDirectory))
        {
            foreach (var dir in Directory.GetDirectories(PluginDirectory))
                DeleteDirectory(dir);
            Console.WriteLine($"✅ All plugins removed from {PluginDirectory}");
        }
        return true;
    }

    // ==================== SDK Targets ====================

    static bool SdkPack()
    {
        var output = Path.Combine(PublishDirectory, "nuget");
        EnsureDirectory(output);
        return DotNet($"pack \"{SdkProject}\" --configuration Release --output \"{output}\"");
    }
}
