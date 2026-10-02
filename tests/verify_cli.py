"""Exercise the actual profile CLI without sign-in or real configuration writes.

Run after the Debug solution build: python3 tests/verify_cli.py
"""
import json
import os
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
APPLICATION = ROOT / "artifacts/bin/EntraAuthProxy/debug/entraauthproxy.dll"


def run_cli(work, config, arguments):
    env = dict(os.environ, ENTRAAUTHPROXY_CONFIG_DIR=str(config))
    return subprocess.run(
        ["dotnet", str(APPLICATION), *arguments],
        cwd=work, env=env, capture_output=True, text=True, timeout=20,
    )


def assert_before_authentication(result):
    output = result.stdout + result.stderr
    for marker in ("Successfully authenticated", "Now listening", "Msal", "BuildAndAuthenticateAsync"):
        assert marker not in output, output


def check_cli():
    cases = 0
    with tempfile.TemporaryDirectory(prefix="entra-cli-") as directory:
        work = Path(directory)
        config = work / "config"
        # Deliberately malformed: conflicts must reject the CLI before JSON loading.
        existing = work / "existing.json"
        existing.write_text("not JSON")
        missing = work / "missing.json"
        help_result = run_cli(work, config, ["--help"])
        assert help_result.returncode == 0, help_result.stderr
        assert "--profile <name>" in help_result.stdout, help_result.stdout
        assert "-c, --config" in help_result.stdout, help_result.stdout

        for alias in ("-c", "--config"):
            for file in (existing, missing):
                for profile_first in (False, True):
                    options = [["--profile", "work"], [alias, str(file)]]
                    if not profile_first:
                        options.reverse()
                    result = run_cli(work, config, sum(options, []))
                    assert result.returncode != 0, result.stdout
                    assert "--profile cannot be combined with -c or --config" in result.stderr, result.stderr
                    assert "Configuration file not found" not in result.stderr, result.stderr
                    assert_before_authentication(result)
                    assert sorted(path.name for path in work.iterdir()) == ["existing.json"], "Conflict caused startup writes"
                    cases += 1

            result = run_cli(work, config, [alias, str(missing)])
            assert result.returncode != 0, result.stdout
            assert f"Configuration file not found at {missing}" in result.stderr, result.stderr
            assert_before_authentication(result)
            assert not config.exists(), "Missing explicit file caused directory creation"
            cases += 1

        result = run_cli(work, config, ["--profile"])
        assert result.returncode != 0, result.stdout
        assert "--profile" in result.stderr, result.stderr
        assert_before_authentication(result)
        assert not config.exists(), "Missing profile value caused directory creation"
        cases += 1

        invalid_names = (
            "", ".", "..", "work.prod", "work prod", " ", "work\tprod", "work\nprod",
            "work/prod", "work\\prod", "../work", "/tmp/work", "C:\\work", "~/work",
            "work:prod", "work@prod", "work+prod", "work%prod", "work💼", "work²", "workⅣ",
            "e\u0301",  # Combining marks are outside L and Nd, even after a letter.
        )
        for name in invalid_names:
            result = run_cli(work, config, ["--profile", name])
            assert result.returncode != 0, (name, result.stdout)
            assert "--profile requires a non-empty name" in result.stderr, (name, result.stderr)
            assert_before_authentication(result)
            assert sorted(path.name for path in work.iterdir()) == ["existing.json"], (name, "Invalid name caused writes")
            cases += 1

        # A valid name reaches the independent conflict validator without a name
        # error, so these cases still exit before any authentication or writes.
        for name in ("work-prod_2", "παραγωγή_٢", "生产_１２", "𐐀_𝟠", "-", "_"):
            result = run_cli(work, config, ["--profile", name, "-c", str(missing)])
            assert result.returncode != 0, result.stdout
            assert "cannot be combined" in result.stderr, (name, result.stderr)
            assert "requires a non-empty name" not in result.stderr, (name, result.stderr)
            assert_before_authentication(result)
            assert not config.exists(), "Valid-name conflict caused writes"
            cases += 1

    print(f"PASS: CLI help and {cases} profile validation/conflict/missing-file cases without startup side effects.")


def check_reserved_headers():
    cases = 0
    for name in ("Authorization", "authorization", "AuThOrIzAtIoN"):
        for value in ("", "secret-reserved-value"):
            for profile in (None, "work"):
                with tempfile.TemporaryDirectory(prefix="entra-reserved-header-") as directory:
                    work = Path(directory)
                    config = work / "config"
                    selected = config / profile / "entraauthproxy.json" if profile else work / "explicit.json"
                    selected.parent.mkdir(parents=True, exist_ok=True)
                    # Invalid authentication settings make any accidental entry to
                    # MSAL observable without signing in or accessing a token cache.
                    selected.write_text(json.dumps({"Headers": {name: value}}))
                    before = {path: path.read_bytes() for path in work.rglob("*") if path.is_file()}
                    arguments = ["--profile", profile] if profile else ["--config", str(selected)]
                    result = run_cli(work, config, arguments)
                    output = result.stdout + result.stderr
                    assert result.returncode != 0, output
                    assert "Headers" in output and "Authorization" in output and "proxy manages" in output, output
                    assert "secret-reserved-value" not in output, output
                    assert "ClientId or EntraAuth:TargetScope is missing" not in output, output
                    assert_before_authentication(result)
                    after = {path: path.read_bytes() for path in work.rglob("*") if path.is_file()}
                    assert after == before, "Reserved header caused configuration/cache writes"
                    cases += 1
    print(f"PASS: {cases} actual CLI reserved-header rejections before authentication/cache/listener startup.")


if __name__ == "__main__":
    check_cli()
    check_reserved_headers()
