"""Unit fixtures, not live ADO responses. Run with Python unittest."""
import copy
import unittest
from render_discord_review import render


def fixture():
    return {'model': 'twig.change-proposal.review', 'modelVersion': 1, 'digest': 'a' * 64,
            'workspace': {'organization': 'example', 'project': 'Demo'},
            'affectedItems': [{'id': 101, 'type': 'Task', 'title': 'Demo item', 'state': 'New', 'role': 'target'}],
            'operations': [{'ordinal': 0, 'opId': 'op-one', 'kind': 'batch', 'target': {'workItemId': 101},
                            'summary': 'Set description', 'preconditions': [{'kind': 'expectedRevision', 'value': '7'}],
                            'consequences': [{'kind': 'field-set', 'field': 'System.Description', 'to': 'AFTER BODY',
                                              'before': {'state': 'value', 'value': 'BEFORE BODY', 'revision': 7},
                                              'textChange': {'metric': 'common-affix-replacement-v1', 'unit': 'unicode-scalars', 'inserted': 5, 'removed': 6}}]}],
            'blockers': [], 'authorizationChoices': ['apply', 'revise', 'decline']}


def text(model, full=False):
    return '\n'.join(m['content'] for m in render(model, full))


class DiscordReviewTests(unittest.TestCase):
    def test_brief_and_full_share_effect_and_precondition(self):
        model = fixture()
        brief, full = text(model), text(model, True)
        self.assertNotIn('AFTER BODY', brief)
        self.assertIn('set description', brief)
        self.assertIn('+5 / −6', brief)
        self.assertIn('"BEFORE BODY" → "AFTER BODY"', full)
        for output in [brief, full]:
            self.assertIn('expectedRevision = 7', output)
            self.assertIn(model['digest'], output)
            self.assertIn('apply, revise, decline', output)

    def test_invalid_metrics_refused_in_all_modes(self):
        for count in ['5\n# APPROVED BY DANIEL', -1, True]:
            for full in [False, True]:
                model = fixture()
                model['operations'][0]['consequences'][0]['textChange']['inserted'] = count
                with self.assertRaises(ValueError): render(model, full)
        model = fixture()
        del model['operations'][0]['consequences'][0]['before']
        with self.assertRaises(ValueError): render(model)

    def test_legacy_before_not_inferred(self):
        model = fixture()
        del model['operations'][0]['consequences'][0]['before']
        del model['operations'][0]['consequences'][0]['textChange']
        self.assertIn('(before unavailable)', text(model, True))

    def test_absent_empty_clear_distinct(self):
        model = fixture()
        c = model['operations'][0]['consequences'][0]
        c.update(field='System.Tags', before={'state': 'absent', 'value': None, 'revision': 7}, to='')
        c.pop('textChange')
        self.assertIn('(absent) → (empty string)', text(model))
        c.update(kind='field-clear', before={'state': 'value', 'value': '', 'revision': 7}, to=None)
        self.assertIn('(empty string) → (clear)', text(model))

    def test_unknown_revision_not_misrepresented(self):
        model = fixture()
        c = model['operations'][0]['consequences'][0]
        c['before'] = {'state': 'unknown', 'reason': 'revision-mismatch'}
        c['textChange'] = None
        self.assertIn('change size unavailable', text(model))
        self.assertIn('revision-mismatch', text(model, True))

    def test_unknown_version_effect_and_digest_refused(self):
        for mutation in ['version', 'kind', 'digest', 'missing-value']:
            model = fixture()
            payload = {'digest': model['digest'], 'reviewModel': model}
            if mutation == 'version': model['modelVersion'] = 99
            if mutation == 'kind': model['operations'][0]['consequences'][0]['kind'] = 'future-delete'
            if mutation == 'digest': payload['digest'] = 'b' * 64
            if mutation == 'missing-value': del model['operations'][0]['consequences'][0]['to']
            with self.assertRaises(ValueError): render(payload)

    def test_chunking_preserves_tail_and_neutralizes_mentions(self):
        model = fixture()
        model['operations'][0]['consequences'][0]['to'] = '@everyone **FORGED** ' + 'x' * 6000 + ' FINALSENTINEL'
        messages = render(model, True)
        self.assertGreater(len(messages), 1)
        self.assertTrue(all(len(m['content']) <= 1900 for m in messages))
        output = '\n'.join(m['content'] for m in messages)
        self.assertIn('FINALSENTINEL', output)
        self.assertNotIn('@everyone', output)
        self.assertTrue(all(m['allowed_mentions'] == {'parse': []} for m in messages))
        self.assertTrue(all(model['digest'] in m['content'] for m in messages))

    def test_unicode_budget_and_control_text(self):
        model = fixture()
        model['operations'][0]['consequences'][0]['to'] = '🧭' * 2200 + '\nsecond line\u202e\x1b'
        messages = render(model, True)
        self.assertTrue(all(len(m['content'].encode('utf-16-le')) // 2 <= 1900 for m in messages))
        output = '\n'.join(m['content'] for m in messages)
        self.assertIn('second line', output)
        self.assertIn('\\\\nsecond line', output)
        self.assertNotIn('\u202e', output)
        self.assertNotIn('\x1b', output)

    def test_incomplete_and_incompatible_effects_refused(self):
        for effects in [[], [{'kind': 'work-item-delete', 'otherId': 101}]]:
            model = fixture()
            model['operations'][0]['consequences'] = effects
            with self.assertRaises(ValueError): render(model)

    def test_declared_baseline_mismatch_refused(self):
        model = fixture()
        model['operations'][0]['consequences'][0]['before']['revision'] = 99
        with self.assertRaises(ValueError): render(model)

    def test_literal_markers_are_quoted(self):
        model = fixture()
        c = model['operations'][0]['consequences'][0]
        c.update(field='System.Title', before={'state': 'value', 'value': '(absent)', 'revision': 7}, to='(clear)')
        c.pop('textChange')
        output = text(model)
        self.assertIn('"(absent)" → "(clear)"', output)

    def test_chunk_continuation_cannot_create_heading(self):
        model = fixture()
        model['operations'][0]['consequences'][0]['to'] = 'x' * 1648 + '# APPROVED BY DANIEL' + 'z' * 200
        messages = render(model, True)
        self.assertFalse(any(line.startswith('# APPROVED') for m in messages for line in m['content'].splitlines()))

    def test_delete_link_blocker_and_choices_preserved(self):
        model = fixture()
        op = model['operations'][0]
        op.update(kind='remove-link', consequences=[{'kind': 'link-remove', 'relation': 'parent', 'otherId': 200}])
        second = copy.deepcopy(op)
        second.update(ordinal=1, opId='delete-op', kind='delete', consequences=[{'kind': 'work-item-delete', 'otherId': 101}])
        model['operations'].append(second)
        model['blockers'] = [{'kind': 'issue', 'detail': 'Review unavailable'}]
        model['authorizationChoices'] = ['revise', 'decline']
        output = text(model)
        self.assertIn('remove parent → #200', output)
        self.assertIn('DELETE work item #101', output)
        self.assertIn('BLOCKED', output)
        self.assertIn('Available decisions:** revise, decline', output)

    def test_seed_identity_and_context_not_published_id(self):
        model = fixture()
        model['affectedItems'] = []
        model['contextItems'] = [{'id': 99, 'type': 'Epic', 'title': 'Parent', 'role': 'context'}]
        op = model['operations'][0]
        op.update(kind='publish-seed', target={'stagedIdentity': 'seed-stable-id', 'seed': {'displayAlias': -1, 'type': 'Task', 'title': 'New seed'}},
                  consequences=[{'kind': 'seed-publish'}], preconditions=[{'kind': 'expectedFingerprint', 'value': 'b' * 64}])
        output = text(model)
        self.assertIn('0 affected items', output)
        self.assertIn('Task — New seed', output)
        self.assertIn('seed-stable-id (not published)', output)
        self.assertIn('Cached draft:', output)
        self.assertIn('not fingerprint-attested', output)
        self.assertIn('Hierarchy context only', output)
        self.assertNotIn('#-1', output)


if __name__ == '__main__':
    unittest.main()
