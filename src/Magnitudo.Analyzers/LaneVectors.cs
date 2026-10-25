using Microsoft.CodeAnalysis;

namespace Magnitudo.Analyzers;

/// <summary>
///     How a vector helper method combines the quantities of its operands.
/// </summary>
internal enum VectorRule
{
    /// <summary>
    ///     The result has no quantity the analyzer can follow.
    /// </summary>
    Unknown,

    /// <summary>
    ///     The operands must agree, and the result has their quantity: <c>Min</c>, <c>ConditionalSelect</c>,
    ///     <c>Create</c>, loads.
    /// </summary>
    Same,

    /// <summary>
    ///     The result has the first operand's quantity, and the others are not checked: <c>AndNot</c>.
    /// </summary>
    First,

    /// <summary>
    ///     The result is the product of two operands: <c>Multiply</c>, <c>Dot</c>.
    /// </summary>
    Multiply,

    /// <summary>
    ///     The result is the quotient of two operands.
    /// </summary>
    Divide,

    /// <summary>
    ///     <c>a * b + c</c>, where <c>c</c> must match the product.
    /// </summary>
    FusedMultiplyAdd,

    /// <summary>
    ///     The operands must agree, and the result is a mask or a <see cref="bool" /> with no quantity.
    /// </summary>
    Compare,

    /// <summary>
    ///     A bitwise AND, which keeps the quantity of the operand that is not a mask.
    /// </summary>
    Mask,

    /// <summary>
    ///     Writes the first operand's lanes to the destination that the second operand names.
    /// </summary>
    Store,

    /// <summary>
    ///     Writes the first operand's lanes, converted to a wider type, to the <see langword="out" /> arguments.
    /// </summary>
    Widen,

    /// <summary>
    ///     A square root, which halves the exponents of its operand.
    /// </summary>
    SquareRoot
}

/// <summary>
///     The rule of one vector helper method, with the parameters that hold no lane values.
/// </summary>
internal readonly struct VectorMethodRule
{
    private readonly int _skippedOrdinals;

    /// <param name="rule">How the operands combine.</param>
    /// <param name="skippedOrdinals">
    ///     Ordinals of parameters with the lane type that are still not operands, such as the condition of
    ///     <c>ConditionalSelect</c>. Ordinals count from zero in the static method, so an extension receiver is 0.
    /// </param>
    internal VectorMethodRule(VectorRule rule, params int[] skippedOrdinals)
    {
        Rule = rule;
        _skippedOrdinals = skippedOrdinals.Aggregate(0, (mask, ordinal) => mask | (1 << ordinal));
    }

    internal VectorRule Rule { get; }

    internal bool Skips(int ordinal)
    {
        return ordinal < 32 && (_skippedOrdinals & (1 << ordinal)) != 0;
    }
}

/// <summary>
///     Recognizes SIMD vector types and the helper methods that work on them.
/// </summary>
/// <remarks>
///     <para>
///         A lane vector is <see cref="System.Numerics.Vector{T}" /> or one of <c>Vector64&lt;T&gt;</c> to
///         <c>Vector512&lt;T&gt;</c>. Their arithmetic works lane by lane, so a vector has one quantity, the quantity
///         of each lane. The helper classes <see cref="System.Numerics.Vector" /> and <c>Vector64</c> to
///         <c>Vector512</c> share method names, so one table covers all of them.
///     </para>
///     <para>
///         Operands are found from parameter types, never from parameter names. A parameter of type <c>T</c>,
///         <c>ref T</c>, <c>T[]</c>, a span of <c>T</c>, or a lane vector holds lane values. An <see cref="int" />
///         index or an <see cref="UIntPtr" /> offset in a generic method does not. The few lane-typed parameters
///         that are not operands are listed by ordinal in the rule table.
///     </para>
///     <para>
///         Hardware intrinsic classes such as <c>Avx</c> and <c>AdvSimd</c> are not covered. Their results are
///         unknown.
///     </para>
/// </remarks>
internal static class LaneVectors
{
    private static readonly HashSet<string> HelperTypes = new(StringComparer.Ordinal)
    {
        "System.Numerics.Vector",
        "System.Runtime.Intrinsics.Vector64",
        "System.Runtime.Intrinsics.Vector128",
        "System.Runtime.Intrinsics.Vector256",
        "System.Runtime.Intrinsics.Vector512"
    };

    private static readonly Dictionary<string, VectorMethodRule> Rules = CreateRules();

    internal static bool IsLaneVector(ITypeSymbol? type)
    {
        return TryGetLaneType(type, out _);
    }

