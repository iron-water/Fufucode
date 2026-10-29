using System.Reflection;
using System.Text.Json;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Plugins.Subagents;

namespace Fufucode.NativeCore.Tests;

/// <summary>
/// Test-only read/set access to real internal production types. No algorithm is copied:
/// every call invokes the production method or reads the production property.
/// </summary>
internal static class InternalAccess
{
    private static readonly Type NativeEntriesType =
        typeof(NativeSessionCoordinator).Assembly.GetType("Fufucode.Core.NativeEntries")
        ?? throw new InvalidOperationException("NativeEntries type missing");

    public static List<JsonElement> NativeEntriesBuild(IReadOnlyList<PureAgentMessage> messages, bool stableIds) =>
        (List<JsonElement>)NativeEntriesType.GetMethod("Build", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object?[] { messages, stableIds })!;

    public static JsonElement NativeEntriesLeaf(IReadOnlyList<PureAgentMessage> messages, bool stableIds) =>
        (JsonElement)NativeEntriesType.GetMethod("Leaf", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object?[] { messages, stableIds })!;

    public static int NativeEntriesResolveIndex(IReadOnlyList<PureAgentMessage> messages, string entryId) =>
        (int)NativeEntriesType.GetMethod("ResolveIndex", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object?[] { messages, entryId })!;

    private static readonly Type GenerationType =
        typeof(SubagentRunner).Assembly.GetType("Fufucode.Core.Backend.SubagentGeneration")
        ?? throw new InvalidOperationException("SubagentGeneration type missing");

    public static object NewGeneration() => Activator.CreateInstance(GenerationType, nonPublic: true)!;

    public static bool GenerationActive(object generation) =>
        (bool)GenerationType.GetProperty("IsActive")!.GetValue(generation)!;

    public static bool GenerationDisposed(object generation) =>
        (bool)GenerationType.GetProperty("IsDisposed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(generation)!;

    public static bool GenerationTryEnter(object generation, out CancellationToken token)
    {
        var args = new object?[] { null };
        var entered = (bool)GenerationType.GetMethod("TryEnter")!.Invoke(generation, args)!;
        token = (CancellationToken)args[0]!;
        return entered;
    }

    public static void GenerationRevoke(object generation) =>
        GenerationType.GetMethod("Revoke")!.Invoke(generation, null);

    public static Task GenerationDrain(object generation, CancellationToken cancellationToken) =>
        (Task)GenerationType.GetMethod("DrainAsync")!.Invoke(generation, new object?[] { cancellationToken })!;

    public static void GenerationExit(object generation) =>
        GenerationType.GetMethod("Exit")!.Invoke(generation, null);

    public static void SetLifetime(SubagentRunner runner, object generation) =>
        typeof(SubagentRunner).GetProperty("Lifetime", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(runner, generation);
}
