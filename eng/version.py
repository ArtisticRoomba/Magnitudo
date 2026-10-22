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
# Docs that quote the package version. Each file must contain a PackageReference to a Magnitudo package.
SYNCED_DOCS = (Path("docs/install.md"),)
PACKAGE_NAMES = ("Magnitudo", "Magnitudo.Analyzers")
RELEASE_NOTES_FILE = Path("RELEASE-NOTES.md")
MASTER_HEADING = "## Master"
# The order matters: Master and every released section list their subsections in this order.
NOTE_SUBSECTIONS = ("Breaking changes", "New features", "Bugfixes", "Other", "Internal")
NO_ENTRIES_YET = "*None yet*"
NO_ENTRIES = "*None*"

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
PACKAGE_REFERENCE = re.compile(r"<PackageReference\b[^>\r\n]*>")
PACKAGE_INCLUDE = re.compile(r'\bInclude="(?P<name>[^"]*)"')
PACKAGE_VERSION = re.compile(r'(\bVersion=")(?P<value>[^"]*)(")')
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


def package_reference_versions(text: str) -> list[tuple[str, str]]:
    """Return the (package, version) pair of each Magnitudo PackageReference in `text`."""
    found: list[tuple[str, str]] = []
    for tag in PACKAGE_REFERENCE.finditer(text):
        include = PACKAGE_INCLUDE.search(tag[0])
        version = PACKAGE_VERSION.search(tag[0])
        if include and version and include["name"] in PACKAGE_NAMES:
            found.append((include["name"], version["value"]))
    return found


def missing_reference_error(path: Path) -> VersionError:
    return VersionError(f"'{path}' has no PackageReference with a version for {' or '.join(PACKAGE_NAMES)}")


def sync_package_references(text: str, version: SemanticVersion, path: Path) -> str:
    """Return `text` with the version of each Magnitudo PackageReference set to `version`.

    Raise VersionError when `text` has no such reference. All other bytes stay as they are.
    """
    count = 0

    def replace_tag(tag: re.Match[str]) -> str:
        nonlocal count
        include = PACKAGE_INCLUDE.search(tag[0])
        if include is None or include["name"] not in PACKAGE_NAMES or PACKAGE_VERSION.search(tag[0]) is None:
            return tag[0]
        count += 1
        return PACKAGE_VERSION.sub(lambda m: m[1] + str(version) + m[3], tag[0], count=1)

    updated = PACKAGE_REFERENCE.sub(replace_tag, text)
    if count == 0:
        raise missing_reference_error(path)
    return updated


def read_doc(root: Path, doc: Path) -> str:
    """Return the text of `doc` with its line endings unchanged. Raise VersionError when it is missing."""
    path = root / doc
    if not path.is_file():
        raise VersionError(f"synced doc '{path}' does not exist")
    return path.read_bytes().decode("utf-8")


def split_lines(text: str) -> list[str]:
    """Split `text` at `\\n` only, keeping each line ending, so that joining the lines returns `text`."""
    return re.findall(r"[^\n]*\n|[^\n]+", text)


def read_release_notes(root: Path) -> str:
    """Return the text of the release notes with its line endings unchanged. Raise VersionError when missing."""
    path = root / RELEASE_NOTES_FILE
    if not path.is_file():
        raise VersionError(f"release notes '{path}' do not exist")
    return path.read_bytes().decode("utf-8")


def find_section(lines: list[str], heading: str, path: Path) -> tuple[int, int]:
    """Return the line range of the `## ` section `heading`, from its heading line to the next `## ` heading.

    Raise VersionError unless the heading appears exactly once.
    """
    starts = [index for index, line in enumerate(lines) if line.rstrip() == heading]
    if len(starts) != 1:
        raise VersionError(f"expected exactly one '{heading}' section in '{path}'")
    end = next((index for index in range(starts[0] + 1, len(lines)) if lines[index].startswith("## ")), len(lines))
    return starts[0], end


def trim_blank_lines(lines: list[str]) -> list[str]:
    """Return `lines` without leading and trailing blank lines."""
    start, end = 0, len(lines)
    while start < end and not lines[start].strip():
        start += 1
    while end > start and not lines[end - 1].strip():
        end -= 1
    return lines[start:end]


def parse_master(body: list[str], path: Path) -> dict[str, list[str]]:
    """Return the entry lines of each subsection in the body of the Master section.

    A subsection that holds only `*None yet*` has no entries. Raise VersionError for an unknown or repeated
    subsection heading, or for text outside a subsection.
    """
    entries: dict[str, list[str]] = {}
    current: list[str] | None = None
    for line in body:
        if line.startswith("### "):
            name = line.rstrip()[4:].strip()
            if name not in NOTE_SUBSECTIONS:
                raise VersionError(f"unknown subsection '{name}' under Master in '{path}'")
            if name in entries:
                raise VersionError(f"duplicate subsection '{name}' under Master in '{path}'")
            current = entries[name] = []
        elif current is not None:
            current.append(line.rstrip("\r\n"))
        elif line.strip():
            raise VersionError(f"text before the first subsection under Master in '{path}'")
    for name, lines in entries.items():
        lines[:] = trim_blank_lines(lines)
        if lines == [NO_ENTRIES_YET]:
            lines.clear()
    return entries


