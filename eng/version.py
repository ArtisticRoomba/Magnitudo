#!/usr/bin/env python3
"""Read, change, and verify the Magnitudo version in Version.props, and create release tags."""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

VERSION_FILE = Path("Version.props")

PRERELEASE_IDENTIFIER = r"(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)"
BUILD_IDENTIFIER = r"[0-9A-Za-z-]+"
SEMVER_PATTERN = re.compile(
    rf"^(?P<major>0|[1-9][0-9]*)\."
    rf"(?P<minor>0|[1-9][0-9]*)\."
    rf"(?P<patch>0|[1-9][0-9]*)"
    rf"(?:-(?P<prerelease>{PRERELEASE_IDENTIFIER}(?:\.{PRERELEASE_IDENTIFIER})*))?"
    rf"(?:\+(?P<build>{BUILD_IDENTIFIER}(?:\.{BUILD_IDENTIFIER})*))?$"
)
XML_VALUES = {
    "Version": re.compile(r"(<Version>)(?P<value>[^<]*)(</Version>)"),
    "AssemblyVersion": re.compile(r"(<AssemblyVersion>)(?P<value>[^<]*)(</AssemblyVersion>)"),
    "FileVersion": re.compile(r"(<FileVersion>)(?P<value>[^<]*)(</FileVersion>)"),
}
MAX_ASSEMBLY_COMPONENT = 65534


class VersionError(RuntimeError):
    """Raised for a failure the command line reports as an error message."""


@dataclass(frozen=True)
class SemanticVersion:
    """A SemVer 2.0.0 version with the .NET assembly-version limit applied to each numeric component."""

    major: int
    minor: int
    patch: int
    prerelease: str | None = None
    build: str | None = None

    @classmethod
    def parse(cls, value: str) -> "SemanticVersion":
        """Parse `value`. Raise VersionError when it is not SemVer or a component exceeds 65534."""
        match = SEMVER_PATTERN.fullmatch(value)
        if match is None:
            raise VersionError(f"'{value}' is not a valid SemVer 2.0.0 version")

        version = cls(
            int(match["major"]),
            int(match["minor"]),
            int(match["patch"]),
            match["prerelease"],
            match["build"],
        )
        for name, component in (
            ("major", version.major),
            ("minor", version.minor),
            ("patch", version.patch),
        ):
            if component > MAX_ASSEMBLY_COMPONENT:
                raise VersionError(
                    f"{name} component {component} exceeds the .NET assembly-version limit "
                    f"of {MAX_ASSEMBLY_COMPONENT}"
                )
        return version

    def bump(self, kind: str, prerelease: str | None = None) -> "SemanticVersion":
        """Return the next version.

        A major, minor, or patch bump resets lower components and sets the prerelease to `prerelease`.
        A prerelease bump increments a trailing number, or appends `.1`, and rejects `prerelease`.
        """
        if prerelease is not None:
            SemanticVersion.parse(f"0.0.0-{prerelease}")
        if kind == "prerelease":
            if prerelease is not None:
                raise VersionError("prerelease bumps do not accept a replacement prerelease value")
            if self.prerelease is None:
                raise VersionError("cannot bump prerelease for a stable version")
            identifiers = self.prerelease.split(".")
            if identifiers[-1].isdigit():
                identifiers[-1] = str(int(identifiers[-1]) + 1)
            else:
                identifiers.append("1")
            return SemanticVersion(self.major, self.minor, self.patch, ".".join(identifiers))
        if kind == "major":
            return SemanticVersion(self.major + 1, 0, 0, prerelease)
        if kind == "minor":
            return SemanticVersion(self.major, self.minor + 1, 0, prerelease)
        if kind == "patch":
            return SemanticVersion(self.major, self.minor, self.patch + 1, prerelease)
        raise VersionError("bump kind must be major, minor, patch, or prerelease")

    def promote(self) -> "SemanticVersion":
        """Return the version without prerelease and build suffixes."""
        return SemanticVersion(self.major, self.minor, self.patch)

    def __str__(self) -> str:
        value = f"{self.major}.{self.minor}.{self.patch}"
        if self.prerelease:
            value += f"-{self.prerelease}"
        if self.build:
            value += f"+{self.build}"
        return value


def repository_root(explicit_root: str | None = None) -> Path:
    """Return `explicit_root`, or the parent of this script's folder, if it contains Version.props."""
    root = Path(explicit_root).resolve() if explicit_root else Path(__file__).resolve().parent.parent
    if (root / VERSION_FILE).is_file():
        return root
    raise VersionError(f"'{root}' is not a Magnitudo repository containing {VERSION_FILE}")


def read_xml_value(text: str, name: str, path: Path) -> str:
    """Return the text of the single `<name>` element. Raise VersionError unless exactly one exists."""
    matches = list(XML_VALUES[name].finditer(text))
    if len(matches) != 1:
        raise VersionError(f"expected exactly one <{name}> element in '{path}'")
    return matches[0]["value"].strip()


