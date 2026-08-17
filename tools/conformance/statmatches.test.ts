import { describe, expect, test } from 'bun:test';
import { formatLootFilter, parseLootFilter } from '../../src/filter/loot-dsl.ts';
import {
  matchesCondition,
  normalizeLootRules,
  type LootCondition,
} from '../../src/filter/loot-filter.ts';
import type { OwnedGear } from '../../src/filter/types.ts';

const item: OwnedGear = {
  uid: null,
  itemId: 'fixture',
  name: 'fixture',
  slotType: 'Chest',
  refine: 0,
  lines: [
    { stat: 'Str', base: 3, rollPct: 80, isChaos: false, over: false },
    { stat: 'Vit', base: 3, rollPct: 60, isChaos: false, over: false },
  ],
  topRolls: 0,
  highRolls: 0,
  avgRollPct: 70,
  favorite: false,
  hasChaos: false,
};

function condition(comparison: string): LootCondition {
  const parsed = parseLootFilter([
    'Show "contract"',
    '    Stat Str >= 70%',
    '    Stat Vit >= 50%',
    '    Stat Agi >= 50%',
    `    StatMatches ${comparison}`,
  ].join('\n'));
  expect(parsed.errors).toEqual([]);
  return parsed.rules[0]!.when;
}

describe('StatMatches', () => {
  test.each([
    ['>= 2', true],
    ['> 2', false],
    ['= 2', true],
    ['<= 1', false],
    ['< 3', true],
  ] as const)('%s counts successful Stat conditions', (comparison, expected) => {
    expect(matchesCondition(item, condition(comparison), {})).toBe(expected);
  });

  test.each([
    ['StatMatches >= -1', 'StatMatches cannot be negative'],
    ['StatMatches >= 4', 'cannot require 4 matches'],
    ['StatMatches >= 3\n    StatMatches <= 1', 'minimum 3 cannot exceed maximum 1'],
  ] as const)('rejects invalid bounds: %s', (lines, message) => {
    const parsed = parseLootFilter([
      'Show "invalid"',
      '    Stat Str >= 1',
      '    Stat Vit >= 1',
      '    Stat Agi >= 1',
      ...lines.split('\n').map((line) => `    ${line.trim()}`),
    ].join('\n'));
    expect(parsed.rules).toHaveLength(0);
    expect(parsed.errors.some((error) => error.message.includes(message))).toBe(true);
  });

  test('null persisted bounds do not override AnyStat', () => {
    const rules = normalizeLootRules([{
      id: 'persisted',
      name: 'persisted',
      enabled: true,
      color: '#4ade80',
      when: {
        stats: [{ stat: 'Agi', minValue: 1 }],
        statMode: 'any',
        minStatMatches: null,
        maxStatMatches: null,
      },
    }]);

    expect(rules[0]!.when.minStatMatches).toBeUndefined();
    expect(rules[0]!.when.maxStatMatches).toBeUndefined();
    expect(matchesCondition(item, rules[0]!.when, {})).toBe(false);
  });
});

describe('RequireStat', () => {
  const text = [
    'Show "Farming Target"',
    '    Name "Flame Tongue Kunai"',
    '    RequireStat Agi >= 3',
    '    Stat DoubleAttack >= 20',
    '    Stat DamageMagic >= 5',
    '    Stat MatkMult >= 5',
    '    Stat Matk >= 5',
    '    StatMatches >= 2',
  ].join('\n');

  const farmingItem = (...stats: Array<[string, number]>): OwnedGear => ({
    ...item,
    name: 'Flame Tongue Kunai',
    lines: stats.map(([stat, base]) => ({
      stat,
      base,
      rollPct: 50,
      isChaos: false,
      over: false,
    })),
  });

  test.each([
    [[['Agi', 3], ['DoubleAttack', 20], ['Matk', 5]], true],
    [[['Agi', 3], ['DoubleAttack', 20]], false],
    [[['DoubleAttack', 20], ['DamageMagic', 5], ['Matk', 5]], false],
    [[['Agi', 2], ['DoubleAttack', 20], ['DamageMagic', 5]], false],
  ] as const)('requires AGI outside the candidate count: %j', (stats, expected) => {
    const parsed = parseLootFilter(text);
    expect(parsed.errors).toEqual([]);
    expect(matchesCondition(farmingItem(...stats), parsed.rules[0]!.when, {})).toBe(expected);
  });

  test('round-trips required stats without turning them into candidates', () => {
    const parsed = parseLootFilter(text);
    const formatted = formatLootFilter(parsed);
    const reparsed = parseLootFilter(formatted);

    expect(reparsed.errors).toEqual([]);
    expect(reparsed.rules[0]!.when.requiredStats).toEqual(parsed.rules[0]!.when.requiredStats);
    expect(reparsed.rules[0]!.when.stats).toEqual(parsed.rules[0]!.when.stats);
  });
});