def roll_release_notes(text: str, version: SemanticVersion, path: Path) -> str:
    """Return `text` with the Master entries moved into a new `## version` section under Master.

    Master is reset to an empty template. Empty subsections are dropped from the new section, and a release
    with no entries says `*None*`. The text outside Master keeps its bytes, and new lines use the file's line ending.
    Raise VersionError when Master is missing or malformed, or when the section for `version` exists.
    """
    lines = split_lines(text)
    start, end = find_section(lines, MASTER_HEADING, path)
    if any(line.rstrip() == f"## {version}" for line in lines):
        raise VersionError(f"'{path}' already has a section for {version}")
    entries = parse_master(lines[start + 1 : end], path)

    eol = "\r\n" if "\r\n" in text else "\n"
    released: list[str] = [f"## {version}", ""]
    populated = [name for name in NOTE_SUBSECTIONS if entries.get(name)]
    for name in populated:
        released += [f"### {name}", "", *entries[name], ""]
    if not populated:
        released += [NO_ENTRIES, ""]
    master = [MASTER_HEADING, ""]
    for name in NOTE_SUBSECTIONS:
        master += [f"### {name}", "", NO_ENTRIES_YET, ""]

    block = master + released
    if end == len(lines):
        block.pop()
    return "".join(lines[:start]) + "".join(line + eol for line in block) + "".join(lines[end:])


def release_notes_section(text: str, heading: str, path: Path) -> str:
    """Return the content below the `## ` section `heading`, without surrounding blank lines."""
    lines = split_lines(text)
    start, end = find_section(lines, heading, path)
    return "\n".join(line.rstrip("\r\n") for line in trim_blank_lines(lines[start + 1 : end]))


def verify_release_notes(root: Path, version: SemanticVersion) -> None:
    """Raise VersionError when the release notes have no section for `version`."""
    path = root / RELEASE_NOTES_FILE
    release_notes_section(read_release_notes(root), f"## {version}", path)


def write_versions(root: Path, version: SemanticVersion) -> None:
    """Write `version` to Version.props, to each synced doc, and to the release notes.

    The docs and release notes are computed first, so a missing doc, a missing reference, or malformed release
    notes leave every file unchanged.
    """
    updated = {doc: sync_package_references(read_doc(root, doc), version, root / doc) for doc in SYNCED_DOCS}
    notes = roll_release_notes(read_release_notes(root), version, root / RELEASE_NOTES_FILE)
    write_version(root / VERSION_FILE, version)
    for doc, text in updated.items():
        (root / doc).write_bytes(text.encode("utf-8"))
    (root / RELEASE_NOTES_FILE).write_bytes(notes.encode("utf-8"))


def verify_docs(root: Path, version: SemanticVersion) -> None:
    """Raise VersionError when a synced doc quotes a version other than `version`."""
    for doc in SYNCED_DOCS:
        path = root / doc
        references = package_reference_versions(read_doc(root, doc))
        if not references:
            raise missing_reference_error(path)
        for name, quoted in references:
            if quoted != str(version):
                raise VersionError(f"{path}: {name} is {quoted} but {VERSION_FILE} is {version}")


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
    """Check that the version files and synced docs agree.

    Also check that the release notes have a section for the version. Then check that the working tree is clean
    and HEAD has the same version.
    """
    path = root / VERSION_FILE
    version = read_version(path)
    text = path.read_text(encoding="utf-8-sig")
    expected_assembly = f"{version.major}.0.0.0"
    expected_file = f"{version.major}.{version.minor}.{version.patch}.0"
    if read_xml_value(text, "AssemblyVersion", path) != expected_assembly:
        raise VersionError(f"{path}: AssemblyVersion must be {expected_assembly}")
    if read_xml_value(text, "FileVersion", path) != expected_file:
        raise VersionError(f"{path}: FileVersion must be {expected_file}")
    verify_docs(root, version)
    verify_release_notes(root, version)

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
    commands.add_parser("verify", help="check version files, synced docs, and that HEAD is clean and committed")
    notes_parser = commands.add_parser("notes", help="print the release notes of a version, or Master")
    notes_parser.add_argument("version", nargs="?")
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
            write_versions(root, version)
            print(version)
        elif args.command == "bump":
            version = read_version(path).bump(args.kind, args.prerelease)
            write_versions(root, version)
            print(version)
        elif args.command == "promote":
            version = read_version(path).promote()
            write_versions(root, version)
            print(version)
        elif args.command == "notes":
            heading = f"## {SemanticVersion.parse(args.version)}" if args.version else MASTER_HEADING
            print(release_notes_section(read_release_notes(root), heading, root / RELEASE_NOTES_FILE))
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
