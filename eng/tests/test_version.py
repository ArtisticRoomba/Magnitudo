import contextlib
import importlib.util
import io
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "version.py"
SPEC = importlib.util.spec_from_file_location("magnitudo_version", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
version_tool = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = version_tool
SPEC.loader.exec_module(version_tool)


PROPS = """<Project>\n    <PropertyGroup>\n        <Version>{0}</Version>\n        <AssemblyVersion>0.0.0.0</AssemblyVersion>\n        <FileVersion>0.1.0.0</FileVersion>\n    </PropertyGroup>\n</Project>\n"""
INSTALL_DOC = (
    "# Install\r\n\r\n```xml\r\n"
    '<PackageReference Include="Magnitudo" Version="{0}"/>\r\n'
    '<PackageReference Include="Magnitudo.Analyzers" Version="{0}" PrivateAssets="all"/>\r\n'
    '<PackageReference Include="Other.Package" Version="{0}"/>\r\n'
    "```\r\n\r\nVersion=\"{0}\" outside a reference stays.\r\n"
)


def notes_template(entries: dict[str, str] | None = None) -> str:
    """Return a release notes file whose Master holds `entries`, followed by a 0.1.0-alpha.1 section."""
    entries = entries or {}
    master = "".join(
        f"### {name}\n\n{entries.get(name, '*None yet*')}\n\n" for name in version_tool.NOTE_SUBSECTIONS
    )
    return (
        "<!-- intro -->\n\n## Master\n\n" + master + "## 0.1.0-alpha.1\n\n### Other\n\n- First release.\n"
    )


def write_release_notes(root: Path, contents: str | None = None) -> None:
    (root / version_tool.RELEASE_NOTES_FILE).write_bytes((contents or notes_template()).encode("utf-8"))


def write_install_doc(root: Path, version: str, contents: str = INSTALL_DOC) -> None:
    path = root / version_tool.SYNCED_DOCS[0]
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(contents.format(version).encode("utf-8"))


class SemanticVersionTests(unittest.TestCase):
    def test_accepts_semver_2_versions(self) -> None:
        values = (
            "0.1.0",
            "1.2.3-alpha",
            "1.2.3-alpha.1",
            "1.2.3-0.3.7",
            "1.2.3+build.007",
            "1.2.3-rc.1+sha.abcdef",
        )
        for value in values:
            with self.subTest(value=value):
                self.assertEqual(value, str(version_tool.SemanticVersion.parse(value)))

    def test_rejects_invalid_semver(self) -> None:
        values = (
            "1",
            "1.2",
            "01.2.3",
            "1.02.3",
            "1.2.03",
            "1.2.3-alpha.01",
            "1.2.3-",
            "1.2.3+",
            "1.2.3_alpha",
        )
        for value in values:
            with self.subTest(value=value):
                with self.assertRaises(version_tool.VersionError):
                    version_tool.SemanticVersion.parse(value)

    def test_bump_resets_lower_components_and_metadata(self) -> None:
        current = version_tool.SemanticVersion.parse("1.2.3-beta.2+build.9")
        self.assertEqual("2.0.0", str(current.bump("major")))
        self.assertEqual("1.3.0-rc.1", str(current.bump("minor", "rc.1")))
        self.assertEqual("1.2.4", str(current.bump("patch")))

    def test_bump_prerelease_keeps_numeric_version(self) -> None:
        current = version_tool.SemanticVersion.parse("1.2.3-alpha.1+build.9")
        self.assertEqual("1.2.3-alpha.2", str(current.bump("prerelease")))
        self.assertEqual("1.2.3-alpha.1", str(version_tool.SemanticVersion.parse("1.2.3-alpha").bump("prerelease")))

    def test_bump_prerelease_rejects_stable_versions_and_replacement_values(self) -> None:
        with self.assertRaisesRegex(version_tool.VersionError, "stable version"):
            version_tool.SemanticVersion.parse("1.2.3").bump("prerelease")
        with self.assertRaisesRegex(version_tool.VersionError, "replacement prerelease"):
            version_tool.SemanticVersion.parse("1.2.3-alpha.1").bump("prerelease", "beta.1")

    def test_promote_removes_prerelease_and_build_metadata(self) -> None:
        current = version_tool.SemanticVersion.parse("1.2.3-rc.1+build.9")
        self.assertEqual("1.2.3", str(current.promote()))


class VersionFileTests(unittest.TestCase):
    def test_write_updates_dotnet_versions_and_preserves_layout(self) -> None:
        contents = """<Project>\n    <PropertyGroup>\n        <Version>0.1.0-alpha.1</Version>\n        <PackageVersion>$(Version)</PackageVersion>\n        <AssemblyVersion>0.0.0.0</AssemblyVersion>\n        <FileVersion>0.1.0.0</FileVersion>\n    </PropertyGroup>\n</Project>\n"""
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "Version.props"
            path.write_text(contents, encoding="utf-8")
            requested = version_tool.SemanticVersion.parse("2.4.6-rc.1+sha.123")
            version_tool.write_version(path, requested)
            updated = path.read_text(encoding="utf-8")

        self.assertIn("<Version>2.4.6-rc.1+sha.123</Version>", updated)
        self.assertIn("<PackageVersion>$(Version)</PackageVersion>", updated)
        self.assertIn("<AssemblyVersion>2.0.0.0</AssemblyVersion>", updated)
        self.assertIn("<FileVersion>2.4.6.0</FileVersion>", updated)
        self.assertEqual(contents.count("\n"), updated.count("\n"))

    def test_main_bumps_prerelease_without_changing_numeric_version(self) -> None:
        contents = """<Project>\n    <PropertyGroup>\n        <Version>0.1.0-alpha.1</Version>\n        <AssemblyVersion>0.0.0.0</AssemblyVersion>\n        <FileVersion>0.1.0.0</FileVersion>\n    </PropertyGroup>\n</Project>\n"""
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / version_tool.VERSION_FILE).write_text(contents, encoding="utf-8")
            write_install_doc(root, "0.1.0-alpha.1")
            write_release_notes(root)

            self.assertEqual(0, version_tool.main(["--repo", str(root), "bump", "prerelease"]))
            updated = (root / version_tool.VERSION_FILE).read_text(encoding="utf-8")

        self.assertIn("<Version>0.1.0-alpha.2</Version>", updated)
        self.assertIn("<FileVersion>0.1.0.0</FileVersion>", updated)


