#!/usr/bin/env python3
"""Keep destructive library operations behind the ownership-checked helper."""
from pathlib import Path
import re


NONCODE = re.compile(r'@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'|//[^\n]*|/\*.*?\*/', re.S)


def violations(text, allowed_file_variables):
    code = NONCODE.sub(lambda match: "\n" * match.group().count("\n"), text)
    errors = []
    for match in re.finditer(r"\b(File|Directory)\s*\.\s*Delete\s*\(([^;]*)\)", code):
        kind, arguments = match.groups()
        if (kind == "Directory" and "," in arguments) or (kind == "File" and arguments.strip() not in allowed_file_variables):
            errors.append(code[:match.start()].count("\n") + 1)
    return errors


def main():
    assert violations("Directory.Delete(root, true);", set())
    assert violations("Directory.Delete(root, recursive);", set())
    assert violations("File.Delete(strmPath);", {"nfoPath"})
    assert not violations("// Directory.Delete(root, true);\nFile.Delete(nfoPath);", {"nfoPath"})
    root = Path(__file__).resolve().parents[2]
    files = {
        "Emby.Xtream.Plugin/Service/StrmSyncService.cs": {"nfoPath"},
        "Emby.Xtream.Plugin/Api/XtreamTunerApi.cs": {"testFile", "bakPath", "tempPath"},
    }
    for name, allowed in files.items():
        bad = violations((root / name).read_text(), allowed)
        if bad:
            raise RuntimeError(f"Unreviewed library delete call(s) in {name} at lines {bad}; use OwnedContentFiles")
    print("PASS delete-site guard and its rejection self-tests")


if __name__ == "__main__":
    main()
