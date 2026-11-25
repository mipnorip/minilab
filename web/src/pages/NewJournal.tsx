import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { api } from '../api';
import type { Journal, Template, User } from '../types';
import { ErrorBox, Field, Loading } from '../ui';

export default function NewJournalPage() {
  const navigate = useNavigate();
  const [templates, setTemplates] = useState<Template[] | null>(null);
  const [users, setUsers] = useState<User[]>([]);
  const [error, setError] = useState<string | null>(null);

  const [kind, setKind] = useState<'form' | 'materials'>('form');
  const [templateId, setTemplateId] = useState<number | ''>('');
  const [materialsType, setMaterialsType] = useState<'reagent' | 'so' | 'precursor'>('reagent');
  const [code, setCode] = useState('');
  const [title, setTitle] = useState('');
  const [ownerId, setOwnerId] = useState<number | ''>('');
  const [memberIds, setMemberIds] = useState<number[]>([]);
  const [checkWeeks, setCheckWeeks] = useState(4);
  const [retention, setRetention] = useState(5);
  const [daily, setDaily] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    api
      .get<Template[]>('/forms/templates')
      .then((list) => {
        setTemplates(list);
        const firstPreset = list.find((t) => t.is_preset);
        if (firstPreset) setTemplateId(firstPreset.id);
      })
      .catch((e) => setError(e.message));
    api.get<User[]>('/auth/users').then(setUsers).catch(() => undefined);
  }, []);

  if (error && !templates) return <ErrorBox message={error} />;
  if (!templates) return <Loading />;

  const engineers = users.filter((u) => u.role === 'engineer' && u.is_active);
  const template = templates.find((t) => t.id === templateId);

  const toggleMember = (id: number) =>
    setMemberIds((prev) => (prev.includes(id) ? prev.filter((x) => x !== id) : [...prev, id]));

  const save = async () => {
    setError(null);
    const owner = users.find((u) => u.id === ownerId);
    if (!code.trim()) return setError('Укажите шифр журнала (например, Ж-04/05)');
    if (!owner) return setError('Выберите ответственного за журнал');
    if (kind === 'form' && !templateId) return setError('Выберите шаблон формы');
    setBusy(true);
    try {
      const created = await api.post<Journal>('/records/journals', {
        code: code.trim(),
        title: (title.trim() || template?.title || 'Журнал').trim(),
        kind,
        form_template_id: kind === 'form' ? templateId : null,
        materials_type: kind === 'materials' ? materialsType : null,
        owner_id: ownerId,
        owner_name: owner.full_name,
        members: engineers
          .filter((u) => memberIds.includes(u.id))
          .map((u) => ({ user_id: u.id, name: u.full_name, role: 'worker' })),
        check_period_weeks: checkWeeks,
        retention_years: retention,
        daily,
      });
      navigate(`/journals/${created.id}`);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ошибка');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div style={{ maxWidth: 760, margin: '0 auto' }}>
      <div className="page-title">
        <div>
          <h1>Новый журнал</h1>
          <p className="subtitle">Создаётся из готового пресета или шаблона формы</p>
        </div>
      </div>
      <div className="card">
        <div className="tabs">
          <button className={kind === 'form' ? 'active' : ''} onClick={() => setKind('form')}>
            Журнал с формой
          </button>
          <button className={kind === 'materials' ? 'active' : ''} onClick={() => setKind('materials')}>
            Журнал учёта материалов
          </button>
        </div>

        <div className="form-grid">
          {kind === 'form' ? (
            <Field label="Шаблон формы" hint={template?.is_preset ? 'пресет из перечня журналов' : undefined}>
              <select
                value={templateId}
                onChange={(e) => setTemplateId(e.target.value ? Number(e.target.value) : '')}
              >
                {templates.map((t) => (
                  <option key={t.id} value={t.id}>
                    {t.code} · {t.title} {t.is_preset ? '(пресет)' : ''}
                  </option>
                ))}
              </select>
            </Field>
          ) : (
            <Field label="Что учитывает журнал">
              <select
                value={materialsType}
                onChange={(e) => setMaterialsType(e.target.value as 'reagent' | 'so' | 'precursor')}
              >
                <option value="reagent">Реактивы</option>
                <option value="so">Стандартные образцы</option>
                <option value="precursor">Прекурсоры (усиленный режим)</option>
              </select>
            </Field>
          )}
          <Field label="Шифр журнала" hint="как в номенклатуре дел, напр. Ж-04/05">
            <input value={code} onChange={(e) => setCode(e.target.value)} placeholder="Ж-04/05" />
          </Field>
          <Field label="Название" hint={`по умолчанию: ${template?.title || '—'}`}>
            <input value={title} onChange={(e) => setTitle(e.target.value)} />
          </Field>
          <Field label="Ответственный">
            <select
              value={ownerId}
              onChange={(e) => setOwnerId(e.target.value ? Number(e.target.value) : '')}
            >
              <option value="">— выберите —</option>
              {users
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

        {kind === 'form' && (
          <label className="checkbox" style={{ margin: '6px 0 12px' }}>
            <input type="checkbox" checked={daily} onChange={(e) => setDaily(e.target.checked)} />
            Ежедневный журнал (контроль отсутствия записи)
          </label>
        )}

        <h3>Участники (могут вносить записи)</h3>
        {engineers.length === 0 && <p className="muted">Нет активных инженеров/лаборантов</p>}
        {engineers.map((u) => (
          <label key={u.id} className="checkbox" style={{ display: 'inline-flex', marginRight: 18 }}>
            <input
              type="checkbox"
              checked={memberIds.includes(u.id)}
              onChange={() => toggleMember(u.id)}
            />
            {u.full_name}
          </label>
        ))}

        <ErrorBox message={error} />
        <div className="row" style={{ justifyContent: 'flex-end', marginTop: 10 }}>
          <button className="btn" onClick={() => navigate('/journals')}>
            Отмена
          </button>
          <button className="btn btn-primary" disabled={busy} onClick={save}>
            Создать журнал
          </button>
        </div>
      </div>
    </div>
  );
}
