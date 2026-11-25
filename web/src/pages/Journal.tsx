import { useCallback, useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { api } from '../api';
import { useAuth } from '../auth';
import type {
  Batch,
  Column,
  Journal,
  MaterialItem,
  Operation,
  RecordRow,
  Revision,
  ReviewLog,
  Template,
  User,
} from '../types';
import { Badge, Empty, ErrorBox, Field, Loading, Modal } from '../ui';
import { computeFormula } from '../formula';
import { downloadCSV, fmtDate, fmtDateTime, todayISO } from '../utils';
import { InventoryModal, ReceiptModal, WriteOffModal } from './MaterialModals';

const REASONS: { code: string; label: string }[] = [
  { code: 'V', label: 'В — ошибка вычисления' },
  { code: 'O', label: 'О — опечатка / ошибочные данные' },
  { code: 'I', label: 'И — иное' },
];

function firstText(data: Record<string, unknown>): string {
  for (const v of Object.values(data)) {
    if (typeof v === 'string' && v.trim()) return v;
  }
  return '';
}

function refLabel(row: RecordRow): string {
  return `№${row.row_no} · ${firstText(row.data)}`;
}

function cellValue(col: Column, row: RecordRow): string {
  const v = row.data[col.key];
  if (v === null || v === undefined || v === '') return '—';
  if (typeof v === 'boolean') return v ? 'да' : 'нет';
  if (col.unit && typeof v === 'number') return `${v} ${col.unit}`;
  return String(v);
}

/* ---------- модальное окно записи (внесение / исправление) ---------- */

function RecordModal({
  journal,
  columns,
  record,
  refRecords,
  materialItems,
  onSaved,
  onClose,
}: {
  journal: Journal;
  columns: Column[];
  record: RecordRow | null;
  refRecords: Record<string, RecordRow[]>;
  materialItems: MaterialItem[];
  onSaved: () => void;
  onClose: () => void;
}) {
  const isEdit = !!record;
  const [values, setValues] = useState<Record<string, unknown>>(() => {
    const init: Record<string, unknown> = {};
    for (const c of columns) {
      if (c.type === 'auto' || c.type === 'formula') continue;
      const v = record?.data?.[c.key];
      init[c.key] = v ?? (c.type === 'date' ? todayISO() : '');
    }
    return init;
  });
  const [reasonCode, setReasonCode] = useState('O');
  const [reasonComment, setReasonComment] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const set = (key: string, v: unknown) => setValues((prev) => ({ ...prev, [key]: v }));

  const formulaPreview = (col: Column): string => {
    const v = computeFormula(col.formula || '', values as Record<string, number>);
    return v === null ? '—' : `${v}${col.unit ? ` ${col.unit}` : ''}`;
  };

  const save = async () => {
    setBusy(true);
    setError(null);
    const data: Record<string, unknown> = {};
    for (const c of columns) {
      if (c.type === 'auto' || c.type === 'formula') continue;
      const v = values[c.key];
      data[c.key] = v === '' ? null : v;
    }
    try {
      if (isEdit) {
        await api.patch(`/records/records/${record!.id}`, {
          data,
          reason_code: reasonCode,
          reason_comment: reasonComment.trim() || null,
        });
      } else {
        await api.post(`/records/journals/${journal.id}/records`, { data });
      }
      onSaved();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ошибка сохранения');
    } finally {
      setBusy(false);
    }
  };

  const inputFor = (col: Column) => {
    const v = values[col.key] ?? '';
    switch (col.type) {
      case 'number':
        return (
          <input type="number" step="any" value={v as string} onChange={(e) => set(col.key, e.target.value)} />
        );
      case 'date':
        return <input type="date" value={v as string} onChange={(e) => set(col.key, e.target.value)} />;
      case 'bool':
        return (
          <label className="checkbox">
            <input type="checkbox" checked={!!v} onChange={(e) => set(col.key, e.target.checked)} /> да
          </label>
        );
      case 'enum':
        return (
          <select value={v as string} onChange={(e) => set(col.key, e.target.value)}>
            <option value="">— не выбрано —</option>
            {(col.options || []).map((o) => (
              <option key={o} value={o}>
                {o}
              </option>
            ))}
          </select>
        );
      case 'ref_journal': {
        const rows = refRecords[col.ref_code || ''] || [];
        return (
          <select
            value={v === null ? '' : String(v)}
            onChange={(e) => set(col.key, e.target.value ? Number(e.target.value) : null)}
          >
            <option value="">— выберите запись —</option>
            {rows.map((r) => (
              <option key={r.id} value={r.id}>
                {refLabel(r)}
              </option>
            ))}
          </select>
        );
      }
      case 'ref_material':
        return (
          <select
            value={v === null ? '' : String(v)}
            onChange={(e) => set(col.key, e.target.value ? Number(e.target.value) : null)}
          >
            <option value="">— выберите материал —</option>
            {materialItems.map((m) => (
              <option key={m.id} value={m.id}>
                {m.name} ({m.type_title})
              </option>
            ))}
          </select>
        );
      default:
        return <input value={v as string} onChange={(e) => set(col.key, e.target.value)} />;
    }
  };

  return (
    <Modal
      title={isEdit ? `Исправление записи №${record!.row_no} · ${journal.code}` : `Новая запись · ${journal.code}`}
      onClose={onClose}
      wide
    >
      <div className="form-grid">
        {columns.map((col) => {
          if (col.type === 'formula') {
            return (
              <Field key={col.key} label={col.title}>
                <div className="mono">{formulaPreview(col)}</div>
              </Field>
            );
          }
          if (col.type === 'auto') {
            const current = isEdit ? record!.data[col.key] : null;
            const hint =
              col.auto === 'row'
                ? 'присвоится автоматически'
                : col.auto === 'date'
                  ? 'дата записи — автоматически'
                  : 'автор записи — автоматически';
            return (
              <Field key={col.key} label={col.title} hint={isEdit ? String(current ?? '—') : hint}>
                <div className="muted">{isEdit ? String(current ?? '—') : '—'}</div>
              </Field>
            );
          }
          return (
            <Field
              key={col.key}
              label={`${col.title}${col.unit ? `, ${col.unit}` : ''}${col.required ? ' *' : ''}`}
            >
              {inputFor(col)}
            </Field>
          );
        })}
      </div>
      {isEdit && (
        <div className="card" style={{ marginTop: 8, marginBottom: 8, padding: 14 }}>
          <b>Причина исправления</b>
          <span className="muted" style={{ marginLeft: 6 }}>
            прежние значения сохраняются в истории записи (ГОСТ 17025)
          </span>
          <Field label="Код причины">
            <select value={reasonCode} onChange={(e) => setReasonCode(e.target.value)}>
              {REASONS.map((r) => (
                <option key={r.code} value={r.code}>
                  {r.label}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Комментарий" hint="необязательно">
            <input value={reasonComment} onChange={(e) => setReasonComment(e.target.value)} />
          </Field>
        </div>
      )}
      <ErrorBox message={error} />
      <div className="row" style={{ justifyContent: 'flex-end' }}>
        <button className="btn" onClick={onClose}>
          Отмена
        </button>
        <button className="btn btn-primary" disabled={busy} onClick={save}>
          {isEdit ? 'Внести исправление' : 'Внести запись'}
        </button>
      </div>
    </Modal>
  );
}

/* ---------- история записи ---------- */

function HistoryModal({
  record,
  columns,
  onClose,
}: {
  record: RecordRow;
  columns: Column[];
  onClose: () => void;
}) {
  const [data, setData] = useState<{ record: RecordRow; revisions: Revision[] } | null>(null);

  useEffect(() => {
    api
      .get<{ record: RecordRow; revisions: Revision[] }>(`/records/records/${record.id}/history`)
      .then(setData)
      .catch(() => setData({ record, revisions: [] }));
  }, [record]);

  const titleByKey = (key: string) => columns.find((c) => c.key === key)?.title || key;

  return (
    <Modal title={`История записи №${record.row_no} · ${record.journal_code || ''}`} onClose={onClose} wide>
      <div className="muted" style={{ marginBottom: 10 }}>
        Автор: {record.author_name} · создана {fmtDateTime(record.created_at)}
        {data ? ` · исправлений: ${data.revisions.length}` : ' · …'}
      </div>
      {data?.revisions.map((rev) => {
        const changed = Object.keys(rev.new_data).filter(
          (k) => JSON.stringify(rev.old_data[k]) !== JSON.stringify(rev.new_data[k]),
        );
        return (
          <div className="revision" key={rev.id}>
            <div className="rev-head">
              <span>
                {fmtDateTime(rev.ts)} · {rev.author_name}
              </span>
              <span className="rev-reason">
                причина: {rev.reason_code}
                {rev.reason_comment ? ` — ${rev.reason_comment}` : ''}
              </span>
            </div>
            {changed.length === 0 ? (
              <span className="muted">без изменения значений</span>
            ) : (
              <table className="tbl">
                <tbody>
                  {changed.map((k) => (
                    <tr key={k}>
                      <td style={{ width: 240 }}>{titleByKey(k)}</td>
                      <td>
                        <span className="diff-old">{String(rev.old_data[k] ?? '—')}</span>
                        {' → '}
                        <b>{String(rev.new_data[k] ?? '—')}</b>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        );
      })}
      {data && data.revisions.length === 0 && <Empty text="Запись ещё не исправлялась" />}
      {!data && <Loading text="Загружаем историю…" />}
    </Modal>
  );
}

/* ---------- отметка проверки журнала ---------- */

function ReviewModal({
  journal,
  onDone,
  onClose,
}: {
  journal: Journal;
  onDone: () => void;
  onClose: () => void;
}) {
  const [note, setNote] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      await api.post(`/records/journals/${journal.id}/review`, { note: note.trim() || null });
      onDone();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ошибка');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal title={`Проверка журнала ${journal.code}`} onClose={onClose}>
      <p className="subtitle">
        Отметка «проверено (кем, когда)» фиксируется в журнале и в аудите — как требует
        процедура ведения журналов.
      </p>
      <Field label="Замечание" hint="необязательно">
        <textarea rows={3} value={note} onChange={(e) => setNote(e.target.value)} />
      </Field>
      <ErrorBox message={error} />
      <div className="row" style={{ justifyContent: 'flex-end' }}>
        <button className="btn" onClick={onClose}>
          Отмена
        </button>
        <button className="btn btn-primary" disabled={busy} onClick={save}>
          Отметить проверку
        </button>
      </div>
    </Modal>
  );
}

/* ---------- участники и настройки журнала (admin) ---------- */

function SettingsModal({
  journal,
  onClose,
  onDone,
}: {
  journal: Journal;
  onClose: () => void;
  onDone: () => void;
}) {
  const [users, setUsers] = useState<User[] | null>(null);
  const [status, setStatus] = useState(journal.status);
  const [ownerId, setOwnerId] = useState<number | ''>(journal.owner_id);
  const [memberIds, setMemberIds] = useState<number[]>(
    (journal.members || []).map((m) => m.user_id),
  );
  const [checkWeeks, setCheckWeeks] = useState(journal.check_period_weeks);
  const [retention, setRetention] = useState(journal.retention_years);
  const [daily, setDaily] = useState(journal.daily);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    api
      .get<User[]>('/auth/users')
      .then(setUsers)
      .catch((e) => setError(e.message));
  }, []);

  const engineers = (users || []).filter((u) => u.role === 'engineer' && u.is_active);
  const owner = (users || []).find((u) => u.id === ownerId);

  const toggleMember = (id: number) =>
    setMemberIds((prev) =>
      prev.includes(id) ? prev.filter((x) => x !== id) : [...prev, id],
    );

  const save = async () => {
    setError(null);
    if (!owner) {
      setError('Выберите ответственного за журнал');
      return;
    }
    setBusy(true);
    try {
      await api.patch(`/records/journals/${journal.id}`, {
        status,
        owner_id: ownerId,
        owner_name: owner.full_name,
        check_period_weeks: checkWeeks,
        retention_years: retention,
        daily,
        members: engineers
          .filter((u) => memberIds.includes(u.id) && u.id !== ownerId)
          .map((u) => ({ user_id: u.id, name: u.full_name, role: 'worker' })),
      });
      onDone();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ошибка');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal title={`Участники и настройки · ${journal.code}`} onClose={onClose}>
      {!users ? (
        <Loading text="Загружаем пользователей…" />
      ) : (
        <>
          <div className="form-grid">
            <Field label="Статус журнала">
              <select value={status} onChange={(e) => setStatus(e.target.value)}>
                <option value="active">ведётся</option>
                <option value="closed">закрыт (записи не вносятся)</option>
                <option value="archived">в архиве (только чтение)</option>
              </select>
            </Field>
            <Field label="Ответственный">
              <select
                value={ownerId}
                onChange={(e) => setOwnerId(e.target.value ? Number(e.target.value) : '')}
              >
                <option value="">— выберите —</option>
                {(users || [])
                  .filter((u) => u.is_active)
                  .map((u) => (
                    <option key={u.id} value={u.id}>
                      {u.full_name} ({u.role_title})
                    </option>
                  ))}
              </select>
            </Field>
            <Field label="Периодичность проверки, нед.">
              <input
                type="number"
                value={checkWeeks}
                onChange={(e) => setCheckWeeks(Number(e.target.value) || 4)}
              />
            </Field>
            <Field label="Срок хранения, лет">
              <input
                type="number"
                value={retention}
                onChange={(e) => setRetention(Number(e.target.value) || 5)}
              />
            </Field>
          </div>
          {journal.kind === 'form' && (
            <label className="checkbox" style={{ marginBottom: 10 }}>
              <input type="checkbox" checked={daily} onChange={(e) => setDaily(e.target.checked)} />
              Ежедневный журнал (контроль отсутствия записи)
            </label>
          )}
          <h3>Участники (могут вносить записи)</h3>
          <p className="subtitle">
            Ответственный участвует автоматически и дополнительно может закрывать записи.
          </p>
          {engineers.length === 0 && <p className="muted">Нет активных инженеров/лаборантов</p>}
          {engineers.map((u) => (
            <label
              key={u.id}
              className="checkbox"
              style={{ display: 'inline-flex', marginRight: 16, marginBottom: 6 }}
            >
              <input
                type="checkbox"
                checked={memberIds.includes(u.id) || u.id === ownerId}
                disabled={u.id === ownerId}
                onChange={() => toggleMember(u.id)}
              />
              {u.full_name}
            </label>
          ))}
          <ErrorBox message={error} />
          <div className="row" style={{ justifyContent: 'flex-end', marginTop: 10 }}>
            <button className="btn" onClick={onClose}>
              Отмена
            </button>
            <button className="btn btn-primary" disabled={busy} onClick={save}>
              Сохранить
            </button>
          </div>
        </>
      )}
    </Modal>
  );
}

/* ---------- страница журнала ---------- */

type ModalState =
  | null
  | { kind: 'record'; record: RecordRow | null }
  | { kind: 'history'; record: RecordRow }
  | { kind: 'review' }
  | { kind: 'receipt' }
  | { kind: 'writeoff' }
  | { kind: 'inventory' }
  | { kind: 'settings' };

export default function JournalPage() {
  const { id } = useParams();
  const { user } = useAuth();
  const journalId = Number(id);

  const [journal, setJournal] = useState<Journal | null>(null);
  const [columns, setColumns] = useState<Column[] | null>(null);
  const [records, setRecords] = useState<RecordRow[] | null>(null);
  const [ops, setOps] = useState<Operation[] | null>(null);
  const [refRecords, setRefRecords] = useState<Record<string, RecordRow[]>>({});
  const [materialItems, setMaterialItems] = useState<MaterialItem[]>([]);
  const [batches, setBatches] = useState<Batch[]>([]);
  const [reviews, setReviews] = useState<ReviewLog[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [modal, setModal] = useState<ModalState>(null);

  const load = useCallback(() => {
    api
      .get<Journal>(`/records/journals/${journalId}`)
      .then((j) => {
        setJournal(j);
        if (j.kind === 'form') {
          api
            .get<Template>(`/forms/templates/${j.form_template_id}`)
            .then((t) => {
              setColumns(t.columns || []);
              const codes = new Set(
                (t.columns || [])
                  .filter((c) => c.type === 'ref_journal' && c.ref_code)
                  .map((c) => c.ref_code as string),
              );
              codes.forEach((code) => {
                api
                  .get<Journal>(`/records/journals/code/${code}`)
                  .then((rj) =>
                    api
                      .get<RecordRow[]>(`/records/journals/${rj.id}/records?limit=50`)
                      .then((rows) => setRefRecords((prev) => ({ ...prev, [code]: rows }))),
                  )
                  .catch(() => undefined);
              });
              if ((t.columns || []).some((c) => c.type === 'ref_material')) {
                api
                  .get<MaterialItem[]>('/materials/items')
                  .then(setMaterialItems)
                  .catch(() => undefined);
              }
            })
            .catch((e) => setError(e.message));
          api
            .get<RecordRow[]>(`/records/journals/${journalId}/records`)
            .then(setRecords)
            .catch((e) => setError(e.message));
        } else {
          setColumns([]);
          api
            .get<Operation[]>(`/materials/operations?item_type=${j.materials_type}`)
            .then(setOps)
            .catch((e) => setError(e.message));
          api
            .get<MaterialItem[]>(
              `/materials/items${j.materials_type ? `?type=${j.materials_type}` : ''}`,
            )
            .then(setMaterialItems)
            .catch(() => undefined);
          api
            .get<Batch[]>('/materials/batches')
            .then((all) => setBatches(all.filter((b) => b.item_type === j.materials_type)))
            .catch(() => undefined);
        }
        api
          .get<ReviewLog[]>(`/records/journals/${j.id}/reviews`)
          .then(setReviews)
          .catch(() => setReviews([]));
      })
      .catch((e) => setError(e.message));
  }, [journalId]);

  useEffect(() => {
    load();
  }, [load]);

  if (error && !journal) return <ErrorBox message={error} />;
  if (!journal || !columns) return <Loading />;

  const isAdmin = user?.role === 'admin';
  const isOwner = !!user && journal.owner_id === user.id;
  const isQM = user?.role === 'quality_manager';
  const canWrite = journal.status === 'active' && (isAdmin || journal.is_member);
  const canReview = (isQM || isAdmin) && journal.status !== 'archived';
  const canCloseRow = (r: RecordRow) => r.status === 'entered' && (isAdmin || isOwner);
  const canEditRow = (r: RecordRow) =>
    r.status === 'entered' &&
    journal.status !== 'archived' &&
    !!user &&
    (isAdmin || r.author_id === user.id || isOwner);

  const closeModal = () => setModal(null);
  const afterSave = () => {
    setModal(null);
    load();
  };

  const closeRecord = async (r: RecordRow) => {
    if (!window.confirm(`Закрыть запись №${r.row_no}? Дальнейшие исправнения будут недоступны.`)) return;
    try {
      await api.post(`/records/records/${r.id}/close`);
      load();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ошибка');
    }
  };

  const exportCSV = () => {
    if (!records) return;
    const header = [
      ...columns.map((c) => c.title + (c.unit ? `, ${c.unit}` : '')),
      'Статус',
      'Автор',
      'Создана',
    ];
    const rows = records.map((r) => [
      ...columns.map((c) => cellValue(c, r)),
      r.status === 'closed' ? 'закрыта' : 'внесена',
      r.author_name,
      fmtDateTime(r.created_at),
    ]);
    downloadCSV(`${journal.code.replace('/', '-')}.csv`, header, rows);
  };

  const lastReview = reviews[0];

  return (
    <div>
      <div className="page-title no-print">
        <div>
          <h1>
            <span className="mono" style={{ color: 'var(--primary)' }}>
              {journal.code}
            </span>{' '}
            {journal.title}
          </h1>
          <p className="subtitle">
            <Link to="/journals">← все журналы</Link> · ответственный: {journal.owner_name} ·
            проверка раз в {journal.check_period_weeks} нед. · хранение{' '}
            {journal.retention_years} лет
            {journal.daily ? ' · ежедневный' : ''}
            {journal.kind === 'materials' ? ` · учёт: ${journal.materials_type}` : ''}
          </p>
        </div>
        <div className="actions">
          {journal.kind === 'form' && canWrite && (
            <button
              className="btn btn-primary"
              onClick={() => setModal({ kind: 'record', record: null })}
            >
              + Новая запись
            </button>
          )}
          {journal.kind === 'materials' && (isAdmin || user?.role === 'engineer') && (
            <button className="btn btn-primary" onClick={() => setModal({ kind: 'receipt' })}>
              + Поступление
            </button>
          )}
          {journal.kind === 'materials' && (isAdmin || user?.role === 'engineer') && (
            <button className="btn" onClick={() => setModal({ kind: 'writeoff' })}>
              Списание
            </button>
          )}
          {journal.kind === 'materials' &&
            journal.materials_type === 'precursor' &&
            (isAdmin || user?.can_wf_precursors) && (
              <button className="btn" onClick={() => setModal({ kind: 'inventory' })}>
                Инвентаризация
              </button>
            )}
          {canReview && (
            <button className="btn" onClick={() => setModal({ kind: 'review' })}>
              ✓ Отметить проверку
            </button>
          )}
          {isAdmin && (
            <button className="btn" onClick={() => setModal({ kind: 'settings' })}>
              Участники и настройки
            </button>
          )}
          {journal.kind === 'form' && (
            <>
              <button className="btn" onClick={exportCSV} disabled={!records?.length}>
                CSV
              </button>
              <button className="btn" onClick={() => window.print()}>
                Печать
              </button>
            </>
          )}
        </div>
      </div>

      <div className="card no-print" style={{ marginBottom: 16, padding: '12px 16px' }}>
        <div className="row">
          <Badge tone={journal.status === 'active' ? 'ok' : journal.status === 'closed' ? 'muted' : 'err'}>
            {journal.status === 'active' ? 'ведётся' : journal.status === 'closed' ? 'закрыт' : 'в архиве'}
          </Badge>
          {lastReview ? (
            <span className="muted">
              последняя проверка: {fmtDate(lastReview.ts)} — {lastReview.reviewer_name}
              {lastReview.note ? ` (${lastReview.note})` : ''}
            </span>
          ) : (
            <span className="muted">проверок ещё не было</span>
          )}
          <span className="muted">
            следующая плановая проверка: {fmtDate(journal.next_review_at)}
          </span>
          {journal.status === 'active' && journal.next_review_at && new Date(journal.next_review_at) < new Date() && (
            <Badge tone="err">проверка просрочена</Badge>
          )}
          {journal.members && journal.members.length > 0 && (
            <span className="muted">
              участники:{' '}
              {journal.members.map((m) => `${m.name}${m.role === 'owner' ? ' (отв.)' : ''}`).join(', ')}
            </span>
          )}
        </div>
      </div>

      {error && <ErrorBox message={error} />}

      {journal.kind === 'form' ? (
        !records || records.length === 0 ? (
          <div className="tbl-wrap">
            <Empty text={canWrite ? 'Записей пока нет — внесите первую' : 'Записей пока нет'} />
          </div>
        ) : (
          <div className="tbl-wrap">
            <table className="tbl">
              <thead>
                <tr>
                  {columns.map((c) => (
                    <th key={c.key} className={c.type === 'number' ? 'num' : ''}>
                      {c.title}
                      {c.unit ? `, ${c.unit}` : ''}
                    </th>
                  ))}
                  <th>Статус</th>
                  <th>Автор</th>
                  <th>Создана</th>
                  <th className="no-print">Действия</th>
                </tr>
              </thead>
              <tbody>
                {records.map((r) => (
                  <tr key={r.id}>
                    {columns.map((c) => {
                      if (c.type === 'ref_journal') {
                        const target = (refRecords[c.ref_code || ''] || []).find(
                          (row) => row.id === r.data[c.key],
                        );
                        return (
                          <td key={c.key} className="mono">
                            {target
                              ? refLabel(target)
                              : r.data[c.key] != null
                                ? `№${r.data[c.key]}`
                                : '—'}
                          </td>
                        );
                      }
                      return (
                        <td key={c.key} className={c.type === 'number' ? 'num mono' : ''}>
                          {cellValue(c, r)}
                        </td>
                      );
                    })}
                    <td>
                      {r.status === 'closed' ? (
                        <Badge tone="muted">закрыта</Badge>
                      ) : (
                        <Badge tone="ok">внесена</Badge>
                      )}
                    </td>
                    <td>{r.author_name}</td>
                    <td className="muted">{fmtDateTime(r.updated_at)}</td>
                    <td className="row-actions no-print">
                      <button className="btn btn-sm" onClick={() => setModal({ kind: 'history', record: r })}>
                        История
                      </button>
                      {canEditRow(r) && (
                        <button
                          className="btn btn-sm"
                          onClick={() => setModal({ kind: 'record', record: r })}
                        >
                          Исправить
                        </button>
                      )}
                      {canCloseRow(r) && (
                        <button className="btn btn-sm btn-danger" onClick={() => closeRecord(r)}>
                          Закрыть
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )
      ) : (
        <div className="tbl-wrap">
          {!ops || ops.length === 0 ? (
            <Empty text="Операций пока нет" />
          ) : (
            <table className="tbl">
              <thead>
                <tr>
                  <th>Дата / время</th>
                  <th>Операция</th>
                  <th>Материал</th>
                  <th>Партия</th>
                  <th className="num">Кол-во</th>
                  <th>Кто</th>
                  <th>Примечание</th>
                </tr>
              </thead>
              <tbody>
                {ops.map((o) => (
                  <tr key={o.id}>
                    <td className="muted">{fmtDateTime(o.ts)}</td>
                    <td>
                      <Badge
                        tone={
                          o.op_type === 'writeoff'
                            ? 'warn'
                            : o.op_type === 'receipt'
                              ? 'ok'
                              : 'info'
                        }
                      >
                        {o.op_type_title}
                      </Badge>
                    </td>
                    <td>
                      {o.item_name}
                      {o.target_journal_code && (
                        <span className="muted">
                          {' '}
                          → {o.target_journal_code}
                          {o.target_record_id ? ` №${o.target_record_id}` : ''}
                        </span>
                      )}
                    </td>
                    <td className="mono">{o.batch_no || '—'}</td>
                    <td className="num">
                      {o.qty} {o.unit}
                    </td>
                    <td>{o.actor_name}</td>
                    <td className="muted">{o.note || ''}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}

      {modal?.kind === 'record' && (
        <RecordModal
          journal={journal}
          columns={columns}
          record={modal.record}
          refRecords={refRecords}
          materialItems={materialItems}
          onSaved={afterSave}
          onClose={closeModal}
        />
      )}
      {modal?.kind === 'history' && (
        <HistoryModal record={modal.record} columns={columns} onClose={closeModal} />
      )}
      {modal?.kind === 'review' && <ReviewModal journal={journal} onDone={afterSave} onClose={closeModal} />}
      {modal?.kind === 'settings' && (
        <SettingsModal journal={journal} onDone={afterSave} onClose={closeModal} />
      )}
      {modal?.kind === 'receipt' && (
        <ReceiptModal items={materialItems} onClose={closeModal} onDone={afterSave} />
      )}
      {modal?.kind === 'writeoff' && (
        <WriteOffModal
          items={materialItems}
          precursor={journal.materials_type === 'precursor'}
          hasRight={isAdmin || !!user?.can_wf_precursors}
          onClose={closeModal}
          onDone={afterSave}
        />
      )}
      {modal?.kind === 'inventory' && (
        <InventoryModal batches={batches} onClose={closeModal} onDone={afterSave} />
      )}
    </div>
  );
}
