# Dimensional analysis

Metron checks dimensional consistency at compile time. A Roslyn analyzer tracks the physical quantity of each value
through a solution and reports expressions that combine dimensions that don't fit, such as adding a pressure to a
temperature.

Every quantity has a dimension, a vector of integer exponents over base dimensions. For length $L$, mass $M$ and
time $T$:

$$
[\text{velocity}] = L\,T^{-1}, \qquad [\text{force}] = M\,L\,T^{-2}
$$

The analyzer applies three rules. Products and quotients add and subtract exponents. Sums, differences and comparisons
require equal dimensions. Everything else is a matter of knowing what each value's dimension is, which the rest of these
docs cover.

```csharp
internal static Pascal CalculatePressure(
    Mole amount,
    Kelvin temperature,
    PascalPerMoleKelvin coefficient)
{
    return amount * temperature * coefficient;
}
```

This compiles because $[n][T][c] = [p]$. Change `amount * temperature` to `amount + temperature` and the build fails with
[METRON001](diagnostics.md#metron001).

## No runtime cost

All of this happens at compile time. Metron uses C# `using` aliases, so the annotated types are erased: a generated alias
such as `Kelvin` or `Pascal` backed by `global::System.Single` is still `System.Single` in CLR metadata, arrays remain
primitive arrays, and arithmetic remains ordinary primitive arithmetic. Operations like vectorization work as they do on
any `float`, and Native AOT and trimming are unaffected. This is a design constraint and unlikely to change.

The cost is that the C# compiler can't tell `Kelvin` from `Pascal`. Only the analyzer can, and it only sees what is
annotated. [How the analyzer reasons](inference.md) says exactly where its knowledge ends.

## Components

- `Metron.Analyzers` generates aliases from a catalog and reports dimension mismatches as build errors.
- `Metron` supplies the `[Quantity]` and `[ElementQuantity]` attributes, for public and cross-assembly declarations
  whose aliases are erased from CLR signatures.

## Reading order

| Page | Covers |
| --- | --- |
| [catalog.md](catalog.md) | The `.metron` file: quantity lines, unit conversions, what is generated, catalog errors. |
| [inference.md](inference.md) | How a value's quantity is determined, what each position accepts, what isn't checked. |
| [diagnostics.md](diagnostics.md) | `METRON000` to `METRON004`, each with failing code, the message, and the fix. |

## Source annotations

Use a generated alias for local variables, parameters, return types, and generic element types.

Use `[Quantity("stableId")]` on public scalar declarations and `[ElementQuantity("stableId")]` on public arrays or
indexable storage when the quantity must remain visible to analyzers in another assembly:

```csharp
[return: Quantity("pressure")]
public static float Pressure(
    [Quantity("amount")] float amount,
    [Quantity("temperature")] float temperature) { ... }

public static void Fill([ElementQuantity("length")] float[] heights) { ... }
```

Unannotated numeric values are deliberately treated as unknown. Numeric literals may adopt the surrounding quantity,
allowing ordinary constants such as zero and scalar factors. When adding new code, annotate its storage boundary and
important intermediate values so dimensional information is not lost.
