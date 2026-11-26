using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Xunit;

namespace OneNoteMcp.Integration;

/// <summary>
/// Story 4.14: the integration test classes that open (by path, by package or by creating one) or close a notebook. They
/// run one at a time, because one of them closing or reopening a throwaway changes what another expects to be open (4.11's
/// sitting: <see cref="OpenNotebookIntegrationTests"/> closed throwaway b while <see cref="CloseNotebookIntegrationTests"/>
/// adopted it). Other collections still run beside them. <see cref="ThrowawayOpenStateGuardTests"/> keeps the list whole.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ThrowawayOpenState
{
    public const string Name = "Throwaway open state";
}

/// <summary>
/// Story 4.14: fails when a test class reaches something that opens or closes a notebook but isn't in
/// <see cref="ThrowawayOpenState"/>. It reads each class's IL (its own methods, its nested state machines, lambdas and
/// helper gateways, and every method of this assembly they call, such as <c>TestNotebook</c>'s and <c>ProbeSession</c>'s);
/// it never touches OneNote, so it runs without <c>ONENOTE_MCP_INTEGRATION</c>. A direct <c>IApplication.OpenHierarchy</c>
/// doesn't count, because most callers create sections with it; opening or creating a notebook goes through
/// <c>TestNotebook</c>'s guarded wrappers, which do count.
/// </summary>
public sealed class ThrowawayOpenStateGuardTests
{
    /// <summary>Methods, on our own types or the COM interface, that open or close a notebook.</summary>
    private static readonly HashSet<string> OpensOrCloses =
    [
        "CloseNotebookAsync", "OpenHierarchyAsync", "CreateNotebookAsync", "OpenPackageAsync", // gateways, TestNotebook
        "OpenExistingAsync", "CreateThrowawayNotebookAsync", "CloseOpenedCopyAsync", "OpenBackupAsync", // TestNotebook
        "CloseNotebook", "OpenPackage", // IApplication, called directly
    ];

    /// <summary>Classes the scan flags that never reach OneNote, with the reason.</summary>
    private static readonly Dictionary<Type, string> Exempt = new()
    {
        [typeof(TestNotebookGuardTests)] = "runs the wrappers' COM lambdas on a recording fake, never on OneNote",
    };

    /// <summary>Use cases whose construction means the class runs an open or a close.</summary>
    private static readonly HashSet<string> OpeningUseCases = ["CloseNotebookUseCase", "OpenNotebookUseCase", "OpenPackageUseCase"];

    private static readonly Dictionary<short, OperandType> Operands = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value, o => o.OperandType);

    [Fact]
    public void EveryTestClassThatOpensOrClosesANotebook_IsInTheThrowawayOpenStateCollection()
    {
        var outside = OpeningTestClasses()
            .Where(t => t.GetCustomAttribute<CollectionAttribute>()?.Name != ThrowawayOpenState.Name)
            .Select(t => t.Name)
            .ToList();

        Assert.True(outside.Count == 0, $"Add [Collection(ThrowawayOpenState.Name)] to: {string.Join(", ", outside)}");
    }

    [Fact]
    public void TheScan_FindsTheClassesThatCloseAndReopenThrowawayB()
    {
        var found = OpeningTestClasses().ToList();

        Assert.Contains(typeof(CloseNotebookIntegrationTests), found);
        Assert.Contains(typeof(OpenNotebookIntegrationTests), found);
        Assert.DoesNotContain(typeof(SyncHierarchyIntegrationTests), found);
    }

    private static IEnumerable<Type> OpeningTestClasses() =>
        typeof(ThrowawayOpenState).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsNested && HasTests(t) && !Exempt.ContainsKey(t))
            .Where(t => WithNested(t).SelectMany(Bodies).Any(b => OpensOrClosesANotebook(b, new Dictionary<MethodBase, bool>())));

    private static bool HasTests(Type t) =>
        t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Any(m => m.IsDefined(typeof(FactAttribute), inherit: true));

    private static IEnumerable<Type> WithNested(Type t) =>
        t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(WithNested).Prepend(t);

    private static IEnumerable<MethodBase> Bodies(Type t)
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All));
    }

    /// <summary>
    /// Whether <paramref name="body"/> reaches an open or a close: directly, or through a method of this assembly that it
    /// calls (helpers such as <c>ProbeSession</c> or a probe's static steps), followed into async state machines and lambdas.
    /// </summary>
    private static bool OpensOrClosesANotebook(MethodBase body, Dictionary<MethodBase, bool> seen)
    {
        if (seen.TryGetValue(body, out var known))
        {
            return known;
        }

        seen[body] = false; // a cycle adds nothing
        var stateMachine = body.GetCustomAttribute<StateMachineAttribute>()?.StateMachineType;
        var result = CalledMethods(body).Any(m => IsOpenOrClose(m) || (m.Module == body.Module && OpensOrClosesANotebook(m, seen)))
            || (stateMachine is not null && Bodies(stateMachine).Any(b => OpensOrClosesANotebook(b, seen)));
        seen[body] = result;
        return result;
    }

    private static bool IsOpenOrClose(MethodBase m) =>
        (m is ConstructorInfo && OpeningUseCases.Contains(m.DeclaringType!.Name))
        || (m is MethodInfo && OpensOrCloses.Contains(m.Name) && m.DeclaringType?.Namespace?.StartsWith("OneNoteMcp", StringComparison.Ordinal) == true);

    /// <summary>The methods a body calls, constructs or takes a delegate to, decoding its IL one instruction at a time.</summary>
    private static IEnumerable<MethodBase> CalledMethods(MethodBase body)
    {
        var il = body.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            yield break;
        }

        var typeArgs = body.DeclaringType is { IsGenericType: true } d ? d.GetGenericArguments() : null;
        var methodArgs = body.IsGenericMethod ? body.GetGenericArguments() : null;
        for (var i = 0; i < il.Length;)
        {
            short code = il[i] == 0xFE ? (short)(0xFE00 | il[i + 1]) : il[i];
            i += il[i] == 0xFE ? 2 : 1;
            var operand = Operands[code];
            if (operand == OperandType.InlineMethod)
            {
                MethodBase? called = null;
                try
                {
                    called = body.Module.ResolveMethod(BitConverter.ToInt32(il, i), typeArgs, methodArgs);
                }
                catch (ArgumentException)
                {
                    // A token this context can't resolve: not one of ours.
                }

                if (called is not null)
                {
                    yield return called;
                }
            }

            i += operand switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, i)),
                _ => 4,
            };
        }
    }
}