def read_version(path: Path) -> SemanticVersion:
    """Read the `<Version>` element of the props file at `path`."""
    return SemanticVersion.parse(read_xml_value(path.read_text(encoding="utf-8-sig"), "Version", path))


def replace_xml_value(text: str, name: str, value: str, path: Path) -> str:
    """Return `text` with the content of the single `<name>` element replaced by `value`."""
    matches = list(XML_VALUES[name].finditer(text))
    if len(matches) != 1:
        raise VersionError(f"expected exactly one <{name}> element in '{path}'")
    match = matches[0]
    return text[: match.start("value")] + value + text[match.end("value") :]


def write_version(path: Path, version: SemanticVersion) -> None:
    """Write `version`, and the assembly and file versions derived from it, to the props file.

    The assembly version is `major.0.0.0`. The file version is `major.minor.patch.0`. The write keeps the
    existing byte order mark and line endings.
    """
    raw = path.read_bytes()
    has_bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig")
    text = replace_xml_value(text, "Version", str(version), path)
    text = replace_xml_value(text, "AssemblyVersion", f"{version.major}.0.0.0", path)
    text = replace_xml_value(
        text,
        "FileVersion",
        f"{version.major}.{version.minor}.{version.patch}.0",
        path,
    )
    encoding = "utf-8-sig" if has_bom else "utf-8"
    path.write_text(text, encoding=encoding, newline="")


def run_git(root: Path, *arguments: str, check: bool = True) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["git", *arguments],
        cwd=root,
        check=check,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )


def verify(root: Path) -> SemanticVersion:
    """Check that the version files agree, the working tree is clean, and HEAD has the same version."""
    path = root / VERSION_FILE
    version = read_version(path)
    text = path.read_text(encoding="utf-8-sig")
    expected_assembly = f"{version.major}.0.0.0"
    expected_file = f"{version.major}.{version.minor}.{version.patch}.0"
    if read_xml_value(text, "AssemblyVersion", path) != expected_assembly:
        raise VersionError(f"{path}: AssemblyVersion must be {expected_assembly}")
    if read_xml_value(text, "FileVersion", path) != expected_file:
        raise VersionError(f"{path}: FileVersion must be {expected_file}")

    status = run_git(root, "status", "--porcelain").stdout
    if status:
        raise VersionError("release verification requires a clean Git working tree and index")

    committed = run_git(root, "show", f"HEAD:{VERSION_FILE.as_posix()}").stdout
    if read_xml_value(committed, "Version", path) != str(version):
        raise VersionError(f"version {version} is not committed at HEAD")
    return version


def create_tag(root: Path) -> str:
    """Verify the repository, then create the local annotated tag `vX.Y.Z`. Do not push it."""
    version = verify(root)
    tag = f"v{version}"
    exists = run_git(root, "rev-parse", "--verify", "--quiet", f"refs/tags/{tag}", check=False)
    if exists.returncode == 0:
        raise VersionError(f"tag '{tag}' already exists")
    run_git(root, "tag", "--annotate", tag, "--message", f"Magnitudo {version}")
    return tag


def build_parser() -> argparse.ArgumentParser:
    """Build the command-line parser."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", help="Magnitudo repository root (defaults to the script's parent)")
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("get", help="print the current version")
    commands.add_parser("promote", help="drop the prerelease suffix")
    commands.add_parser("verify", help="check version files and that HEAD is clean and committed")
    commands.add_parser("tag", help="create the annotated vX.Y.Z tag locally")

    set_parser = commands.add_parser("set", help="set an exact version")
    set_parser.add_argument("version")

    bump_parser = commands.add_parser("bump", help="advance the version")
    bump_parser.add_argument("kind", choices=("major", "minor", "patch", "prerelease"))
    bump_parser.add_argument("--prerelease")
    return parser


def main(arguments: list[str] | None = None) -> int:
    """Run a command. Return 0 on success, or 2 after printing an error to stderr."""
    args = build_parser().parse_args(arguments)
    try:
        root = repository_root(args.repo)
        path = root / VERSION_FILE
        if args.command == "get":
            print(read_version(path))
        elif args.command == "set":
            version = SemanticVersion.parse(args.version)
            write_version(path, version)
            print(version)
        elif args.command == "bump":
            version = read_version(path).bump(args.kind, args.prerelease)
            write_version(path, version)
            print(version)
        elif args.command == "promote":
            version = read_version(path).promote()
            write_version(path, version)
            print(version)
        elif args.command == "verify":
            print(f"{verify(root)} verified")
        elif args.command == "tag":
            print(create_tag(root))
        return 0
    except (OSError, subprocess.SubprocessError, VersionError) as exception:
        print(f"error: {exception}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