    /// <summary>
    ///     Returns <see langword="true" /> for a lane vector and gives its lane type, such as <see cref="float" />
    ///     for <c>Vector&lt;float&gt;</c>.
    /// </summary>
    internal static bool TryGetLaneType(ITypeSymbol? type, out ITypeSymbol lane)
    {
        lane = null!;
        if (type is not INamedTypeSymbol { TypeArguments.Length: 1 } named)
            return false;

        var definition = named.OriginalDefinition;
        string ns = definition.ContainingNamespace.ToDisplayString();
        bool isVector = ns switch
        {
            "System.Numerics" => definition.MetadataName == "Vector`1",
            "System.Runtime.Intrinsics" => definition.MetadataName is
                "Vector64`1" or "Vector128`1" or "Vector256`1" or "Vector512`1",
            _ => false
        };

        if (!isVector)
            return false;

        lane = named.TypeArguments[0];
        return true;
    }

    /// <summary>
    ///     Returns <see langword="true" /> for a method on a vector helper class or on a lane vector type.
    /// </summary>
    internal static bool IsVectorMethod(IMethodSymbol method)
    {
        var type = method.ContainingType;
        return type is not null &&
               (HelperTypes.Contains(type.OriginalDefinition.ToDisplayString()) || IsLaneVector(type));
    }

    /// <summary>
    ///     Returns the rule for a vector method, or an <see cref="VectorRule.Unknown" /> rule for a method not in
    ///     the table.
    /// </summary>
    internal static VectorMethodRule GetRule(IMethodSymbol method)
    {
        // A constructor of Vector<T> loads or broadcasts, like Create.
        string name = method.MethodKind == MethodKind.Constructor ? "Create" : method.Name;
        return Rules.TryGetValue(name, out var rule) ? rule : new VectorMethodRule(VectorRule.Unknown);
    }

    /// <summary>
    ///     Decides whether a parameter of a vector method takes lane values.
    /// </summary>
    /// <param name="method">The called method, constructed with its type arguments.</param>
    /// <param name="parameter">A parameter of <paramref name="method" />.</param>
    /// <param name="rule">The method's rule, which lists lane-typed parameters that are not operands.</param>
    internal static bool IsOperand(IMethodSymbol method, IParameterSymbol parameter, VectorMethodRule rule)
    {
        if (rule.Skips(parameter.Ordinal))
            return false;

        // The definition keeps T, so a Vector128<int> index (int) and its lanes (T) stay apart.
        var definitionType = parameter.OriginalDefinition.Type;
        if (definitionType is ITypeParameterSymbol || IsLaneVector(definitionType))
            return true;

        if (TryGetBufferElement(definitionType, out var element))
            return element is ITypeParameterSymbol || SymbolEqualityComparer.Default.Equals(element, GetLane(method));

        // A non-generic overload such as Vector128.Create(float, float, float, float) spells out the lane type.
        bool isGeneric = method.OriginalDefinition.IsGenericMethod ||
                         method.ContainingType.OriginalDefinition.IsGenericType;
        return !isGeneric && SymbolEqualityComparer.Default.Equals(definitionType, GetLane(method));
    }

