using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Magnitudo.Analyzers;

/// <summary>
///     The parsed contents of all <c>*.magnitudo</c> files: quantity definitions and unit conversions.
/// </summary>
internal sealed class UnitCatalog
{
    private readonly ImmutableDictionary<string, QuantityDefinition> _byAlias;
    private readonly ImmutableDictionary<string, QuantityDefinition> _byId;

    private UnitCatalog(
        IEnumerable<QuantityDefinition> definitions,
        IEnumerable<UnitConversionDefinition>? conversions = null)
    {
        QuantityDefinition[] materialized = definitions.ToArray();
        _byAlias = materialized.ToImmutableDictionary(
            definition => definition.Alias,
            StringComparer.Ordinal);

        // The first definition wins if an ID repeats. Parse rejects duplicates before it builds a catalog.
        _byId = materialized.GroupBy(definition => definition.Id, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        Definitions = materialized.ToImmutableArray();
        Conversions = (conversions ?? Array.Empty<UnitConversionDefinition>()).ToImmutableArray();
    }

    internal ImmutableArray<QuantityDefinition> Definitions { get; }
    internal ImmutableArray<UnitConversionDefinition> Conversions { get; }

    internal static UnitCatalog Empty { get; } = new(Array.Empty<QuantityDefinition>());

    internal bool TryGetAlias(string alias, out QuantityDefinition definition)
    {
        return _byAlias.TryGetValue(alias, out definition!);
    }

    internal bool TryGetId(string id, out QuantityDefinition definition)
    {
        return _byId.TryGetValue(id, out definition!);
    }

    /// <summary>
    ///     Parses every <c>*.magnitudo</c> file in <paramref name="files" />.
    /// </summary>
    /// <param name="files">The additional files of the compilation. Other extensions are ignored.</param>
    /// <param name="cancellationToken">Cancels reading the file text.</param>
    /// <param name="reportDiagnostic">
    ///     Receives QNT000 for each invalid line. When <see langword="null" />, the parser skips invalid lines silently.
    /// </param>
    /// <returns>
    ///     The catalog, or <see cref="Empty" /> when no line defines a quantity. A catalog with only conversion
    ///     lines is empty, but it still reports each conversion as an unknown quantity. The generator and the analyzer both call
    ///     this method, but only the generator passes <paramref name="reportDiagnostic" />. That way each error is
    ///     reported once.
    /// </returns>
    internal static UnitCatalog Parse(
        IEnumerable<AdditionalText> files, CancellationToken cancellationToken,
        Action<Diagnostic>? reportDiagnostic = null)
    {
        var definitions = new List<QuantityDefinition>();
        var conversions = new List<UnitConversionDefinition>();
        var conversionNames = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files.Where(file =>
                     file.Path.EndsWith(".magnitudo", StringComparison.OrdinalIgnoreCase)))
        {
            var text = file.GetText(cancellationToken);
            if (text is null)
                continue;

            for (int lineIndex = 0; lineIndex < text.Lines.Count; lineIndex++)
            {
                string line = text.Lines[lineIndex].ToString().Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                try
                {
                    if (IsConversionLine(line))
                    {
                        var conversion = ParseConversionLine(line);
                        if (!conversionNames.Add(conversion.UnitName))
                            throw new FormatException($"Conversion unit '{conversion.UnitName}' is declared more than once.");

                        conversions.Add(conversion);
                    }
                    else
                    {
                        var definition = ParseQuantityLine(line);
                        if (aliases.Contains(definition.Alias))
                            throw new FormatException($"Alias '{definition.Alias}' is declared more than once.");

                        if (ids.Contains(definition.Id))
                            throw new FormatException($"Quantity ID '{definition.Id}' is declared more than once.");

                        aliases.Add(definition.Alias);
                        ids.Add(definition.Id);

                        definitions.Add(definition);
                    }
                }
                catch (FormatException exception)
                {
                    if (reportDiagnostic is null)
                        continue;

                    var textLine = text.Lines[lineIndex];
                    var location = Location.Create(
                        file.Path,
                        textLine.Span,
                        new LinePositionSpan(
                            new LinePosition(lineIndex, 0),
                            new LinePosition(lineIndex, textLine.Span.Length)));

                    reportDiagnostic(Diagnostic.Create(Diagnostics.InvalidCatalog, location, exception.Message));
                }
            }
        }

        if (definitions.Count == 0 && conversions.Count == 0)
            return Empty;

        var catalog = new UnitCatalog(definitions, conversions);
        if (reportDiagnostic is not null)
        {
            foreach (var conversion in conversions)
            {
                if (!catalog.TryGetId(conversion.QuantityId, out _))
                {
                    reportDiagnostic(
                        Diagnostic.Create(
                            Diagnostics.InvalidCatalog,
                            Location.None,
                            $"Conversion '{conversion.UnitName}' references unknown quantity '{conversion.QuantityId}'."));
                }
            }
        }

        return definitions.Count == 0 ? Empty : catalog;
    }

