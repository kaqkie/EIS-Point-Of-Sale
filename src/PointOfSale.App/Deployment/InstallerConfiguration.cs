using System.IO;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using PointOfSale.App.Options;

namespace PointOfSale.App.Deployment;

public enum SqlEngineKind
{
    None = 0,
    SqlExpress = 1,
    LocalDb = 2
}

/// <summary>
/// Central packaging metadata, directory layout, hardware binding, and SQL Express provisioning helpers
/// for MSI / ClickOnce / MSIX deployments of Albert Retail Terminal.
/// </summary>
public static class InstallerConfiguration
{
    public const string ApplicationExecutableName = "AlbertRetailTerminal.exe";
    public const string MsixManifestFileName = "AppxManifest.xml";
    public const string AppInstallerFileName = "AlbertRetailTerminal.appinstaller";

    public static string ProductVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public static string ResolveApplicationBaseDirectory() =>
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static string ResolveProgramDataRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AlbertRetailTerminal");

    public static IReadOnlyList<string> ResolveStandardDirectoryPaths(InstallerPackagingOptions? options = null)
    {
        options ??= new InstallerPackagingOptions();
        var baseDir = ResolveApplicationBaseDirectory();
        var programData = ResolveProgramDataRoot();

        var paths = new List<string>(options.RelativeDataDirectories.Length + 2)
        {
            Path.Combine(programData, "Secrets"),
            Path.Combine(programData, "Backups")
        };

        foreach (var relative in options.RelativeDataDirectories)
        {
            if (string.IsNullOrWhiteSpace(relative))
            {
                continue;
            }

            paths.Add(Path.IsPathRooted(relative)
                ? relative
                : Path.Combine(baseDir, relative.Replace('/', Path.DirectorySeparatorChar)));
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void EnsureStandardDirectories(InstallerPackagingOptions? options = null)
    {
        foreach (var path in ResolveStandardDirectoryPaths(options))
        {
            Directory.CreateDirectory(path);
        }
    }

    public static string GetPrimaryMacAddress()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            var mac = nic.GetPhysicalAddress().ToString();
            if (mac.Length >= 12 && mac != "000000000000")
            {
                return string.Join(
                    ':',
                    Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2)));
            }
        }

        return "00:00:00:00:00:00";
    }

    public static string ComputeHardwareFingerprintSha256()
    {
        var builder = new StringBuilder(256);
        builder.Append(Environment.MachineName);
        builder.Append('|');
        builder.Append(Environment.OSVersion.VersionString);
        builder.Append('|');
        builder.Append(GetPrimaryMacAddress());

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static bool HardwareFingerprintsMatch(string storedFingerprintSha256)
    {
        if (string.IsNullOrWhiteSpace(storedFingerprintSha256))
        {
            return false;
        }

        return string.Equals(
            storedFingerprintSha256.Trim(),
            ComputeHardwareFingerprintSha256(),
            StringComparison.OrdinalIgnoreCase);
    }

    public static string BuildSqlExpressSilentInstallArguments(string instanceName = "SQLEXPRESS")
    {
        return string.Join(' ',
            "/QUIET",
            "IACCEPTSQLSERVERLICENSETERMS=1",
            "ACTION=Install",
            "FEATURES=SQLEngine",
            "INSTANCENAME=" + instanceName,
            "SECURITYMODE=SQL",
            "SAPWD=\"{CHANGE_ME}\"",
            "SQLSVCACCOUNT=\"NT AUTHORITY\\NETWORK SERVICE\"",
            "SQLSYSADMINACCOUNTS=\"BUILTIN\\Administrators\"",
            "TCPENABLED=1",
            "NPENABLED=0");
    }

    public static string BuildLocalDbConnectionString(string databaseName = "PointOfSale", string instance = "MSSQLLocalDB") =>
        $"Server=(localdb)\\{instance};Database={databaseName};Trusted_Connection=True;TrustServerCertificate=True;";

    public static string BuildSqlExpressConnectionString(string databaseName = "PointOfSale", string instance = "SQLEXPRESS") =>
        $"Server=.\\{instance};Database={databaseName};Trusted_Connection=True;TrustServerCertificate=True;";

    public static string ResolveDeploymentOverridePath() =>
        Path.Combine(ResolveProgramDataRoot(), "appsettings.Deployment.json");

    public static SqlEngineKind DetectSqlEngine(
        string expressInstance = "SQLEXPRESS",
        string localDbInstance = "MSSQLLocalDB",
        bool allowLocalDbFallback = true)
    {
        if (CanOpenMaster($@".\{expressInstance}"))
        {
            return SqlEngineKind.SqlExpress;
        }

        if (allowLocalDbFallback && CanOpenMaster($@"(localdb)\{localDbInstance}"))
        {
            return SqlEngineKind.LocalDb;
        }

        return SqlEngineKind.None;
    }

    public static void WriteDeploymentConnectionOverride(string connectionString, string requiredInstanceHint)
    {
        var path = ResolveDeploymentOverridePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = new Dictionary<string, object>
        {
            ["ConnectionStrings"] = new Dictionary<string, string>
            {
                ["PosDatabase"] = connectionString
            },
            ["DatabaseBootstrap"] = new Dictionary<string, string>
            {
                ["RequiredInstanceHint"] = requiredInstanceHint
            }
        };
        var json = System.Text.Json.JsonSerializer.Serialize(
            payload,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private static bool CanOpenMaster(string server)
    {
        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = "master",
                IntegratedSecurity = true,
                TrustServerCertificate = true,
                ConnectTimeout = 3
            };
            using var connection = new Microsoft.Data.SqlClient.SqlConnection(builder.ConnectionString);
            connection.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string BuildClickOncePublishCommand(string repoRoot, string configuration = "Release")
    {
        var project = Path.Combine(repoRoot, "src", "PointOfSale.App", "PointOfSale.App.csproj");
        return $"dotnet publish \"{project}\" -c {configuration} /p:PublishProfile=ClickOnceProfile";
    }

    public static string BuildMsixPackagingCommand(string repoRoot, string configuration = "Release")
    {
        var script = Path.Combine(repoRoot, "Deployment", "Package-Msix.ps1");
        return $"powershell -ExecutionPolicy Bypass -File \"{script}\" -Configuration {configuration}";
    }
}
