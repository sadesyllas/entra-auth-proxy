"""Validate maintained configuration/docs, removal inventory, and real CLI starter generation.

Run from any directory after `dotnet build EntraAuthProxy.sln`:
    python3 tests/verify_repository.py
"""
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1]
sample = json.loads((root / "sample-entraauthproxy.json").read_text())
examples = re.findall(r"```json\n(.*?)\n```", (root / "README.md").read_text(), re.S)
assert len(examples) == 2, "Expected setup and routing examples"
for example in examples:
    json.loads(example if example.lstrip().startswith("{") else "{" + example + "}")

with tempfile.TemporaryDirectory(prefix="entra-starter-") as directory:
    work = Path(directory)
    config = work / "config"
    env = dict(os.environ, ENTRAAUTHPROXY_CONFIG_DIR=str(config))
    result = subprocess.run(
        ["dotnet", str(root / "artifacts/bin/EntraAuthProxy/debug/entraauthproxy.dll")],
        cwd=work, env=env, capture_output=True, text=True, timeout=20,
    )
    assert result.returncode == 0, result.stderr
    assert "Created default configuration" in result.stdout, result.stdout
    assert "Successfully authenticated" not in result.stdout, result.stdout
    assert json.loads((config / "entraauthproxy.json").read_text()) == sample

# Include hidden tracked files and nonignored new files. Only these
# dedicated negative checks may contain the old identifiers.
pattern = re.compile(r"bifrost|x-bf|virtual[\s_-]?key", re.I)
allowed = {"tests/ProxyBehaviorChecks/Program.cs", "tests/verify_repository.py"}
files = subprocess.check_output(
    ["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"], cwd=root
).decode().split("\0")
matched = set()
for name in set(files) - {""}:
    path = root / name
    if path.is_file() and pattern.search(path.read_text()):
        assert name in allowed, f"Unexpected removal reference: {name}"
        matched.add(name)
print("PASS: sample, 2 README JSON examples, isolated starter generation/exit, maintained-file audit.")
print("Allowed negative-test matches: " + ", ".join(sorted(matched)))