    private static QuantityDefinition ParseQuantityLine(string line)
    {
        string[] parts = line.Split('|');
        if (parts.Length != 4)
            throw new FormatException("Expected 'id | alias | storage type | dimensions'.");

        string id = parts[0].Trim();
        string alias = parts[1].Trim();
        string storageType = parts[2].Trim();
        if (!IsIdentifier(id) || !IsIdentifier(alias) || storageType.Length == 0)
            throw new FormatException("Quantity IDs and aliases must be identifiers and a storage type is required.");

        if (IsReservedAliasName(alias))
            throw new FormatException($"Alias '{alias}' is a C# keyword.");

        if (StorageTypes.IsNativeInteger(storageType))
            throw new FormatException(
                $"Storage type '{storageType}' is not supported. Use 'int' or 'long'.");

        var dimensions = ImmutableSortedDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        string dimensionText = parts[3].Trim();
        if (dimensionText.Length != 0 && dimensionText != "1")
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string dimensionEntry in dimensionText.Split(','))
            {
                string[] pair = dimensionEntry.Split('=');
                if (pair.Length != 2 ||
                    !IsIdentifier(pair[0].Trim()) ||
                    !int.TryParse(
                        pair[1].Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int exponent))
                    throw new FormatException($"Invalid dimension entry '{dimensionEntry.Trim()}'.");

                string name = pair[0].Trim();
                if (!names.Add(name))
                    throw new FormatException($"Dimension '{name}' is listed more than once.");

                if (exponent != 0)
                    dimensions[name] = exponent;
            }
        }

        return new QuantityDefinition(id, alias, storageType, new DimensionVector(dimensions.ToImmutable()));
    }

    private static UnitConversionDefinition ParseConversionLine(string line)
    {
        string[] parts = line.Split('|');
        if (parts.Length != 6 || parts[0].Trim() != "convert")
            throw new FormatException("Expected 'convert | quantity id | unit name | storage type | scale | offset'.");

        string quantityId = parts[1].Trim();
        string unitName = parts[2].Trim();
        string storageType = parts[3].Trim();
        if (!IsIdentifier(quantityId) || !IsIdentifier(unitName) || storageType.Length == 0)
            throw new FormatException("Conversion quantity IDs and unit names must be identifiers.");

        if (!StorageTypes.IsFloatingPoint(storageType))
            throw new FormatException(
                $"Conversion '{unitName}' needs a float, double, or decimal storage type, not '{storageType}'.");

        if (!double.TryParse(
                parts[4].Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double scale) ||
            scale == 0d ||
            double.IsNaN(scale) ||
            double.IsInfinity(scale))
            throw new FormatException("Conversion scale must be a finite, nonzero number.");

        if (!double.TryParse(
                parts[5].Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double offset) ||
            double.IsNaN(offset) ||
            double.IsInfinity(offset))
            throw new FormatException("Conversion offset must be a finite number.");

        if (!StorageTypes.Fits(scale, storageType, out bool isZero))
            throw new FormatException($"Conversion scale is outside the range of '{storageType}'.");

        if (isZero)
            throw new FormatException($"Conversion scale rounds to zero in '{storageType}'.");

        if (!StorageTypes.Fits(offset, storageType, out _))
            throw new FormatException($"Conversion offset is outside the range of '{storageType}'.");

        return new UnitConversionDefinition(quantityId, unitName, storageType, scale, offset);
    }

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="alias" /> cannot be a type name.
    /// </summary>
    /// <remarks>
    ///     Reserved keywords cannot name a type. The contextual names below are legal identifiers, but an alias
    ///     with that name would change what the same word means elsewhere: <c>var x = 1;</c> would declare a
    ///     <c>float</c>-typed local.
    /// </remarks>
    private static bool IsReservedAliasName(string alias)
    {
        return SyntaxFacts.GetKeywordKind(alias) != SyntaxKind.None ||
               alias is "var" or "dynamic" or "nint" or "nuint" or "_";
    }

    private static bool IsConversionLine(string line)
    {
        int separator = line.IndexOf('|');
        string first = separator < 0 ? line : line.Substring(0, separator);
        return first.Trim() == "convert";
    }

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0 || !(char.IsLetter(value[0]) || value[0] == '_'))
            return false;

        return value.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_');
    }
}

