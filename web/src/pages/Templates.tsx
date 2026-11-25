import { useEffect, useState } from 'react';
import { api } from '../api';
import type { Column, Template } from '../types';
import { Badge, ErrorBox, Field, Loading, Modal } from '../ui';

const COLUMN_TYPES: { value: Column['type']; label: string }[] = [
  { value: 'text', label: 'Текст' },
  { value: 'number', label: 'Число' },
  { value: 'date', label: 'Дата' },
  { value: 'bool', label: 'Да/нет' },
  { value: 'enum', label: 'Список значений' },
  { value: 'ref_journal', label: 'Ссылка на запись журнала' },
  { value: 'ref_material', label: 'Ссылка на материал' },
  { value: 'formula', label: 'Формула (расчёт)' },
  { value: 'auto', label: 'Автополе' },
];

const AUTO_KINDS: { value: 'row' | 'date' | 'author'; label: string }[] = [
  { value: 'row', label: '№ по порядку' },
  { value: 'date', label: 'дата записи' },
  { value: 'author', label: 'автор записи' },
];

function emptyColumn(): Column {
  return { key: '', title: '', type: 'text' };
}

/* ---------- редактор колонок (конструктор) ---------- */

function TemplateEditor({
  mode,
  initialCode,
  initialTitle,
  initialColumns,
  busy,
  saveError,
  onSave,
  onCancel,
}: {
  mode: 'create' | 'version';
  initialCode: string;
  initialTitle: string;
  initialColumns: Column[];
  busy: boolean;
  saveError: string | null;
  onSave: (code: string, title: string, columns: Column[]) => void;
  onCancel: () => void;
}) {
  const [code, setCode] = useState(initialCode);
  const [title, setTitle] = useState(initialTitle);
  const [columns, setColumns] = useState<Column[]>(initialColumns.map((c) => ({ ...c })));

  const update = (i: number, patch: Partial<Column>) =>
    setColumns((prev) => prev.map((c, idx) => (idx === i ? { ...c, ...patch } : c)));
  const move = (i: number, dir: -1 | 1) =>
    setColumns((prev) => {
      const next = [...prev];
      const j = i + dir;
      if (j < 0 || j >= next.length) return prev;
      [next[i], next[j]] = [next[j], next[i]];
      return next;
    });
  const remove = (i: number) => setColumns((prev) => prev.filter((_, idx) => idx !== i));
  const add = () => setColumns((prev) => [...prev, emptyColumn()]);

  return (
    <div>
      <div className="form-row-2">
        <Field label="Код шаблона" hint={mode === 'version' ? 'изменить нельзя — создаётся новая версия' : 'латиница, например T-13'}>
          <input
            value={code}
            onChange={(e) => setCode(e.target.value.toUpperCase())}
            disabled={mode === 'version'}
          />
        </Field>
        <Field label="Название шаблона">
          <input value={title} onChange={(e) => setTitle(e.target.value)} disabled={mode === 'version'} />
        </Field>
      </div>

      {columns.map((col, i) => (
        <div key={i} className="col-row" style={{ gridTemplateColumns: '1fr' }}>
          <div style={{ display: 'grid', gridTemplateColumns: '140px 1fr 190px 90px 120px 30px 30px 30px', gap: 8, alignItems: 'center' }}>
            <input
              placeholder="ключ (лат.)"
              value={col.key}
              onChange={(e) => update(i, { key: e.target.value.toLowerCase().replace(/[^a-z0-9_]/g, '') })}
              disabled={mode === 'version'}
              className="mono"
            />
            <input
              placeholder="название колонки"
              value={col.title}
              onChange={(e) => update(i, { title: e.target.value })}
            />
            <select value={col.type} onChange={(e) => update(i, { type: e.target.value as Column['type'] })}>
              {COLUMN_TYPES.map((t) => (
                <option key={t.value} value={t.value}>
                  {t.label}
                </option>
              ))}
            </select>
            <label className="checkbox">
              <input
                type="checkbox"
                checked={!!col.required}
                onChange={(e) => update(i, { required: e.target.checked })}
              />
              обяз.
            </label>
            {col.type !== 'auto' && <span />}
            {col.type === 'auto' && (
              <select
                value={col.auto || 'row'}
                onChange={(e) => update(i, { auto: e.target.value as 'row' | 'date' | 'author' })}
              >
                {AUTO_KINDS.map((a) => (
                  <option key={a.value} value={a.value}>
                    {a.label}
                  </option>
                ))}
              </select>
            )}
            <button className="icon-btn" title="выше" onClick={() => move(i, -1)}>↑</button>
            <button className="icon-btn" title="ниже" onClick={() => move(i, 1)}>↓</button>
            <button className="icon-btn" title="удалить" onClick={() => remove(i)}>✕</button>
          </div>
          <div style={{ display: 'grid', gridTemplateColumns: '140px 1fr', gap: 8, marginTop: 6 }}>
            {col.type === 'number' && (
              <Field label="Единица">
                <input value={col.unit || ''} onChange={(e) => update(i, { unit: e.target.value })} placeholder="г, мл, °C…" />
              </Field>
            )}
            {col.type === 'enum' && (
              <Field label="Варианты" hint="через запятую">
                <input
                  value={(col.options || []).join(', ')}
                  onChange={(e) => update(i, { options: e.target.value.split(',').map((s) => s.trim()).filter(Boolean) })}
                />
              </Field>
            )}
            {col.type === 'formula' && (
              <Field label="Формула" hint="поля записи, + - * / и скобки: mass_g * 1000 / volume_ml">
                <input
                  className="mono"
                  value={col.formula || ''}
                  onChange={(e) => update(i, { formula: e.target.value })}
                  placeholder="a * 1000 / b"
                />
              </Field>
            )}
            {col.type === 'ref_journal' && (
              <Field label="Шифр журнала-источника">
                <input value={col.ref_code || ''} onChange={(e) => update(i, { ref_code: e.target.value })} placeholder="Ж-04/01" />
              </Field>
            )}
            {col.type === 'formula' && (
              <Field label="Единица результата">
                <input value={col.unit || ''} onChange={(e) => update(i, { unit: e.target.value })} placeholder="г/л" />
              </Field>
            )}
          </div>
        </div>
      ))}

      <button className="btn" onClick={add} style={{ marginBottom: 14 }}>
        + Колонка
      </button>

      <ErrorBox message={saveError} />
      <div className="row" style={{ justifyContent: 'flex-end' }}>
        <button className="btn" onClick={onCancel}>
          Отмена
        </button>
        <button
          className="btn btn-primary"
          disabled={busy || columns.length === 0 || !code.trim() || !title.trim()}
          onClick={() => onSave(code.trim(), title.trim(), columns)}
        >
          {mode === 'create' ? 'Создать шаблон' : 'Создать новую версию'}
        </button>
      </div>
    </div>
  );
}

