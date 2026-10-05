#!/usr/bin/env python3
"""Validate an already-built CLI offline in at most 15 seconds; never build or run test hosts."""
import argparse
import base64
import importlib.util
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parent.parent


def worker(binary, directory):
    workspace, home = directory / 'workspace', directory / 'home'
    (workspace / '.twig').mkdir(parents=True)
    home.mkdir()
    (workspace / 'twig.json').write_text(json.dumps({'organization': 'fixture.invalid', 'project': 'PresenterSmoke'}))
    command = ['dotnet', str(binary)] if binary.suffix == '.dll' else [str(binary)]
    env = {key: value for key, value in os.environ.items() if key in {'PATH', 'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_ROOT_ARM64', 'LD_LIBRARY_PATH'}}
    env.update(HOME=str(home), USERPROFILE=str(home), TWIG_USER_HOME=str(home / 'metadata'),
               TERM='dumb', NO_COLOR='1', DOTNET_CLI_TELEMETRY_OPTOUT='1')
    for key in ('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'http_proxy', 'https_proxy', 'all_proxy'):
        env[key] = 'http://127.0.0.1:1'
    env['NO_PROXY'] = env['no_proxy'] = ''
    def run(*arguments, expected=0):
        result = subprocess.run(command + list(arguments), cwd=workspace, env=env, capture_output=True, text=True)
        if result.returncode != expected:
            raise RuntimeError(f'{arguments}: exit {result.returncode}, expected {expected}: {result.stderr}')
        return result
    run('--version')
    for arguments in (('--help',), ('config', '--help'), ('connection', 'default', '--help')):
        run(*arguments)
    run('config', 'display.icons', 'unicode', '-o', 'json')
    spec = importlib.util.spec_from_file_location('presenter_fixture', ROOT / 'tools/verify-proposal-presenters.py')
    fixture = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(fixture)
    fixture.attach_offline_fixture(command, workspace, home, env)
    status = json.loads(run('auth', 'status', '-o', 'json').stdout)
    if status.get('identity') != 'offline-reviewer':
        raise RuntimeError('Current-store binding metadata was not admitted')
    cache = workspace / '.twig/cache/twig.db'
    with sqlite3.connect(cache) as db:
        db.execute('INSERT INTO work_items (id,type,title,state,iteration_path,area_path,revision,is_seed,fields_json,is_dirty,last_synced_at) VALUES (?,?,?,?,?,?,?,?,?,?,?)',
                   (42, 'Task', 'Offline smoke item', 'New', 'PresenterSmoke', 'PresenterSmoke', 1, 0, '{}', 0, '2026-01-01T00:00:00+00:00'))
    proposal = {'version': 1, 'workspace': {'organization': 'fixture.invalid', 'project': 'PresenterSmoke'},
                'operations': [{'id': 'offline-smoke', 'kind': 'batch', 'workItemId': 42, 'expectedRevision': 1, 'fields': {'System.Title': 'Reviewed smoke item'}}]}
    (workspace / 'smoke.json').write_text(json.dumps(proposal))
    preview = json.loads(run('proposal', 'preview', '--file', 'smoke.json', '-o', 'json').stdout)
    if not preview.get('canApply') or len(preview.get('operations', [])) != 1:
        raise RuntimeError('Offline native proposal preview failed')
    token_cache = home / 'metadata/credentials/cred-cccccccccccccccccccccccccccccccc.token-cache'
    expiry, token = token_cache.read_text().splitlines()[:2]
    header, payload, signature = token.split('.')
    claims = json.loads(base64.urlsafe_b64decode(payload + '=' * (-len(payload) % 4)))
    claims['oid'] = 'dddddddd-dddd-dddd-dddd-dddddddddddd'
    payload = base64.urlsafe_b64encode(json.dumps(claims).encode()).decode().rstrip('=')
    token_cache.write_text(f'{expiry}\n{header}.{payload}.{signature}\n')
    refusal = run('show', '42', '--refresh', '-o', 'json', expected=1)
    error = json.loads(refusal.stderr).get('error', '')
    if 'bound principal' not in error:
        raise RuntimeError('Wrong-account refusal was not established')
    with sqlite3.connect(workspace / '.twig/cache/pending.db') as db:
        if db.execute('SELECT state,confirmed_at FROM proposal_journals').fetchall() != [('Planned', None)]:
            raise RuntimeError('Smoke must leave its proposal unconfirmed and never apply')
    print('SMOKE PASSED: startup/help, current schema/binding, offline proposal, wrong-account refusal; no work HTTP or apply')
    return 0


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stderr.reconfigure(encoding='utf-8')
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--binary', type=Path, default=ROOT / 'src/Twig/bin/Debug/net11.0/twig.dll')
    parser.add_argument('--timeout', type=int, default=15, choices=range(1, 16))
    parser.add_argument('--worker', type=Path, help=argparse.SUPPRESS)
    args = parser.parse_args()
    binary = args.binary.resolve()
    if not binary.is_file():
        parser.error('Build the CLI first or supply --binary; the smoke never compiles')
    if args.worker:
        return worker(binary, args.worker)
    started = time.monotonic()
    with tempfile.TemporaryDirectory(prefix='twig-build-smoke-') as directory:
        with subprocess.Popen([sys.executable, str(Path(__file__).resolve()), '--binary', str(binary), '--worker', directory],
                              cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                              encoding='utf-8', start_new_session=os.name != 'nt') as process:
            try:
                stdout, stderr = process.communicate(timeout=args.timeout)
            except subprocess.TimeoutExpired:
                spec = importlib.util.spec_from_file_location('ci_runner', ROOT / 'tools/run-ci-tests.py')
                runner = importlib.util.module_from_spec(spec)
                sys.modules[spec.name] = runner
                spec.loader.exec_module(runner)
                runner.stop_session(process, False)
                print(f'SMOKE FAILED: {args.timeout}s deadline exceeded; no timeout expansion', file=sys.stderr)
                return 1
        print(stdout, end='')
        print(stderr, end='', file=sys.stderr)
        print(f'SMOKE DURATION: {time.monotonic() - started:.2f}s')
        return process.returncode


if __name__ == '__main__':
    sys.exit(main())
