export type Role = 'admin' | 'head' | 'quality_manager' | 'engineer';

export interface User {
  id: number;
  login: string;
  full_name: string;
  role: Role;
  role_title: string;
  can_wf_precursors: boolean;
  is_active: boolean;
}

export type ColumnType =
  | 'text'
  | 'number'
  | 'date'
  | 'bool'
  | 'enum'
  | 'ref_journal'
  | 'ref_material'
  | 'formula'
  | 'auto';

export interface Column {
  key: string;
  title: string;
  type: ColumnType;
  required?: boolean;
  unit?: string | null;
  options?: string[] | null;
  formula?: string | null;
  ref_code?: string | null;
  auto?: 'row' | 'date' | 'author';
}

export interface Template {
  id: number;
  code: string;
  title: string;
  is_preset: boolean;
  current_version_id: number | null;
  version: number | null;
  columns?: Column[];
}

export interface TemplateVersion {
  id: number;
  template_id: number;
  version: number;
  comment: string | null;
  created_at: string;
  columns?: Column[];
}

export interface JournalMember {
  user_id: number;
  name: string;
  role: 'owner' | 'worker';
}

export interface Journal {
  id: number;
  code: string;
  title: string;
  kind: 'form' | 'materials';
  form_template_id: number | null;
  form_version_id: number | null;
  materials_type: 'reagent' | 'so' | 'precursor' | null;
  owner_id: number;
  owner_name: string;
  check_period_weeks: number;
  retention_years: number;
  status: 'active' | 'closed' | 'archived';
  daily: boolean;
  last_review_at: string | null;
  next_review_at: string | null;
  is_member?: boolean;
  members?: JournalMember[];
}

export interface RecordRow {
  id: number;
  journal_id: number;
  journal_code: string | null;
  row_no: number;
  data: Record<string, unknown>;
  status: 'entered' | 'closed';
  author_id: number;
  author_name: string;
  created_at: string;
  updated_at: string;
}

export interface Revision {
  id: number;
  record_id: number;
  old_data: Record<string, unknown>;
  new_data: Record<string, unknown>;
  reason_code: 'V' | 'O' | 'I';
  reason_comment: string | null;
  author_name: string;
  ts: string;
}

export interface ReviewLog {
  id: number;
  reviewer_name: string;
  note: string | null;
  ts: string;
}

export type MaterialType = 'reagent' | 'so' | 'precursor';

export interface MaterialItem {
  id: number;
  name: string;
  type: MaterialType;
  type_title: string;
  unit: string;
  storage_place: string;
  is_active: boolean;
  total_qty: number;
  min_expires: string | null;
}

export interface Batch {
  id: number;
  item_id: number;
  item_name: string;
  item_type: MaterialType;
  unit: string;
  batch_no: string;
  qty: number;
  expires_on: string | null;
  days_left: number | null;
  status: 'active' | 'blocked';
  received_at: string;
  received_by_name: string;
}

export type OpType = 'receipt' | 'writeoff' | 'inventory' | 'check';

export interface Operation {
  id: number;
  ts: string;
  item_id: number;
  item_name: string;
  item_type: MaterialType;
  op_type: OpType;
  op_type_title: string;
  batch_no: string | null;
  qty: number;
  unit: string;
  actor_name: string;
  note: string | null;
  target_journal_code: string | null;
  target_record_id: number | null;
}

export interface AppNotification {
  id: number;
  ts: string;
  type: 'material_expiring' | 'daily_missing' | 'review_due';
  title: string;
  body: string;
  entity_type: string | null;
  entity_id: string | null;
  is_read: boolean;
}

export interface AuditEvent {
  id: number;
  ts: string;
  actor_id: number | null;
  actor_name: string;
  action: string;
  entity_type: string;
  entity_id: string;
  entity_title: string | null;
  reason: string | null;
  payload: Record<string, unknown>;
}
