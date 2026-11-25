import { useCallback, useEffect, useState } from 'react';
import { api } from '../api';
import { useAuth } from '../auth';
import type { Batch, MaterialItem, MaterialType, Operation, OpType } from '../types';
import { Badge, Empty, ErrorBox, Field, Loading, Modal } from '../ui';
import { fmtDate, fmtDateTime } from '../utils';
import { InventoryModal, ReceiptModal, WriteOffModal } from './MaterialModals';

function expiresBadge(item: MaterialItem) {
  if (!item.min_expires) return <Badge tone="muted">без срока</Badge>;
  const days = Math.round((new Date(item.min_expires).getTime() - Date.now()) / 86400000);
  if (days < 0) return <Badge tone="err">истёк</Badge>;
  if (days <= 14) return <Badge tone="warn">{days} дн.</Badge>;
  return <Badge tone="ok">{fmtDate(item.min_expires)}</Badge>;
}

function ItemModal({ onClose, onDone }: { onClose: () => void; onDone: () => void }) {
  const [name, setName] = useState('');
  const [type, setType] = useState<MaterialType>('reagent');
  const [unit, setUnit] = useState('');
  const [storage, setStorage] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      await api.post('/materials/items', {
        name: name.trim(),
        type,
        unit: unit.trim(),
        storage_place: storage.trim(),
      });
      onDone();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ошибка');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal title="Новый материал" onClose={onClose}>
      <Field label="Наименование">
        <input value={name} onChange={(e) => setName(e.target.value)} autoFocus />
      </Field>
      <Field label="Тип">
        <select value={type} onChange={(e) => setType(e.target.value as MaterialType)}>
          <option value="reagent">Реактив</option>
          <option value="so">Стандартный образец</option>
          <option value="precursor">Прекурсор</option>
        </select>
      </Field>
      <div className="form-grid">
        <Field label="Единица">
          <input value={unit} onChange={(e) => setUnit(e.target.value)} placeholder="г, мл, амп." />
        </Field>
        <Field label="Место хранения">
          <input value={storage} onChange={(e) => setStorage(e.target.value)} placeholder="Шкаф №3" />
        </Field>
      </div>
      <ErrorBox message={error} />
      <div className="row" style={{ justifyContent: 'flex-end' }}>
        <button className="btn" onClick={onClose}>
          Отмена
        </button>
        <button
          className="btn btn-primary"
          disabled={busy || !name.trim() || !unit.trim() || !storage.trim()}
          onClick={save}
        >
          Создать
        </button>
      </div>
    </Modal>
  );
}