    /// <summary>
    ///     Returns <see langword="true" /> for an array, <see cref="Span{T}" />, or <see cref="ReadOnlySpan{T}" />,
    ///     and gives its element type.
    /// </summary>
    internal static bool TryGetBufferElement(ITypeSymbol type, out ITypeSymbol element)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                element = array.ElementType;
                return true;
            case INamedTypeSymbol { TypeArguments.Length: 1 } named when
                named.OriginalDefinition.ToDisplayString() is "System.Span<T>" or "System.ReadOnlySpan<T>":
                element = named.TypeArguments[0];
                return true;
            default:
                element = null!;
                return false;
        }
    }

    /// <summary>
    ///     Returns <see langword="true" /> for methods that return a reference to an element of their first argument:
    ///     <c>MemoryMarshal.GetArrayDataReference</c>, <c>MemoryMarshal.GetReference</c>, and <c>Unsafe.Add</c>.
    /// </summary>
    /// <remarks>
    ///     These feed <c>Vector.LoadUnsafe</c> and <c>StoreUnsafe</c>. For <c>Unsafe.Add</c> the first argument is
    ///     already an element reference.
    /// </remarks>
    internal static bool IsElementReference(IMethodSymbol method)
    {
        string type = method.ContainingType.ToDisplayString();
        return type switch
        {
            "System.Runtime.InteropServices.MemoryMarshal" => method.Name is "GetArrayDataReference" or "GetReference",
            "System.Runtime.CompilerServices.Unsafe" => method.Name == "Add",
            _ => false
        };
    }

    /// <summary>
    ///     Returns <see langword="true" /> for <c>MemoryMarshal.Cast&lt;T, Vector&lt;T&gt;&gt;</c> and the reverse
    ///     direction, which regroup elements into lanes without changing their values.
    /// </summary>
    internal static bool IsLaneCast(IMethodSymbol method)
    {
        if (method.Name != "Cast" ||
            method.ContainingType.ToDisplayString() != "System.Runtime.InteropServices.MemoryMarshal" ||
            method.TypeArguments.Length != 2)
            return false;

        var from = method.TypeArguments[0];
        var to = method.TypeArguments[1];
        return SymbolEqualityComparer.Default.Equals(from, to) ||
               (TryGetLaneType(to, out var lane) && SymbolEqualityComparer.Default.Equals(lane, from)) ||
               (TryGetLaneType(from, out lane) && SymbolEqualityComparer.Default.Equals(lane, to));
    }

    /// <summary>
    ///     Returns the lane type of a call: that of its first vector parameter, otherwise that of its result.
    /// </summary>
    private static ITypeSymbol? GetLane(IMethodSymbol method)
    {
        foreach (var parameter in method.Parameters)
        {
            if (TryGetLaneType(parameter.Type, out var lane))
                return lane;
        }

        if (method.MethodKind == MethodKind.Constructor && TryGetLaneType(method.ContainingType, out var own))
            return own;

        return TryGetLaneType(method.ReturnType, out var result) ? result : null;
    }

    private static Dictionary<string, VectorMethodRule> CreateRules()
    {
        var rules = new Dictionary<string, VectorMethodRule>(StringComparer.Ordinal);

        void Add(VectorRule rule, params string[] names)
        {
            foreach (string name in names)
                rules[name] = new VectorMethodRule(rule);
        }

        Add(
            VectorRule.Same,
            "Abs", "Negate", "Add", "Subtract",
            "Min", "Max", "MinNumber", "MaxNumber", "MinNative", "MaxNative",
            "MinMagnitude", "MaxMagnitude", "MinMagnitudeNumber", "MaxMagnitudeNumber",
            "Clamp", "ClampNative", "Round", "Floor", "Ceiling", "Truncate",
            "Narrow", "WidenLower", "WidenUpper",
            "ConvertToSingle", "ConvertToDouble", "ConvertToInt32", "ConvertToInt64",
            "Create", "CreateScalar", "CreateScalarUnsafe",
            "Load", "LoadUnsafe", "LoadAligned", "LoadAlignedNonTemporal",
            "GetElement", "ToScalar", "WithElement", "GetLower", "GetUpper", "WithLower", "WithUpper", "Sum",
            "AsVector", "AsVector64", "AsVector128", "AsVector256", "AsVector512");

        Add(VectorRule.First, "AndNot");
        Add(VectorRule.Multiply, "Multiply", "Dot");
        Add(VectorRule.Divide, "Divide");
        Add(VectorRule.FusedMultiplyAdd, "FusedMultiplyAdd", "MultiplyAddEstimate");
        Add(
            VectorRule.Compare,
            "Equals", "EqualsAll", "EqualsAny",
            "LessThan", "LessThanAll", "LessThanAny",
            "LessThanOrEqual", "LessThanOrEqualAll", "LessThanOrEqualAny",
            "GreaterThan", "GreaterThanAll", "GreaterThanAny",
            "GreaterThanOrEqual", "GreaterThanOrEqualAll", "GreaterThanOrEqualAny");

        Add(VectorRule.Mask, "BitwiseAnd");
        Add(VectorRule.Store, "Store", "StoreUnsafe", "StoreAligned", "StoreAlignedNonTemporal", "CopyTo", "TryCopyTo");
        Add(VectorRule.Widen, "Widen");

        // System.Numerics.Vector calls it SquareRoot, and Vector64 to Vector512 call it Sqrt.
        Add(VectorRule.SquareRoot, "SquareRoot", "Sqrt");

        // ConditionalSelect(condition, left, right): the condition has the lane type but is a mask.
        rules["ConditionalSelect"] = new VectorMethodRule(VectorRule.Same, 0);

        // CopySign(value, sign): only the sign of the second operand is used.
        rules["CopySign"] = new VectorMethodRule(VectorRule.Same, 1);

        return rules;
    }
}
