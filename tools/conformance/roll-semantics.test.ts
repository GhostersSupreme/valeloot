import { describe, expect, test } from 'bun:test';
import { formatLootFilter, parseLootFilter } from '../../src/filter/loot-dsl.ts';
import { matchesCondition } from '../../src/filter/loot-filter.ts';
import type { OwnedGear } from '../../src/filter/types.ts';

const artifact: OwnedGear = {
  uid: 'artifact-1',
  itemId: 'fixture-artifact',
  name: 'Fixture Artifact',
  slotType: 'Artifact',
  refine: 0,
  lines: [
    { stat: 'Vit', base: 3, rollPct: 80, isChaos: false, over: false },
    { stat: 'HpMult', base: 2, rollPct: 70, isChaos: false, over: false },
    { stat: 'MatkMult', base: 2, rollPct: 60, isChaos: false, over: false },
  ],
  topRolls: 3,
  highRolls: 0,
  avgRollPct: 70,
  favorite: false,
};

function parseWhen(...lines: string[]) {
  const parsed = parseLootFilter(['Show "fixture"', ...lines.map((line) => `    ${line}`)].join('\n'));
  expect(parsed.errors).toEqual([]);
  return parsed.rules[0]!.when;
}

describe('displayed and raw roll semantics', () => {
  test('artifact displayed maxima satisfy the reported rule', () => {
    const when = parseWhen(
      'Type Artifact',
      'Stat Vit >= 3',
      'Stat HpMult >= 2',
      'Stat MatkMult >= 2',
    );
    expect(matchesCondition(artifact, when, {})).toBe(true);
  });

  test('a lower displayed value fails while percent form remains raw', () => {
    const low = { ...artifact, lines: [{ ...artifact.lines[0]!, base: 2 }] };
    expect(matchesCondition(low, parseWhen('Stat Vit >= 3'), {})).toBe(false);
    expect(matchesCondition(artifact, parseWhen('Stat Vit >= 90%'), {})).toBe(false);
  });

  test('TopRolls uses displayed maxima and HighRolls uses Threshold', () => {
    expect(matchesCondition(artifact, parseWhen('TopRolls >= 3'), { threshold: 90 })).toBe(true);
    expect(matchesCondition(artifact, parseWhen('HighRolls >= 1'), { threshold: 90 })).toBe(false);
    expect(matchesCondition({ ...artifact, topRolls: null }, parseWhen('TopRolls <= 0'), {})).toBe(false);
  });

  test('legacy AvgRoll remains an alias and formatting emits AvgRollPct', () => {
    const legacy = parseLootFilter('Show "legacy"\n    AvgRoll < 71');
    const explicit = parseLootFilter('Show "explicit"\n    AvgRollPct < 71');
    expect(legacy.errors).toEqual([]);
    expect(explicit.errors).toEqual([]);
    expect(legacy.rules[0]!.when.maxAvgRollPct).toBe(explicit.rules[0]!.when.maxAvgRollPct);
    expect(formatLootFilter(legacy)).toContain('AvgRollPct');
    expect(formatLootFilter(legacy)).not.toContain('AvgRoll   ');
  });
});
