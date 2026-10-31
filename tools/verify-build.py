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


class SmokeTrace:
    """Optional safe boundary records, written outside the captured worker pipes."""
    PHASES = {'supervisor', 'worker', 'version', 'help', 'config-help', 'connection-help',
              'configure', 'fixture', 'identity', 'preview', 'wrong-account', 'journal',
              'git-init', 'git-common', 'git-dir', 'git-root', 'fixture-identities'}
    EVENTS = {'start', 'end', 'error', 'timeout'}

    def __init__(self, path):
        self.path = path
        self.started = time.monotonic()

    def write(self, phase, event, exit_code=None):
        if phase not in self.PHASES or event not in self.EVENTS:
            raise ValueError('Unknown smoke trace boundary')
        if self.path is None:
            return
        self.path.parent.mkdir(parents=True, exist_ok=True)
        row = {'phase': phase, 'event': event,
               'elapsedSeconds': round(time.monotonic() - self.started, 6)}
        if exit_code is not None:
            row['exitCode'] = exit_code
        with self.path.open('a', encoding='utf-8') as output:
            output.write(json.dumps(row) + '\n')
            output.flush()


def run_worker(command, timeout, trace):
    trace.write('supervisor', 'start')
    with subprocess.Popen(command, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          text=True, encoding='utf-8', start_new_session=os.name != 'nt') as process:
        try:
            stdout, stderr = process.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            spec = importlib.util.spec_from_file_location('ci_runner', ROOT / 'tools/run-ci-tests.py')
            runner = importlib.util.module_from_spec(spec)
            sys.modules[spec.name] = runner
            spec.loader.exec_module(runner)
            runner.stop_session(process, False)
            trace.write('supervisor', 'timeout')
            print(f'SMOKE FAILED: {timeout}s deadline exceeded; no timeout expansion', file=sys.stderr)
            return 1
    trace.write('supervisor', 'end', process.returncode)
    print(stdout, end='')
    print(stderr, end='', file=sys.stderr)
    return process.returncode