export default function MaterialsPage() {
  const { user } = useAuth();
  const [tab, setTab] = useState<'items' | 'batches' | 'ops'>('items');
  const [items, setItems] = useState<MaterialItem[] | null>(null);
  const [batches, setBatches] = useState<Batch[] | null>(null);
  const [ops, setOps] = useState<Operation[] | null>(null);
  const [opsFilter, setOpsFilter] = useState<'' | OpType>('');
  const [error, setError] = useState<string | null>(null);
  const [modal, setModal] = useState<null | 'receipt' | 'writeoff' | 'inventory' | 'item'>(null);

  const load = useCallback(() => {
    api
      .get<MaterialItem[]>('/materials/items')
      .then(setItems)
      .catch((e) => setError(e.message));
    api.get<Batch[]>('/materials/batches').then(setBatches).catch(() => setBatches([]));
    api
      .get<Operation[]>(`/materials/operations${opsFilter ? `?op_type=${opsFilter}` : ''}`)
      .then(setOps)
      .catch((e) => setError(e.message));
  }, [opsFilter]);

  useEffect(() => {
    load();
  }, [load]);

  const canOperate = user?.role === 'admin' || user?.role === 'engineer';
  const afterSave = () => {
    setModal(null);
    load();
  };

  if (error && !items) return <ErrorBox message={error} />;
  if (!items || !batches || !ops) return <Loading />;

  return (
    <div>
      <div className="page-title">
        <div>
          <h1>Материалы</h1>
          <p className="subtitle">
            Реактивы, стандартные образцы и прекурсоры · поступления, списания, сроки годности
          </p>
        </div>
        <div className="actions">
          {canOperate && (
            <button className="btn btn-primary" onClick={() => setModal('receipt')}>
              + Поступление
            </button>
          )}
          {canOperate && (
            <button className="btn" onClick={() => setModal('writeoff')}>
              Списание
            </button>
          )}
          {(user?.role === 'admin' || user?.can_wf_precursors) && (
            <button className="btn" onClick={() => setModal('inventory')}>
              Инвентаризация
            </button>
          )}
          {user?.role === 'admin' && (
            <button className="btn" onClick={() => setModal('item')}>
              + Материал
            </button>
          )}
        </div>
      </div>
      <ErrorBox message={error} />

      <div className="tabs">
        <button className={tab === 'items' ? 'active' : ''} onClick={() => setTab('items')}>
          Номенклатура
        </button>
        <button className={tab === 'batches' ? 'active' : ''} onClick={() => setTab('batches')}>
          Партии
        </button>
        <button className={tab === 'ops' ? 'active' : ''} onClick={() => setTab('ops')}>
          Операции (журналы учёта)
        </button>
      </div>

      {tab === 'items' && (
        <div className="tbl-wrap">
          {items.length === 0 ? (
            <Empty text="Материалов нет" />
          ) : (
            <table className="tbl">
              <thead>
                <tr>
                  <th>Наименование</th>
                  <th>Тип</th>
                  <th>Ед.</th>
                  <th>Место хранения</th>
                  <th className="num">Остаток</th>
                  <th>Ближайший срок годности</th>
                </tr>
              </thead>
              <tbody>
                {items.map((i) => (
                  <tr key={i.id}>
                    <td>{i.name}</td>
                    <td>
                      <Badge tone={i.type === 'precursor' ? 'err' : i.type === 'so' ? 'info' : undefined}>
                        {i.type_title}
                      </Badge>
                    </td>
                    <td>{i.unit}</td>
                    <td>{i.storage_place}</td>
                    <td className="num">
                      {i.total_qty} {i.unit}
                    </td>
                    <td>{expiresBadge(i)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}

      {tab === 'batches' && (
        <div className="tbl-wrap">
          {batches.length === 0 ? (
            <Empty text="Партий нет" />
          ) : (
            <table className="tbl">
              <thead>
                <tr>
                  <th>Материал</th>
                  <th>Партия</th>
                  <th className="num">Остаток</th>
                  <th>Срок годности</th>
                  <th>Статус</th>
                  <th>Поступление</th>
                </tr>
              </thead>
              <tbody>
                {batches.map((b) => (
                  <tr key={b.id}>
                    <td>
                      {b.item_name}
                      {b.item_type === 'precursor' && (
                        <>
                          {' '}
                          <Badge tone="err">прекурсор</Badge>
                        </>
                      )}
                    </td>
                    <td className="mono">{b.batch_no}</td>
                    <td className="num">
                      {b.qty} {b.unit}
                    </td>
                    <td>
                      {b.expires_on ? (
                        <>
                          {fmtDate(b.expires_on)}{' '}
                          {b.days_left !== null && b.days_left < 0 ? (
                            <Badge tone="err">истёк</Badge>
                          ) : b.days_left !== null && b.days_left <= 14 ? (
                            <Badge tone="warn">{b.days_left} дн.</Badge>
                          ) : null}
                        </>
                      ) : (
                        '—'
                      )}
                    </td>
                    <td>
                      {b.status === 'active' ? (
                        <Badge tone="ok">активна</Badge>
                      ) : (
                        <Badge tone="err">заблокирована</Badge>
                      )}
                    </td>
                    <td className="muted">
                      {fmtDate(b.received_at)} · {b.received_by_name}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}

      {tab === 'ops' && (
        <>
          <div className="filters">
            <select value={opsFilter} onChange={(e) => setOpsFilter(e.target.value as '' | OpType)}>
              <option value="">все операции</option>
              <option value="receipt">поступление</option>
              <option value="writeoff">списание</option>
              <option value="inventory">инвентаризация</option>
              <option value="check">проверка пригодности</option>
            </select>
          </div>
          <div className="tbl-wrap">
            {ops.length === 0 ? (
              <Empty text="Операций нет" />
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
                            o.op_type === 'writeoff' ? 'warn' : o.op_type === 'receipt' ? 'ok' : 'info'
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
        </>
      )}

      {modal === 'receipt' && (
        <ReceiptModal items={items} onClose={() => setModal(null)} onDone={afterSave} />
      )}
      {modal === 'writeoff' && (
        <WriteOffModal
          items={items}
          precursor={false}
          hasRight={user?.role === 'admin' || !!user?.can_wf_precursors}
          onClose={() => setModal(null)}
          onDone={afterSave}
        />
      )}
      {modal === 'inventory' && (
        <InventoryModal
          batches={batches.filter((b) => b.item_type === 'precursor')}
          onClose={() => setModal(null)}
          onDone={afterSave}
        />
      )}
      {modal === 'item' && <ItemModal onClose={() => setModal(null)} onDone={afterSave} />}
    </div>
  );
}
