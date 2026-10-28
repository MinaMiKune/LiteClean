using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace LiteClean.Tests;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class TestAttribute : Attribute
{
    public TestAttribute(string? name = null) => Name = name;

    public string? Name { get; }
}

internal sealed class SkipException : Exception
{
    public SkipException(string message) : base(message)
    {
    }
}

internal sealed class TestFailureException : Exception
{
    public TestFailureException(string message) : base(message)
    {
    }
}

internal static class Check
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new TestFailureException(message ?? "Expected true, got false.");
        }
    }

    public static void False(bool condition, string? message = null)
    {
        if (condition)
        {
            throw new TestFailureException(message ?? "Expected false, got true.");
        }
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new TestFailureException(
                message ?? $"Expected: {Format(expected)}{Environment.NewLine}Actual:   {Format(actual)}");
        }
    }

    public static void NotEqual<T>(T unexpected, T actual, string? message = null)
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
        {
            throw new TestFailureException(message ?? $"Did not expect: {Format(actual)}");
        }
    }

    public static void Contains(string expectedSubstring, string? actual, string? message = null)
    {
        if (actual is null || !actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new TestFailureException(
                message ?? $"Expected string to contain '{expectedSubstring}'. Actual: {actual}");
        }
    }

    public static void DoesNotContain(string unexpectedSubstring, string? actual, string? message = null)
    {
        if (actual is not null && actual.Contains(unexpectedSubstring, StringComparison.Ordinal))
        {
            throw new TestFailureException(
                message ?? $"Expected string not to contain '{unexpectedSubstring}'.");
        }
    }

    public static void FileExists(string path, string? message = null)
    {
        if (!File.Exists(path))
        {
            throw new TestFailureException(message ?? $"File does not exist: {path}");
        }
    }

    public static void FileMissing(string path, string? message = null)
    {
        if (File.Exists(path))
        {
            throw new TestFailureException(message ?? $"File still exists: {path}");
        }
    }

    public static void DirectoryExists(string path, string? message = null)
    {
        if (!Directory.Exists(path))
        {
            throw new TestFailureException(message ?? $"Directory does not exist: {path}");
        }
    }

    public static void DirectoryMissing(string path, string? message = null)
    {
        if (Directory.Exists(path))
        {
            throw new TestFailureException(message ?? $"Directory still exists: {path}");
        }
    }

    public static void BytesEqual(byte[] expected, byte[] actual, string? message = null)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new TestFailureException(
                message ?? $"Byte arrays differ. Expected {expected.Length} bytes, actual {actual.Length} bytes.");
        }
    }

    public static TException Throws<TException>(Action action, string? message = null)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            throw new TestFailureException(
                message ?? $"Expected {typeof(TException).Name}, got {ex.GetType().Name}: {ex.Message}");
        }

        throw new TestFailureException(message ?? $"Expected {typeof(TException).Name}, no exception was thrown.");
    }

    private static string Format<T>(T value) => value switch
    {
        null => "<null>",
        string text => $"\"{text}\"",
        IEnumerable<int> numbers => string.Join(", ", numbers),
        _ => value.ToString() ?? "<null>",
    };
}

internal sealed class TestRunner
{
    private readonly string? _filter;

    public TestRunner(string? filter) => _filter = filter;