/// <summary>
///     One <c>convert</c> line: a unit of a quantity, defined as <c>value * scale + offset</c> in the quantity's
///     canonical unit.
/// </summary>
internal sealed class UnitConversionDefinition
{
    internal UnitConversionDefinition(
        string quantityId, string unitName, string storageType,
        double scale, double offset)
    {
        QuantityId = quantityId;
        UnitName = unitName;
        StorageType = storageType;
        Scale = scale;
        Offset = offset;
    }

    internal string QuantityId { get; }
    internal string UnitName { get; }
    internal string StorageType { get; }
    internal double Scale { get; }
    internal double Offset { get; }
}

/// <summary>
///     One quantity line: a stable ID, the alias, the storage type behind the alias, and the dimension.
/// </summary>
internal sealed class QuantityDefinition
{
    internal QuantityDefinition(string id, string alias, string storageType, DimensionVector dimensions)
    {
        Id = id;
        Alias = alias;
        StorageType = storageType;
        Dimensions = dimensions;
    }

    internal string Id { get; }
    internal string Alias { get; }
    internal string StorageType { get; }
    internal DimensionVector Dimensions { get; }
}

/// <summary>
///     A dimension as an exponent vector over named base dimensions.
/// </summary>
/// <remarks>
///     The vector never stores a zero exponent, so two vectors are equal when their nonzero exponents match.
///     Dimensionless is the empty vector. Equality ignores quantity names and IDs.
/// </remarks>
internal sealed class DimensionVector : IEquatable<DimensionVector>
{
    private readonly ImmutableSortedDictionary<string, int> _exponents;

    internal DimensionVector(ImmutableSortedDictionary<string, int> exponents)
    {
        _exponents = exponents;
    }

    public bool Equals(DimensionVector? other)
    {
        return other is not null && _exponents.SequenceEqual(other._exponents);
    }

    /// <summary>
    ///     Returns <see langword="true" /> when the dimension is dimensionless.
    /// </summary>
    public bool IsScalar()
    {
        return _exponents.All(e => e.Value == 0);
    }

    internal DimensionVector Multiply(DimensionVector other)
    {
        return Combine(other, 1);
    }

    internal DimensionVector Divide(DimensionVector other)
    {
        return Combine(other, -1);
    }

    internal DimensionVector Invert()
    {
        return new DimensionVector(
            _exponents.ToImmutableSortedDictionary(pair => pair.Key, pair => -pair.Value, StringComparer.Ordinal));
    }

    /// <summary>
    ///     Halves every exponent, which is the dimension of a square root.
    /// </summary>
    /// <param name="root">The halved dimension, or <see langword="null" /> when the method returns false.</param>
    /// <returns>
    ///     <see langword="false" /> when an exponent is odd. Exponents are integers, so <c>length^1</c> has no square
    ///     root in the model.
    /// </returns>
    internal bool TrySquareRoot(out DimensionVector? root)
    {
        if (_exponents.Any(pair => pair.Value % 2 != 0))
        {
            root = null;
            return false;
        }

        root = new DimensionVector(
            _exponents.ToImmutableSortedDictionary(pair => pair.Key, pair => pair.Value / 2, StringComparer.Ordinal));
        return true;
    }