describe('AnyOf', () => {
  const text = [
    'Show "AGI plus attack"',
    '    Stat Agi >= 1',
    '    AnyOf',
    '        Stat AtkMult >= 1',
    '        Stat Atk >= 1',
    '    Highlight glow',
  ].join('\n');

  const withStats = (...stats: string[]): OwnedGear => ({
    ...item,
    lines: stats.map((stat) => ({
      stat,
      base: 1,
      rollPct: 50,
      isChaos: false,
      over: false,
    })),
  });

  test.each([
    [['Agi', 'Atk'], true],
    [['Agi', 'AtkMult'], true],
    [['Agi'], false],
    [['Atk', 'AtkMult'], false],
  ] as const)('matches required stats plus grouped alternatives: %j', (stats, expected) => {
    const parsed = parseLootFilter(text);
    expect(parsed.errors).toEqual([]);
    expect(matchesCondition(withStats(...stats), parsed.rules[0]!.when, {})).toBe(expected);
  });

  test('requires one match from every AnyOf group', () => {
    const parsed = parseLootFilter([
      'Show "two groups"',
      '    AnyOf',
      '        Stat Atk >= 1',
      '        Stat AtkMult >= 1',
      '    AnyOf',
      '        Stat Vit >= 1',
      '        Stat Hp >= 1',
    ].join('\n'));

    expect(parsed.errors).toEqual([]);
    expect(matchesCondition(withStats('Atk', 'Vit'), parsed.rules[0]!.when, {})).toBe(true);
    expect(matchesCondition(withStats('Atk'), parsed.rules[0]!.when, {})).toBe(false);
  });

  test('formats grouped stats without losing their nesting', () => {
    const parsed = parseLootFilter(text);
    const reparsed = parseLootFilter(formatLootFilter(parsed));

    expect(reparsed.errors).toEqual([]);
    expect(reparsed.rules[0]!.when.anyOfStats).toEqual(parsed.rules[0]!.when.anyOfStats);
  });

  test.each([
    [
      ['Show "empty"', '    AnyOf', '    Stat Agi >= 1'].join('\n'),
      'requires at least one indented Stat',
    ],
    [
      ['Show "wrong child"', '    AnyOf', '        Refine >= 1'].join('\n'),
      'accepts only Stat conditions',
    ],
  ] as const)('rejects malformed groups', (filter, message) => {
    const parsed = parseLootFilter(filter);
    expect(parsed.rules).toHaveLength(0);
    expect(parsed.errors.some((error) => error.message.includes(message))).toBe(true);
  });
});

describe('Stat comparison bounds', () => {
  const gear = (base: number | null, rollPct = 50): OwnedGear => ({
    ...item,
    lines: base === null ? [] : [{
      stat: 'DamageFromMagic',
      base,
      rollPct,
      isChaos: false,
      over: false,
    }],
  });

  const parse = (body: string): LootCondition => {
    const parsed = parseLootFilter(`Show "negative"\n${body}`);
    expect(parsed.errors).toEqual([]);
    return parsed.rules[0]!.when;
  };

  test.each([
    ['<= -3', -3, true],
    ['<= -3', -5, true],
    ['<= -3', -2, false],
    ['< -3', -4, true],
    ['< -3', -3, false],
    ['= -3', -3, true],
    ['= -3', -4, false],
  ] as const)('Stat DamageFromMagic %s against %d', (comparison, base, expected) => {
    const when = parse(`    Stat DamageFromMagic ${comparison}`);
    expect(matchesCondition(gear(base), when, {})).toBe(expected);
  });

  test('does not satisfy an upper bound when the stat is missing', () => {
    const when = parse('    Stat DamageFromMagic <= -3');
    expect(matchesCondition(gear(null), when, {})).toBe(false);
  });

  test('applies inclusive and strict roll-percentage ceilings', () => {
    const inclusive = parse('    Stat DamageFromMagic <= 50%');
    const strict = parse('    Stat DamageFromMagic < 50%');
    expect(matchesCondition(gear(-3, 50), inclusive, {})).toBe(true);
    expect(matchesCondition(gear(-3, 51), inclusive, {})).toBe(false);
    expect(matchesCondition(gear(-3, 49), strict, {})).toBe(true);
    expect(matchesCondition(gear(-3, 50), strict, {})).toBe(false);
  });

  test('uses the same upper-bound predicate for RequireStat and AnyOf', () => {
    const required = parse('    RequireStat DamageFromMagic <= -3');
    expect(matchesCondition(gear(-3), required, {})).toBe(true);
    expect(matchesCondition(gear(-2), required, {})).toBe(false);

    const anyOf = parse([
      '    AnyOf',
      '        Stat DamageFromMagic <= -3',
      '        Stat DamageFromMelee <= -3',
    ].join('\n'));
    expect(matchesCondition(gear(-5), anyOf, {})).toBe(true);
    expect(matchesCondition(gear(-2), anyOf, {})).toBe(false);
  });

  test('round-trips upper and exact stat bounds without turning them into minimums', () => {
    const parsed = parseLootFilter([
      'Show "bounds"',
      '    RequireStat DamageFromMagic <= -3',
      '    Stat DamageFromMelee < -2',
      '    AnyOf',
      '        Stat DamageFromRanged = -5',
      '        Stat DamageFromPoison <= 50%',
    ].join('\n'));
    expect(parsed.errors).toEqual([]);

    const formatted = formatLootFilter(parsed);
    expect(formatted).toContain('RequireStat DamageFromMagic <= -3');
    expect(formatted).toContain('Stat      DamageFromMelee <= -3');
    expect(formatted).toContain('Stat  DamageFromRanged = -5');
    expect(formatLootFilter(parseLootFilter(formatted))).toBe(formatted);
  });
});
