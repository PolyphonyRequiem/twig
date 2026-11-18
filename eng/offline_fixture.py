"""Private fictional ADO binding for the offline CLI smoke."""

import base64
import datetime
import hashlib
import json
from pathlib import Path
import sqlite3
import subprocess


def attach_offline_fixture(command, workspace, home, env, trace=None):
    """Use fictional bound metadata/token in private stores; all HTTP is blocked."""
    def run(label, argv, **kwargs):
        if trace is not None:
            trace.write(label, 'start')
        result = subprocess.run(argv, **kwargs)
        if trace is not None:
            trace.write(label, 'end', result.returncode)
        return result
    run('git-init', ['git', 'init', '--quiet', str(workspace)], check=True)
    def git_path(flag):
        label = {'--git-common-dir': 'git-common', '--absolute-git-dir': 'git-dir',
                 '--show-toplevel': 'git-root'}[flag]
        value = run(label, ['git', '-C', str(workspace), 'rev-parse',
                           '--path-format=absolute', flag],
                    check=True, stdout=subprocess.PIPE, text=True).stdout.strip()
        return str(Path(value).resolve())
    fingerprint = {'gitCommonDir': git_path('--git-common-dir'),
                   'worktreeGitDir': git_path('--absolute-git-dir'),
                   'worktreeRoot': git_path('--show-toplevel')}
    canonical = lambda value: json.dumps(value, sort_keys=True, separators=(',', ':'))
    connection = hashlib.sha256(canonical({'organization': 'fixture.invalid',
                                          'project': 'PresenterSmoke'}).encode()).hexdigest()
    metadata = home / 'metadata'
    env['TWIG_USER_HOME'] = str(metadata)
    for key in ('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'http_proxy', 'https_proxy', 'all_proxy'):
        env[key] = 'http://127.0.0.1:1'
    env['NO_PROXY'] = env['no_proxy'] = ''
    run('fixture-identities', command + ['auth', 'identities', '-o', 'json'], cwd=workspace,
        env=env, check=True, capture_output=True, text=True, timeout=45)
    stamp = '2026-01-01T00:00:00+00:00'
    tenant, actor = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
    issuer = f'https://sts.windows.net/{tenant}/'
    credential_ref = 'cred-cccccccccccccccccccccccccccccccc'
    with sqlite3.connect(metadata / 'system.db') as db:
        db.execute('INSERT INTO connections VALUES (?,?,?,?,?,?)', (connection, 'fixture.invalid', 'PresenterSmoke', None, stamp, stamp))
        db.execute('INSERT INTO worktrees VALUES (?,?,?,?,?,?)', (canonical(fingerprint), connection, str(workspace), stamp, stamp, None))
        db.execute('INSERT INTO identities (identity_id,name,tenant_id,object_id,issuer,authority_host,credential_ref,account_name,created_at,updated_at) VALUES (?,?,?,?,?,?,?,?,?,?)',
                   ('fixture-identity', 'offline-reviewer', tenant, actor, issuer, 'login.microsoftonline.com', credential_ref, 'reviewer@fixture.invalid', stamp, stamp))
        db.execute('INSERT INTO connection_bindings VALUES (?,?,?,?,?,?)', ('fixture-binding', connection, 'fixture-identity', 1, stamp, stamp))
        db.execute('INSERT INTO connection_defaults VALUES (?,?,?,?)', (connection, 'fixture-binding', 1, stamp))
    local = workspace / '.twig'
    (local / 'layout.json').write_text(json.dumps({'$schema': 'twig-layout/v1', 'version': 1, 'initializedAt': stamp, 'createdBy': 'offline-fixture'}))
    (local / 'worktree.json').write_text(json.dumps({'$schema': 'twig-worktree/v1', 'version': 1, 'worktreeFingerprint': fingerprint}))
    (local / 'attachment.json').write_text(json.dumps({'$schema': 'twig-attachment/v1', 'version': 1, 'revision': 0, 'connectionRef': connection}))
    expiry = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(hours=1)
    encode = lambda value: base64.urlsafe_b64encode(json.dumps(value).encode()).decode().rstrip('=')
    token = encode({'alg': 'none'}) + '.' + encode({'aud': '499b84ac-1321-427f-aa17-267ca6975798',
            'tid': tenant, 'oid': actor, 'iss': issuer, 'exp': int(expiry.timestamp())}) + '.fictional'
    credentials = metadata / 'credentials'
    credentials.mkdir(exist_ok=True)
    (credentials / f'{credential_ref}.token-cache').write_text(f'{int(expiry.timestamp() * 10000000) + 621355968000000000}\n{token}\n')
