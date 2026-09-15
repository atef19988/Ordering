using System.Reflection;
using Ordering.Application.Abstractions;

namespace Ordering.UnitTests.Architecture;

/// <summary>
/// Guards the dependency rule <c>Api -> Infrastructure -> Application -> Domain</c> at the assembly
/// level, where it cannot be argued with.
/// </summary>
public class ArchitectureTests
{
    private static readonly string[] AllowedApplicationReferences =
    [
        "System.",
        "netstandard",
        "Microsoft.Extensions.",
        "FluentValidation",
        "Ordering.Domain",
    ];

    private static readonly string[] ForbiddenInApplication =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Data.SqlClient",
        "Dapper",
    ];

    [Fact]
    public void Domain_references_nothing_but_the_runtime()
    {
        var references = ReferencesOf(Assembly.Load("Ordering.Domain"));

        Assert.All(references, name => Assert.StartsWith("System.", name, StringComparison.Ordinal));
    }

    [Fact]
    public void Application_references_no_EF_SqlClient_or_Dapper()
    {
        var references = ReferencesOf(typeof(Result).Assembly);

        Assert.DoesNotContain(references, name => ForbiddenInApplication.Any(f => name.StartsWith(f, StringComparison.Ordinal)));
    }

    [Fact]
    public void Application_depends_only_on_Domain_and_framework_abstractions()
    {
        var references = ReferencesOf(typeof(Result).Assembly);

        Assert.All(references, name => Assert.Contains(AllowedApplicationReferences, allowed => name.StartsWith(allowed, StringComparison.Ordinal)));
    }

    private static string[] ReferencesOf(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
}
