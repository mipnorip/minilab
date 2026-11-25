import { useCallback, useEffect, useState } from 'react';
import { api } from '../api';
import type { Role, User } from '../types';
import { Badge, ErrorBox, Field, Loading, Modal } from '../ui';

const ROLES: { value: Role; label: string }[] = [
  { value: 'engineer', label: 'Инженер / лаборант' },
  { value: 'quality_manager', label: 'Менеджер по качеству' },
  { value: 'head', label: 'Заведующий лабораторией' },
  { value: 'admin', label: 'Администратор' },
];

function UserModal({
  user,
  onClose,
  onDone,
}: {
  user: User | null;
  onClose: () => void;
  onDone: () => void;
}) {
  const isEdit = !!user;
  const [fullName, setFullName] = useState(user?.full_name || '');
  const [login, setLogin] = useState(user?.login || '');
  const [password, setPassword] = useState('');
  const [role, setRole] = useState<Role>(user?.role || 'engineer');
  const [pwp, setPwp] = useState(user?.can_wf_precursors || false);
  const [isActive, setIsActive] = useState(user?.is_active ?? true);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      if (isEdit) {
        await api.patch(`/auth/users/${user!.id}`, {
          full_name: fullName.trim(),
          role,
          can_wf_precursors: pwp,
          is_active: isActive,
          ...(password ? { password } : {}),
        });
      } else {
        await api.post('/auth/users', {
          login: login.trim(),
          full_name: fullName.trim(),
          password,
          role,
          can_wf_precursors: pwp,
        });
      }
      onDone();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ошибка');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal title={isEdit ? `Пользователь: ${user!.full_name}` : 'Новый пользователь'} onClose={onClose}>
      <Field label="ФИО">
        <input value={fullName} onChange={(e) => setFullName(e.target.value)} autoFocus />
      </Field>
      {!isEdit && (
        <Field label="Логин" hint="латиница">
          <input value={login} onChange={(e) => setLogin(e.target.value)} />
        </Field>
      )}
      <Field label={isEdit ? 'Новый пароль' : 'Пароль'} hint={isEdit ? 'оставьте пустым, чтобы не менять' : undefined}>
        <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} />
      </Field>
      <Field label="Роль">
        <select value={role} onChange={(e) => setRole(e.target.value as Role)}>
          {ROLES.map((r) => (
            <option key={r.value} value={r.value}>
              {r.label}
            </option>
          ))}
        </select>
      </Field>
      <label className="checkbox" style={{ marginBottom: 8 }}>
        <input type="checkbox" checked={pwp} onChange={(e) => setPwp(e.target.checked)} />
        Отдельное право на списание прекурсоров
      </label>
      {isEdit && (
        <label className="checkbox" style={{ marginBottom: 8 }}>
          <input type="checkbox" checked={isActive} onChange={(e) => setIsActive(e.target.checked)} />
          Активен
        </label>
      )}
      <ErrorBox message={error} />
      <div className="row" style={{ justifyContent: 'flex-end' }}>
        <button className="btn" onClick={onClose}>
          Отмена
        </button>
        <button
          className="btn btn-primary"
          disabled={busy || !fullName.trim() || (!isEdit && (!login.trim() || !password))}
          onClick={save}
        >
          {isEdit ? 'Сохранить' : 'Создать'}
        </button>
      </div>
    </Modal>
  );
}

export default function UsersPage() {
  const [users, setUsers] = useState<User[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<User | null | 'new'>(null);

  const load = useCallback(() => {
    api
      .get<User[]>('/auth/users')
      .then(setUsers)
      .catch((e) => setError(e.message));
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  if (error && !users) return <ErrorBox message={error} />;
  if (!users) return <Loading />;

  return (
    <div>
      <div className="page-title">
        <div>
          <h1>Пользователи</h1>
          <p className="subtitle">Роли и отдельные права (прекурсоры)</p>
        </div>
        <div className="actions">
          <button className="btn btn-primary" onClick={() => setEditing('new')}>
            + Пользователь
          </button>
        </div>
      </div>
      <ErrorBox message={error} />
      <div className="tbl-wrap">
        <table className="tbl">
          <thead>
            <tr>
              <th>ФИО</th>
              <th>Логин</th>
              <th>Роль</th>
              <th>Прекурсоры</th>
              <th>Статус</th>
              <th>Действия</th>
            </tr>
          </thead>
          <tbody>
            {users.map((u) => (
              <tr key={u.id}>
                <td>{u.full_name}</td>
                <td className="mono">{u.login}</td>
                <td>{u.role_title}</td>
                <td>
                  {u.can_wf_precursors ? (
                    <Badge tone="warn">право есть</Badge>
                  ) : (
                    <span className="muted">—</span>
                  )}
                </td>
                <td>{u.is_active ? <Badge tone="ok">активен</Badge> : <Badge tone="err">отключён</Badge>}</td>
                <td className="row-actions">
                  <button className="btn btn-sm" onClick={() => setEditing(u)}>
                    Изменить
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {editing !== null && (
        <UserModal
          user={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onDone={() => {
            setEditing(null);
            load();
          }}
        />
      )}
    </div>
  );
}
