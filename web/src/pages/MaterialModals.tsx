import { useState } from 'react';
import { api } from '../api';
import type { Batch, MaterialItem } from '../types';
import { ErrorBox, Field, Modal } from '../ui';

export function ReceiptModal({
  items,
  onClose,
  onDone,
}: {
  items: MaterialItem[];
  onClose: () => void;
  onDone: () => void;
}) {
  const [itemId, setItemId] = useState<number | ''>('');
  const [qty, setQty] = useState('');
  const [batchNo, setBatchNo] = useState('');
  const [expires, setExpires] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const item = items.find((i) => i.id === itemId);

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      await api.post('/materials/receipts', {
        item_id: itemId,
        qty: Number(String(qty).replace(',', '.')),
        batch_no: batchNo,
        expires_on: expires || null,
      });
      onDone();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ошибка');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal title="Поступление материала" onClose={onClose}>
      <Field label="Материал">
        <select
          value={itemId}
          onChange={(e) => setItemId(e.target.value ? Number(e.target.value) : '')}
        >
          <option value="">— выберите —</option>
          {items.map((i) => (
            <option key={i.id} value={i.id}>
              {i.name} ({i.type_title})
            </option>
          ))}
        </select>
      </Field>
      <div className="form-grid">
        <Field label={`Количество${item ? `, ${item.unit}` : ''}`}>
          <input
            type="number"
            step="any"
            value={qty}
            onChange={(e) => setQty(e.target.value)}
          />
        </Field>
        <Field label="Партия">
          <input value={batchNo} onChange={(e) => setBatchNo(e.target.value)} />
        </Field>
      </div>
      <Field label="Годен до" hint="необязательно">
        <input type="date" value={expires} onChange={(e) => setExpires(e.target.value)} />
      </Field>
      <ErrorBox message={error} />
      <div className="row" style={{ justifyContent: 'flex-end' }}>
        <button className="btn" onClick={onClose}>
          Отмена
        </button>
        <button
          className="btn btn-primary"
          disabled={busy || !itemId || !qty || !batchNo.trim()}
          onClick={save}
        >
          Оформить поступление
        </button>
      </div>
    </Modal>
  );
}

export function WriteOffModal({
  items,
  precursor,
  hasRight,
  onClose,
  onDone,
}: {
  items: MaterialItem[];
  precursor: boolean;
  hasRight: boolean;
  onClose: () => void;
  onDone: () => void;
}) {
  const [itemId, setItemId] = useState<number | ''>('');
  const [qty, setQty] = useState('');
  const [targetJournal, setTargetJournal] = useState('');
  const [targetRecord, setTargetRecord] = useState('');
  const [note, setNote] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const item = items.find((i) => i.id === itemId);

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      await api.post('/materials/writeoffs', {
        item_id: itemId,
        qty: Number(String(qty).replace(',', '.')),
        target_journal_code: targetJournal.trim() || null,
        target_record_id: targetRecord ? Number(targetRecord) : null,
        note: note.trim() || null,
      });
      onDone();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ошибка');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal title="Списание материала" onClose={onClose}>
      {precursor && !hasRight && (
        <div className="error-box">
          Прекурсоры: списание требует отдельного права. Сервис не пропустит операцию — её
          выполнит сотрудник с правом (например, eng1).
        </div>
      )}
      <Field label="Материал">
        <select
          value={itemId}
          onChange={(e) => setItemId(e.target.value ? Number(e.target.value) : '')}
        >
          <option value="">— выберите —</option>
          {items.map((i) => (
            <option key={i.id} value={i.id}>
              {i.name} ({i.type_title}) — остаток {i.total_qty} {i.unit}
            </option>
          ))}
        </select>
      </Field>
      <div className="form-grid">
        <Field label={`Количество${item ? `, ${item.unit}` : ''}`}>
          <input type="number" step="any" value={qty} onChange={(e) => setQty(e.target.value)} />
        </Field>
        <Field label="Шифр журнала (куда)" hint="необязательно, прослеживаемость">
          <input
            value={targetJournal}
            placeholder="Ж-04/02"
            onChange={(e) => setTargetJournal(e.target.value)}
          />
        </Field>
      </div>
      <Field label="№ записи (куда)" hint="необязательно">
        <input
          type="number"
          value={targetRecord}
          onChange={(e) => setTargetRecord(e.target.value)}
        />
      </Field>
      <Field label="Примечание">
        <input value={note} onChange={(e) => setNote(e.target.value)} />
      </Field>
      <ErrorBox message={error} />
      <div className="row" style={{ justifyContent: 'flex-end' }}>
        <button className="btn" onClick={onClose}>
          Отмена
        </button>
        <button
          className="btn btn-primary"
          disabled={busy || !itemId || !qty}
          onClick={save}
        >
          Списать
        </button>
      </div>
    </Modal>
  );
}

export function InventoryModal({
  batches,
  onClose,
  onDone,
}: {
  batches: Batch[];
  onClose: () => void;
  onDone: () => void;
}) {
  const [batchId, setBatchId] = useState<number | ''>('');
  const [actual, setActual] = useState('');
  const [note, setNote] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const batch = batches.find((b) => b.id === batchId);

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      await api.post(`/materials/batches/${batchId}/inventory`, {
        actual_qty: Number(String(actual).replace(',', '.')),
        note: note.trim() || null,
      });
      onDone();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ошибка');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal title="Инвентаризация партии (прекурсоры)" onClose={onClose}>
      <Field label="Партия">
        <select
          value={batchId}
          onChange={(e) => setBatchId(e.target.value ? Number(e.target.value) : '')}
        >
          <option value="">— выберите —</option>
          {batches.map((b) => (
            <option key={b.id} value={b.id}>
              {b.item_name} · партия {b.batch_no} · остаток {b.qty} {b.unit}
            </option>
          ))}
        </select>
      </Field>
      <Field
        label={`Фактический остаток${batch ? `, ${batch.unit}` : ''}`}
        hint={batch ? `по учёту: ${batch.qty}` : undefined}
      >
        <input type="number" step="any" value={actual} onChange={(e) => setActual(e.target.value)} />
      </Field>
      <Field label="Примечание">
        <input value={note} onChange={(e) => setNote(e.target.value)} />
      </Field>
      <ErrorBox message={error} />
      <div className="row" style={{ justifyContent: 'flex-end' }}>
        <button className="btn" onClick={onClose}>
          Отмена
        </button>
        <button className="btn btn-primary" disabled={busy || !batchId || !actual} onClick={save}>
          Зафиксировать
        </button>
      </div>
    </Modal>
  );
}
