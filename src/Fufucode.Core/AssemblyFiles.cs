using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Fufucode.Core;

internal static class AssemblyFiles
{
    // Disk plugins retain their real path; bundled and stream-loaded assemblies have none.
    // Callers explicitly choose a fallback instead of treating an empty location as a shared identity.
    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "An absent assembly location is the supported null result.")]
    internal static string? Location(Assembly assembly) =>
        !assembly.IsDynamic && assembly.Location is { Length: > 0 } path ? path : null;
}
