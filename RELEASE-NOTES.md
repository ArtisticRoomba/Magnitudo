<!--
Add an entry under "## Master" in the same pull request as the change.
`python3 eng/version.py set|bump|promote` moves the Master entries into a new version section.
Do not edit released sections by hand pretty please.
-->

## Master

### Breaking changes

*None yet*

### New features

- Magnitudo targets netstandard2.0 as well as net10.0.
- The analyzer builds against Roslyn 4.0, so it loads in the .NET 6 SDK and Visual Studio 2022 17.0 and later.

### Bugfixes

- The analyzer checks field and property initializers (QNT002).
- Generated aliases compile on C# 10 and 11. Aliases for keyword types such as `float` needed C# 12 before.
- README links work on nuget.org.

### Other

- The docs cover params arrays and alias fields from referenced assemblies.
- The docs have an install requirements section.

### Internal

- The test suite is much larger.
- `eng/version.py` keeps `docs/install.md` in sync with `Version.props`.
- `RELEASE-NOTES.md` and the version script track release notes.

## 0.2.0-alpha.1

### New features

- First public prerelease with the catalog format, generated quantity aliases, and diagnostics QNT000 to QNT004.
