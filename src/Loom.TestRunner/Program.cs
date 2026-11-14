using System.Collections.ObjectModel;
using System.Reflection;

namespace Loom.TestRunner;

/// <summary>
/// Minimal in-process xunit runner used in restricted environments (the DSH sandbox denies the
/// VSTest testhost the process handles it needs). Supports [Fact] and [Theory]+[InlineData].
/// Usage: dotnet run --project src\Loom.TestRunner [-- path\to\Loom.Tests.dll]
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var assemblyPath = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.Combine(AppContext.BaseDirectory, "Loom.Tests.dll");

        if (!File.Exists(assemblyPath))
        {
            Console.Error.WriteLine("Test assembly not found: " + assemblyPath);
            return 2;
        }

        var assembly = Assembly.LoadFrom(assemblyPath);
        var testDirectory = Path.GetDirectoryName(assemblyPath)!;
        AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
        {
            try
            {
                var simpleName = new AssemblyName(eventArgs.Name).Name;
                if (string.IsNullOrEmpty(simpleName)) return null;
                var candidate = Path.Combine(testDirectory, simpleName + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            }
            catch
            {
                return null;
            }
        };
        // Load dependent assemblies (Loom.Core, xunit) next to the test assembly.
        foreach (var dependency in Directory.EnumerateFiles(testDirectory, "*.dll"))
        {
            var name = Path.GetFileNameWithoutExtension(dependency);
            if (name is "Loom.Tests" or "Loom.TestRunner") continue;
            try
            {
                var alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies()
                    .Any(existing => string.Equals(existing.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
                if (!alreadyLoaded) Assembly.LoadFrom(dependency);
            }
            catch
            {
                // best effort
            }
        }
        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var failures = new List<string>();

        foreach (var type in assembly.GetTypes()
                     .Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition)
                     .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(method => IsFact(method) || IsTheory(method))
                .OrderBy(method => method.Name, StringComparer.Ordinal)
                .ToList();
            if (methods.Count == 0) continue;

            foreach (var method in methods)
            {
                var cases = new List<object?[]>();
                if (IsTheory(method))
                {
                    foreach (var inlineData in GetInlineData(method))
                    {
                        cases.Add(inlineData);
                    }

                    if (cases.Count == 0)
                    {
                        skipped++;
                        Console.WriteLine($"SKIP  {type.Name}.{method.Name} (no InlineData)");
                        continue;
                    }
                }
                else
                {
                    cases.Add(Array.Empty<object?>());
                }

                foreach (var testCase in cases)
                {
                    var label = $"{type.Name}.{method.Name}" +
                                (testCase.Length > 0 ? "(" + string.Join(", ", testCase.Select(Format)) + ")" : "");
                    object? instance = null;
                    try
                    {
                        instance = method.IsStatic ? null : Activator.CreateInstance(type);
                        var result = method.Invoke(instance, testCase);
                        if (result is Task task)
                        {
                            await task.ConfigureAwait(false);
                        }
                        else if (result is ValueTask valueTask)
                        {
                            await valueTask.ConfigureAwait(false);
                        }

                        passed++;
                        Console.WriteLine("PASS  " + label);
                    }
                    catch (Exception exception)
                    {
                        failed++;
                        var actual = exception is TargetInvocationException { InnerException: not null }
                            ? exception.InnerException!
                            : exception;
                        failures.Add($"{label}\n    {actual.GetType().Name}: {actual.Message}");
                        Console.WriteLine("FAIL  " + label);
                        Console.WriteLine("    " + actual.GetType().Name + ": " + actual.Message);
                    }
                    finally
                    {
                        if (instance is IDisposable disposable)
                        {
                            try { disposable.Dispose(); } catch { }
                        }
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Total: {passed + failed + skipped}  Passed: {passed}  Failed: {failed}  Skipped: {skipped}");
        if (failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Failures:");
            foreach (var failure in failures) Console.WriteLine(failure);
        }

        return failed == 0 ? 0 : 1;
    }

    private static bool IsFact(MethodInfo method) => HasAttribute(method, "Xunit.FactAttribute");
    private static bool IsTheory(MethodInfo method) => HasAttribute(method, "Xunit.TheoryAttribute");

    private static bool HasAttribute(MethodInfo method, string fullName) =>
        method.GetCustomAttributesData().Any(attribute => attribute.AttributeType.FullName == fullName);

    private static IEnumerable<object?[]> GetInlineData(MethodInfo method)
    {
        foreach (var attribute in method.GetCustomAttributesData())
        {
            if (attribute.AttributeType.FullName != "Xunit.InlineDataAttribute") continue;
            var values = new List<object?>();
            foreach (var argument in attribute.ConstructorArguments)
            {
                if (argument.Value is ReadOnlyCollection<CustomAttributeTypedArgument> collection)
                {
                    values.AddRange(collection.Select(item => Unwrap(item.Value)));
                }
                else if (argument.Value is IEnumerable<CustomAttributeTypedArgument> enumerable)
                {
                    values.AddRange(enumerable.Select(item => Unwrap(item.Value)));
                }
                else
                {
                    values.Add(Unwrap(argument.Value));
                }
            }

            yield return values.ToArray();
        }
    }

    private static object? Unwrap(object? value)
    {
        if (value is Type type) return type;
        if (value is CustomAttributeTypedArgument typed) return Unwrap(typed.Value);
        return value;
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        string text => "\"" + text + "\"",
        _ => value.ToString() ?? "",
    };
}
