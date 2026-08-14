import { describe, expect, test } from 'bun:test';
import { completionContextAt, editIndent } from './autocomplete.ts';

const atEnd = (text: string) => completionContextAt(text, text.length);

describe('completionContextAt', () => {
  test('keeps completing catalog-backed values', () => {
    expect(atEnd('  Type fe')).toEqual({ kind: 'type', token: 'fe', from: 7 });
    expect(atEnd('  Stat Mp')).toEqual({ kind: 'stat', token: 'Mp', from: 7 });
    expect(atEnd('  Highlight ma')).toEqual({ kind: 'level', token: 'ma', from: 12 });
    expect(atEnd('AlwaysShow fo')).toEqual({ kind: 'item', token: 'fo', from: 11 });
  });

  test('continues through operators and typed values', () => {
    expect(atEnd('  Stat MpCost >')).toEqual({ kind: 'statOperator', token: '>', from: 14 });
    expect(atEnd('  Stat MpCost >= 1')).toEqual({ kind: 'statValue', token: '1', from: 17 });
    expect(atEnd('  StatMatches >= 2')).toEqual({ kind: 'countValue', token: '2', from: 17 });
    expect(atEnd('  AvgRollPct < 3')).toEqual({ kind: 'percentValue', token: '3', from: 15 });
    expect(atEnd('  Refine > 1')).toEqual({ kind: 'refineValue', token: '1', from: 11 });
    expect(atEnd('Threshold 9')).toEqual({ kind: 'thresholdValue', token: '9', from: 10 });
  });

  test('completes reusable tags but not free-form colors', () => {
    expect(atEnd('  Color #5')).toBeNull();
    expect(atEnd('  Tag FE')).toEqual({ kind: 'tag', token: 'FE', from: 6 });
  });

  test('completes the current value in comma-separated lists', () => {
    expect(atEnd('  Type Feet, he')).toEqual({ kind: 'type', token: 'he', from: 13 });
    expect(atEnd('  Name "Focused Tops", "ca')).toEqual({ kind: 'item', token: 'ca', from: 23 });
  });

  test('does not suggest inside comments or after complete scalar values', () => {
    expect(atEnd('  # Stat Mp')).toBeNull();
    expect(atEnd('  Highlight mark extra')).toBeNull();
  });
});

describe('editIndent', () => {
  test('inserts one indentation level at a caret', () => {
    expect(editIndent('Show "rule"\n', 12, 12, false)).toEqual({
      text: 'Show "rule"\n    ',
      selectionStart: 16,
      selectionEnd: 16,
    });
  });

  test('indents every selected line', () => {
    const text = '  Stat Atk\n    Stat Crit';
    expect(editIndent(text, 2, text.length, false)).toEqual({
      text: '      Stat Atk\n        Stat Crit',
      selectionStart: 6,
      selectionEnd: text.length + 8,
    });
  });

  test('dedents spaces and tabs without moving focus', () => {
    const text = '    Stat Atk\n\tStat Crit';
    expect(editIndent(text, 0, text.length, true)).toEqual({
      text: 'Stat Atk\nStat Crit',
      selectionStart: 0,
      selectionEnd: text.length - 5,
    });
    expect(editIndent('    Stat Atk', 12, 12, true)).toEqual({
      text: 'Stat Atk',
      selectionStart: 8,
      selectionEnd: 8,
    });
  });
});
