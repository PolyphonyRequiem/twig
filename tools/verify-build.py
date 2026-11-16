#!/usr/bin/env python3
"""Validate an already-built CLI offline in at most 15 seconds; never build or run test hosts."""
import argparse
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
    PHASES = {'supervisor', 'worker', 'version', 'help', 'auth-help', 'connection-help',
              'initialize', 'schema', 'draft', 'detail', 'preview', 'wrong-connection',
              'wrong-digest', 'journal', 'git-init'}
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


def stop_worker(process):
    spec = importlib.util.spec_from_file_location('ci_runner', ROOT / 'tools/run-ci-tests.py')
    runner = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = runner
    spec.loader.exec_module(runner)
    try:
        runner.stop_session(process)
        return True
    except (runner.RunFailure, OSError, subprocess.SubprocessError) as error:
        print(f'SMOKE CLEANUP UNCONFIRMED: {error}; root exit={process.poll()}; survivors unknown',
              file=sys.stderr, flush=True)
        return False


def run_worker(command, timeout, trace):
    trace.write('supervisor', 'start')
    # Popen.__exit__ performs an untimed wait, including after failed tree cleanup.
    process = subprocess.Popen(command, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               text=True, encoding='utf-8', start_new_session=os.name != 'nt')
    try:
        stdout, stderr = process.communicate(timeout=timeout)
    except subprocess.TimeoutExpired as error:
        for value, destination in ((error.stdout, sys.stdout), (error.stderr, sys.stderr)):
            if value:
                print(value.decode('utf-8', errors='replace') if isinstance(value, bytes) else value,
                      end='', file=destination, flush=True)
        stopped = stop_worker(process)
        if not stopped:
            trace.write('supervisor', 'error')
        trace.write('supervisor', 'timeout')
        cleanup = 'owned tree stopped' if stopped else 'tree termination unconfirmed'
        print(f'SMOKE FAILED: {timeout}s deadline exceeded; {cleanup}; no timeout expansion',
              file=sys.stderr, flush=True)
        return 1
    except KeyboardInterrupt:
        stopped = stop_worker(process)
        trace.write('supervisor', 'error', 130)
        cleanup = 'owned tree stopped' if stopped else 'tree termination unconfirmed'
        print(f'SMOKE INTERRUPTED: {cleanup}', file=sys.stderr, flush=True)
        return 130
    trace.write('supervisor', 'end', process.returncode)
    print(stdout, end='')
    print(stderr, end='', file=sys.stderr)
    return process.returncode


