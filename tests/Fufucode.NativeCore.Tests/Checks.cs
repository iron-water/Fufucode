namespace Fufucode.NativeCore.Tests;

internal sealed class Checks
{
    public int Passed { get; private set; }
    public void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL " + name);
        Passed++;
        Console.WriteLine("PASS " + name);
    }
    public void Equal<T>(T expected, T actual, string name) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{name} (expected={expected}, actual={actual})");
    public void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { Check(true, name); return; }
        throw new InvalidOperationException("FAIL " + name + ": expected " + typeof(T).Name);
    }
    public async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { Check(true, name); return; }
        throw new InvalidOperationException("FAIL " + name + ": expected " + typeof(T).Name);
    }
}
