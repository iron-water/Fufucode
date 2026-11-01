using System.Security.Cryptography;
using System.Text;

namespace Fufucode.Plugins.AutoDev;

/// <summary>Shared, stable identity and location for the AutoDev rebirth kernel.</summary>
internal static class RebirthPaths
{
    public static string Stable(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();

    public static string ProjectId(string projectPath) => "project-" + Stable(projectPath);

    public static string Database(string dataRoot, string projectPath) =>
        Path.Combine(dataRoot, "auto-dev", "rebirth", Stable(projectPath), "kernel.db");
}
