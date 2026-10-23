# The catalog format

A catalog lists the quantities a project uses and the external units it converts to and from. The generator reads every
`.magnitudo` file passed as an `AdditionalFiles` item and merges them into one catalog. Without a valid quantity line,
the generator emits nothing and the analyzer reports nothing.

```xml
<ItemGroup>
    <AdditionalFiles Include="MyProject.magnitudo"/>
</ItemGroup>
```

Lines are pipe-separated. Lines that start with `#` and blank lines are ignored.

## Quantity lines

```text
stableId | Alias | global::System.Single | baseDimension=exponent,...
```

| Field | Rule |
| --- | --- |
| `stableId` | An identifier. `[Quantity("...")]` refers to the quantity by this name. |
| `Alias` | An identifier, unique across the catalog, and not a C# keyword. Code uses it as a type name. |
| Storage type | A type name. The generator maps it to a `global using` target and does not check that the type exists. Native integers are rejected. |
| Dimensions | Comma-separated `name=exponent` pairs with integer exponents. `1` or an empty field means dimensionless. |

An identifier starts with a letter or `_` and continues with letters, digits, or `_`. The parser drops zero exponents,
so `length=1,time=0` equals `length=1`.

```text
# Base quantities
length      | Metre          | global::System.Single | length=1
time        | Second         | global::System.Single | time=1
mass        | Kilogram       | global::System.Single | mass=1
temperature | Kelvin         | global::System.Single | temperature=1

# Derived
velocity    | MetrePerSecond | global::System.Single | length=1,time=-1
pressure    | Pascal         | global::System.Single | mass=1,length=-1,time=-2

# Same dimension, double storage
temperature64 | Kelvin64     | global::System.Double | temperature=1

# Dimensionless
ratio       | Ratio          | global::System.Single | 1
```

The generator writes one line per quantity to `Magnitudo.QuantityAliases.g.cs`:

```csharp
global using Kelvin = global::System.Single;
global using Kelvin64 = global::System.Double;
```

Keywords such as `float`, `string`, and `object` become their `global::System` types, and `float?` becomes
`global::System.Nullable<global::System.Single>`. Before C# 12 a `using` alias cannot name a keyword or `T?`, so the
mapping keeps the aliases valid on C# 10 and 11. An alias cannot carry a nullable annotation, so `string?` becomes
`global::System.String`. Any other name passes through unchanged, and a nullable form of it needs C# 12.

The generator rejects `nint`, `nuint`, `System.IntPtr`, and `System.UIntPtr` as storage types, with or without `?`.
`global using X = nint;` does not compile on Roslyn 4.0, the minimum version. Use `long` or `int` instead.

An alias name that is a C# keyword is also rejected. That covers reserved words such as `class` and the contextual
names `var`, `dynamic`, `nint`, `nuint`, and `_`. An alias named `var` would change the meaning of `var x = 1;`.

Two aliases with the same exponents are compatible, so `Kelvin` and `Kelvin64` mix freely. The analyzer does not check
the narrowing from `double` to `float`. The C# compiler handles that as usual.

## Conversion lines

A conversion relates an external unit to the canonical quantity. Use conversions at the edge of a program: sensor
input, user input, and printed output. Work in canonical units everywhere else.

```text
convert | stableId | ExternalUnitName | global::System.Single | scale | offset
```

| Field | Rule |
| --- | --- |
| `stableId` | The ID of an existing quantity line. |
| `ExternalUnitName` | An identifier, unique across all conversions in the catalog. |
| Storage type | The type of the external value: `float`, `double`, `decimal`, or the `System` form of one. |
| `scale` | A finite, nonzero number, parsed with the invariant culture. It must stay nonzero and in range after conversion to the storage type. |
| `offset` | A finite number that is in range for the storage type. |

The canonical value $c$ relates to the external value $x$ by $c = x \cdot s + b$, where $s$ is `scale` and $b$ is
`offset`. The generator emits both directions as `From<Unit>` and `To<Unit>` methods:

```text
temperature | Kelvin | global::System.Single | temperature=1
convert | temperature | Celsius | global::System.Single | 1 | 273.15
```

```csharp
[return: global::Magnitudo.QuantityAttribute("temperature")]
public static global::System.Single FromCelsius(global::System.Single value) => value * 1.0f + 273.15f;

public static global::System.Single ToCelsius(
    [global::Magnitudo.QuantityAttribute("temperature")] global::System.Single value) => (value - 273.15f) / 1.0f;
```

