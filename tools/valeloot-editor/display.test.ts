import { describe, expect, test } from 'bun:test';
import { formatPrintedValue } from './display.ts';

describe('printed stat values', () => {
  test('distinguishes negative values from unresolved facts', () => {
    expect(formatPrintedValue(3)).toBe('+3');
    expect(formatPrintedValue(-5)).toBe('-5');
    expect(formatPrintedValue(null)).toBe('<s>?</s>');
  });
});
