# Magnitudo

Magnitudo is a .NET library for representing physical quantities, dimensions, and units, with dimensional consistency
checked at compile time. Quantities are described in a catalog file, a source generator turns each one into a
`global using` alias (`Kelvin`, `Pascal`, and so on), and an analyzer fails the build when an expression mixes
dimensions that don't fit, like adding a pressure to a temperature.

The aliases are erased. `Kelvin` is still a `float` in the compiled assembly, so there is no wrapper type, no runtime
cost, and nothing that gets in the way of Native AOT or trimming.

Magnitudo originated as the dimensional-analysis system used by the [Numos](https://github.com/ArtisticRoomba/Numos)
atmospherics simulation library and was later separated into a general-purpose library.

## Install

```bash
dotnet add package Magnitudo --prerelease
dotnet add package Magnitudo.Analyzers --prerelease
```

`Magnitudo` holds the `[Quantity]` and `[ElementQuantity]` attributes. `Magnitudo.Analyzers` is a development
dependency, so reference it with `PrivateAssets="all"` and it won't flow to your consumers:

```xml
<PackageReference Include="Magnitudo" Version="0.2.0-alpha.1"/>
<PackageReference Include="Magnitudo.Analyzers" Version="0.2.0-alpha.1" PrivateAssets="all"/>
```

## Add a catalog

Put your quantities in a `*.magnitudo` file and include it as an `AdditionalFiles` item:

```xml
<ItemGroup>
    <AdditionalFiles Include="MyProject.magnitudo"/>
</ItemGroup>
```

```text
temperature | Kelvin | global::System.Single | temperature=1
pressure    | Pascal | global::System.Single | mass=1,length=-1,time=-2
```

With that catalog, this fails to build:

```csharp
static float Total(Pascal pressure, Kelvin temperature) => pressure + temperature;
// error QNT001: Operator '+' cannot combine 'length^-1 mass^1 time^-2' and 'temperature'
```

The docs, in reading order:

- [Dimensional analysis](docs/dimensional_analysis.md): the model and what it costs at runtime.
- [The catalog](docs/catalog.md): the `.magnitudo` format, unit conversions, and catalog errors.
- [How the analyzer reasons](docs/inference.md): where quantities come from, what each position accepts, and what isn't
  checked.
- [Diagnostics](docs/diagnostics.md): `QNT000` to `QNT004`, with failing examples and fixes.
- [Versioning and releasing](docs/versioning.md).

## Build, test, pack

```bash
dotnet build Magnitudo.slnx -c Release -warnaserror
dotnet test Magnitudo.slnx -c Release
dotnet pack Magnitudo.slnx -c Release
```

Packages land in `artifacts/packages`. Versions for both packages come from `Version.props`.

## License

Magnitudo is licensed under the MIT license. See LICENSE.TXT for more info.