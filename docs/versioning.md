# Versioning and releasing Magnitudo

Magnitudo uses Semantic Versioning 2.0.0. `Magnitudo` and `Magnitudo.Analyzers` always ship with the same version, stored in
`Version.props`. Don't edit it by hand; `eng/version.py` keeps `Version`, `AssemblyVersion` and `FileVersion` in sync.

```shell
python3 eng/version.py get
python3 eng/version.py set 0.3.0-beta.1
python3 eng/version.py bump patch --prerelease rc.1
python3 eng/version.py bump prerelease     # 0.2.0-alpha.1 -> 0.2.0-alpha.2
python3 eng/version.py promote             # 0.2.0-rc.2 -> 0.2.0
```

`bump major|minor|patch` resets the lower components and drops any prerelease unless `--prerelease` is given.
`bump prerelease` increments the last numeric prerelease identifier and needs a prerelease version to start from.

## Releasing

Commit the version change, then tag and push the tag:

```shell
python3 eng/version.py verify
python3 eng/version.py tag
git push origin v0.2.0-alpha.1
```

`verify` checks that the assembly/file versions match, the tree is clean, and the version is committed at `HEAD`.
`tag` runs `verify` and creates an annotated `vX.Y.Z[-prerelease]` tag locally. Neither pushes anything.

Pushing a `v*` tag runs `.github/workflows/publish.yml`. It checks the tag against `Version.props`, builds, tests, packs,
and confirms both packages carry the tagged version. A separate job in the `nuget-release` environment then pushes them
to nuget.org.
