# Numos.Units

Compile-time dimensional analysis for C#. You describe your quantities in a catalog file, a source generator turns
each one into a `global using` alias (`Kelvin`, `Pascal`, and so on), and an analyzer fails the build when an
expression mixes dimensions that don't fit, like adding a pressure to a temperature.

The aliases are erased. `Kelvin` is still a `float` in the compiled assembly, so there is no wrapper type, no runtime
cost, and nothing that gets in the way of Native AOT or trimming.

It was split out of [Numos](https://github.com/ArtisticRoomba/Numos), which still uses it.

## Install

```bash
dotnet add package Numos.Units --prerelease
dotnet add package Numos.Units.Analyzers --prerelease
```

`Numos.Units` holds the `[Quantity]` and `[ElementQuantity]` attributes. `Numos.Units.Analyzers` is a development
dependency, so reference it with `PrivateAssets="all"` and it won't flow to your consumers:

```xml
<PackageReference Include="Numos.Units" Version="0.2.0-alpha.1"/>
<PackageReference Include="Numos.Units.Analyzers" Version="0.2.0-alpha.1" PrivateAssets="all"/>
```

## Add a catalog

Put your quantities in a `*.numosunits` file and include it as an `AdditionalFiles` item:

```xml
<ItemGroup>
    <AdditionalFiles Include="MyProject.numosunits"/>
</ItemGroup>
```

```text
temperature | Kelvin | global::System.Single | temperature=1
pressure    | Pascal | global::System.Single | mass=1,length=-1,time=-2
```

The catalog format, source annotations, and diagnostics (`NUMOSUNIT000` to `NUMOSUNIT004`) are described in
[docs/dimensional_analysis.md](docs/dimensional_analysis.md).

## Build, test, pack

```bash
dotnet build Numos.Units.slnx -c Release -warnaserror
dotnet test Numos.Units.slnx -c Release
dotnet pack Numos.Units.slnx -c Release
```

Packages land in `artifacts/packages`. Versions for both packages come from `Version.props`.

## License

MIT, see `LICENSE.TXT`.