def worker(binary, directory, trace):
    trace.write('worker', 'start')
    directory = directory.resolve()
    workspace, home = directory / 'workspace', directory / 'home'
    workspace.mkdir()
    home.mkdir()
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
    for label, arguments in (('help', ('--help',)), ('auth-help', ('auth', '--help')),
                             ('connection-help', ('connection', 'status', '--help'))):
        run(label, *arguments)
    trace.write('git-init', 'start')
    git = subprocess.run(['git', 'init', '--quiet', str(workspace)], env=env, capture_output=True, text=True)
    trace.write('git-init', 'end', git.returncode)
    if git.returncode != 0:
        raise RuntimeError(f'Fresh smoke worktree initialization failed: {git.stderr}')
    initialized = json.loads(run('initialize', 'init', '--provider', 'twig', '-o', 'json').stdout)
    if initialized.get('coreInitialized') is not True:
        raise RuntimeError('The real fresh native initializer did not establish a Core workspace')
    core_db = workspace / '.twig/core.db'
    marker = json.loads((workspace / '.twig/core-workspace.json').read_text(encoding='utf-8-sig'))
    connection = marker['connection']
    if connection['provider']['value'] != 'twig' or Path(marker['databasePath']) != core_db:
        raise RuntimeError('Fresh initialization did not record its exact native store')
    schema = json.loads(run('schema', 'process', '-o', 'json').stdout)
    types = schema['types']
    if not types or not schema['schemaFingerprint']:
        raise RuntimeError('Native schema discovery returned no usable type evidence')
    title = 'Offline smoke draft'
    seed = json.loads(run('draft', 'seed', 'new', '--type', types[0]['typeName'],
                          '--title', title, '--no-parent', '-o', 'json').stdout)
    if seed.get('isSeed') is not True or seed.get('title') != title or not seed.get('localId'):
        raise RuntimeError('Structured native seed creation lost its actual local draft identity')
    detail = json.loads(run('detail', 'show', '--local-id', seed['localId'], '-o', 'json').stdout)
    if detail.get('title') != title or detail.get('localId') != seed['localId']:
        raise RuntimeError('Current detail reads did not reopen the exact staged draft')

    def snapshot():
        with sqlite3.connect(core_db.as_uri() + '?mode=ro', uri=True) as db:
            rows = db.execute('SELECT version, document FROM core_snapshots').fetchall()
        if len(rows) != 1:
            raise RuntimeError('Fresh native store must have exactly one actual connection snapshot')
        version, document = rows[0]
        stored = json.loads(document)
        if stored['Version'] != 3 or stored['StoreVersion'] != version:
            raise RuntimeError('Current native store snapshot/version evidence disagrees')
        return stored

    staged = snapshot()
    if len(staged['Drafts']) != 1 or staged['Proposals'] or staged['Journal'] or staged['Mappings'] or staged['Items']:
        raise RuntimeError('Seed staging must retain one unpublished draft without transport or proposal effects')
    draft = staged['Drafts'][0]
    if draft['Key']['LocalId'] != seed['localId'] or draft['Type'] != types[0]['typeName']:
        raise RuntimeError('Staged Core draft does not match the public CLI result')
    fields = {}
    for field in draft['Changes']:
        value = field['Value']
        if value['Kind'] != 1 or not isinstance(value['Text'], str):
            raise RuntimeError('Fresh native smoke draft must expose actual typed text fields')
        fields[field['Reference']] = [value['Kind'], value['Text']]
    if not fields:
        raise RuntimeError('The actual native draft must contain nonempty typed fields')
    proposal = {'version': 3, 'connection': {'provider': connection['provider']['value'],
                    'scope': connection['resourceScope'], 'destination': connection['destination'],
                    'identity': connection['identityRef']},
                'schema': schema['schemaFingerprint'], 'storeVersion': staged['StoreVersion'],
                'policyIdentity': None, 'policyRevision': None,
                'operations': [{'id': 'offline-smoke', 'kind': 0, 'item': ['local', seed['localId']],
                    'revision': None, 'type': draft['Type'], 'fields': fields, 'other': None,
                    'relation': None, 'origin': 0, 'concurrency': 0, 'localVersion': None,
                    'body': None, 'resource': None}]}
    (workspace / 'smoke.json').write_text(json.dumps(proposal))
    preview = json.loads(run('preview', 'proposal', 'preview', '--file', 'smoke.json', '-o', 'json').stdout)
    if preview.get('canApply') is not True or len(preview.get('operations', [])) != 1:
        raise RuntimeError(f'Actual native draft proposal was not successfully reviewed: {preview}')
    digest = preview['digest']
    if len(digest) != 64 or any(character not in '0123456789abcdef' for character in digest):
        raise RuntimeError('Current proposal review did not return its canonical digest')
    wrong_digest = ('1' if digest[0] == '0' else '0') + digest[1:]
    refusal = run('wrong-digest', 'proposal', 'preview', '--file', 'smoke.json',
                  '--expect-digest', wrong_digest, '-o', 'json', expected=1)
    if 'exact expected review digest' not in json.loads(refusal.stderr).get('error', ''):
        raise RuntimeError('Mismatched review digest was not explicitly refused')
    proposal['connection']['destination'] += '-wrong'
    (workspace / 'wrong-connection.json').write_text(json.dumps(proposal))
    refusal = run('wrong-connection', 'proposal', 'preview', '--file', 'wrong-connection.json',
                  '-o', 'json', expected=1)
    if 'another immutable workspace connection' not in json.loads(refusal.stderr).get('error', ''):
        raise RuntimeError('Mismatched immutable proposal connection was not explicitly refused')
    status = json.loads(run('journal', 'proposal', 'status', '--file', 'smoke.json', '-o', 'json').stdout)
    if status.get('found') is not True or status.get('digest') != digest or len(status.get('operations', [])) != 1:
        raise RuntimeError('Current proposal status did not read the exact retained review')
    operation = status['operations'][0]
    if operation.get('status') != 'Planned' or operation.get('attempt') is not None or operation.get('receipts') != []:
        raise RuntimeError('Public journal inspection must report only an unattempted planned operation')
    retained = snapshot()
    if (retained['StoreVersion'] != staged['StoreVersion'] + 1
            or len(retained['Proposals']) != 1 or len(retained['Journal']) != 1
            or retained['Proposals'][0]['Digest'] != digest):
        raise RuntimeError('Core preview must retain only its exact unconfirmed proposal')
    row = retained['Journal'][0]
    if row['Digest'] != digest or row['Status'] != 0 or row['Attempt'] != '00000000-0000-0000-0000-000000000000' or row['Receipts']:
        raise RuntimeError('Core smoke must never acquire Applying or a transport attempt')
    if retained['Drafts'] != staged['Drafts'] or retained['Items'] or retained['Mappings']:
        raise RuntimeError('Review/refusals must preserve the unpublished draft without native creation')
    if (workspace / '.twig/cache').exists():
        raise RuntimeError('Native host/Core smoke must not materialize a legacy cache')
    trace.write('worker', 'end', 0)
    print('SMOKE PASSED: fresh native init, discovered schema, draft/detail, proposal review/status, wrong-connection/digest refusals; no legacy cache, work HTTP or apply')
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
