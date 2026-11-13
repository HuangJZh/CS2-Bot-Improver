"""Validate the KV3 wiring and the contract with installed Valve subtrees."""
import json
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DATA = ROOT / 'addons/counterstrikesharp/plugins/BotAimImprover/retake'


def parse_kv3(text):
    text = re.sub(r'<!--.*?-->', '', text, flags=re.S)
    text = re.sub(r'//[^\n]*', '', text)
    tokens = re.findall(r'"(?:\\.|[^"\\])*"|[{}\[\]=,]|[^\s{}\[\]=,]+', text)
    cursor = 0

    def value():
        nonlocal cursor
        token = tokens[cursor]
        cursor += 1
        if token == '{':
            result = {}
            while tokens[cursor] != '}':
                key = tokens[cursor]
                cursor += 1
                assert tokens[cursor] == '=', 'Missing object assignment'
                cursor += 1
                result[key] = value()
            cursor += 1
            return result
        if token == '[':
            result = []
            while tokens[cursor] != ']':
                result.append(value())
                if tokens[cursor] != ']':
                    assert tokens[cursor] == ',', 'Missing array separator'
                    cursor += 1
            cursor += 1
            return result
        if token.startswith('"'):
            return json.loads(token)
        return float(token)

    result = value()
    assert cursor == len(tokens), 'Unexpected trailing data'
    return result


def walk(node):
    if isinstance(node, dict):
        yield node
        for child in node.values():
            yield from walk(child)
    elif isinstance(node, list):
        for child in node:
            yield from walk(child)


class RetakeTreeTests(unittest.TestCase):
    def test_actual_valve_modules_are_referenced(self):
        tree = parse_kv3((DATA / 'retake.kv3').read_text())
        references = {n['file'] for n in walk(tree) if n.get('type') == 'subtree'}
        expected = {f'scripts/ai/retakes/modules/bt_{name}.kv3' for name in (
            'defuse_planted_bomb', 'hide_near_planted_bomb',
            'hold_near_planted_bomb', 'watch_approach_points')}
        self.assertTrue(expected <= references)
        self.assertIn('scripts/ai/modules/bt_attack.kv3', references)

    def test_config_does_not_double_charge_human_reaction(self):
        settings = parse_kv3((DATA / 'retake_config.kv3').read_text())['default']
        self.assertEqual(settings['reaction_time'], 0)
        self.assertEqual(len(settings['attack']), 13)
        self.assertTrue(all(len(row) == 5 for row in settings['attack']))
        self.assertGreaterEqual(settings['attack'][1][0], .5, 'Native pistol input must not cut off a charge/windup every 0.2s')
        tree = parse_kv3((DATA / 'retake.kv3').read_text())
        self.assertEqual(tree['config'], 'scripts/ai/custom_improver/retake_config.kv3')

    def test_invalid_array_is_rejected(self):
        with self.assertRaises(AssertionError):
            parse_kv3('{ children = [ {} {} ] }')

    def test_damage_reacquisition_cannot_preempt_established_attack(self):
        tree = parse_kv3((DATA / 'retake.kv3').read_text())
        selector = next(n for n in walk(tree) if n.get('type') == 'selector')
        self.assertEqual(selector['children'][0]['file'], 'scripts/ai/modules/bt_attack.kv3')
        self.assertEqual(selector['children'][1]['entity_type_filter'], 'DAMAGE')



if __name__ == '__main__':
    unittest.main()
