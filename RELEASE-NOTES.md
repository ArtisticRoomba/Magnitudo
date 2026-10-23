<!--
Add an entry under "## Master" in the same pull request as the change.
`python3 eng/version.py set|bump|promote` moves the Master entries into a new version section.
Do not edit released sections by hand pretty please.
-->

## Master

### Breaking changes

*None yet*

### New features

*None yet*

### Bugfixes

- Arrays and collections declared nullable, such as `Mole[]?` or `List<Metre>?`, keep their element quantity. Null-conditional indexing (`values?[0]`) keeps it too.

### Other

*None yet*

### Internal

*None yet*

## 0.3.0-beta.2

### Other

- The package metadata lists ArtisticRoomba and Magnitudo contributors as authors.

## 0.3.0-beta.1

### Breaking changes

- The analyzer checks field and property initializers (QNT002). Code that built before can now fail.
- Compound assignment into an unannotated target reports QNT002. `total += pressure` on a plain `float` is an error.
- `UnitConversions` is internal. It was public.
- QNT000 rejects `nint` and `nuint` storage, conversions on non-floating storage, keyword alias names, and conversion
  scales or offsets that are zero or out of range.

### New features

- Magnitudo targets netstandard2.0 as well as net10.0.
- The analyzer builds against Roslyn 4.0, so it loads in the .NET 6 SDK and Visual Studio 2022 17.0 and later.

### Bugfixes

- Generated aliases for keyword, `string`, `object`, and nullable storage types compile on C# 10 and 11.
- The analyzer no longer fails with AD0001 on declarations from a project reference in the IDE.
- A user type named like an alias no longer gets the alias's quantity.
- Generic types such as `ILookup<K, Metre>` no longer cause false QNT002 reports.
- Messages use the invariant culture.
- A catalog with only conversion lines reports the missing quantity.
- README links work on nuget.org.

### Other

- The docs cover params arrays and alias fields from referenced assemblies.
- The docs have an install requirements section.
- The docs cover switch expressions, pattern variables, and quantities that interfaces and overrides do not inherit.

### Internal

- The test suite is much larger.
- `eng/version.py` keeps `docs/install.md` in sync with `Version.props`.
- `RELEASE-NOTES.md` and the version script track release notes.

## 0.2.0-alpha.1

### New features

- First public prerelease with the catalog format, generated quantity aliases, and diagnostics QNT000 to QNT004.
