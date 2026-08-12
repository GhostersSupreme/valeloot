import { expect, test } from 'bun:test';
import { formatLootFilter, parseLootFilter } from '../../src/filter/loot-dsl.ts';
import { matchLoot, normalizeLootRules } from '../../src/filter/loot-filter.ts';
import type { OwnedGear } from '../../src/filter/types.ts';

const item: OwnedGear = {
  uid: 'one', itemId: 'Card One', name: 'Card One', slotType: 'Card', refine: 0,
  lines: [], topRolls: 0, highRolls: 0, avgRollPct: null, favorite: false, hasChaos: false,
};

test('background modes survive parse, format, and matching', () => {
  const parsed = parseLootFilter([
    'Show "solid"',
    '    Type Card',
    '    Background fill',
    '    Border off',
    '',
    'Show "rotating"',
    '    Type Gem',
    '    Background holo',
    '',
    'Show "default"',
    '    Favorite',
  ].join('\n'));

  expect(parsed.errors).toEqual([]);
  expect(parsed.rules.map((rule) => [rule.background, rule.border])).toEqual([
    ['fill', false],
    ['holo', true],
    ['border', true],
  ]);
  expect(matchLoot(item, parsed.rules, { threshold: 90 })).toMatchObject({
    background: 'fill',
    border: false,
  });

  const formatted = formatLootFilter(parsed);
  expect(formatted).toContain('Background fill');
  expect(formatted).toContain('Background holo');
  expect(formatted).toContain('Border     off');
  expect(formatted).not.toContain('Border     on');
  expect(formatted).not.toContain('Background border');
  expect(parseLootFilter(formatted).rules.map((rule) => [rule.background, rule.border])).toEqual([
    ['fill', false],
    ['holo', true],
    ['border', true],
  ]);
});

test('stored rules normalize unknown backgrounds and non-boolean borders to defaults', () => {
  const rules = normalizeLootRules([
    { id: 'valid', name: 'valid', enabled: true, color: '#ffffff', background: 'holo', border: false, when: {} },
    { id: 'invalid', name: 'invalid', enabled: true, color: '#ffffff', background: 'foil', border: 'off', when: {} },
  ]);

  expect(rules.map((rule) => [rule.background, rule.border])).toEqual([
    ['holo', false],
    ['border', true],
  ]);
});
