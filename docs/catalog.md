# The catalog

A catalog is a text file with the `.metron` extension. It declares the quantities your project uses and, optionally,
the external units you convert from and to. Metron reads every `.metron` file passed to the compiler as an
`AdditionalFiles` item, merges them into one catalog, and generates one `global using` alias per quantity.

```xml
<ItemGroup>
    <AdditionalFiles Include="MyProject.metron"/>
</ItemGroup>
```

Without a valid quantity line, the generator emits nothing and the analyzer stays quiet.

## Dimensions

Each quantity carries a vector of integer exponents over a set of base dimensions. Writing $[q]$ for the dimension of
a quantity $q$ and $L$, $M$, $T$ for length, mass and time:

$$
[\text{velocity}] = L\,T^{-1}, \qquad [\text{pressure}] = M\,L^{-1}\,T^{-2}
$$

The catalog spells the same thing as `length=1,time=-1` and `mass=1,length=-1,time=-2`.

Base dimensions are plain names, not a fixed enum. A project that needs something the SI base set doesn't cover, such as
a simulation `tick`, adds it by using the name:

```text
perTick | PerTick | global::System.Single | tick=-1
```

Two quantities with the same exponent vector are the same dimension to the analyzer, whatever their names. See
[Same dimension, different meaning](inference.md#same-dimension-different-meaning).

## Quantity lines

```text
stableId | Alias | global::System.Single | baseDimension=exponent,...
```

| Field | Rule |
| --- | --- |
| `stableId` | An identifier. The name `[Quantity("...")]` uses to refer to this line. |
| `Alias` | An identifier. Becomes the alias you write in code. Unique across the catalog. |
| storage type | Any type name. Emitted verbatim on the right side of the alias. Not checked by Metron. |
| dimensions | Comma-separated `name=exponent` pairs. Exponents are integers. `1` or an empty field means dimensionless. |

An identifier starts with a letter or `_` and continues with letters, digits or `_`. A zero exponent is dropped, so
`length=1,time=0` is the same as `length=1`.

A complete small catalog:

```text
# Base quantities
length      | Metre          | global::System.Single | length=1
time        | Second         | global::System.Single | time=1
mass        | Kilogram       | global::System.Single | mass=1
temperature | Kelvin         | global::System.Single | temperature=1
amount      | Mole           | global::System.Single | amount=1

# Derived
velocity    | MetrePerSecond | global::System.Single | length=1,time=-1
force       | Newton         | global::System.Single | mass=1,length=1,time=-2
pressure    | Pascal         | global::System.Single | mass=1,length=-1,time=-2

# Same dimension as temperature, double storage
temperature64 | Kelvin64     | global::System.Double | temperature=1

# No dimensions
ratio       | Ratio          | global::System.Single | 1
```

Lines starting with `#` and blank lines are ignored.

### What gets generated

For each quantity, one line in `Metron.QuantityAliases.g.cs`:

```csharp
global using Kelvin = global::System.Single;
global using Kelvin64 = global::System.Double;
global using Metre = global::System.Single;
```

An alias is a compile-time name. `Kelvin` is `System.Single` in the compiled assembly, so `Kelvin[]` is a `float[]`,
arithmetic is ordinary float arithmetic, and nothing is boxed or wrapped. The same holds for Native AOT and trimming:
there is no Metron type left at runtime.

The converse is that the C# compiler doesn't distinguish `Kelvin` from `Pascal`. Only the analyzer does.

### Several storage types

The same dimensions may appear under more than one alias. This is how a project keeps single- and double-precision
versions of a quantity:

```text
temperature   | Kelvin   | global::System.Single | temperature=1
temperature64 | Kelvin64 | global::System.Double | temperature=1
```

The analyzer treats `Kelvin` and `Kelvin64` as compatible. It does not check that a narrowing `double` to `float`
conversion is intended; the C# compiler does that as usual.

## Conversion lines

```text
convert | stableId | ExternalUnitName | global::System.Single | scale | offset
```

| Field | Rule |
| --- | --- |
| `stableId` | The `stableId` of an existing quantity line. |
| `ExternalUnitName` | An identifier. Unique across all conversions, not just within one quantity. |
| storage type | The type of the external value, emitted for the method's parameter and return type. |
| `scale` | A finite, nonzero number. Parsed with the invariant culture. |
| `offset` | A finite number. |

The conversion relates the external value $x$ to the canonical value $c$ by

$$
c = x \cdot s + b
$$

where $s$ is `scale` and $b$ is `offset`. Metron emits both directions:

| Method | Computes |
| --- | --- |
| `From<Unit>(x)` | $x \cdot s + b$ |
| `To<Unit>(c)` | $(c - b) / s$ |

Given a Celsius and a Fahrenheit conversion:

```text
temperature | Kelvin | global::System.Single | temperature=1
convert | temperature | Celsius    | global::System.Single | 1         | 273.15
convert | temperature | Fahrenheit | global::System.Single | 0.5555556 | 255.37222
```

the generator adds:

```csharp
namespace Metron.Generated
{
    public static class UnitConversions
    {
        [return: global::Metron.QuantityAttribute("temperature")]
        public static global::System.Single FromCelsius(global::System.Single value) => value * 1.0f + 273.15f;

        public static global::System.Single ToCelsius(
            [global::Metron.QuantityAttribute("temperature")] global::System.Single value) => (value - 273.15f) / 1.0f;

        // FromFahrenheit and ToFahrenheit follow the same shape.
    }
}
```

Number literals take an `f` suffix when the storage type ends in `Single` and a `d` suffix otherwise.

The `[Quantity]` attributes on the generated methods are what let the analyzer follow a value across the boundary.
`FromCelsius(20f)` is a `temperature`; passing a `Pascal` to `ToCelsius` is a [METRON003](diagnostics.md#metron003).
`Metron.Generated.UnitConversions` is public, so it needs the `Metron` package referenced for the attribute type.

Conversions are meant for the edge of a program: reading a sensor, parsing user input, printing a result. Inside, work
in canonical units. Because the formula is affine, `FromCelsius(a) - FromCelsius(b)` is a difference in kelvin, which
is what you want; `FromCelsius(a) + FromCelsius(b)` is a sum of two absolute temperatures, which the analyzer can't
tell is meaningless.

## Catalog errors

A malformed line is reported as [METRON000](diagnostics.md#metron000), with the location of the line, and skipped. The
rest of the catalog still loads, so one typo doesn't turn every alias into a missing-type error. It will still produce
errors wherever the skipped alias is used.

| Line | Message |
| --- | --- |
| `pressure \| Pascal \| global::System.Single` | `Expected 'id \| alias \| storage type \| dimensions'.` |
| `pressure \| 9Pascal \| global::System.Single \| mass=1` | `Quantity IDs and aliases must be identifiers and a storage type is required.` |
| `pressure \| Pascal \| global::System.Single \| mass:1` | `Invalid dimension entry 'mass:1'.` |
| `pressure \| Pascal \| global::System.Single \| mass=one` | `Invalid dimension entry 'mass=one'.` |
| a second line using alias `Pascal` | `Alias 'Pascal' is declared more than once.` |
| `convert \| a \| C \| global::System.Single \| 0 \| 0` | `Conversion scale must be a finite, nonzero number.` |
| `convert \| a \| C \| global::System.Single \| 1` | `Expected 'convert \| quantity id \| unit name \| storage type \| scale \| offset'.` |
| `convert \| nope \| C \| global::System.Single \| 1 \| 0` | `Conversion 'C' references unknown quantity 'nope'.` |
| a second `convert` line naming `C` | `Conversion unit 'C' is declared more than once.` |

Each message is prefixed with `Invalid units catalog: `. The unknown-quantity error has no file location, because it's
found after the whole catalog is read.

### Things the parser doesn't catch

- **Duplicate `stableId`s.** Aliases are checked for uniqueness; IDs are not. If two lines share an ID, the first one
  wins for `[Quantity("...")]` lookups and the second is silently ignored there. Keep IDs unique.
- **IDs beginning with `convert`.** A line is treated as a conversion if it starts with `convert`, so a quantity whose
  ID is `converter` or `convection` is parsed as a malformed conversion and rejected. Rename the ID.
- **Bad storage types.** `global::System.Singel` is passed through. The error shows up in the generated file as an
  ordinary C# compile error.
