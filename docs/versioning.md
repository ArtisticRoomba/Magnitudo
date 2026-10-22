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

## Release

Commit the version change. Then tag it and push the tag:

```shell
python3 eng/version.py verify
python3 eng/version.py tag
git push origin v0.2.0-alpha.1
```

`verify` checks that the assembly and file versions match, `docs/install.md` quotes the same version, the tree is
clean, and `HEAD` contains the version. `tag` runs `verify` and creates an annotated `vX.Y.Z[-prerelease]` tag
locally. Neither command pushes.

A pushed `v*` tag runs `.github/workflows/publish.yml`. The workflow checks the tag against `Version.props`. It builds,
tests, and packs, then confirms that both packages carry the tagged version. A separate job in the `nuget-release`
environment pushes them to nuget.org.