    public int RunAll()
    {
        PrintEnvironment();
        var tests = DiscoverTests();
        if (tests.Count == 0)
        {
            Console.Error.WriteLine("No tests discovered.");
            return 2;
        }

        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var failures = new List<string>();
        var total = Stopwatch.StartNew();

        foreach (var test in tests)
        {
            var display = $"{test.Type.Name}.{test.Method.Name}";
            if (!string.IsNullOrWhiteSpace(test.Attribute.Name))
            {
                display += $" ({test.Attribute.Name})";
            }

            Console.Write($"[ RUN      ] {display} ... ");
            var watch = Stopwatch.StartNew();
            try
            {
                var instance = Activator.CreateInstance(test.Type);
                test.Method.Invoke(instance, null);
                watch.Stop();
                Console.WriteLine($"PASS ({watch.ElapsedMilliseconds} ms)");
                passed++;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is SkipException skip)
            {
                watch.Stop();
                Console.WriteLine($"SKIP ({watch.ElapsedMilliseconds} ms): {skip.Message}");
                skipped++;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is TestFailureException failure)
            {
                watch.Stop();
                Console.WriteLine($"FAIL ({watch.ElapsedMilliseconds} ms)");
                Console.WriteLine(Indent(failure.Message));
                failed++;
                failures.Add($"{display}: {failure.Message}");
            }
            catch (TargetInvocationException ex)
            {
                watch.Stop();
                Console.WriteLine($"FAIL ({watch.ElapsedMilliseconds} ms)");
                var inner = ex.InnerException ?? ex;
                Console.WriteLine(Indent($"{inner.GetType().Name}: {inner.Message}{Environment.NewLine}{inner.StackTrace}"));
                failed++;
                failures.Add($"{display}: {inner.GetType().Name}: {inner.Message}");
            }
            catch (Exception ex)
            {
                watch.Stop();
                Console.WriteLine($"FAIL ({watch.ElapsedMilliseconds} ms)");
                Console.WriteLine(Indent($"{ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}"));
                failed++;
                failures.Add($"{display}: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (test.Type.GetInterfaces().Contains(typeof(IDisposable)) &&
                    test.Instance is IDisposable disposable)
                {
                    try
                    {
                        disposable.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"WARNING: test type dispose failed: {ex.Message}");
                    }
                }
            }
        }

        total.Stop();
        Console.WriteLine();
        Console.WriteLine(
            $"SUMMARY: total={tests.Count} passed={passed} failed={failed} skipped={skipped} " +
            $"duration={total.Elapsed.TotalSeconds:0.0}s");
        if (failures.Count > 0)
        {
            Console.WriteLine("FAILURES:");
            foreach (var failure in failures)
            {
                Console.WriteLine($"  - {failure}");
            }
        }

        return failed == 0 ? 0 : 1;
    }

    private List<DiscoveredTest> DiscoverTests()
    {
        var result = new List<DiscoveredTest>();
        foreach (var type in typeof(TestRunner).Assembly.GetTypes()
                     .Where(static type => type is { IsClass: true, IsAbstract: false })
                     .OrderBy(static type => type.FullName, StringComparer.Ordinal))
        {
            var typeFilter = _filter is not null &&
                             type.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase);
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => method.GetCustomAttribute<TestAttribute>() is not null)
                .OrderBy(static method => method.Name, StringComparer.Ordinal);
            foreach (var method in methods)
            {
                if (method.GetParameters().Length != 0)
                {
                    throw new InvalidOperationException($"Test {type.Name}.{method.Name} must not take parameters.");
                }

                var attribute = method.GetCustomAttribute<TestAttribute>()!;
                if (_filter is not null &&
                    !typeFilter &&
                    !method.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) &&
                    !(attribute.Name?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    continue;
                }

                result.Add(new DiscoveredTest(type, method, attribute));
            }
        }

        return result;
    }

    private static void PrintEnvironment()
    {
        var process = Process.GetCurrentProcess();
        Console.WriteLine("LiteClean test harness");
        Console.WriteLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"Process: {RuntimeInformation.ProcessArchitecture}, 64-bit={Environment.Is64BitProcess}");
        Console.WriteLine($"CPU: {Environment.ProcessorCount} logical processors");
        Console.WriteLine($"Temp: {Path.GetTempPath()}");
        Console.WriteLine($"Working set: {process.WorkingSet64 / (1024.0 * 1024.0):0.0} MB");
        Console.WriteLine();
    }

    private static string Indent(string text) =>
        string.Join(
            Environment.NewLine,
            text.Split(["\r\n", "\n"], StringSplitOptions.None)
                .Select(static line => "             " + line));

    private sealed record DiscoveredTest(Type Type, MethodInfo Method, TestAttribute Attribute)
    {
        public object? Instance { get; set; }
    }
}
