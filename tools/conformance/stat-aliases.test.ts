import { describe, expect, test } from 'bun:test';
import { parseLootFilter } from '../../src/filter/loot-dsl.ts';
import {
  STAT_ALIASES,
  canonicalStatName,
  matchesCondition,
  type LootCondition,
} from '../../src/filter/loot-filter.ts';
import type { OwnedGear } from '../../src/filter/types.ts';

function itemWith(stat: string): OwnedGear {
  return {
    uid: null,
    itemId: 'fixture',
    name: 'fixture',
    slotType: 'Chest',
    refine: 0,
    lines: [{ stat, base: 3, rollPct: 80, isChaos: false, over: false }],
    topRolls: 0,
    highRolls: 0,
    avgRollPct: 80,
  };
}

function parseStat(stat: string): LootCondition {
  const parsed = parseLootFilter(`Show "alias"\n    Stat ${stat} >= 70%`);
  expect(parsed.errors).toEqual([]);
  expect(parsed.rules).toHaveLength(1);
  return parsed.rules[0]!.when;
}

describe('player-facing stat aliases', () => {
  test.each(STAT_ALIASES)('$friendly resolves to canonical $internal', ({ friendly, internal }) => {
    const condition = parseStat(friendly.toLowerCase());
    expect(condition.stats?.[0]?.stat).toBe(internal);
    expect(matchesCondition(itemWith(internal.toUpperCase()), condition, {})).toBe(true);

    // Direct and persisted condition objects cross the same comparison boundary as parsed text.
    expect(matchesCondition(itemWith(internal), {
      stats: [{ stat: friendly.toUpperCase(), minRollPct: 70 }],
    }, {})).toBe(true);
    expect(canonicalStatName(internal.toLowerCase())).toBe(internal.toLowerCase());
  });

  test.each([
    ['AtkMult', 'Atk'],
    ['MatkMult', 'Matk'],
    ['DefMult', 'Def'],
    ['MdefMult', 'Mdef'],
    ['HpMult', 'Hp'],
    ['MpMult', 'Mp'],
    ['HpRegenMult', 'HpRegen'],
    ['MpRegenMult', 'MpRegen'],
  ] as const)('does not alias colliding %s to %s', (source, collision) => {
    expect(canonicalStatName(collision)).toBe(collision);
    expect(matchesCondition(itemWith(source), parseStat(collision), {})).toBe(false);
    expect(matchesCondition(itemWith(collision), parseStat(collision.toLowerCase()), {})).toBe(true);
  });
});
