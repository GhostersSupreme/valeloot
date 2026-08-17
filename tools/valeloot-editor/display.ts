/** Format a catalog-backed printed stat value for the editor's item facts. */
export function formatPrintedValue(printed: number | null): string {
  if (printed === null) return '<s>?</s>';
  return printed < 0 ? String(printed) : `+${printed}`;
}
