import { useCallback, useEffect, useState } from 'react';
import { api } from '../api';
import type { AuditEvent } from '../types';
import { Badge, Empty, ErrorBox, Loading } from '../ui';
import { fmtDateTime } from '../utils';

const ENTITY_TYPES: { value: string; label: string }[] = [
  { value: '', label: 'все объекты' },
  { value: 'journal', label: 'журналы' },
  { value: 'record', label: 'записи' },
  { value: 'material', label: 'материалы' },
  { value: 'batch', label: 'партии' },
  { value: 'user', label: 'пользователи' },
  { value: 'form', label: 'формы' },
];

function actionLabel(action: string): { text: string; tone: 'ok' | 'warn' | 'err' | 'info' | 'muted' } {
  if (action.startsWith('record')) return { text: 'запись', tone: 'info' };
  if (action.startsWith('journal')) return { text: 'журнал', tone: 'ok' };
  if (action.startsWith('material') || action.startsWith('batch')) return { text: 'материалы', tone: 'warn' };
  if (action.startsWith('user')) return { text: 'пользователь', tone: 'err' };
  if (action.startsWith('form')) return { text: 'форма', tone: 'info' };
  if (action === 'login') return { text: 'вход', tone: 'muted' };
  return { text: action, tone: 'muted' };
}

export default function AuditPage() {
  const [events, setEvents] = useState<AuditEvent[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [entityType, setEntityType] = useState('');
  const [dateFrom, setDateFrom] = useState('');
  const [dateTo, setDateTo] = useState('');

  const load = useCallback(() => {
    const params = new URLSearchParams();
    if (entityType) params.set('entity_type', entityType);
    if (dateFrom) params.set('date_from', dateFrom);
    if (dateTo) params.set('date_to', dateTo);
    params.set('limit', '300');
    api
      .get<AuditEvent[]>(`/audit/events?${params.toString()}`)
      .then(setEvents)
      .catch((e) => setError(e.message));
  }, [entityType, dateFrom, dateTo]);

  useEffect(() => {
    load();
  }, [load]);

  return (
    <div>
      <div className="page-title">
        <div>
          <h1>Журнал аудита</h1>
          <p className="subtitle">
            Append-only: кто — когда — что — почему. Изменить или удалить события нельзя.
          </p>
        </div>
      </div>
      <div className="filters">
        <select value={entityType} onChange={(e) => setEntityType(e.target.value)}>
          {ENTITY_TYPES.map((t) => (
            <option key={t.value} value={t.value}>
              {t.label}
            </option>
          ))}
        </select>
        <input type="date" value={dateFrom} onChange={(e) => setDateFrom(e.target.value)} />
        <input type="date" value={dateTo} onChange={(e) => setDateTo(e.target.value)} />
        <button className="btn" onClick={load}>
          Обновить
        </button>
      </div>
      <ErrorBox message={error} />
      {events === null ? (
        <Loading />
      ) : events.length === 0 ? (
        <div className="tbl-wrap">
          <Empty text="Событий нет" />
        </div>
      ) : (
        <div className="tbl-wrap">
          <table className="tbl">
            <thead>
              <tr>
                <th>Время</th>
                <th>Кто</th>
                <th>Действие</th>
                <th>Объект</th>
                <th>Причина</th>
              </tr>
            </thead>
            <tbody>
              {events.map((e) => {
                const meta = actionLabel(e.action);
                return (
                  <tr key={e.id}>
                    <td className="muted" style={{ whiteSpace: 'nowrap' }}>
                      {fmtDateTime(e.ts)}
                    </td>
                    <td>{e.actor_name}</td>
                    <td>
                      <Badge tone={meta.tone}>{meta.text}</Badge>
                      <span className="muted mono" style={{ marginLeft: 6 }}>
                        {e.action}
                      </span>
                    </td>
                    <td>
                      {e.entity_title || `${e.entity_type} #${e.entity_id}`}
                      {e.payload && Object.keys(e.payload).length > 0 && (
                        <details style={{ marginTop: 4 }}>
                          <summary className="muted" style={{ cursor: 'pointer' }}>
                            подробности
                          </summary>
                          <pre
                            className="mono"
                            style={{
                              margin: '6px 0 0',
                              padding: 8,
                              background: '#f6f8fb',
                              borderRadius: 6,
                              overflow: 'auto',
                            }}
                          >
                            {JSON.stringify(e.payload, null, 2)}
                          </pre>
                        </details>
                      )}
                    </td>
                    <td className="muted">{e.reason || ''}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
