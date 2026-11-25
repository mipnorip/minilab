import { useCallback, useEffect, useState } from 'react';
import { api } from '../api';
import { useAuth } from '../auth';
import type { AppNotification } from '../types';
import { Badge, ErrorBox, Loading } from '../ui';
import { fmtDateTime } from '../utils';

const TYPE_META: Record<string, { label: string; tone: 'warn' | 'err' | 'info' }> = {
  material_expiring: { label: 'срок годности', tone: 'warn' },
  daily_missing: { label: 'запись не внесена', tone: 'err' },
  review_due: { label: 'проверка журнала', tone: 'info' },
};

export default function NotificationsPage() {
  const { user } = useAuth();
  const [items, setItems] = useState<AppNotification[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [info, setInfo] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => {
    api
      .get<AppNotification[]>('/notifications/list')
      .then(setItems)
      .catch((e) => setError(e.message));
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  const markRead = async (id: number) => {
    try {
      await api.post(`/notifications/${id}/read`);
      load();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ошибка');
    }
  };

  const markAll = async () => {
    try {
      await api.post('/notifications/read-all');
      load();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ошибка');
    }
  };

  const runTick = async () => {
    setBusy(true);
    setError(null);
    setInfo(null);
    try {
      const res = await api.post<{ created: number }>('/notifications/tick');
      setInfo(`Проверка выполнена. Создано новых уведомлений: ${res.created}`);
      load();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ошибка');
    } finally {
      setBusy(false);
    }
  };

  if (error && !items) return <ErrorBox message={error} />;
  if (!items) return <Loading />;
  const unread = items.filter((n) => !n.is_read).length;

  return (
    <div>
      <div className="page-title">
        <div>
          <h1>Уведомления</h1>
          <p className="subtitle">
            Непрочитанных: {unread}. Планировщик следит за сроками годности, ежедневными
            записями и проверками журналов.
          </p>
        </div>
        <div className="actions no-print">
          {user?.role === 'admin' && (
            <button className="btn" onClick={runTick} disabled={busy}>
              {busy ? 'Проверяем…' : 'Проверить сейчас'}
            </button>
          )}
          {unread > 0 && (
            <button className="btn" onClick={markAll}>
              Прочитать все
            </button>
          )}
        </div>
      </div>
      {info && <div className="card" style={{ marginBottom: 14, padding: '10px 16px' }}>{info}</div>}
      {error && <ErrorBox message={error} />}
      <div className="tbl-wrap">
        {items.length === 0 ? (
          <div className="empty">Уведомлений нет</div>
        ) : (
          items.map((n) => {
            const meta = TYPE_META[n.type] || { label: n.type, tone: 'info' as const };
            return (
              <div key={n.id} className={'notif' + (n.is_read ? '' : ' unread')}>
                <Badge tone={meta.tone}>{meta.label}</Badge>
                <div className="notif-body">
                  <div className="notif-title">{n.title}</div>
                  <div className="notif-text">{n.body}</div>
                  <div className="notif-text muted" style={{ marginTop: 2 }}>
                    {fmtDateTime(n.ts)}
                  </div>
                </div>
                {!n.is_read && (
                  <button className="btn btn-sm" onClick={() => markRead(n.id)}>
                    Прочитано
                  </button>
                )}
              </div>
            );
          })
        )}
      </div>
    </div>
  );
}
