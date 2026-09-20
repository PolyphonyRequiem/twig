#!/usr/bin/env python3
"""Capture offline source fixtures; optionally inspect an installed CLI in its own cold cache."""
import argparse
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import tempfile


def capture(argv, cwd, output, name, timeout=300, environment=None):
    try:
        proc = subprocess.run(argv, cwd=cwd, env=environment, capture_output=True, timeout=timeout)
        stdout, stderr, code = proc.stdout, proc.stderr, proc.returncode
    except subprocess.TimeoutExpired as error:
        stdout, stderr, code = error.stdout or b'', error.stderr or b'', None
    (output / (name + '.stdout')).write_bytes(stdout)
    (output / (name + '.stderr')).write_bytes(stderr)
    record = {
        'argv': argv, 'cwd': str(cwd), 'exit_code': code,
        'stdout': name + '.stdout', 'stderr': name + '.stderr',
        'result_bytes': len(stdout) + len(stderr),
        'result_characters': len(stdout.decode('utf-8', errors='replace')) + len(stderr.decode('utf-8', errors='replace')),
        'tokens': None,
    }
    (output / (name + '.json')).write_text(json.dumps(record, indent=2) + '\n')
    return record


def cold_cli(prefix, output, label):
    # Each executable gets a NEW workspace and HOME; neither sees the operational
    # cache or credentials, and no binary ever opens another binary's cache.
    with tempfile.TemporaryDirectory(prefix='twig-baseline-' + label + '-') as temporary:
        fixture = Path(temporary)
        (fixture / 'twig.json').write_text(json.dumps({'organization': 'fixture.invalid', 'project': 'Fixture'}))
        (fixture / '.twig').mkdir()
        home = fixture / 'home'
        home.mkdir()
        runtime_keys = {
            'PATH', 'SystemRoot', 'WINDIR', 'COMSPEC', 'TEMP', 'TMP', 'TMPDIR',
            'LANG', 'LC_ALL', 'TERM', 'DOTNET_ROOT', 'DOTNET_ROOT_X64',
            'LD_LIBRARY_PATH', 'DYLD_LIBRARY_PATH',
        }
        environment = {key: value for key, value in os.environ.items() if key in runtime_keys}
        environment.update(HOME=str(home), USERPROFILE=str(home),
                           DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1')
        calls = []
        for name, args in [('version', ['version']), ('show-help', ['show', '--help']),
                           ('cold-show', ['show', '42', '-o', 'json']),
                           ('cold-process', ['process', 'Frobnicator', '-o', 'json'])]:
            calls.append(capture([*prefix, *args], fixture, output, label + '-' + name, environment=environment))
        schemas = []
        for db in (fixture / '.twig').rglob('twig.db'):
            with closing(sqlite3.connect(db.as_uri() + '?mode=ro', uri=True)) as connection:
                row = connection.execute("SELECT value FROM metadata WHERE key='schema_version'").fetchone()
            schemas.append({'relative_path': str(db.relative_to(fixture)), 'mirror_schema': row[0] if row else None})
        return {'adapter': 'Python subprocess; not a fresh Hermes/OMP agent session',
                'binding': {'organization': 'fixture.invalid', 'project': 'Fixture', 'credential_home': 'fresh empty temporary directory'},
                'calls': calls, 'schemas': schemas}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True, help='Fresh output directory; never overwritten')
    parser.add_argument('--installed', type=Path, help='Optional existing installed CLI; never updated')
    parser.add_argument('--require-fixed', action='store_true', help='Fail if desired defect outcomes remain unmet')
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[2]
    output = args.output.resolve()
    try:
        output.mkdir(parents=True, exist_ok=False)
    except FileExistsError:
        parser.error('output already exists; choose a fresh evidence directory')
    project = repo / 'tools/agent-efficiency-baseline/Baseline.csproj'
    build = capture(['dotnet', 'build', str(project), '--nologo'], repo, output, 'build')
    if build['exit_code'] != 0:
        print(json.dumps({'status': 'build failed', 'evidence': str(output)}))
        return build['exit_code'] or 1
    # Resolve framework from the SDK's evaluated project, not a second pinned version.
    framework = subprocess.check_output(['dotnet', 'msbuild', str(project), '-getProperty:TargetFramework'], cwd=repo, text=True).strip()
    runner = project.parent / 'bin/Debug' / framework / 'Baseline.dll'
    command = ['dotnet', str(runner), str(output / 'observations.json')]
    if args.require_fixed:
        command.append('--require-fixed')
    run = capture(command, repo, output, 'fixtures')
    summary = {'schema': 'twig.agent-efficiency-capture.v1',
               'source_commit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=repo, text=True).strip(),
               'build': build, 'fixtures': run, 'outer_model_calls': None, 'tokens': None,
               'notes': ['Subprocess result characters are not billed tokens.',
                         'Fixture output contains complete sanitized inputs and observations, not live ADO proof.']}
    # The process-rule provider is internal to the composition root. Reuse its
    # existing real supplier/unsupplied-field regressions rather than mock a gate
    # result or expose production internals to this diagnostic tool.
    test_environment = dict(os.environ)
    test_environment['TWIG_TEST_LOG_DIR'] = str(output / 'test-logs')
    regression = capture(['bash', 'tools/run-tests.sh', 'Infrastructure'], repo,
                         output, 'infrastructure-regressions', timeout=600, environment=test_environment)
    regression['credential_home'] = 'inherited; existing repository suite owns its fixture isolation'
    summary['infrastructure_regressions'] = regression
    if args.installed:
        binary = args.installed.resolve(strict=True)
        before = hashlib.sha256(binary.read_bytes()).hexdigest()
        summary['installed'] = {'path': str(binary), 'sha256': before,
                                'source_provenance': 'unknown; version is not ancestry',
                                **cold_cli([str(binary)], output, 'installed')}
        source = repo / 'src/Twig/bin/Debug' / framework / 'twig.dll'
        summary['source_cli'] = cold_cli(['dotnet', str(source)], output, 'source')
        after = hashlib.sha256(binary.read_bytes()).hexdigest()
        summary['installed']['unchanged'] = before == after
        if before != after:
            raise RuntimeError('Installed binary changed during capture')
    (output / 'summary.json').write_text(json.dumps(summary, indent=2) + '\n')
    overall = (regression['exit_code'] or 1) if regression['exit_code'] != 0 else (
        run['exit_code'] if run['exit_code'] is not None else 1)
    print(json.dumps({'summary': str(output / 'summary.json'), 'exit_code': overall,
                      'fixture_exit_code': run['exit_code'],
                      'regression_exit_code': regression['exit_code']}))
    return overall


if __name__ == '__main__':
    raise SystemExit(main())
