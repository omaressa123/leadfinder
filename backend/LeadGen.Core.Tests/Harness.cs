namespace LeadGen.Core.Tests;

public static class T
{
    private static int _pass, _fail;
    private static readonly List<string> Failures = new();

    public static void Section(string name) => Console.WriteLine($"\n== {name}");

    public static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {name}"); }
        else
        {
            _fail++;
            Failures.Add(name);
            Console.WriteLine($"  FAIL  {name}{(detail is null ? "" : $"  [{detail}]")}");
        }
    }

    public static void Eq<TV>(string name, TV expected, TV actual) =>
        Check(name, EqualityComparer<TV>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");

    public static async Task Run(string name, Func<Task> test)
    {
        Section(name);
        try { await test(); }
        catch (Exception ex)
        {
            _fail++;
            Failures.Add($"{name} (exception)");
            Console.WriteLine($"  FAIL  unhandled exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static int Summary()
    {
        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        foreach (var f in Failures) Console.WriteLine($"  - {f}");
        return _fail == 0 ? 0 : 1;
    }
}
