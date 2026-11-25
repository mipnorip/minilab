import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api } from '../api';
import { useAuth } from '../auth';
import type { Journal } from '../types';
import { Badge, ErrorBox, Empty, Loading } from '../ui';
import { fmtDate, isOverdue } from '../utils';

function JournalsTable({ journals }: { journals: Journal[] }) {
  return (
    <div className="tbl-wrap">
      <table className="tbl">
        <thead>
          <tr>
            <th>Шифр</th>
            <th>Журнал</th>
            <th>Вид</th>
            <th>Статус</th>
            <th>Ответственный</th>
            <th>Периодичность</th>
            <th>Следующая проверка</th>
          </tr>
        </thead>
        <tbody>
          {journals.map((j) => (
            <tr key={j.id}>
              <td className="mono">
                <Link to={`/journals/${j.id}`}>{j.code}</Link>
              </td>
              <td>
                <Link to={`/journals/${j.id}`}>{j.title}</Link>
                {j.daily && (
                  <>
                    {' '}
                    <Badge tone="info">ежедневный</Badge>
                  </>
                )}
              </td>
              <td>
                {j.kind === 'form' ? (
                  <Badge>форма</Badge>
                ) : (
                  <Badge tone="warn">
                    материалы
                    {j.materials_type === 'precursor' ? ' · прекурсоры' : ''}
                  </Badge>
                )}
              </td>
              <td>
                {j.status === 'active' && <Badge tone="ok">ведётся</Badge>}
                {j.status === 'closed' && <Badge tone="muted">закрыт</Badge>}
                {j.status === 'archived' && <Badge tone="err">в архиве</Badge>}
              </td>
              <td>{j.owner_name}</td>
              <td>{j.check_period_weeks} нед.</td>
              <td>
                {fmtDate(j.next_review_at)}
                {j.status === 'active' && isOverdue(j.next_review_at) && (
                  <>
                    {' '}
                    <Badge tone="err">просрочена</Badge>
                  </>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export default function JournalsPage() {
  const { user } = useAuth();
  const [journals, setJournals] = useState<Journal[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api
      .get<Journal[]>('/records/journals')
      .then(setJournals)
      .catch((e) => setError(e.message));
  }, []);

  if (error) return <ErrorBox message={error} />;
  if (!journals) return <Loading />;

  const my = journals.filter((j) => j.is_member && j.status === 'active');
  const rest = journals.filter((j) => !(j.is_member && j.status === 'active'));

  return (
    <div>
      <div className="page-title">
        <div>
          <h1>Журналы</h1>
          <p className="subtitle">
            Электронные журналы лаборатории · всего ведётся: {journals.length}
          </p>
        </div>
        {user?.role === 'admin' && (
          <div className="actions">
            <Link to="/journals/new" className="btn btn-primary">
              + Создать журнал
            </Link>
          </div>
        )}
      </div>
      {my.length > 0 && (
        <>
          <h2 style={{ fontSize: 15, margin: '0 0 8px' }}>Мои журналы</h2>
          <JournalsTable journals={my} />
          <div style={{ height: 22 }} />
        </>
      )}
      <h2 style={{ fontSize: 15, margin: '0 0 8px' }}>Все журналы лаборатории</h2>
      {rest.length ? <JournalsTable journals={rest} /> : <Empty text="Других журналов нет" />}
    </div>
  );
}
