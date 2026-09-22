#!/usr/bin/env python3
"""Offline real-CLI smoke: canonical preview -> CLI/full/JSON -> Discord consumer.

Creates a new private fictional workspace; never calls apply, sync, or ADO. Requires
an already built Twig CLI. No credentials inherited. Results retained with --output.
"""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import tempfile

REPO = Path(__file__).resolve().parents[1]


def check_terminal(command, workspace, env, root):
    """Exercise real terminal detection and Details/Back/Cancel, not mocked readers."""
    import fcntl
    import pty
    import select
    import struct
    import termios
    import time
    master, slave = pty.openpty()
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', 50, 100, 0, 0))
    terminal_env = dict(env, TERM='xterm-256color', COLORTERM='truecolor')
    terminal_env.pop('NO_COLOR', None)
    proc = subprocess.Popen(command + ['proposal', 'preview', '--file', 'proposal.json', '--interactive'],
                            cwd=workspace, env=terminal_env, stdin=slave, stdout=slave, stderr=slave)
    os.close(slave)
    captured = bytearray()
    sent = 0
    deadline = time.monotonic() + 45
    try:
        while time.monotonic() < deadline:
            if select.select([master], [], [], 0.2)[0]:
                try:
                    chunk = os.read(master, 65536)
                except OSError:
                    break
                if not chunk:
                    break
                captured.extend(chunk)
            prompt_count = captured.count(b'Review only')
            if prompt_count > sent and sent < 3:
                os.write(master, [b'd\n', b'b\n', b'cancel\n'][sent])
                sent += 1
            if proc.poll() is not None:
                break
        proc.wait(timeout=3)
    finally:
        if proc.poll() is None:
            proc.kill()
            proc.wait()
        os.close(master)
        (root / 'interactive.ansi').write_bytes(captured)
    output = captured.decode('utf-8', errors='replace')
    assert proc.returncode == 0 and sent == 3, output
    assert 'full' in output and 'Not applied' in output, output
    assert 'Change Proposal review' not in output, output
    assert 'digest:' not in output and 'workspace:' not in output, output
    assert '(ad hoc)' not in output and '[0]' not in output and 'op-101' not in output, output
    assert '\x1b[?1049h' not in output and '\x1b[?47h' not in output, 'Unexpected full-screen UI'
    assert '\x1b[' in output, 'Real terminal did not emit styled output'
    assert any(0xe000 <= ord(c) <= 0xf8ff or 0xf0000 <= ord(c) <= 0xffffd for c in output), 'Configured Nerd Font icons did not reach the real presenter'
    return True


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--binary', required=True, type=Path, help='Built twig apphost or Twig.dll')
    ap.add_argument('--output', type=Path)
    args = ap.parse_args()
    root = args.output.resolve() if args.output else Path(tempfile.mkdtemp(prefix='twig-presenter-smoke-'))
    root.mkdir(parents=True, exist_ok=True)
    workspace = root / 'workspace'
    if workspace.exists():
        raise RuntimeError('Refusing reused fixture workspace; choose a new --output')
    (workspace / '.twig').mkdir(parents=True)
    home = root / 'home'
    home.mkdir(exist_ok=True)
    (workspace / 'twig.json').write_text(json.dumps({'organization': 'fixture.invalid', 'project': 'PresenterSmoke'}))
    binary = args.binary.resolve()
    command = ['dotnet', str(binary)] if binary.suffix == '.dll' else [str(binary)]
    env = {k: v for k, v in os.environ.items() if k in {'PATH', 'LANG', 'LC_ALL', 'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'LD_LIBRARY_PATH'}}
    env.update(HOME=str(home), USERPROFILE=str(home), XDG_CONFIG_HOME=str(home / '.config'),
               TERM='dumb', NO_COLOR='1', DOTNET_CLI_TELEMETRY_OPTOUT='1')

    def run(label, argv, expected: int | None = 0):
        p = subprocess.run(command + argv, cwd=workspace, env=env, capture_output=True, text=True, timeout=45)
        (root / (label + '.stdout')).write_text(p.stdout)
        (root / (label + '.stderr')).write_text(p.stderr)
        if expected is not None and p.returncode != expected:
            raise AssertionError(f'{label}: exit {p.returncode}, expected {expected}\n{p.stderr}\n{p.stdout}')
        return p

    run('initialize', ['show', '101', '-o', 'json'], expected=None)
    run('configure-icons', ['config', 'display.icons', 'nerd', '-o', 'json'])
    cache = workspace / '.twig/cache/twig.db'
    if not cache.exists():
        raise AssertionError('CLI did not initialize the private cache')
    before = '<p>' + 'Previously reviewed text. ' * 120 + '</p>'
    after = '<p>' + 'Replacement review text. ' * 120 + 'FINALBODYMARKER</p>'
    rows = [
        (101, 'Epic', 'Review changes', 'Active', None, 'Reviewer', {'System.Description': 'Root'}),
        (102, 'Feature', 'Human approval', 'Active', 101, None, {'System.Description': 'Feature'}),
        (103, 'Task', 'Field review', 'New', 102, 'Reviewer', {}),
        (104, 'Bug', 'Long description', 'Active', 102, 'Reviewer', {'System.Description': before}),
        (105, 'Task', 'Explicit empty values', 'New', 102, None, {'System.Description': ''}),
        (106, 'Task', 'Tags and assignment', 'New', 102, 'Reviewer', {'System.Tags': 'review;terminal'}),
    ]
    with sqlite3.connect(cache) as db:
        # Faithful fictional process metadata: Nerd mode intentionally falls back
        # to Unicode when these ADO icon IDs have not been cached.
        for name, icon in [('Epic', 'icon_crown'), ('Feature', 'icon_trophy'), ('Task', 'icon_check_box'), ('Bug', 'icon_insect')]:
            db.execute('INSERT INTO process_types (type_name, states_json, icon_id, last_synced_at) VALUES (?, ?, ?, ?)',
                       (name, '[]', icon, '2026-01-01T00:00:00Z'))
        for ref, label, kind in [('System.Title', 'Title', 'String'), ('System.AssignedTo', 'Assigned to', 'String'),
                                 ('System.State', 'State', 'String'), ('System.Description', 'Description', 'Html'), ('System.Tags', 'Tags', 'String')]:
            db.execute('INSERT INTO field_definitions (ref_name,display_name,data_type,last_synced_at) VALUES (?,?,?,?)',
                       (ref, label, kind, '2026-01-01T00:00:00Z'))
        for ident, kind, title, state, parent, assignee, fields in rows:
            db.execute('INSERT INTO work_items (id,type,title,state,parent_id,assigned_to,iteration_path,area_path,revision,is_seed,fields_json,is_dirty,last_synced_at) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)',
                       (ident, kind, title, state, parent, assignee, 'PresenterSmoke', 'PresenterSmoke', 1, 0, json.dumps(fields), 0, '2026-01-01T00:00:00+00:00'))
    changes = [{'System.Title': 'Readable review changes'}, {'System.AssignedTo': 'Reviewer'},
               {'System.State': 'Active'}, {'System.Description': after}, {'System.Description': None},
               {'System.Tags': 'review;terminal;accessibility', 'System.AssignedTo': None}]
    proposal = {'version': 1, 'workspace': {'organization': 'fixture.invalid', 'project': 'PresenterSmoke'},
                'operations': [{'id': f'op-{row[0]}', 'kind': 'batch', 'workItemId': row[0], 'expectedRevision': 1, 'fields': fields}
                               for row, fields in zip(rows, changes)]}
    (workspace / 'proposal.json').write_text(json.dumps(proposal, indent=2))
    preview = ['proposal', 'preview', '--file', 'proposal.json']
    raw = run('preview-json', preview + ['-o', 'json']).stdout
    payload = json.loads(raw)
    model = payload['reviewModel']
    assert len(model['affectedItems']) == 6 and len(model['operations']) == 6
    assert model['authorizationChoices'] == ['apply', 'revise', 'decline']
    effects = [c for op in model['operations'] for c in op['consequences']]
    assert len(effects) == 7
    desc = next(c for c in effects if c.get('to') == after)
    assert desc['before']['value'] == before and desc['before']['state'] == 'value'
    assert desc['textChange']['metric'] == 'common-affix-replacement-v1'
    brief = run('preview-brief', preview).stdout
    full = run('preview-full', preview + ['--full']).stdout
    for human in (brief, full):
        assert 'Change Proposal review' not in human
        assert 'digest:' not in human and 'workspace:' not in human
        assert 'recipe:' not in human and 'rationale:' not in human
        assert '(ad hoc)' not in human
        assert '[0]' not in human and 'op-101' not in human and 'expectedRevision' not in human
        assert 'change:' not in human and 'apply available:' not in human
        assert 'authorization choices' not in human and 'sign-off' not in human
        assert 'AFK-steered' not in human and 'human-steered' not in human
        assert 'blockers (0)' not in human and 'pending local changes (0)' not in human
    assert model['digest'] not in brief and model['digest'] not in full
    assert 'FINALBODYMARKER' not in brief and 'FINALBODYMARKER' in full
    minimal = run('preview-minimal', preview + ['-o', 'minimal', '--full']).stdout
    minimal_model = next(line.partition('=')[2] for line in minimal.splitlines() if line.startswith('reviewModel='))
    assert json.loads(minimal_model) == model, 'Minimal preview lost semantic model coverage'
    run('noninteractive-refusal', preview + ['--interactive'], expected=2)
    terminal_checked = check_terminal(command, workspace, env, root) if os.name == 'posix' else False
    # Same cached item, mismatched expected revision: never manufacture the before baseline.
    with sqlite3.connect(cache) as db:
        db.execute('UPDATE work_items SET revision=2 WHERE id=104')
    stale = json.loads(run('preview-stale', preview + ['-o', 'json']).stdout)['reviewModel']
    c = next(c for op in stale['operations'] for c in op['consequences'] if c.get('to') == after)
    assert c['before']['state'] == 'unknown' and c['before']['reason'] == 'revision-mismatch'
    assert c['textChange'] is None
    consumer = REPO / '.github/skills/twig-review-presenter/scripts/render_discord_review.py'
    spec = importlib.util.spec_from_file_location('discord_review_consumer', consumer)
    assert spec is not None and spec.loader is not None
    adapter = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(adapter)
    messages = adapter.render(payload)
    expanded = adapter.render(payload, full=True)
    (root / 'discord-brief.json').write_text(json.dumps(messages, ensure_ascii=False, indent=2))
    (root / 'discord-full.json').write_text(json.dumps(expanded, ensure_ascii=False, indent=2))
    assert all(len(m['content']) <= 1900 for m in messages + expanded)
    assert 'FINALBODYMARKER' not in '\n'.join(m['content'] for m in messages)
    assert 'FINALBODYMARKER' in '\n'.join(m['content'] for m in expanded)
    for ident in range(101, 107):
        assert f'#{ident}' in brief
        assert f'#{ident}' in '\n'.join(m['content'] for m in messages)
    with sqlite3.connect(cache) as db:
        for ref in ('Custom.BusinessPriority', 'Custom.SupportPriority'):
            db.execute('INSERT INTO field_definitions (ref_name,display_name,data_type,last_synced_at) VALUES (?,?,?,?)',
                       (ref, 'Priority', 'String', '2026-01-01T00:00:00Z'))
    collision = dict(proposal, operations=[{'id': 'ambiguous-labels', 'kind': 'batch', 'workItemId': 103, 'expectedRevision': 1,
                         'fields': {'Custom.BusinessPriority': 'high', 'Custom.SupportPriority': 'low'}}])
    (workspace / 'collision.json').write_text(json.dumps(collision))
    collision_args = ['proposal', 'preview', '--file', 'collision.json']
    collision_payload = json.loads(run('collision-json', collision_args + ['-o', 'json']).stdout)
    collision_human = run('collision-human', collision_args).stdout
    collision_discord = '\n'.join(m['content'] for m in adapter.render(collision_payload))
    for effect in collision_payload['reviewModel']['operations'][0]['consequences']:
        assert effect['field'] in effect['fieldLabel'], 'Ambiguous label was not resolved in shared model'
        assert effect['field'] in collision_human and effect['field'] in collision_discord
    with sqlite3.connect(workspace / '.twig/cache/pending.db') as db:
        journal = db.execute('SELECT state,confirmed_at FROM proposal_journals').fetchall()
        assert journal and all(s == 'Planned' and c is None for s, c in journal)
    result = {'passed': True, 'binary': str(binary), 'fixture': 'fictional offline cache',
              'operations': len(model['operations']), 'effects': len(effects), 'discordMessages': len(messages),
              'applyInvoked': False, 'journal': journal, 'terminalInteractiveChecked': terminal_checked, 'evidence': str(root)}
    (root / 'result.json').write_text(json.dumps(result, indent=2))
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
