#!/usr/bin/env python3
"""Offline reference consumer: Twig preview JSON -> Discord message payloads.

No network, credentials, approval, or mutation. Exact-value expansion is explicit.
"""
import argparse
import json
import re
import sys
import unicodedata
from urllib.parse import urlparse

KINDS = {'batch', 'add-link', 'remove-link', 'publish-seed', 'delete'}
EFFECTS = {'field-set', 'field-clear', 'link-add', 'link-remove', 'seed-publish', 'work-item-delete'}
ICONS = {'Epic': '👑', 'Feature': '🏆', 'Task': '☑', 'Bug': '🐛'}


def escape(value):
    value = ''.join(c if c in '\n\t' or unicodedata.category(c) not in {'Cc', 'Cf'} else f'\\u{ord(c):04x}' for c in str(value))
    value = value.replace('@', '@\u200b')
    return re.sub(r'([\\`*_~|<>#\[\]])', r'\\\1', value).replace('\n', '\n> ')


def value_text(value):
    if value is None:
        return '(absent)'
    if value == '':
        return '(empty string)'
    return escape(json.dumps(value, ensure_ascii=False))


def check_model(payload):
    if not isinstance(payload, dict):
        raise ValueError('Expected a complete preview object')
    model = payload.get('reviewModel', payload)
    if not isinstance(model, dict) or model.get('model') != 'twig.change-proposal.review' or type(model.get('modelVersion')) is not int or model['modelVersion'] != 1:
        raise ValueError('Unsupported or missing complete review model')
    if not isinstance(model.get('digest'), str) or not re.fullmatch('[0-9a-f]{64}', model['digest']):
        raise ValueError('Missing or malformed proposal digest')
    if payload is not model and payload.get('digest') != model['digest']:
        raise ValueError('Preview/model digest mismatch')
    for key in ('affectedItems', 'operations', 'blockers', 'authorizationChoices'):
        if not isinstance(model.get(key), list):
            raise ValueError(f'Missing complete {key}')
    workspace = model.get('workspace', {})
    if not workspace.get('organization') or not workspace.get('project'):
        raise ValueError('Missing workspace')
    ids = [i['id'] for i in model['affectedItems']]
    if len(ids) != len(set(ids)):
        raise ValueError('Duplicate affected item IDs')
    seen = set()
    for n, op in enumerate(model['operations']):
        if op.get('ordinal') != n or op.get('kind') not in KINDS or not op.get('opId') or op['opId'] in seen:
            raise ValueError('Incomplete, unordered or unsupported operation')
        seen.add(op['opId'])
        if not isinstance(op.get('preconditions'), list) or not isinstance(op.get('consequences'), list):
            raise ValueError('Missing operation safety data')
        allowed_effects = {'batch': {'field-set', 'field-clear'}, 'add-link': {'link-add'},
                           'remove-link': {'link-remove'}, 'publish-seed': {'seed-publish'},
                           'delete': {'work-item-delete'}}[op['kind']]
        if not op['consequences'] or any(c.get('kind') not in allowed_effects for c in op['consequences']):
            raise ValueError('Missing or incompatible consequences; refuse partial presentation')
        if op['kind'] != 'batch' and len(op['consequences']) != 1:
            raise ValueError('Expected exactly one consequence for this operation')
        required = 'expectedFingerprint' if op['kind'] == 'publish-seed' else 'expectedRevision'
        preconditions = [p for p in op['preconditions'] if p.get('kind') == required]
        if len(preconditions) != 1:
            raise ValueError('Missing or duplicate operation precondition')
        expected = str(preconditions[0]['value'])
        if required == 'expectedRevision' and (not expected.isdecimal() or int(expected) <= 0):
            raise ValueError('Invalid expected revision')
        if required == 'expectedFingerprint' and not re.fullmatch('[0-9a-f]{64}', expected):
            raise ValueError('Invalid expected fingerprint')
        for c in op['consequences']:
            before = c.get('before')
            metric = c.get('textChange')
            if metric is not None:
                if not isinstance(metric, dict) or metric.get('metric') != 'common-affix-replacement-v1' or metric.get('unit') != 'unicode-scalars':
                    raise ValueError('Unsupported text-change metric')
                if any(type(metric.get(k)) is not int or metric[k] < 0 for k in ('inserted', 'removed')):
                    raise ValueError('Text-change counts must be nonnegative integers')
                if not isinstance(before, dict) or before.get('state') not in {'value', 'absent'} or c.get('field', '').casefold() != 'system.description':
                    raise ValueError('Text-change metric requires a known description baseline')
            if before is not None:
                if not isinstance(before, dict) or before.get('state') not in {'value', 'absent', 'unknown'}:
                    raise ValueError('Invalid before-value observation')
                if before['state'] in {'value', 'absent'}:
                    if type(before.get('revision')) is not int or str(before['revision']) != expected or before.get('reason'):
                        raise ValueError('Inconsistent before revision/provenance; refuse stale baseline')
                    if before['state'] == 'value' and not isinstance(before.get('value'), str):
                        raise ValueError('Known value must be an exact string')
                    if before['state'] == 'absent' and before.get('value') is not None:
                        raise ValueError('Absent baseline cannot carry a string value')
                if before['state'] == 'unknown' and c.get('textChange') is not None:
                    raise ValueError('Cannot display a metric for an unknown baseline')
            if c['kind'] in {'field-set', 'field-clear'}:
                if not isinstance(c.get('field'), str) or not c['field'] or 'to' not in c:
                    raise ValueError('Incomplete field consequence')
                if c['kind'] == 'field-set' and not isinstance(c['to'], str):
                    raise ValueError('Field set must carry exact string value')
                if c['kind'] == 'field-clear' and c['to'] is not None:
                    raise ValueError('Field clear must carry null')
            if c['kind'] in {'link-add', 'link-remove'} and (not c.get('relation') or not isinstance(c.get('otherId'), int)):
                raise ValueError('Incomplete link consequence')
            if c['kind'] == 'work-item-delete' and not isinstance(c.get('otherId'), int):
                raise ValueError('Incomplete deletion consequence')
    if any(c not in {'apply', 'revise', 'decline'} for c in model['authorizationChoices']):
        raise ValueError('Unknown authorization choice')
    return model


