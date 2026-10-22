# Versioning and releasing

Magnitudo uses Semantic Versioning 2.0.0. `Magnitudo` and `Magnitudo.Analyzers` always ship with the same version,
stored in `Version.props`.

## Change the version

Do not edit `Version.props` by hand. `eng/version.py` keeps `Version`, `AssemblyVersion`, and `FileVersion` in sync.
Each command that writes a version also updates the `PackageReference` versions in `docs/install.md`. The command fails
before it writes anything if that file has no `Magnitudo` or `Magnitudo.Analyzers` reference:

```shell
python3 eng/version.py get
python3 eng/version.py set 0.3.0-beta.1
python3 eng/version.py bump patch --prerelease rc.1
python3 eng/version.py bump prerelease     # 0.2.0-alpha.1 -> 0.2.0-alpha.2
python3 eng/version.py promote             # 0.2.0-rc.2 -> 0.2.0
```

`bump major|minor|patch` resets the lower components. It drops any prerelease unless you pass `--prerelease`.
`bump prerelease` increments the last numeric prerelease identifier. It fails on a version without a prerelease.

## Release notes

`RELEASE-NOTES.md` lists the changes in each version, newest first. Add an entry under `## Master` in the same PR as
the change. Master always holds five subsections in this order, and an empty one says `*None yet*`:

- Breaking changes: changes that make existing code fail to build or behave differently.
- New features: new catalog options, diagnostics, or target frameworks.
- Bugfixes: corrections to wrong behavior.
- Other: documentation and other user-visible changes.
- Internal: tests, tooling, and refactors that users do not see.

`set`, `bump`, and `promote` roll the notes over. The command moves the Master entries into a new `## <version>`
section directly under Master. It drops empty subsections from that section and resets Master to the empty template.
The rollover writes with the version files, so malformed notes fail the command before it changes anything. The
notes are malformed when Master is missing, has an unknown subsection heading, or the version already has a section.

To print the notes of one version, or of Master when you omit the version:

```shell
python3 eng/version.py notes 0.2.0-alpha.1
python3 eng/version.py notes
```

## Release

Commit the version change. Then tag it and push the tag:

```shell
python3 eng/version.py verify
python3 eng/version.py tag
git push origin v0.2.0-alpha.1
```

`verify` checks that the assembly and file versions match and `docs/install.md` quotes the same version. It also checks
that `RELEASE-NOTES.md` has a section for the version, the tree is clean, and `HEAD` contains the version. Entries
still under Master do not fail `verify`. `tag` runs `verify` and creates an annotated `vX.Y.Z[-prerelease]` tag
locally. Neither command pushes.

A pushed `v*` tag runs `.github/workflows/publish.yml`. The workflow checks the tag against `Version.props`. It builds,
tests, and packs, then confirms that both packages carry the tagged version. A separate job in the `nuget-release`
environment pushes them to nuget.org.
