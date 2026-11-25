import type { ReactNode } from 'react';

export function Modal({
  title,
  onClose,
  children,
  wide,
}: {
  title: string;
  onClose: () => void;
  children: ReactNode;
  wide?: boolean;
}) {
  return (
    <div
      className="modal-overlay"
      onMouseDown={(e) => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <div className={wide ? 'modal modal-wide' : 'modal'}>
        <div className="modal-head">
          <h3>{title}</h3>
          <button className="btn btn-ghost" onClick={onClose} aria-label="Закрыть">
            ✕
          </button>
        </div>
        <div className="modal-body">{children}</div>
      </div>
    </div>
  );
}

export function Field({
  label,
  children,
  hint,
}: {
  label: string;
  children: ReactNode;
  hint?: string;
}) {
  return (
    <label className="field">
      <span className="field-label">{label}</span>
      {children}
      {hint ? <span className="field-hint">{hint}</span> : null}
    </label>
  );
}

export function ErrorBox({ message }: { message: string | null }) {
  if (!message) return null;
  return <div className="error-box">{message}</div>;
}

export function Badge({
  tone,
  children,
}: {
  tone?: 'ok' | 'warn' | 'err' | 'info' | 'muted';
  children: ReactNode;
}) {
  return <span className={`badge badge-${tone || 'muted'}`}>{children}</span>;
}

export function Empty({ text }: { text: string }) {
  return <div className="empty">{text}</div>;
}

export function Loading({ text = 'Загрузка…' }: { text?: string }) {
  return <div className="page-loading">{text}</div>;
}
