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
| `Alias` | An identifier, unique across the catalog. Code uses it as a type name. |
| Storage type | Any type name. The generator emits it verbatim and does not check it. |
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
| Storage type | The type of the external value. |
| `scale` | A finite, nonzero number, parsed with the invariant culture. |
| `offset` | A finite number. |

The canonical value $c$ relates to the external value $x$ by $c = x \cdot s + b$, where $s$ is `scale` and $b$ is
`offset`. The generator emits both directions in the public class `Magnitudo.Generated.UnitConversions`:

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

The attributes let the analyzer check both sides. `FromCelsius(20f)` is a temperature, and `ToCelsius(pressure)` is
[QNT003](diagnostics.md#qnt003). The project needs the `Magnitudo` package for the attribute type.

Number literals get an `f` suffix when the storage type is `float` or ends in `Single`. They get `m` for `decimal` or a
name ending in `Decimal`, and `d` otherwise.

## Catalog errors

The analyzer reports a malformed line as [QNT000](diagnostics.md#qnt000) and skips it. The rest of the catalog still
loads. Each message starts with `Invalid units catalog: `.

| Line | Message |
| --- | --- |
| `pressure \| Pascal \| global::System.Single` | `Expected 'id \| alias \| storage type \| dimensions'.` |
| `pressure \| 9Pascal \| global::System.Single \| mass=1` | `Quantity IDs and aliases must be identifiers and a storage type is required.` |
| `pressure \| Pascal \| global::System.Single \| mass:1` | `Invalid dimension entry 'mass:1'.` |
| `pressure \| Pascal \| global::System.Single \| length=-1.5` | `Invalid dimension entry 'length=-1.5'.` |
| `pressure \| Pascal \| global::System.Single \| mass=1,mass=2` | `Dimension 'mass' is listed more than once.` |
| A second line with alias `Pascal` | `Alias 'Pascal' is declared more than once.` |
| A second line with ID `pressure` | `Quantity ID 'pressure' is declared more than once.` |
| `convert \| a \| C \| global::System.Single \| 0 \| 0` | `Conversion scale must be a finite, nonzero number.` |
| `convert \| a \| C \| global::System.Single \| 1` | `Expected 'convert \| quantity id \| unit name \| storage type \| scale \| offset'.` |
| `convert \| nope \| C \| global::System.Single \| 1 \| 0` | `Conversion 'C' references unknown quantity 'nope'.` |
| A second `convert` line for `C` | `Conversion unit 'C' is declared more than once.` |

The unknown-quantity error has no file location. The parser finds it only after it reads the whole catalog.

The parser does not check storage types. A typo such as `global::System.Singel` appears as an ordinary C# error in the
generated file.
