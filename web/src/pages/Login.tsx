import { useState } from 'react';
import type { FormEvent } from 'react';
import { Navigate } from 'react-router-dom';
import { useAuth } from '../auth';
import { ErrorBox } from '../ui';

export default function LoginPage() {
  const { user, login } = useAuth();
  const [name, setName] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  if (user) return <Navigate to="/journals" replace />;

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await login(name.trim(), password);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Ошибка входа');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="login-wrap">
      <form className="login-card" onSubmit={submit}>
        <div className="logo">ЭЖЛ · ЭЛЕКТРОННЫЕ ЖУРНАЛЫ ЛАБОРАТОРИИ</div>
        <h1>Вход в систему</h1>
        <p className="subtitle">ГОСТ ISO/IEC 17025-2019 · ЦКП «КУЭП»</p>
        <label className="field">
          <span className="field-label">Логин</span>
          <input value={name} onChange={(e) => setName(e.target.value)} autoFocus />
        </label>
        <label className="field">
          <span className="field-label">Пароль</span>
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
        </label>
        <ErrorBox message={error} />
        <button
          className="btn btn-primary"
          disabled={busy || !name || !password}
          style={{ width: '100%', justifyContent: 'center' }}
        >
          {busy ? 'Входим…' : 'Войти'}
        </button>
        <div className="demo-creds">
          <b>Демо-пользователи</b> — пароль у всех: <span className="mono">demo123</span>
          <table>
            <tbody>
              <tr>
                <td className="mono">admin</td>
                <td>администратор</td>
              </tr>
              <tr>
                <td className="mono">head</td>
                <td>заведующий лабораторией</td>
              </tr>
              <tr>
                <td className="mono">qm</td>
                <td>менеджер по качеству</td>
              </tr>
              <tr>
                <td className="mono">eng1</td>
                <td>инженер · право на прекурсоры</td>
              </tr>
              <tr>
                <td className="mono">lab1</td>
                <td>лаборант</td>
              </tr>
              <tr>
                <td className="mono">lab2</td>
                <td>лаборант</td>
              </tr>
            </tbody>
          </table>
        </div>
      </form>
    </div>
  );
}