/* ---------- страница шаблонов ---------- */

export default function TemplatesPage() {
  const [templates, setTemplates] = useState<Template[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<
    null | { mode: 'create' } | { mode: 'version'; template: Template; columns: Column[] }
  >(null);
  const [busy, setBusy] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  const load = () => {
    api
      .get<Template[]>('/forms/templates')
      .then(setTemplates)
      .catch((e) => setError(e.message));
  };

  useEffect(() => {
    load();
  }, []);

  const openVersion = (t: Template) => {
    setSaveError(null);
    api
      .get<Template>(`/forms/templates/${t.id}`)
      .then((full) => setEditing({ mode: 'version', template: t, columns: full.columns || [] }))
      .catch((e) => setError(e.message));
  };

  const saveCreate = async (code: string, title: string, columns: Column[]) => {
    setBusy(true);
    setSaveError(null);
    try {
      await api.post('/forms/templates', { code, title, columns, is_preset: false });
      setEditing(null);
      load();
    } catch (e) {
      setSaveError(e instanceof Error ? e.message : 'Ошибка');
    } finally {
      setBusy(false);
    }
  };

  const saveVersion = async (_code: string, _title: string, columns: Column[]) => {
    if (!editing || editing.mode !== 'version') return;
    setBusy(true);
    setSaveError(null);
    try {
      await api.post(`/forms/templates/${editing.template.id}/versions`, { columns });
      setEditing(null);
      load();
    } catch (e) {
      setSaveError(e instanceof Error ? e.message : 'Ошибка');
    } finally {
      setBusy(false);
    }
  };

  if (error && !templates) return <ErrorBox message={error} />;
  if (!templates) return <Loading />;

  return (
    <div>
      <div className="page-title">
        <div>
          <h1>Шаблоны форм (конструктор)</h1>
          <p className="subtitle">
            Пресеты журналов и пользовательские шаблоны. Новая версия формы не ломает ранее
            внесённые записи.
          </p>
        </div>
        <div className="actions">
          <button
            className="btn btn-primary"
            onClick={() => {
              setSaveError(null);
              setEditing({ mode: 'create' });
            }}
          >
            + Новый шаблон
          </button>
        </div>
      </div>
      <ErrorBox message={error} />
      <div className="tbl-wrap">
        <table className="tbl">
          <thead>
            <tr>
              <th>Код</th>
              <th>Название</th>
              <th>Версия</th>
              <th>Тип</th>
              <th>Действия</th>
            </tr>
          </thead>
          <tbody>
            {templates.map((t) => (
              <tr key={t.id}>
                <td className="mono">{t.code}</td>
                <td>{t.title}</td>
                <td>
                  <Badge tone="info">v{t.version ?? 1}</Badge>
                </td>
                <td>{t.is_preset ? <Badge tone="warn">пресет</Badge> : <Badge>пользовательский</Badge>}</td>
                <td className="row-actions">
                  <button className="btn btn-sm" onClick={() => openVersion(t)}>
                    Новая версия
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {editing?.mode === 'create' && (
        <Modal title="Новый шаблон формы" onClose={() => setEditing(null)} wide>
          <TemplateEditor
            mode="create"
            initialCode=""
            initialTitle=""
            initialColumns={[emptyColumn()]}
            busy={busy}
            saveError={saveError}
            onSave={saveCreate}
            onCancel={() => setEditing(null)}
          />
        </Modal>
      )}
      {editing?.mode === 'version' && (
        <Modal
          title={`Новая версия · ${editing.template.code} ${editing.template.title}`}
          onClose={() => setEditing(null)}
          wide
        >
          <TemplateEditor
            mode="version"
            initialCode={editing.template.code}
            initialTitle={editing.template.title}
            initialColumns={editing.columns}
            busy={busy}
            saveError={saveError}
            onSave={saveVersion}
            onCancel={() => setEditing(null)}
          />
        </Modal>
      )}
    </div>
  );
}