def heading(item, target):
    if item:
        label = f"{ICONS.get(item.get('type'), '•')} {escape(item.get('type') or 'Unknown type')} #{item['id']} — {escape(item.get('title') or 'Title unavailable')}"
        url = item.get('url')
        if isinstance(url, str):
            parsed = urlparse(url)
            if parsed.scheme == 'https' and parsed.hostname == 'dev.azure.com' and not any(c in url for c in '\n\r<>[]()'):
                label = f'[{label}](<{url}>)'
        return '**' + label + '**'
    if target.get('workItemId') is not None:
        return f"**#{target['workItemId']} — context unavailable**"
    seed = target.get('seed') or {}
    identity = escape(target.get('stagedIdentity') or '(identity unavailable)')
    display = f"{escape(seed.get('type') or 'Seed')} — {escape(seed.get('title') or 'Title unavailable')}"
    context = []
    if seed.get('state') is not None:
        context.append('state ' + escape(seed['state']))
    if seed.get('parentId') is not None:
        context.append('parent ' + escape(seed['parentId']))
    extra = ('\nCached context: ' + '; '.join(context)) if context else ''
    return f"**Cached draft: {display}**\nStaged identity: {identity} (not published){extra}\n*Cached display is not fingerprint-attested publication content.*"


def effect_line(effect, full):
    kind = effect['kind']
    if kind in {'field-set', 'field-clear'}:
        field = effect.get('fieldLabel') or effect['field']
        # Optional enrichment is never inferred from null alone.
        before = '(before unavailable)'
        observation = effect.get('before')
        if observation:
            state = observation.get('state')
            if state == 'value':
                before = value_text(observation['value'])
            elif state == 'absent':
                before = '(absent)'
            elif state == 'unknown':
                before = '(before unavailable: ' + escape(observation.get('reason') or 'unknown') + ')'
            else:
                raise ValueError('Unsupported before-value observation')
        if not full and effect['field'].casefold() == 'system.description':
            action = 'clear description' if kind == 'field-clear' else 'set description'
            metric = effect.get('textChange')
            if metric:
                if metric.get('metric') != 'common-affix-replacement-v1' or metric.get('unit') != 'unicode-scalars':
                    raise ValueError('Unsupported text-change metric')
                info = f"+{metric['inserted']} / −{metric['removed']} Unicode scalars; common-affix replacement, not minimal edits"
            else:
                info = 'change size unavailable; prior baseline unknown'
            return f"> **{escape(field)}:** {action} — {info}. Exact values: expand details."
        after = '(clear)' if kind == 'field-clear' else value_text(effect.get('to'))
        line = f"> **{escape(field)}:** {before} → {after}"
        if full and observation:
            line += f"\n> Baseline: {escape(observation.get('source') or 'source unspecified')}, revision {escape(observation.get('revision'))}"
        return line
    if kind in {'link-add', 'link-remove'}:
        return f"> **Link:** {'add' if kind == 'link-add' else 'remove'} {escape(effect['relation'])} → #{effect['otherId']}"
    if kind == 'work-item-delete':
        return f"> **DELETE work item #{effect['otherId']}**"
    return '> **Publish staged seed** — creates a work item; no published ID yet.'


