# Getting started

This page takes a project from no Magnitudo to its first build error.

## Add the packages

```xml
<ItemGroup>
    <PackageReference Include="Magnitudo" Version="0.3.0-beta.2"/>
    <PackageReference Include="Magnitudo.Analyzers" Version="0.3.0-beta.2" PrivateAssets="all"/>
</ItemGroup>
```

`Magnitudo` holds the `[Quantity]` and `[ElementQuantity]` attributes. Your compiled code references them, so it is a
normal dependency. `Magnitudo.Analyzers` holds the generator and the analyzer. It runs only inside the compiler, so
`PrivateAssets="all"` keeps it out of your consumers' dependency graph.

Both packages always ship with the same version. Do not mix versions.

## Requirements

The `Magnitudo` package targets `netstandard2.0` and `net10.0`, so it works from .NET Framework 4.6.1 and later.

The generated aliases are `global using` directives, which need C# 10 or later. Projects that target .NET Framework or
`netstandard` default to C# 7.3, so set the language version:

```xml
<PropertyGroup>
    <LangVersion>10</LangVersion>
</PropertyGroup>
```

The analyzer needs a compiler that supports Roslyn 4.0. Use one of these or a later release:

| Tool | Minimum version |
| --- | --- |
| .NET SDK | 6.0 |
| Visual Studio | 2022 17.0 |

An older compiler reports warning CS8032 and skips the analyzer. No aliases are generated, so every alias in your
code is an unknown type.

## Write a catalog

Create a file with the `.magnitudo` extension and pass it to the compiler:

```xml
<ItemGroup>
    <AdditionalFiles Include="MyProject.magnitudo"/>
</ItemGroup>
```

```text
length         | Metre               | global::System.Single | length=1
amount         | Mole                | global::System.Single | amount=1
temperature    | Kelvin              | global::System.Single | temperature=1
pressure       | Pascal              | global::System.Single | mass=1,length=-1,time=-2
gasCoefficient | PascalPerMoleKelvin | global::System.Single | mass=1,length=-1,time=-2,amount=-1,temperature=-1
```

Each line is a quantity: a stable ID, the alias name, the storage type, and the dimension exponents. The
[catalog format](catalog.md) covers every field.

## Use the aliases

The generator emits one `global using` per line, so `Metre`, `Kelvin`, `Pascal`, and the rest are now types in every file:

```csharp
internal static Pascal IdealGasPressure(Mole amount, Kelvin temperature, PascalPerMoleKelvin coefficient)
{
    return amount * temperature * coefficient;
}
```

This compiles because the exponents of the three factors add up to a pressure. Change one `*` to `+` and the build
fails with [QNT001](diagnostics.md#qnt001).

## Annotate public API

Aliases vanish from compiled metadata. Another assembly that calls a `Pascal` method sees a plain `float`. Put the
quantity in an attribute on any public declaration that other assemblies call:

```csharp
[return: Quantity("pressure")]
public static float Pressure([Quantity("amount")] float amount, [Quantity("temperature")] float temperature) { ... }

public static void Fill([ElementQuantity("length")] float[] heights) { ... }
```

Inside one assembly, the alias is enough.

## Migrate existing code

A large codebase rarely compiles clean on the first build. Downgrade the noisiest rule while you add annotations:

```ini
[*.cs]
dotnet_diagnostic.QNT003.severity = warning
```

Plain `float` code compiles without errors until it meets a quantity. Passing it to a quantity parameter is
[QNT003](diagnostics.md#qnt003), and storing a quantity in it is [QNT002](diagnostics.md#qnt002), including `total +=
pressure`. Start at storage boundaries such as fields, arrays, and public methods. Work inward from there. [How
Magnitudo works](how-it-works.md) explains which positions accept unannotated values.
