import { useEffect, useState } from 'react';
import { NavLink, Outlet, useNavigate } from 'react-router-dom';
import { useAuth } from '../auth';
import { api } from '../api';
import type { AppNotification } from '../types';

export default function Layout() {
  const { user, logout } = useAuth();
  const navigate = useNavigate();
  const [unread, setUnread] = useState(0);

  useEffect(() => {
    let alive = true;
    const load = () => {
      api
        .get<AppNotification[]>('/notifications/list?limit=100')
        .then((list) => {
          if (alive) setUnread(list.filter((n) => !n.is_read).length);
        })
        .catch(() => undefined);
    };
    load();
    const timer = setInterval(load, 60000);
    return () => {
      alive = false;
      clearInterval(timer);
    };
  }, []);

  if (!user) return null;
  const role = user.role;
  const linkClass = (isActive: boolean) => (isActive ? 'active' : '');

  return (
    <div>
      <header className="topbar">
        <span className="brand">ЭЖЛ · Журналы лаборатории</span>
        <nav>
          <NavLink to="/journals" className={({ isActive }) => linkClass(isActive)}>
            Журналы
          </NavLink>
          <NavLink to="/materials" className={({ isActive }) => linkClass(isActive)}>
            Материалы
          </NavLink>
          <NavLink to="/notifications" className={({ isActive }) => linkClass(isActive)}>
            Уведомления
          </NavLink>
          {(role === 'quality_manager' || role === 'head' || role === 'admin') && (
            <NavLink to="/audit" className={({ isActive }) => linkClass(isActive)}>
              Аудит
            </NavLink>
          )}
          {role === 'admin' && (
            <NavLink to="/templates" className={({ isActive }) => linkClass(isActive)}>
              Шаблоны форм
            </NavLink>
          )}
          {role === 'admin' && (
            <NavLink to="/users" className={({ isActive }) => linkClass(isActive)}>
              Пользователи
            </NavLink>
          )}
        </nav>
        <button
          className="bell"
          title="Уведомления"
          onClick={() => navigate('/notifications')}
        >
          🔔
          {unread > 0 && <span className="count">{unread > 99 ? '99+' : unread}</span>}
        </button>
        <div className="user">
          <span>{user.full_name}</span>
          <span className="role">{user.role_title}</span>
          <button
            className="btn btn-sm"
            onClick={() => {
              logout();
              navigate('/login');
            }}
          >
            Выйти
          </button>
        </div>
      </header>
      <main className="page">
        <Outlet />
      </main>
    </div>
  );
}
