#!/usr/bin/env python3
"""Prove deletion tests fail when critical protections are deliberately removed.

Only isolated temporary source copies are changed. A compile failure does NOT
count as a caught safety defect. No server, settings or library is contacted.
"""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


def main():
    root = Path(__file__).resolve().parents[2]
    relative = Path("Emby.Xtream.Plugin/Service/OwnedContentFiles.cs")
    original = (root / relative).read_text()
    mutations = {
        "commit ownership check": ("if (!IsOwnedStrm(path, root, config)) return false;", "if (false) return false;"),
        "root containment": ("if (!full.StartsWith(boundary, comparison)) return false;", "if (false) return false;"),
        "symbolic links": ("(File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0", "false"),
        "exact provider host": ("!string.Equals(source.Host, provider.Host, StringComparison.OrdinalIgnoreCase)", "false"),
        "account username": ('Uri.UnescapeDataString(parts[1]) != (config.Username ?? string.Empty)', "false"),
        "account password": ('Uri.UnescapeDataString(parts[2]) != (config.Password ?? string.Empty)', "false"),
        "STRM extension": ('!string.Equals(Path.GetExtension(path), ".strm", StringComparison.OrdinalIgnoreCase)', "false"),
    }
    files = [root / "Directory.Build.props"]
    for folder in ("Emby.Xtream.Plugin", "Emby.Xtream.Plugin.Tests"):
        files.extend((root / folder).rglob("*"))
    with tempfile.TemporaryDirectory(prefix="xc2emby-delete-mutations-") as temp:
        work = Path(temp)
        for source in files:
            name = source.relative_to(root)
            if any(part in {"bin", "obj", "artifacts", ".git"} for part in name.parts):
                continue
            if source.is_file() and not source.is_symlink():
                destination = work / name
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source, destination)
        project = "Emby.Xtream.Plugin.Tests/Emby.Xtream.Plugin.Tests.csproj"
        cache = os.environ.get("NUGET_PACKAGES", str(Path.home() / ".nuget/packages"))
        subprocess.run(["dotnet", "restore", project, "--source", cache, "-p:NuGetAudit=false"], cwd=work, check=True)
        command = ["dotnet", "run", "--project", project, "-c", "Release", "--no-restore", "--", "--safety-only"]
        subprocess.run(command, cwd=work, check=True)
        for label, (before, after) in mutations.items():
            if original.count(before) != 1:
                raise RuntimeError(f"Mutation site moved or became ambiguous: {label}")
            (work / relative).write_text(original.replace(before, after))
            result = subprocess.run(command, cwd=work, text=True, capture_output=True)
            output = result.stdout + result.stderr
            if result.returncode == 0 or "FAIL safety:" not in output:
                raise RuntimeError(f"Safety mutation was not caught by a running test: {label}\n{output}")
            print(f"PASS safety tests caught removed protection: {label}", flush=True)
        print(f"All {len(mutations)} deletion mutations caught.")


if __name__ == "__main__":
    main()
