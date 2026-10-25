# Magnitudo

Magnitudo is a compile-time dimensional analysis library for C#.

Magnitudo itself is simple:

- You list your quantities in a catalog file,
- A source generator turns each one into a `global using` alias, and
- An analyzer fails the build when an expression mixes dimensions.

```csharp
static float Total(Pascal pressure, Kelvin temperature) => pressure + temperature;
// error QNT001: Operator '+' cannot combine 'length^-1 mass^1 time^-2' and 'temperature'
```

## Strengths

- Zero runtime cost: a `Kelvin` is a `float` in the compiled assembly
- Arrays stay primitive arrays, so SIMD, spans, and interop work as usual
- SIMD code is checked too: a `Vector<Pascal>` is a pressure, lane by lane
- Native AOT and trimming are unaffected
- Dimensions are integer exponent vectors over any base names you choose, not a fixed SI set
- Several storage types per quantity (`Kelvin` and `Kelvin64`)
- Generated, checked conversions to and from external units such as Celsius
- Quantities survive across assemblies through `[Quantity]` and `[ElementQuantity]`
- Mismatches are ordinary Roslyn errors, controlled with `.editorconfig` like any other

## Weaknesses

- The C# compiler cannot tell `Kelvin` from `Pascal`, so checks cover only annotated code
- Quantities with the same exponents are interchangeable, so torque and energy look identical
- `Math.Pow`, `Math.Cbrt`, most other functions, and `Math.Sqrt` of an odd exponent return an unknown quantity
- Tuples, `foreach` variables, and whole-collection assignments are not checked
- No fractional exponents

## Installation

```bash
dotnet add package Magnitudo --prerelease
dotnet add package Magnitudo.Analyzers --prerelease
```

Then add a `.magnitudo` catalog as an `AdditionalFiles` item. [Getting started](https://github.com/ArtisticRoomba/Magnitudo/blob/master/docs/install.md) walks through it.

## Recommended reading

- [Getting started](https://github.com/ArtisticRoomba/Magnitudo/blob/master/docs/install.md)
- [How Magnitudo works](https://github.com/ArtisticRoomba/Magnitudo/blob/master/docs/how-it-works.md)
- [The catalog format](https://github.com/ArtisticRoomba/Magnitudo/blob/master/docs/catalog.md)
- [Diagnostics](https://github.com/ArtisticRoomba/Magnitudo/blob/master/docs/diagnostics.md)
- [Gotchas](https://github.com/ArtisticRoomba/Magnitudo/blob/master/docs/gotchas.md)
- [What the analyzer does not check](https://github.com/ArtisticRoomba/Magnitudo/blob/master/docs/missing.md)
- [Versioning and releasing](https://github.com/ArtisticRoomba/Magnitudo/blob/master/docs/versioning.md)

## Bug reports and contributions

Bug reports and pull requests are welcome on GitHub. Build and test with:

```bash
dotnet build Magnitudo.slnx -c Release
dotnet test Magnitudo.slnx -c Release
```

## Copyright, credits, and license

Magnitudo is licensed under the MIT license. See `LICENSE.TXT` for more info.

Magnitudo began as `Numos.Units`, the dimensional analysis layer of the [Numos](https://github.com/ArtisticRoomba/Numos)
atmospherics library. Numos needed to track quantities through a solution without wrapper types or runtime cost.
