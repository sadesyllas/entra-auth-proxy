"""Validate maintained configuration/docs, removal inventory, and real CLI starter generation.

Run from any directory after `dotnet build EntraAuthProxy.sln`:
    python3 tests/verify_repository.py
"""
import json
from pathlib import Path
import re
import subprocess
import tempfile
from verify_cli import assert_before_authentication, check_cli, check_reserved_headers, run_cli

root = Path(__file__).resolve().parents[1]
sample = json.loads((root / "sample-entraauthproxy.json").read_text())
assert sample["Headers"] == {}, "Starter sample must add no custom headers by default"
examples = re.findall(r"```json\n(.*?)\n```", (root / "README.md").read_text(), re.S)
assert examples, "Expected maintained README JSON examples"
parsed_examples = []
for example in examples:
    parsed_examples.append(json.loads(example if example.lstrip().startswith("{") else "{" + example + "}"))
assert any("EntraAuth" in example for example in parsed_examples), "Expected setup example"
assert any("ReverseProxy" in example for example in parsed_examples), "Expected routing example"
header_examples = [example["Headers"] for example in parsed_examples if "Headers" in example]
assert any(len(headers) >= 2 for headers in header_examples), "Expected a useful multiple-header example"
for headers in header_examples:
    assert isinstance(headers, dict) and all(isinstance(value, str) for value in headers.values()), "Headers examples must be flat string maps"
    assert all(name.lower() != "authorization" for name in headers), "Headers examples must respect the reserved name"

check_cli()
check_reserved_headers()

with tempfile.TemporaryDirectory(prefix="entra-starter-") as directory:
    work = Path(directory)
    config = work / "config"
    result = run_cli(work, config, [])
    assert result.returncode == 0, result.stderr
    assert "Created default configuration" in result.stdout, result.stdout
    assert_before_authentication(result)
    assert json.loads((config / "entraauthproxy.json").read_text()) == sample

for name in ("work-prod_2", "παραγωγή_٢", "生产_１２", "𐐀_𝟠"):
    with tempfile.TemporaryDirectory(prefix="entra-profile-starter-") as directory:
        work = Path(directory)
        config = work / "config"
        config.mkdir()
        base_file = config / "entraauthproxy.json"
        # A malformed base and local file prove neither is loaded for the profile
        # when the environment-selected directory disables local lookup.
        base_file.write_text("unprofiled configuration must be ignored")
        (work / "entraauthproxy.json").write_text("local configuration must be ignored")
        before = {path: path.read_bytes() for path in work.rglob("*") if path.is_file()}
        result = run_cli(work, config, ["--profile", name])
        assert result.returncode == 0, (name, result.stderr)
        selected = config / name / "entraauthproxy.json"
        assert f"Created default configuration at {selected}" in result.stdout, result.stdout
        assert_before_authentication(result)
        assert json.loads(selected.read_text()) == sample
        for path, contents in before.items():
            assert path.read_bytes() == contents, f"Unexpected change to {path}"
        assert set(path for path in work.rglob("*") if path.is_file()) == set(before) | {selected}, "Unexpected configuration/cache writes"

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
print(f"PASS: sample, {len(examples)} README JSON examples, isolated default/ASCII/Unicode profile starters, pre-authentication exit, maintained-file audit.")
print("Allowed negative-test matches: " + ", ".join(sorted(matched)))
