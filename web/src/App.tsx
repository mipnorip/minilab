import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { useEffect } from 'react';
import type { ReactElement } from 'react';
import { AuthProvider, useAuth } from './auth';
import LoginPage from './pages/Login';
import Layout from './pages/Layout';
import JournalsPage from './pages/Journals';
import JournalPage from './pages/Journal';
import NewJournalPage from './pages/NewJournal';
import TemplatesPage from './pages/Templates';
import MaterialsPage from './pages/Materials';
import NotificationsPage from './pages/Notifications';
import AuditPage from './pages/Audit';
import UsersPage from './pages/Users';

function RequireAuth({ children }: { children: ReactElement }) {
  const { user, loading } = useAuth();
  if (loading) return <div className="page-loading">Загрузка…</div>;
  if (!user) return <Navigate to="/login" replace />;
  return children;
}

export default function App() {
  // пульс desktop-приложения: пока окно открыто, сервер жив
  useEffect(() => {
    const timer = setInterval(() => {
      fetch('/api/ping', { method: 'POST' }).catch(() => undefined);
    }, 5000);
    return () => clearInterval(timer);
  }, []);

  return (
    <AuthProvider>
      <BrowserRouter>
        <Routes>
          <Route path="/login" element={<LoginPage />} />
          <Route
            element={
              <RequireAuth>
                <Layout />
              </RequireAuth>
            }
          >
            <Route index element={<Navigate to="/journals" replace />} />
            <Route path="journals" element={<JournalsPage />} />
            <Route path="journals/new" element={<NewJournalPage />} />
            <Route path="journals/:id" element={<JournalPage />} />
            <Route path="materials" element={<MaterialsPage />} />
            <Route path="notifications" element={<NotificationsPage />} />
            <Route path="audit" element={<AuditPage />} />
            <Route path="users" element={<UsersPage />} />
            <Route path="templates" element={<TemplatesPage />} />
          </Route>
        </Routes>
      </BrowserRouter>
    </AuthProvider>
  );
}
