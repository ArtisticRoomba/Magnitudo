# Versioning and releasing Quantitas

Quantitas uses Semantic Versioning 2.0.0. `Quantitas` and `Quantitas.Analyzers` always ship with the same version, stored in
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

## NuGet trusted publishing

Publishing uses GitHub OIDC, so there is no API key secret. One-time setup:

1. On nuget.org, open **Trusted Publishing** under your account menu and add a policy with
   - Repository owner: `ArtisticRoomba`
   - Repository: `Quantitas`
   - Workflow file: `publish.yml`
   - Environment: `nuget-release`
2. In the GitHub repo, create an environment named `nuget-release` (add required reviewers if you want a manual gate).
3. Add a repository variable `NUGET_USER` set to your nuget.org profile name (not your email).

A brand-new package ID has to be created under an account before a policy can cover it. Either do the first push with
a one-off API key scoped to `Quantitas*`, or create the policy as "pending" so it reserves the IDs.
