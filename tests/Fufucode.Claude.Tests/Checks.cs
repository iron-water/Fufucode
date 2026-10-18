namespace Fufucode.Claude.Tests;

/// <summary>最小断言计数器；与仓库其它定向测试保持一致的输出格式。</summary>
internal sealed class Checks
{
    public int Passed { get; private set; }

    public void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL " + name);
        Passed++;
        Console.WriteLine($"PASS {name}");
    }

    public void Equal<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"FAIL {name}: expected <{expected}>, actual <{actual}>");
        Passed++;
        Console.WriteLine($"PASS {name}");
    }

    public void Throws<TException>(Action action, string name) where TException : Exception
    {
        try { action(); }
        catch (TException) { Passed++; Console.WriteLine($"PASS {name}"); return; }
        throw new Exception($"FAIL {name}: expected {typeof(TException).Name}");
    }
}