class InstallDocTests(unittest.TestCase):
    def create_root(self, directory: str, props_version: str = "0.1.0-alpha.1", doc_version: str = "0.1.0-alpha.1") -> Path:
        root = Path(directory)
        (root / version_tool.VERSION_FILE).write_text(PROPS.format(props_version), encoding="utf-8")
        write_install_doc(root, doc_version)
        write_release_notes(root)
        return root

    def read_doc(self, root: Path) -> bytes:
        return (root / version_tool.SYNCED_DOCS[0]).read_bytes()

    def expected_doc(self, version: str) -> bytes:
        expected = INSTALL_DOC.format("OLD")
        expected = expected.replace(
            'Include="Magnitudo" Version="OLD"', f'Include="Magnitudo" Version="{version}"'
        ).replace('Include="Magnitudo.Analyzers" Version="OLD"', f'Include="Magnitudo.Analyzers" Version="{version}"')
        return expected.encode("utf-8")

    def test_set_updates_both_references_and_leaves_the_rest_unchanged(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = self.create_root(directory)
            self.assertEqual(0, version_tool.main(["--repo", str(root), "set", "0.3.0-beta.1"]))
            updated = self.read_doc(root)

        self.assertEqual(self.expected_doc("0.3.0-beta.1").replace(b"OLD", b"0.1.0-alpha.1"), updated)

    def test_bump_updates_install_doc(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = self.create_root(directory)
            self.assertEqual(0, version_tool.main(["--repo", str(root), "bump", "minor"]))
            updated = self.read_doc(root)

        self.assertEqual(self.expected_doc("0.2.0").replace(b"OLD", b"0.1.0-alpha.1"), updated)

    def test_promote_updates_install_doc(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = self.create_root(directory)
            self.assertEqual(0, version_tool.main(["--repo", str(root), "promote"]))
            updated = self.read_doc(root)

        self.assertEqual(self.expected_doc("0.1.0").replace(b"OLD", b"0.1.0-alpha.1"), updated)

    def test_missing_install_doc_fails_before_writing_version_props(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = self.create_root(directory)
            (root / version_tool.SYNCED_DOCS[0]).unlink()
            self.assertEqual(2, version_tool.main(["--repo", str(root), "set", "0.2.0"]))
            props = (root / version_tool.VERSION_FILE).read_text(encoding="utf-8")

        self.assertEqual(PROPS.format("0.1.0-alpha.1"), props)

    def test_install_doc_without_reference_fails_before_writing_version_props(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = self.create_root(directory)
            write_install_doc(root, "0.1.0-alpha.1", "# Install\n\n<PackageReference Include=\"Other\" Version=\"{0}\"/>\n")
            with self.assertRaisesRegex(version_tool.VersionError, "no PackageReference"):
                version_tool.write_versions(root, version_tool.SemanticVersion.parse("0.2.0"))
            props = (root / version_tool.VERSION_FILE).read_text(encoding="utf-8")

        self.assertEqual(PROPS.format("0.1.0-alpha.1"), props)

    def test_verify_docs_accepts_matching_versions(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = self.create_root(directory)
            version_tool.verify_docs(root, version_tool.SemanticVersion.parse("0.1.0-alpha.1"))

    def test_verify_rejects_install_doc_mismatch(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = self.create_root(directory, doc_version="0.0.9")
            with self.assertRaisesRegex(version_tool.VersionError, r"install\.md.*0\.0\.9.*0\.1\.0-alpha\.1"):
                version_tool.verify(root)


class ReleaseNotesTests(unittest.TestCase):
    VERSION = version_tool.SemanticVersion.parse("0.2.0")
    PATH = Path("RELEASE-NOTES.md")

    def roll(self, text: str) -> str:
        return version_tool.roll_release_notes(text, self.VERSION, self.PATH)

    def test_rollover_moves_entries_drops_empty_subsections_and_resets_master(self) -> None:
        text = notes_template({"New features": "- Added a thing.\n- Added another.", "Internal": "- Tests."})
        expected = (
            "<!-- intro -->\n\n## Master\n\n"
            + "".join(f"### {name}\n\n*None yet*\n\n" for name in version_tool.NOTE_SUBSECTIONS)
            + "## 0.2.0\n\n### New features\n\n- Added a thing.\n- Added another.\n\n### Internal\n\n- Tests.\n\n"
            + "## 0.1.0-alpha.1\n\n### Other\n\n- First release.\n"
        )
        self.assertEqual(expected, self.roll(text))

    def test_rollover_of_empty_master_says_none(self) -> None:
        self.assertIn("## 0.2.0\n\n*None*\n\n## 0.1.0-alpha.1", self.roll(notes_template()))

    def test_rollover_keeps_text_outside_master_byte_for_byte(self) -> None:
        text = notes_template({"Bugfixes": "- Fixed."})
        before = "<!-- intro -->\n\n"
        after = "## 0.1.0-alpha.1\n\n### Other\n\n- First release.\n"
        rolled = self.roll(text)
        self.assertTrue(rolled.startswith(before))
        self.assertTrue(rolled.endswith(after))

    def test_rollover_preserves_crlf(self) -> None:
        text = notes_template({"Bugfixes": "- Fixed."}).replace("\n", "\r\n")
        rolled = self.roll(text)
        self.assertNotIn("\n", rolled.replace("\r\n", ""))
        self.assertIn("## 0.2.0\r\n\r\n### Bugfixes\r\n\r\n- Fixed.\r\n\r\n## 0.1.0-alpha.1", rolled)

    def test_rollover_of_final_master_section_adds_no_trailing_blank_line(self) -> None:
        text = "## Master\n\n### Other\n\n- Note.\n"
        self.assertTrue(self.roll(text).endswith("## 0.2.0\n\n### Other\n\n- Note.\n"))

    def test_rollover_accepts_a_partial_master_template(self) -> None:
        rolled = self.roll("## Master\n\n### Bugfixes\n\n- Fixed.\n\n## 0.1.0\n\nold\n")
        self.assertIn("## 0.2.0\n\n### Bugfixes\n\n- Fixed.\n\n## 0.1.0\n\nold\n", rolled)

    def test_rollover_rejects_malformed_notes(self) -> None:
        cases = {
            "missing Master": ("## 0.1.0\n\nold\n", "'## Master' section"),
            "duplicate Master": ("## Master\n\n## Master\n", "'## Master' section"),
            "unknown subsection": ("## Master\n\n### Bugfix\n\n- x\n", "unknown subsection 'Bugfix'"),
            "duplicate subsection": ("## Master\n\n### Other\n\n### Other\n", "duplicate subsection"),
            "stray text": ("## Master\n\nstray\n\n### Other\n", "before the first subsection"),
            "existing version": ("## Master\n\n## 0.2.0\n\nold\n", "already has a section for 0.2.0"),
        }
        for name, (text, message) in cases.items():
            with self.subTest(name):
                with self.assertRaisesRegex(version_tool.VersionError, message):
                    self.roll(text)

    def create_root(self, directory: str, notes: str | None = None) -> Path:
        root = Path(directory)
        (root / version_tool.VERSION_FILE).write_text(PROPS.format("0.1.0-alpha.1"), encoding="utf-8")
        write_install_doc(root, "0.1.0-alpha.1")
        write_release_notes(root, notes)
        return root

    def test_each_writing_command_rolls_the_notes_over(self) -> None:
        commands = (
            (["set", "0.3.0"], "0.3.0"),
            (["bump", "minor"], "0.2.0"),
            (["promote"], "0.1.0"),
        )
        for arguments, version in commands:
            with self.subTest(arguments), tempfile.TemporaryDirectory() as directory:
                root = self.create_root(directory, notes_template({"Bugfixes": "- Fixed."}))
                self.assertEqual(0, version_tool.main(["--repo", str(root), *arguments]))
                updated = (root / version_tool.RELEASE_NOTES_FILE).read_text(encoding="utf-8")
                self.assertIn(f"## {version}\n\n### Bugfixes\n\n- Fixed.\n", updated)

    def test_malformed_notes_fail_before_writing_any_file(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = self.create_root(directory, "## Master\n\n### Typo\n")
            install = (root / version_tool.SYNCED_DOCS[0]).read_bytes()
            self.assertEqual(2, version_tool.main(["--repo", str(root), "set", "0.2.0"]))
            props = (root / version_tool.VERSION_FILE).read_text(encoding="utf-8")
            self.assertEqual(install, (root / version_tool.SYNCED_DOCS[0]).read_bytes())

        self.assertEqual(PROPS.format("0.1.0-alpha.1"), props)

    def test_missing_notes_fail_before_writing_version_props(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = self.create_root(directory)
            (root / version_tool.RELEASE_NOTES_FILE).unlink()
            self.assertEqual(2, version_tool.main(["--repo", str(root), "set", "0.2.0"]))
            props = (root / version_tool.VERSION_FILE).read_text(encoding="utf-8")

        self.assertEqual(PROPS.format("0.1.0-alpha.1"), props)

    def test_notes_command_prints_a_version_or_master(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = self.create_root(directory, notes_template({"Bugfixes": "- Fixed."}))
            for arguments, expected in (
                (["notes", "0.1.0-alpha.1"], "### Other\n\n- First release.\n"),
                (["notes"], "### Breaking changes\n\n*None yet*\n\n### New features"),
            ):
                with self.subTest(arguments), contextlib.redirect_stdout(io.StringIO()) as output:
                    self.assertEqual(0, version_tool.main(["--repo", str(root), *arguments]))
                    self.assertTrue(output.getvalue().startswith(expected))
            self.assertEqual("### Other\n\n- First release.\n", self.print_notes(root, "0.1.0-alpha.1"))
            self.assertEqual(2, version_tool.main(["--repo", str(root), "notes", "9.9.9"]))

    def print_notes(self, root: Path, version: str) -> str:
        with contextlib.redirect_stdout(io.StringIO()) as output:
            version_tool.main(["--repo", str(root), "notes", version])
        return output.getvalue()

    def test_verify_requires_a_section_for_the_current_version(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = self.create_root(directory, notes_template({"Bugfixes": "- Pending."}))
            version_tool.verify_release_notes(root, version_tool.SemanticVersion.parse("0.1.0-alpha.1"))
            with self.assertRaisesRegex(version_tool.VersionError, "'## 0.1.0' section"):
                version_tool.verify_release_notes(root, version_tool.SemanticVersion.parse("0.1.0"))

    def test_verify_rejects_missing_notes_file(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = self.create_root(directory)
            (root / version_tool.RELEASE_NOTES_FILE).unlink()
            with self.assertRaisesRegex(version_tool.VersionError, "do not exist"):
                version_tool.verify(root)


class GitReleaseTests(unittest.TestCase):
    VERSION_FILE = """<Project>\n    <PropertyGroup>\n        <Version>0.1.0-alpha.1</Version>\n        <PackageVersion>$(Version)</PackageVersion>\n        <AssemblyVersion>0.0.0.0</AssemblyVersion>\n        <FileVersion>0.1.0.0</FileVersion>\n    </PropertyGroup>\n</Project>\n"""

    def create_repository(self, directory: str) -> Path:
        root = Path(directory)
        (root / version_tool.VERSION_FILE).write_text(self.VERSION_FILE, encoding="utf-8")
        write_install_doc(root, "0.1.0-alpha.1")
        write_release_notes(root)
        subprocess.run(["git", "init", "--initial-branch=master"], cwd=root, check=True, capture_output=True)
        subprocess.run(["git", "config", "user.name", "Magnitudo Tests"], cwd=root, check=True)
        subprocess.run(["git", "config", "user.email", "tests@example.invalid"], cwd=root, check=True)
        subprocess.run(["git", "add", "."], cwd=root, check=True)
        subprocess.run(["git", "commit", "--message", "Initial versions"], cwd=root, check=True, capture_output=True)
        return root

    def test_tag_creates_annotated_component_tag(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = self.create_repository(directory)
            tag = version_tool.create_tag(root)
            tag_type = version_tool.run_git(root, "cat-file", "-t", tag).stdout.strip()

        self.assertEqual("v0.1.0-alpha.1", tag)
        self.assertEqual("tag", tag_type)

    def test_verify_rejects_dirty_repository(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = self.create_repository(directory)
            (root / "uncommitted.txt").write_text("dirty", encoding="utf-8")
            with self.assertRaisesRegex(version_tool.VersionError, "clean Git working tree"):
                version_tool.verify(root)


if __name__ == "__main__":
    unittest.main()
