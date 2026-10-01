# Dimensional Analysis

Metron checks dimensional consistency at compile time. A Roslyn analyzer tracks the physical quantity of each value through a solution and reports expressions that combine dimensions that don't fit, such as adding a pressure to a temperature.

This is done entirely at compile time, and to preserve Native AOT compatibility, will likely never change. Using C# `alias`es allows us to annotate while keeping the underlying types with no overhead, so operations like vectorization are still done in the same manner with no required adjustment.
A generated alias such as `Kelvin` or `Pascal` that uses `global::System.Single` as the backing is still `System.Single` in CLR metadata, arrays remain primitive arrays, and arithmetic remains ordinary primitive arithmetic.

The system has two components:

- `Metron.Analyzers` generates aliases from a catalog and reports dimension mismatches as build errors.
- `Metron` supplies metadata attributes for public and cross-assembly declarations whose aliases are erased from
  CLR signatures.

## Catalog format

The catalog is any file with the `.metron` extension, included as an MSBuild `AdditionalFiles` item:

```xml
<ItemGroup>
    <AdditionalFiles Include="MyProject.metron"/>
</ItemGroup>
```

Lines starting with `#` are comments. Quantity declarations use four pipe-separated fields:

```text
stableId | Alias | global::System.Single | baseDimension=exponent,...
```

Base dimensions are names rather than a fixed enum, so a project can introduce another independent dimension without
changing the analyzer. `1` denotes a dimensionless quantity. Multiple aliases may use the same dimensions and different
primitive storage types, such as `Kelvin` and `Kelvin64`.

Boundary conversions use six fields:

```text
convert | stableId | ExternalUnitName | global::System.Single | scale | offset
```

The generated conversion follows `canonical = input * scale + offset` and emits `FromExternalUnitName` and
`ToExternalUnitName` methods in `Metron.Generated.UnitConversions`. These generated conversions also carry relevant metadata, so the analyzer can follow inputs and outputs around the solution just fine.

## Source annotations

Use a generated alias for local variables, parameters, return types, and generic element types:

```csharp
internal static Pascal CalculatePressure(
    Mole amount,
    Kelvin temperature,
    PascalPerMoleKelvin coefficient)
{
    return amount * temperature * coefficient;
}
```

Use `[Quantity("stableId")]` on public scalar declarations and `[ElementQuantity("stableId")]` on public arrays or
indexable storage when the quantity must remain visible to analyzers in another assembly.

Unannotated numeric values are deliberately treated as unknown. Numeric
literals may adopt the surrounding quantity, allowing ordinary constants such as zero and scalar factors. When adding
new code, annotate its storage boundary and important intermediate values so dimensional information is not lost.

## Diagnostics

All diagnostics are errors.

| Id             | Title                           | Reported when                                                                   |
|----------------|---------------------------------|---------------------------------------------------------------------------------|
| `METRON000` | Invalid units catalog           | A `.metron` file can't be parsed or defines an invalid quantity/conversion. |
| `METRON001` | Incompatible quantities         | A binary operator combines two operands whose dimensions don't fit.             |
| `METRON002` | Incompatible quantity assignment | A value is assigned to a target with a different quantity.                      |
| `METRON003` | Incompatible argument quantity  | An argument's quantity differs from the parameter's.                            |
| `METRON004` | Incompatible return quantity    | A returned expression's quantity differs from the method's return quantity.     |
