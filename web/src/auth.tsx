import { createContext, useCallback, useContext, useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { api, clearToken, getToken, setToken } from './api';
import type { User } from './types';

interface AuthState {
  user: User | null;
  loading: boolean;
  login: (loginName: string, password: string) => Promise<void>;
  logout: () => void;
}

const AuthContext = createContext<AuthState>({
  user: null,
  loading: true,
  login: async () => undefined,
  logout: () => undefined,
});

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    if (!getToken()) {
      setLoading(false);
      return;
    }
    api
      .get<User>('/auth/me')
      .then(setUser)
      .catch(() => {
        clearToken();
        setUser(null);
      })
      .finally(() => setLoading(false));
  }, []);

  const doLogin = useCallback(async (loginName: string, password: string) => {
    const res = await api.post<{ token: string; user: User }>('/auth/login', {
      login: loginName,
      password,
    });
    setToken(res.token);
    setUser(res.user);
  }, []);

  const doLogout = useCallback(() => {
    clearToken();
    setUser(null);
  }, []);

  return (
    <AuthContext.Provider value={{ user, loading, login: doLogin, logout: doLogout }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() {
  return useContext(AuthContext);
}
