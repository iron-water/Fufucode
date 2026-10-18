using System;
using System.Collections.Generic;
using System.Linq;

namespace Fufucode.App.Features.Plugins;

internal static class PluginRecordExtensions
{
    public static LocalPluginVersion? FindVersionFor(this PluginRecord record, string? versionId,
        IReadOnlyList<LocalPluginVersion> versions) =>
        versionId is { Length: > 0 }
            ? versions.FirstOrDefault(v => string.Equals(v.VersionId, versionId, StringComparison.Ordinal))
            : null;
}