By default the methods live in `internal static class Magnitudo.Generated.UnitConversions`. The class is internal
because every project that uses the catalog generates its own copy. A public copy would clash with the one in a
referenced project (CS0436). An internal copy still clashes when a library grants `InternalsVisibleTo` to another
project that uses the catalog.

### Putting conversions in your own class

Mark a class with `[UnitConversions]` to receive the methods instead. Use it to expose conversions from a library or
to avoid the `InternalsVisibleTo` clash.

```csharp
namespace Numos.Units;

[Magnitudo.UnitConversions]
public static partial class Temperatures { }
```

`Temperatures.FromCelsius(20f)` now exists, with the quantity attributes shown above and XML docs on each method.

- The class must be `static partial`, non-generic, and not nested. Otherwise the generator reports
  [QNT005](diagnostics.md#qnt005) and skips the class.
- The generated part has no accessibility modifier, so the modifier on your declaration applies. Without one the class
  is internal.
- The generator skips the default class once any class has the attribute.
- Each marked class receives every conversion in the catalog.

The attributes let the analyzer check both sides. `FromCelsius(20f)` is a temperature, and `ToCelsius(pressure)` is
[QNT003](diagnostics.md#qnt003). The project needs the `Magnitudo` package for the attribute type.

Conversions need floating-point storage because the generated expression multiplies by a fractional literal. With `int`
storage that expression fails with CS0266. Number literals get an `f` suffix for `float`, `m` for `decimal`, and `d` for
`double`.

A zero scale is invalid because `To<Unit>` divides by it. The parser checks the scale after it converts the number to
the storage type. `1e-50` becomes `0f` in `float`, so the parser rejects it. It also rejects a value outside the range
of the type, such as `1e30` for `decimal`.

## Catalog errors

The generator reports a malformed line as [QNT000](diagnostics.md#qnt000) and skips it. The rest of the catalog still
loads. The analyzer reads the same catalog but stays silent, so each error appears once. Each message starts with
`Invalid units catalog: `.

| Line | Message |
| --- | --- |
| `pressure \| Pascal \| global::System.Single` | `Expected 'id \| alias \| storage type \| dimensions'.` |
| `pressure \| 9Pascal \| global::System.Single \| mass=1` | `Quantity IDs and aliases must be identifiers and a storage type is required.` |
| `pressure \| Pascal \| global::System.Single \| mass:1` | `Invalid dimension entry 'mass:1'.` |
| `pressure \| Pascal \| global::System.Single \| length=-1.5` | `Invalid dimension entry 'length=-1.5'.` |
| `pressure \| Pascal \| global::System.Single \| mass=1,mass=2` | `Dimension 'mass' is listed more than once.` |
| A second line with alias `Pascal` | `Alias 'Pascal' is declared more than once.` |
| A second line with ID `pressure` | `Quantity ID 'pressure' is declared more than once.` |
| `pressure \| Pascal \| nint \| mass=1` | `Storage type 'nint' is not supported. Use 'int' or 'long'.` |
| `pressure \| var \| global::System.Single \| mass=1` | `Alias 'var' is a C# keyword.` |
| `convert \| a \| C \| global::System.Single \| 0 \| 0` | `Conversion scale must be a finite, nonzero number.` |
| `convert \| a \| C \| int \| 12 \| 0` | `Conversion 'C' needs a float, double, or decimal storage type, not 'int'.` |
| `convert \| a \| C \| decimal \| 1e-30 \| 0` | `Conversion scale rounds to zero in 'decimal'.` |
| `convert \| a \| C \| float \| 1e39 \| 0` | `Conversion scale is outside the range of 'float'.` |
| `convert \| a \| C \| float \| 1 \| 1e39` | `Conversion offset is outside the range of 'float'.` |
| `convert \| a \| C \| global::System.Single \| 1` | `Expected 'convert \| quantity id \| unit name \| storage type \| scale \| offset'.` |
| `convert \| nope \| C \| global::System.Single \| 1 \| 0` | `Conversion 'C' references unknown quantity 'nope'.` |
| A second `convert` line for `C` | `Conversion unit 'C' is declared more than once.` |

The unknown-quantity error has no file location. The parser finds it only after it reads the whole catalog. A catalog
with only `convert` lines reports it for every conversion.

The parser does not check that a storage type exists. A typo such as `global::System.Singel` appears as an ordinary C#
error in the generated file.