def units(value):
    # Conservative transport budget: supplementary characters occupy two UTF-16 units.
    return len(value.encode('utf-16-le', errors='replace')) // 2


def render(payload, full=False, limit=1900):
    model = check_model(payload)
    by_id = {i['id']: i for i in model['affectedItems']}
    workspace = model['workspace']
    blocks = [f"**Review proposed changes**\n{escape(workspace['organization'])}/{escape(workspace['project'])}\n{len(model['operations'])} operations · {len(model['affectedItems'])} affected items"]
    if model.get('rationale'):
        blocks.append('**Rationale:** ' + escape(model['rationale']))
    context = model.get('contextItems') or []
    for item in context:
        blocks.append('**Hierarchy context only:** ' + heading(item, {'workItemId': item['id']}))
    for item in model['affectedItems']:
        if item.get('role') == 'peer':
            blocks.append('**Link peer:** ' + heading(item, {'workItemId': item['id']}))
    for op in model['operations']:
        target = op['target']
        item = by_id.get(target.get('workItemId'))
        lines = [heading(item, target),
                 f"Operation {op['ordinal'] + 1}: {escape(op['opId'])} — {escape(op['summary'])}"]
        if item and item.get('parentId') is not None:
            lines.append(f"Parent: #{item['parentId']}")
        if item and item.get('state'):
            lines.append('Current state: ' + escape(item['state']))
        lines += [effect_line(c, full) for c in op['consequences']]
        lines += [f"> **Requires:** {escape(p['kind'])} = {escape(p['value'])}" for p in op['preconditions']]
        blocks.append('\n'.join(lines))
    for blocker in model['blockers']:
        blocks.append('**BLOCKED:** ' + escape(blocker['detail']))
    choices = ', '.join(escape(x) for x in model['authorizationChoices']) or '(none)'
    blocks.append(f"**Available decisions:** {choices}\n*This renderer does not authorize or apply. Details must use the retained review.*")
    # Split without dropping any data. Every message identifies the exact proposal.
    footer = '\n\nProposal `' + model['digest'] + '`'
    capacity = limit - len(footer) - 50
    if capacity < 200:
        raise ValueError('Message budget too small')
    chunks = []
    current = ''
    for block_number, block in enumerate(blocks, 1):
        fragments = [block]
        if units(block) > capacity:
            # Quote every fragment and identify its source block. Escape pairs are
            # atomic: a split cannot turn escaped user text into active Markdown.
            fragments = []
            tokens = re.finditer(r'\\.|\\$|[^\\]', block, flags=re.S)
            piece = []
            size = 0
            budget = capacity - 70
            for match in tokens:
                token = match.group()
                weight = units(token) + 2 * token.count('\n')
                if piece and size + weight > budget:
                    fragments.append(''.join(piece))
                    piece, size = [], 0
                piece.append(token)
                size += weight
            if piece:
                fragments.append(''.join(piece))
            fragments = [f'Review block {block_number}, part {i}\n> ' + fragment.replace('\n', '\n> ')
                         for i, fragment in enumerate(fragments, 1)]
        for fragment in fragments:
            if current and units(current) + units(fragment) + 2 > capacity:
                chunks.append(current)
                current = ''
            current += ('\n\n' if current else '') + fragment
    if current:
        chunks.append(current)
    return [{'content': f'Review {n}/{len(chunks)}\n' + text + footer,
             'allowed_mentions': {'parse': []}, 'flags': 4}
            for n, text in enumerate(chunks, 1)]


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('preview', help='Saved Twig proposal preview JSON')
    parser.add_argument('--full', action='store_true')
    args = parser.parse_args()
    try:
        with open(args.preview, encoding='utf-8') as stream:
            result = render(json.load(stream), args.full)
        json.dump({'messages': result, 'appliesChanges': False}, sys.stdout, ensure_ascii=False, indent=2)
        print()
    except (ValueError, KeyError, TypeError) as exc:
        parser.exit(1, f'Cannot present review: {exc}\n')
