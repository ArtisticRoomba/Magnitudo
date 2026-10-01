# Π METRON

Dimensional Analysis for .NET

Metron is a .NET library for representing physical quantities, dimensions, and units, with dimensional consistency
checked at compile time. You describe your quantities in a catalog file, a source generator turns each one into a
`global using` alias (`Kelvin`, `Pascal`, and so on), and an analyzer fails the build when an expression mixes
dimensions that don't fit, like adding a pressure to a temperature.

The aliases are erased. `Kelvin` is still a `float` in the compiled assembly, so there is no wrapper type, no runtime
cost, and nothing that gets in the way of Native AOT or trimming.

Metron originated as the dimensional-analysis system used by the [Numos](https://github.com/ArtisticRoomba/Numos) atmospherics simulator and was later separated into a general-purpose library. Numos depends on it.

## Install

```bash
dotnet add package Metron --prerelease
dotnet add package Metron.Analyzers --prerelease
```

`Metron` holds the `[Quantity]` and `[ElementQuantity]` attributes. `Metron.Analyzers` is a development
dependency, so reference it with `PrivateAssets="all"` and it won't flow to your consumers:

```xml
<PackageReference Include="Metron" Version="0.2.0-alpha.1"/>
<PackageReference Include="Metron.Analyzers" Version="0.2.0-alpha.1" PrivateAssets="all"/>
```

## Add a catalog

Put your quantities in a `*.metron` file and include it as an `AdditionalFiles` item:

```xml
<ItemGroup>
    <AdditionalFiles Include="MyProject.metron"/>
</ItemGroup>
```

```text
temperature | Kelvin | global::System.Single | temperature=1
pressure    | Pascal | global::System.Single | mass=1,length=-1,time=-2
```

The catalog format, source annotations, and diagnostics (`METRON000` to `METRON004`) are described in
[docs/dimensional_analysis.md](docs/dimensional_analysis.md).

## Build, test, pack

```bash
dotnet build Metron.slnx -c Release -warnaserror
dotnet test Metron.slnx -c Release
dotnet pack Metron.slnx -c Release
```

Packages land in `artifacts/packages`. Versions for both packages come from `Version.props`.

## License

MIT, see `LICENSE.TXT`.
