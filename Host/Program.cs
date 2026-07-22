using System.Diagnostics;

var repoRoot = LocateRepoRoot();
var configuration = InferBuildConfiguration();
var appProjectDir = Path.Combine(repoRoot, "src", "PointOfSale.App");
var outputDir = Path.Combine(repoRoot, "artifacts", "bin", "PointOfSale.App", configuration, "net8.0-windows");
var exePath = Path.Combine(outputDir, "AlbertRetailTerminal.exe");
var dllPath = Path.Combine(outputDir, "AlbertRetailTerminal.dll");

Directory.SetCurrentDirectory(appProjectDir);

if (File.Exists(exePath))
{
    using var process = Process.Start(new ProcessStartInfo
    {
        FileName = exePath,
        WorkingDirectory = appProjectDir,
        UseShellExecute = false,
    })!;
    process.WaitForExit();
    Environment.ExitCode = process.ExitCode;
    return;
}

if (File.Exists(dllPath))
{
    using var process = Process.Start(new ProcessStartInfo
    {
        FileName = "dotnet",
        Arguments = $"\"{dllPath}\"",
        WorkingDirectory = appProjectDir,
        UseShellExecute = false,
    })!;
    process.WaitForExit();
    Environment.ExitCode = process.ExitCode;
    return;
}

Console.Error.WriteLine(
    "Albert Retail Terminal is not built. From the solution root, run: dotnet build src/PointOfSale.App/PointOfSale.App.csproj");
Environment.ExitCode = 1;

static string LocateRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "PointOfSale.sln")))
            return dir.FullName;
        dir = dir.Parent;
    }

    throw new InvalidOperationException("Could not locate repository root (PointOfSale.sln).");
}

static string InferBuildConfiguration()
{
    var path = AppContext.BaseDirectory;
    return path.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        ? "Release"
        : "Debug";
}
