/**
 * Безопасный вычислитель формул (зеркало серверной реализации в records-svc).
 * Поддерживает числа, поля записи, + - * / и скобки.
 */

type Token =
  | { kind: 'num'; value: number }
  | { kind: 'id'; name: string }
  | { kind: 'op'; op: string };

function tokenize(expr: string): Token[] | null {
  const tokens: Token[] = [];
  let i = 0;
  while (i < expr.length) {
    const ch = expr[i];
    if (/\s/.test(ch)) {
      i += 1;
      continue;
    }
    if (/[0-9.]/.test(ch)) {
      const m = /^\d+(\.\d+)?/.exec(expr.slice(i));
      if (!m) return null;
      tokens.push({ kind: 'num', value: parseFloat(m[0]) });
      i += m[0].length;
      continue;
    }
    if (/[A-Za-z_]/.test(ch)) {
      const m = /^[A-Za-z_][A-Za-z0-9_]*/.exec(expr.slice(i));
      if (!m) return null;
      tokens.push({ kind: 'id', name: m[0] });
      i += m[0].length;
      continue;
    }
    if ('+-*/()'.includes(ch)) {
      tokens.push({ kind: 'op', op: ch });
      i += 1;
      continue;
    }
    return null;
  }
  return tokens;
}

export function computeFormula(expr: string | null | undefined, values: Record<string, unknown>): number | null {
  const tokens = tokenize(String(expr || ''));
  if (!tokens || tokens.length === 0) return null;
  let idx = 0;

  const peek = () => tokens[idx] ?? null;

  function parseExpr(): number | null {
    let val = parseTerm();
    while (peek() && peek()!.kind === 'op' && (peek() as any).op === '+' || (peek() && peek()!.kind === 'op' && (peek() as any).op === '-')) {
      const op = (tokens[idx] as any).op;
      idx += 1;
      const rhs = parseTerm();
      if (val === null || rhs === null) val = null;
      else val = op === '+' ? val + rhs : val - rhs;
    }
    return val;
  }

  function parseTerm(): number | null {
    let val = parseFactor();
    while (peek() && peek()!.kind === 'op' && ((peek() as any).op === '*' || (peek() as any).op === '/')) {
      const op = (tokens[idx] as any).op;
      idx += 1;
      const rhs = parseFactor();
      if (val === null || rhs === null) val = null;
      else if (op === '*') val = val * rhs;
      else val = rhs === 0 ? null : val / rhs;
    }
    return val;
  }

  function parseFactor(): number | null {
    const tok = peek();
    if (!tok) return null;
    if (tok.kind === 'num') {
      idx += 1;
      return tok.value;
    }
    if (tok.kind === 'op' && tok.op === '-') {
      idx += 1;
      const v = parseFactor();
      return v === null ? null : -v;
    }
    if (tok.kind === 'op' && tok.op === '(') {
      idx += 1;
      const v = parseExpr();
      const closing = peek();
      if (closing && closing.kind === 'op' && (closing as any).op === ')') {
        idx += 1;
        return v;
      }
      return null;
    }
    if (tok.kind === 'id') {
      idx += 1;
      const v = values[tok.name];
      if (typeof v !== 'number' || !isFinite(v)) return null;
      return v;
    }
    return null;
  }

  try {
    const result = parseExpr();
    if (idx !== tokens.length || result === null || !isFinite(result)) return null;
    return Math.round(result * 1e6) / 1e6;
  } catch {
    return null;
  }
}