    private DimensionVector Combine(DimensionVector other, int sign)
    {
        var result = _exponents.ToBuilder();
        foreach (KeyValuePair<string, int> pair in other._exponents)
        {
            result.TryGetValue(pair.Key, out int current);
            int exponent = current + sign * pair.Value;
            if (exponent == 0)
                result.Remove(pair.Key);
            else
                result[pair.Key] = exponent;
        }

        return new DimensionVector(result.ToImmutable());
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as DimensionVector);
    }

    public override int GetHashCode()
    {
        int hash = 17;
        foreach (KeyValuePair<string, int> pair in _exponents)
            hash = unchecked(hash * 31 + pair.Key.GetHashCode() * 397 ^ pair.Value);

        return hash;
    }

    public override string ToString()
    {
        return _exponents.Count == 0
            ? "dimensionless"
            : string.Join(
                " ",
                _exponents.Select(pair => _exponents.Count == 1 && pair.Value == 1
                    ? pair.Key
                    : pair.Key + "^" + pair.Value.ToString(CultureInfo.InvariantCulture)));
    }
}

/// <summary>
///     Recognizes the storage type names that the generator maps, in keyword and <c>System</c> forms.
/// </summary>
internal static class StorageTypes
{
    /// <summary>
    ///     Returns the C# keyword for a primitive type written as a keyword, <c>System.X</c>, or
    ///     <c>global::System.X</c>. Returns <see langword="null" /> for any other name.
    /// </summary>
    internal static string? GetKeyword(string type)
    {
        type = type.Trim();
        if (type.StartsWith("global::", StringComparison.Ordinal))
            type = type.Substring("global::".Length);

        if (type.StartsWith("System.", StringComparison.Ordinal))
        {
            return type.Substring("System.".Length) switch
            {
                "Boolean" => "bool",
                "Byte" => "byte",
                "SByte" => "sbyte",
                "Int16" => "short",
                "UInt16" => "ushort",
                "Int32" => "int",
                "UInt32" => "uint",
                "Int64" => "long",
                "UInt64" => "ulong",
                "Single" => "float",
                "Double" => "double",
                "Decimal" => "decimal",
                "Char" => "char",
                "String" => "string",
                "Object" => "object",
                "IntPtr" => "nint",
                "UIntPtr" => "nuint",
                _ => null
            };
        }

        return type switch
        {
            "bool" or "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong"
                or "float" or "double" or "decimal" or "char" or "string" or "object" or "nint" or "nuint" => type,
            _ => null
        };
    }

    /// <summary>
    ///     Returns <see langword="true" /> for <c>float</c>, <c>double</c>, <c>decimal</c>, and their <c>System</c>
    ///     forms. A nullable type is not floating-point storage for a conversion.
    /// </summary>
    internal static bool IsFloatingPoint(string type)
    {
        return GetKeyword(type) is "float" or "double" or "decimal";
    }

    /// <summary>
    ///     Returns <see langword="true" /> for <c>nint</c>, <c>nuint</c>, <c>IntPtr</c>, <c>UIntPtr</c>, and their
    ///     nullable forms.
    /// </summary>
    internal static bool IsNativeInteger(string type)
    {
        type = type.Trim();
        if (type.EndsWith("?", StringComparison.Ordinal))
            type = type.Substring(0, type.Length - 1);

        return GetKeyword(type) is "nint" or "nuint";
    }

    /// <summary>
    ///     Formats <paramref name="value" /> with the invariant culture so that it reads as a floating-point literal.
    /// </summary>
    internal static string FormatNumber(double value)
    {
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        if (!text.Contains('.') && !text.Contains('E') && !text.Contains('e'))
            text += ".0";

        return text;
    }

    /// <summary>
    ///     Checks that the generated literal for <paramref name="value" /> compiles in <paramref name="type" />.
    /// </summary>
    /// <param name="value">A finite number.</param>
    /// <param name="type">A floating-point storage type.</param>
    /// <param name="isZero">
    ///     Set when a nonzero <paramref name="value" /> becomes zero in <paramref name="type" />. Dividing by such
    ///     a scale fails at run time.
    /// </param>
    /// <returns><see langword="false" /> when the literal is outside the range of the type.</returns>
    internal static bool Fits(double value, string type, out bool isZero)
    {
        isZero = false;
        switch (GetKeyword(type))
        {
            case "float":
                float single = (float)value;
                isZero = value != 0d && single == 0f;
                return !float.IsInfinity(single);
            case "decimal":
                // The compiler parses the literal text as a decimal, so the check does the same.
                if (!decimal.TryParse(
                        FormatNumber(value),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out decimal number))
                    return false;

                isZero = value != 0d && number == 0m;
                return true;
            default:
                return true;
        }
    }
}
