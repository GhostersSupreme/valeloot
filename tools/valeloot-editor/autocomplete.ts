export type CompletionKind =
  | 'keyword'
  | 'cond'
  | 'item'
  | 'stat'
  | 'type'
  | 'level'
  | 'background'
  | 'border'
  | 'sound'
  | 'statOperator'
  | 'operator'
  | 'minimumOperator'
  | 'statValue'
  | 'countValue'
  | 'percentValue'
  | 'refineValue'
  | 'thresholdValue'
  | 'tag';

export interface CompletionContext {
  readonly kind: CompletionKind;
  readonly token: string;
  readonly from: number;
}
export interface TextEdit {
  readonly text: string;
  readonly selectionStart: number;
  readonly selectionEnd: number;
}

const INDENT = '    ';

/** Apply editor-style Tab or Shift+Tab behavior to a textarea value and selection. */
export function editIndent(text: string, start: number, end: number, outdent: boolean): TextEdit {
  if (!outdent && start === end) {
    return {
      text: text.slice(0, start) + INDENT + text.slice(end),
      selectionStart: start + INDENT.length,
      selectionEnd: end + INDENT.length,
    };
  }

  const firstLineStart = text.lastIndexOf('\n', Math.max(0, start - 1)) + 1;
  const selectionAnchor = end > start && text[end - 1] === '\n' ? end - 1 : end;
  const lastLineStart = text.lastIndexOf('\n', Math.max(0, selectionAnchor - 1)) + 1;
  const nextBreak = text.indexOf('\n', lastLineStart);
  const blockEnd = nextBreak < 0 ? text.length : nextBreak;
  const lines = text.slice(firstLineStart, blockEnd).split('\n');

  if (!outdent) {
    const replacement = lines.map((line) => INDENT + line).join('\n');
    const added = INDENT.length * lines.length;
    return {
      text: text.slice(0, firstLineStart) + replacement + text.slice(blockEnd),
      selectionStart: start + INDENT.length,
      selectionEnd: end + added,
    };
  }

  let removed = 0;
  let removedFromFirst = 0;
  const replacement = lines.map((line, index) => {
    const prefix = /^(?: {1,4}|\t)/.exec(line)?.[0] || '';
    if (index === 0) removedFromFirst = prefix.length;
    removed += prefix.length;
    return line.slice(prefix.length);
  }).join('\n');
  return {
    text: text.slice(0, firstLineStart) + replacement + text.slice(blockEnd),
    selectionStart: Math.max(firstLineStart, start - removedFromFirst),
    selectionEnd: Math.max(firstLineStart, end - removed),
  };
}


const BOUNDED_COUNTS: Record<string, true> = {
  statmatches: true,
  toprolls: true,
  highrolls: true,
};

/** Classify the token before the caret by the value the filter grammar expects there. */
export function completionContextAt(text: string, caret: number): CompletionContext | null {
  const upto = text.slice(0, caret);
  const lineStart = upto.lastIndexOf('\n') + 1;
  const line = upto.slice(lineStart);
  if (line.trimStart().startsWith('#')) return null;

  const indented = /^\s/.test(line);
  const words = line.trimStart().split(/\s+/);
  const first = (words[0] || '').toLowerCase();
  const tail = (kind: CompletionKind, token = ''): CompletionContext => ({
    kind,
    token,
    from: lineStart + line.length - token.length,
  });

  if (!indented) {
    if (words.length <= 1) return tail('keyword', words[0]);
    if (first === 'alwaysshow' || first === 'alwayshide') return listContext('item', line, lineStart);
    if (first === 'threshold' && words.length === 2) return tail('thresholdValue', words[1]);
    return null;
  }

  if (words.length <= 1) return tail('cond', words[0]);
  if (first === 'name') return listContext('item', line, lineStart);
  if (first === 'type') return listContext('type', line, lineStart);

  if (first === 'stat' || first === 'requirestat') {
    if (words.length === 2) return tail('stat', words[1]);
    if (words.length === 3) return tail('statOperator', words[2]);
    if (words.length === 4) return tail('statValue', words[3]);
    return null;
  }

  if (BOUNDED_COUNTS[first]) {
    if (words.length === 2) return tail('operator', words[1]);
    if (words.length === 3) return tail('countValue', words[2]);
    return null;
  }
  if (first === 'avgrollpct' || first === 'avgroll') {
    if (words.length === 2) return tail('operator', words[1]);
    if (words.length === 3) return tail('percentValue', words[2]);
    return null;
  }
  if (first === 'refine') {
    if (words.length === 2) return tail('minimumOperator', words[1]);
    if (words.length === 3) return tail('refineValue', words[2]);
    return null;
  }

  if (first === 'highlight' && words.length === 2) return tail('level', words[1]);
  if (first === 'background' && words.length === 2) return tail('background', words[1]);
  if (first === 'border' && words.length === 2) return tail('border', words[1]);
  if (first === 'sound' && words.length === 2) return tail('sound', words[1]);
  if (first === 'tag' && words.length === 2) return tail('tag', words[1]);
  return null;
}


function listContext(kind: 'item' | 'type', line: string, lineStart: number): CompletionContext {
  const afterKeyword = line.replace(/^\s*\S+\s*/, '');
  const comma = afterKeyword.lastIndexOf(',');
  const afterComma = afterKeyword.slice(comma + 1);
  const whitespace = afterComma.length - afterComma.trimStart().length;
  const token = afterComma.slice(whitespace).replace(/^"/, '');
  return {
    kind,
    token,
    from: lineStart + line.length - token.length - (/^"/.test(afterComma.slice(whitespace)) ? 1 : 0),
  };
}