def worker(binary, directory, trace):
    trace.write('worker', 'start')
    directory = directory.resolve()
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
    def run(label, *arguments, expected=0):
        trace.write(label, 'start')
        result = subprocess.run(command + list(arguments), cwd=workspace, env=env, capture_output=True, text=True)
        trace.write(label, 'end', result.returncode)
        if result.returncode != expected:
            raise RuntimeError(f'{arguments}: exit {result.returncode}, expected {expected}: {result.stderr}')
        return result
    run('version', '--version')
    for label, arguments in (('help', ('--help',)), ('config-help', ('config', '--help')),
                             ('connection-help', ('connection', 'default', '--help'))):
        run(label, *arguments)
    run('configure', 'config', 'display.icons', 'unicode', '-o', 'json')
    spec = importlib.util.spec_from_file_location('presenter_fixture', ROOT / 'tools/verify-proposal-presenters.py')
    fixture = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(fixture)
    trace.write('fixture', 'start')
    fixture.attach_offline_fixture(command, workspace, home, env, trace=trace)
    trace.write('fixture', 'end')
    status = json.loads(run('identity', 'auth', 'status', '-o', 'json').stdout)
    if status.get('identity') != 'offline-reviewer':
        raise RuntimeError('Current-store binding metadata was not admitted')
    cache = workspace / '.twig/cache/twig.db'
    with sqlite3.connect(cache) as db:
        db.execute('INSERT INTO work_items (id,type,title,state,iteration_path,area_path,revision,is_seed,fields_json,is_dirty,last_synced_at) VALUES (?,?,?,?,?,?,?,?,?,?,?)',
                   (42, 'Task', 'Offline smoke item', 'New', 'PresenterSmoke', 'PresenterSmoke', 1, 0, '{}', 0, '2026-01-01T00:00:00+00:00'))
    collection, project_id = '11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222'
    actor = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
    identity_ref = json.dumps(['fixture-binding', 'fixture-identity', 'aad', actor], separators=(',', ':'))
    connection_data = {'Provider': {'Value': 'ado'}, 'ResourceScope': collection,
                       'Destination': project_id, 'IdentityRef': identity_ref}
    connection_text = json.dumps(connection_data, separators=(',', ':')).replace('\\"', '\\u0022')
    instance = '33333333-3333-3333-3333-333333333333'
    core_db = workspace / '.twig/core.db'
    with sqlite3.connect(core_db) as db:
        db.execute(f'PRAGMA application_id={0x54574353}')
        db.execute('PRAGMA user_version=3')
        db.execute('PRAGMA journal_mode=WAL')
        db.execute('CREATE TABLE core_identity(id TEXT PRIMARY KEY NOT NULL,workspace_connection TEXT) STRICT')
        db.execute('INSERT INTO core_identity VALUES (?,?)', (instance, connection_text))
        db.execute('CREATE TABLE core_snapshots(connection TEXT PRIMARY KEY NOT NULL,version INTEGER NOT NULL CHECK(version>0),document TEXT NOT NULL) STRICT')
        snapshot = {'Version': 3, 'StoreVersion': 1, 'Items': [], 'Relationships': [], 'Drafts': [],
                    'Proposals': [], 'Journal': [], 'Mappings': [], 'Benches': []}
        db.execute('INSERT INTO core_snapshots VALUES (?,?,?)', (connection_text, 1, json.dumps(snapshot, separators=(',', ':'))))
    (workspace / '.twig/core-workspace.json').write_text(json.dumps({
        'version': 1, 'databasePath': str(core_db), 'databaseInstance': instance,
        'connection': {'provider': {'value': 'ado'}, 'resourceScope': collection,
                       'destination': project_id, 'identityRef': identity_ref}, 'principalId': actor}))
    key = ['remote', 'ado', collection, '42']
    proposal = {'version': 3, 'connection': {'provider': 'ado', 'scope': collection,
                    'destination': project_id, 'identity': identity_ref},
                'schema': 'fictional-offline-schema', 'storeVersion': 1,
                'policyIdentity': None, 'policyRevision': None,
                'operations': [{'id': 'offline-smoke', 'kind': 1, 'item': key,
                    'revision': {'item': key, 'token': 'ado-rev:1'}, 'type': None,
                    'fields': {'System.Title': [1, 'Reviewed smoke item']}, 'other': None,
                    'relation': None, 'origin': 0, 'concurrency': 0, 'localVersion': None,
                    'body': None, 'resource': None}]}
    (workspace / 'smoke.json').write_text(json.dumps(proposal))
    preview = json.loads(run('preview', 'proposal', 'preview', '--file', 'smoke.json', '-o', 'json').stdout)
    if len(preview.get('operations', [])) != 1:
        raise RuntimeError('Offline native proposal preview lost its operation')
    decisions = preview.get('reviewModel', {}).get('policyDecisions', [])
    if preview.get('canApply') or not any(
            decision.get('kind') == 'process-required-fields'
            and decision.get('outcome') == 'unavailable'
            and decision.get('blocking') is True
            for decision in decisions):
        raise RuntimeError('Offline native proposal preview did not fail closed on unavailable process rules')
    token_cache = home / 'metadata/credentials/cred-cccccccccccccccccccccccccccccccc.token-cache'
    expiry, token = token_cache.read_text().splitlines()[:2]
    header, payload, signature = token.split('.')
    claims = json.loads(base64.urlsafe_b64decode(payload + '=' * (-len(payload) % 4)))
    claims['oid'] = 'dddddddd-dddd-dddd-dddd-dddddddddddd'
    payload = base64.urlsafe_b64encode(json.dumps(claims).encode()).decode().rstrip('=')
    token_cache.write_text(f'{expiry}\n{header}.{payload}.{signature}\n')
    refusal = run('wrong-account', 'show', '42', '--refresh', '-o', 'json', expected=1)
    error = json.loads(refusal.stderr).get('error', '')
    if 'bound principal' not in error:
        raise RuntimeError('Wrong-account refusal was not established')
    trace.write('journal', 'start')
    with sqlite3.connect(core_db) as db:
        retained = json.loads(db.execute('SELECT document FROM core_snapshots WHERE connection=?', (connection_text,)).fetchone()[0])
        if len(retained['Proposals']) != 1 or len(retained['Journal']) != 1:
            raise RuntimeError('Core preview must retain the exact unconfirmed proposal')
        row = retained['Journal'][0]
        if row['Status'] != 0 or row['Attempt'] != '00000000-0000-0000-0000-000000000000' or row['Receipts']:
            raise RuntimeError('Core smoke must never acquire Applying or a transport attempt')
    trace.write('journal', 'end')
    trace.write('worker', 'end', 0)
    print('SMOKE PASSED: startup/help, current schema/binding, offline fail-closed proposal preview, wrong-account refusal; no work HTTP or apply')
    return 0


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stderr.reconfigure(encoding='utf-8')
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--binary', type=Path, default=ROOT / 'src/Twig/bin/Debug/net11.0/twig.dll')
    parser.add_argument('--timeout', type=int, default=15, choices=range(1, 16))
    parser.add_argument('--worker', type=Path, help=argparse.SUPPRESS)
    parser.add_argument('--trace', type=Path, help='Optional safe, flushed JSONL phase trace; no arguments or payloads')
    args = parser.parse_args()
    binary = args.binary.resolve()
    if not binary.is_file():
        parser.error('Build the CLI first or supply --binary; the smoke never compiles')
    trace = SmokeTrace(args.trace.resolve() if args.trace else None)
    if args.worker:
        try:
            return worker(binary, args.worker, trace)
        except BaseException:
            trace.write('worker', 'error')
            raise
    started = time.monotonic()
    with tempfile.TemporaryDirectory(prefix='twig-build-smoke-') as directory:
        command = [sys.executable, str(Path(__file__).resolve()), '--binary', str(binary), '--worker', directory]
        if trace.path is not None:
            command.extend(['--trace', str(trace.path)])
        result = run_worker(command, args.timeout, trace)
        print(f'SMOKE DURATION: {time.monotonic() - started:.2f}s')
        return result


if __name__ == '__main__':
    sys.exit(main())
