# Magnitudo

Magnitudo is compile-time dimensional analysis for C#. You list your quantities in a catalog file. A source generator
turns each one into a `global using` alias, and an analyzer fails the build when an expression mixes dimensions.

```csharp
static float Total(Pascal pressure, Kelvin temperature) => pressure + temperature;
// error QNT001: Operator '+' cannot combine 'length^-1 mass^1 time^-2' and 'temperature'
```

> [!WARNING]
> Magnitudo is young and under active development. Packages use [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html).
> Until the public API settles, releases stay in the `0.x` series and ship as prereleases.

## Strengths

- Zero runtime cost: a `Kelvin` is a `float` in the compiled assembly
- Arrays stay primitive arrays, so SIMD, spans, and interop work as usual
- Native AOT and trimming are unaffected
- Dimensions are integer exponent vectors over any base names you choose, not a fixed SI set
- Several storage types per quantity (`Kelvin` and `Kelvin64`)
- Generated, checked conversions to and from external units such as Celsius
- Quantities survive across assemblies through `[Quantity]` and `[ElementQuantity]`
- Mismatches are ordinary Roslyn errors, controlled with `.editorconfig` like any other

## Weaknesses

- The C# compiler cannot tell `Kelvin` from `Pascal`, so checks cover only annotated code
- Quantities with the same exponents are interchangeable, so torque and energy look identical
- `Math.Sqrt`, `Math.Pow`, and most other functions return an unknown quantity
- Tuples, `foreach` variables, and whole-collection assignments are not checked
- No fractional exponents

## Installation

```bash
dotnet add package Magnitudo --prerelease
dotnet add package Magnitudo.Analyzers --prerelease
```

Then add a `.magnitudo` catalog as an `AdditionalFiles` item. [Getting started](docs/install.md) walks through it.

## Recommended reading

- [Getting started](docs/install.md)
- [How Magnitudo works](docs/how-it-works.md)
- [The catalog format](docs/catalog.md)
- [Diagnostics](docs/diagnostics.md)
- [Gotchas](docs/gotchas.md)
- [What the analyzer does not check](docs/missing.md)
- [Versioning and releasing](docs/versioning.md)

## Bug reports and contributions

Bug reports and pull requests are welcome on GitHub. Build and test with:

```bash
dotnet build Magnitudo.slnx -c Release -warnaserror
dotnet test Magnitudo.slnx -c Release
```

## Copyright, credits, and license

Magnitudo is licensed under the MIT license. See `LICENSE.TXT` for more info.

Magnitudo began as `Numos.Units`, the dimensional analysis layer of the [Numos](https://github.com/ArtisticRoomba/Numos)
atmospherics library. Numos needed to track quantities through a solution without wrapper types or runtime cost.
