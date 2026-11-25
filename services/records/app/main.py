"""records-svc — реестр журналов, записи, исправления и проверки.

Схема БД: records. Ключевые правила ГОСТ ISO/IEC 17025-2019:
  * записи нельзя удалить (API не предоставляет удаления);
  * исправление = прежнее значение сохраняется в ревизии + причина + автор;
  * отметка о проверке журнала фиксируется отдельным логом;
  * прослеживаемость через связи записей (ref_journal/ref_material).
"""

import os
import re
from datetime import date, datetime, timedelta, timezone

import httpx
import jwt
from fastapi import FastAPI, HTTPException, Request
from pydantic import BaseModel, Field
from sqlalchemy import JSON, BigInteger, Boolean, DateTime, Integer, String, create_engine, text
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column, sessionmaker

JWT_SECRET = os.getenv("JWT_SECRET", "dev-jwt-secret")
INTERNAL_KEY = os.getenv("INTERNAL_KEY", "dev-internal-key")
FORMS_URL = os.getenv("FORMS_URL", "http://forms-svc:8002")
AUDIT_URL = os.getenv("AUDIT_URL", "http://audit-svc:8006")
DB_URL = os.getenv("DATABASE_URL", "postgresql+psycopg://minilab:minilab@db:5432/minilab")

REASON_CODES = {"V": "ошибка вычисления", "O": "опечатка/ошибочные данные", "I": "иное"}

engine = create_engine(DB_URL, pool_pre_ping=True)
with engine.begin() as conn:
    conn.execute(text("CREATE SCHEMA IF NOT EXISTS records"))
SessionLocal = sessionmaker(bind=engine, expire_on_commit=False)


class Base(DeclarativeBase):
    pass


class Journal(Base):
    __tablename__ = "journals"
    __table_args__ = {"schema": "records"}

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    code: Mapped[str] = mapped_column(String(32), unique=True, index=True)
    title: Mapped[str] = mapped_column(String(128))
    kind: Mapped[str] = mapped_column(String(16))  # form | materials
    form_template_id: Mapped[int | None] = mapped_column(Integer, nullable=True)
    form_version_id: Mapped[int | None] = mapped_column(Integer, nullable=True)
    materials_type: Mapped[str | None] = mapped_column(String(16), nullable=True)  # reagent | so | precursor
    owner_id: Mapped[int] = mapped_column(Integer)
    owner_name: Mapped[str] = mapped_column(String(128))
    check_period_weeks: Mapped[int] = mapped_column(Integer, default=4)
    retention_years: Mapped[int] = mapped_column(Integer, default=5)
    status: Mapped[str] = mapped_column(String(16), default="active")  # active | closed | archived
    daily: Mapped[bool] = mapped_column(Boolean, default=False)
    created_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), default=lambda: datetime.now(timezone.utc)
    )
    last_review_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)


class Member(Base):
    __tablename__ = "journal_members"
    __table_args__ = {"schema": "records"}

    journal_id: Mapped[int] = mapped_column(Integer, primary_key=True)
    user_id: Mapped[int] = mapped_column(Integer, primary_key=True)
    name: Mapped[str] = mapped_column(String(128))
    role: Mapped[str] = mapped_column(String(16))  # owner | worker


class Record(Base):
    __tablename__ = "records"
    __table_args__ = {"schema": "records"}

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True, autoincrement=True)
    journal_id: Mapped[int] = mapped_column(Integer, index=True)
    row_no: Mapped[int] = mapped_column(Integer)
    data: Mapped[dict] = mapped_column(JSON, default=dict)
    status: Mapped[str] = mapped_column(String(16), default="entered")  # entered | closed
    author_id: Mapped[int] = mapped_column(Integer)
    author_name: Mapped[str] = mapped_column(String(128))
    created_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), default=lambda: datetime.now(timezone.utc)
    )
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), default=lambda: datetime.now(timezone.utc)
    )


class Revision(Base):
    __tablename__ = "record_revisions"
    __table_args__ = {"schema": "records"}

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True, autoincrement=True)
    record_id: Mapped[int] = mapped_column(BigInteger, index=True)
    old_data: Mapped[dict] = mapped_column(JSON)
    new_data: Mapped[dict] = mapped_column(JSON)
    reason_code: Mapped[str] = mapped_column(String(4))
    reason_comment: Mapped[str | None] = mapped_column(String(256), nullable=True)
    author_id: Mapped[int] = mapped_column(Integer)
    author_name: Mapped[str] = mapped_column(String(128))
    ts: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), default=lambda: datetime.now(timezone.utc)
    )


class ReviewLog(Base):
    __tablename__ = "review_logs"
    __table_args__ = {"schema": "records"}

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True, autoincrement=True)
    journal_id: Mapped[int] = mapped_column(Integer, index=True)
    reviewer_id: Mapped[int] = mapped_column(Integer)
    reviewer_name: Mapped[str] = mapped_column(String(128))
    note: Mapped[str | None] = mapped_column(String(256), nullable=True)
    ts: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), default=lambda: datetime.now(timezone.utc)
    )


Base.metadata.create_all(engine)


# ---------- аутентификация и права ----------

def user_from_request(request: Request) -> dict:
    key = request.headers.get("X-Internal-Key")
    if key and key == INTERNAL_KEY:
        return {"id": 0, "name": "system", "role": "system", "pwp": True}
    auth = request.headers.get("Authorization", "")
    if not auth.startswith("Bearer "):
        raise HTTPException(401, "Требуется авторизация")
    try:
        payload = jwt.decode(auth[7:], JWT_SECRET, algorithms=["HS256"])
    except Exception:
        raise HTTPException(401, "Неверный или истёкший токен")
    return {
        "id": int(payload["sub"]),
        "login": payload.get("login"),
        "name": payload.get("name"),
        "role": payload.get("role"),
        "pwp": bool(payload.get("pwp")),
    }


def is_member(user: dict, journal: Journal, members: list[Member]) -> bool:
    return any(m.user_id == user["id"] for m in members)


def can_view(user: dict, journal: Journal, members: list[Member]) -> bool:
    if user["role"] in ("admin", "head", "quality_manager", "system"):
        return True
    if journal.kind == "materials":
        return True
    return is_member(user, journal, members)


def can_write(user: dict, journal: Journal, members: list[Member]) -> bool:
    if user["role"] == "admin":
        return True
    if user["role"] != "engineer":
        return False
    return is_member(user, journal, members)


def can_close(user: dict, journal: Journal, members: list[Member]) -> bool:
    if user["role"] == "admin":
        return True
    if user["role"] != "engineer":
        return False
    return any(m.user_id == user["id"] and m.role == "owner" for m in members)


def audit_emit(action: str, actor: dict, entity_type: str, entity_id, entity_title=None, payload=None, reason=None):
    try:
        httpx.post(
            f"{AUDIT_URL}/events",
            json={
                "actor_id": actor.get("id"), "actor_name": actor.get("name"),
                "action": action, "entity_type": entity_type, "entity_id": str(entity_id),
                "entity_title": entity_title, "reason": reason, "payload": payload or {},
            },
            headers={"X-Internal-Key": INTERNAL_KEY}, timeout=3,
        )
    except Exception:
        pass


# ---------- безопасный вычислитель формул ----------

_TOKEN_RE = re.compile(r"\s*(?:(\d+(?:\.\d+)?)|([A-Za-z_][A-Za-z0-9_]*)|(.))")


def _tokenize(expr: str):
    tokens = []
    pos = 0
    while pos < len(expr):
        m = _TOKEN_RE.match(expr, pos)
        if not m:
            return None
        pos = m.end()
        if m.group(1) is not None:
            tokens.append(("num", float(m.group(1))))
        elif m.group(2) is not None:
            tokens.append(("id", m.group(2)))
        else:
            ch = m.group(3)
            if ch in "+-*/()":
                tokens.append(("op", ch))
            else:
                return None
    return tokens


def safe_eval(expr: str, values: dict):
    """Вычисляет формулу вида 'a * 1000 / b'. None-операнды и ошибки -> None."""
    tokens = _tokenize(str(expr or ""))
    if not tokens:
        return None
    idx = 0

    def peek():
        return tokens[idx] if idx < len(tokens) else None

    def parse_expr():
        nonlocal idx
        val = parse_term()
        while peek() and peek()[0] == "op" and peek()[1] in "+-":
            op = tokens[idx][1]
            idx += 1
            rhs = parse_term()
            if val is None or rhs is None:
                val = None
            elif op == "+":
                val = val + rhs
            else:
                val = val - rhs
        return val

    def parse_term():
        nonlocal idx
        val = parse_factor()
        while peek() and peek()[0] == "op" and peek()[1] in "*/":
            op = tokens[idx][1]
            idx += 1
            rhs = parse_factor()
            if val is None or rhs is None:
                val = None
            elif op == "*":
                val = val * rhs
            elif rhs == 0:
                val = None
            else:
                val = val / rhs
        return val

    def parse_factor():
        nonlocal idx
        tok = peek()
        if not tok:
            return None
        if tok[0] == "num":
            idx += 1
            return tok[1]
        if tok[0] == "op" and tok[1] == "-":
            idx += 1
            v = parse_factor()
            return None if v is None else -v
        if tok[0] == "op" and tok[1] == "(":
            idx += 1
            v = parse_expr()
            closing = peek()
            if closing and closing == ("op", ")"):
                idx += 1
                return v
            return None
        if tok[0] == "id":
            idx += 1
            v = values.get(tok[1])
            if v is None or isinstance(v, bool) or not isinstance(v, (int, float)):
                return None
            return float(v)
        return None

    try:
        result = parse_expr()
        if idx != len(tokens):
            return None
        if result is None:
            return None
        return round(float(result), 6)
    except Exception:
        return None


# ---------- колонки формы (получение из forms-svc с кэшем) ----------

_FORM_CACHE: dict[tuple[int, int], list] = {}


def fetch_columns(template_id: int, version_id: int) -> list:
    cache_key = (template_id, version_id)
    if cache_key in _FORM_CACHE:
        return _FORM_CACHE[cache_key]
    try:
        resp = httpx.get(
            f"{FORMS_URL}/templates/{template_id}/versions/{version_id}",
            headers={"X-Internal-Key": INTERNAL_KEY}, timeout=5,
        )
        if resp.status_code != 200:
            raise HTTPException(502, "Не удалось получить схему формы из forms-svc")
        columns = resp.json().get("columns", [])
    except HTTPException:
        raise
    except Exception:
        raise HTTPException(502, "forms-svc недоступен")
    _FORM_CACHE[cache_key] = columns
    return columns


def resolve_current_version(template_id: int) -> int:
    try:
        resp = httpx.get(
            f"{FORMS_URL}/templates/{template_id}",
            headers={"X-Internal-Key": INTERNAL_KEY}, timeout=5,
        )
    except Exception:
        raise HTTPException(502, "forms-svc недоступен")
    if resp.status_code != 200:
        raise HTTPException(404, "Шаблон формы не найден")
    version_id = resp.json().get("current_version_id")
    if not version_id:
        raise HTTPException(422, "У шаблона нет версий")
    return version_id


# ---------- валидация данных записи по колонкам ----------

def apply_columns(columns: list, raw: dict, db, user: dict, row_no: int, old_data: dict | None = None) -> dict:
    """Нормализует и валидирует данные записи; заполняет автополя и формулы."""
    data: dict = {}
    errors: list[str] = []

    for c in columns:
        if c["type"] != "auto":
            continue
        key = c["key"]
        if old_data is not None and key in old_data:
            data[key] = old_data[key]
            continue
        if c.get("auto") == "row":
            data[key] = row_no
        elif c.get("auto") == "date":
            data[key] = date.today().isoformat()
        elif c.get("auto") == "author":
            data[key] = user.get("name") or ""

    for c in columns:
        ctype, key, title = c["type"], c["key"], c["title"]
        if ctype in ("auto", "formula"):
            continue
        v = raw.get(key)

        if ctype == "number":
            if v in (None, ""):
                v = None
            elif isinstance(v, bool):
                errors.append(f"«{title}»: ожидается число")
            else:
                try:
                    v = float(str(v).replace(",", "."))
                except ValueError:
                    errors.append(f"«{title}»: ожидается число")
                    v = None

        elif ctype == "date":
            if v in (None, ""):
                v = None
            else:
                try:
                    v = datetime.strptime(str(v)[:10], "%Y-%m-%d").date().isoformat()
                except ValueError:
                    errors.append(f"«{title}»: дата в формате ГГГГ-ММ-ДД")
                    v = None

        elif ctype == "bool":
            v = None if v in (None, "") else bool(v)

        elif ctype == "enum":
            if v in (None, ""):
                v = None
            elif str(v) not in (c.get("options") or []):
                errors.append(f"«{title}»: выберите значение из списка")
                v = None

        elif ctype in ("ref_journal", "ref_material"):
            if v in (None, ""):
                v = None
            else:
                try:
                    v = int(v)
                except (TypeError, ValueError):
                    errors.append(f"«{title}»: неверная ссылка")
                    v = None
                else:
                    if ctype == "ref_journal" and v is not None:
                        target = db.get(Record, v)
                        if not target:
                            errors.append(f"«{title}»: запись №{v} не найдена")
                        else:
                            tj = db.get(Journal, target.journal_id)
                            if not tj or tj.code != c.get("ref_code"):
                                errors.append(f"«{title}»: запись не из журнала {c.get('ref_code')}")

        else:  # text
            v = str(v).strip() if v not in (None, "") else None

        if c.get("required") and (v is None or v == ""):
            errors.append(f"«{title}»: обязательное поле")
        data[key] = v

    if errors:
        raise HTTPException(422, "; ".join(errors))

    for _ in range(5):
        for c in columns:
            if c["type"] == "formula":
                data[c["key"]] = safe_eval(c.get("formula", ""), data)

    return data


# ---------- сериализация ----------

def journal_out(j: Journal, members: list[Member], with_members: bool = False) -> dict:
    base = (j.last_review_at or j.created_at) + timedelta(weeks=j.check_period_weeks)
    out = {
        "id": j.id,
        "code": j.code,
        "title": j.title,
        "kind": j.kind,
        "form_template_id": j.form_template_id,
        "form_version_id": j.form_version_id,
        "materials_type": j.materials_type,
        "owner_id": j.owner_id,
        "owner_name": j.owner_name,
        "check_period_weeks": j.check_period_weeks,
        "retention_years": j.retention_years,
        "status": j.status,
        "daily": j.daily,
        "last_review_at": j.last_review_at.isoformat() if j.last_review_at else None,
        "next_review_at": base.isoformat(),
    }
    if with_members:
        out["members"] = [
            {"user_id": m.user_id, "name": m.name, "role": m.role} for m in members
        ]
    return out


def record_out(r: Record, journal_code: str = None) -> dict:
    return {
        "id": r.id,
        "journal_id": r.journal_id,
        "journal_code": journal_code,
        "row_no": r.row_no,
        "data": r.data,
        "status": r.status,
        "author_id": r.author_id,
        "author_name": r.author_name,
        "created_at": r.created_at.isoformat(),
        "updated_at": r.updated_at.isoformat(),
    }


def revision_out(rv: Revision) -> dict:
    return {
        "id": rv.id,
        "record_id": rv.record_id,
        "old_data": rv.old_data,
        "new_data": rv.new_data,
        "reason_code": rv.reason_code,
        "reason_comment": rv.reason_comment,
        "author_name": rv.author_name,
        "ts": rv.ts.isoformat(),
    }


# ---------- API ----------

app = FastAPI(title="records-svc", version="1.0")


class MemberIn(BaseModel):
    user_id: int
    name: str
    role: str = "worker"  # owner | worker


class JournalCreate(BaseModel):
    code: str = Field(min_length=1, max_length=32)
    title: str = Field(min_length=2, max_length=128)
    kind: str  # form | materials
    form_template_id: int | None = None
    materials_type: str | None = None
    owner_id: int
    owner_name: str
    members: list[MemberIn] = []
    check_period_weeks: int = 4
    retention_years: int = 5
    daily: bool = False


class JournalPatch(BaseModel):
    title: str | None = None
    owner_id: int | None = None
    owner_name: str | None = None
    members: list[MemberIn] | None = None
    status: str | None = None
    check_period_weeks: int | None = None
    retention_years: int | None = None
    daily: bool | None = None
    form_version_id: int | None = None


class RecordCreate(BaseModel):
    data: dict


class RecordCorrect(BaseModel):
    data: dict
    reason_code: str
    reason_comment: str | None = None


class ReviewIn(BaseModel):
    note: str | None = None


def get_journal_or_404(db, journal_id: int) -> Journal:
    j = db.get(Journal, journal_id)
    if not j:
        raise HTTPException(404, "Журнал не найден")
    return j


def get_members(db, journal_id: int) -> list[Member]:
    return db.query(Member).filter(Member.journal_id == journal_id).all()


def check_view(db, user, journal) -> list[Member]:
    members = get_members(db, journal.id)
    if not can_view(user, journal, members):
        raise HTTPException(403, "Нет доступа к журналу")
    return members


@app.get("/health")
def health():
    return {"status": "ok", "service": "records"}


@app.get("/journals")
def list_journals(request: Request, daily: bool | None = None):
    user = user_from_request(request)
    with SessionLocal() as db:
        result = []
        for j in db.query(Journal).order_by(Journal.code).all():
            if daily is not None and j.daily != daily:
                continue
            members = get_members(db, j.id)
            if not can_view(user, j, members):
                continue
            out = journal_out(j, members)
            out["is_member"] = is_member(user, j, members)
            result.append(out)
        return result


@app.get("/journals/code/{code:path}")
def get_journal_by_code(code: str, request: Request):
    user = user_from_request(request)
    with SessionLocal() as db:
        j = db.query(Journal).filter(Journal.code == code).first()
        if not j:
            raise HTTPException(404, "Журнал не найден")
        members = check_view(db, user, j)
        out = journal_out(j, members, with_members=True)
        out["is_member"] = is_member(user, j, members)
        return out


@app.get("/journals/review-due")
def review_due(request: Request, before: str | None = None):
    """Журналы, у которых истекает/истекла плановая проверка. Для scheduler-svc.

    Важно: объявлен ДО /journals/{journal_id}, иначе параметрический маршрут
    перехватит путь и вернёт 422 вместо списка.
    """
    user = user_from_request(request)
    if user["role"] not in ("system", "quality_manager", "admin"):
        raise HTTPException(403, "Недостаточно прав")
    cutoff = before or datetime.now(timezone.utc).isoformat()
    with SessionLocal() as db:
        out = []
        for j in db.query(Journal).filter(Journal.status == "active").all():
            base = (j.last_review_at or j.created_at) + timedelta(weeks=j.check_period_weeks)
            if base.isoformat() <= cutoff:
                members = get_members(db, j.id)
                item = journal_out(j, members)
                item["members"] = [{"user_id": m.user_id, "name": m.name, "role": m.role} for m in members]
                out.append(item)
        return out


@app.get("/journals/{journal_id}")
def get_journal(journal_id: int, request: Request):
    user = user_from_request(request)
    with SessionLocal() as db:
        j = get_journal_or_404(db, journal_id)
        members = check_view(db, user, j)
        out = journal_out(j, members, with_members=True)
        out["is_member"] = is_member(user, j, members)
        return out


@app.post("/journals", status_code=201)
def create_journal(body: JournalCreate, request: Request):
    user = user_from_request(request)
    if user["role"] != "admin":
        raise HTTPException(403, "Журналы создаёт только администратор")
    if body.kind not in ("form", "materials"):
        raise HTTPException(422, "kind должен быть form или materials")

    form_version_id = None
    if body.kind == "form":
        if not body.form_template_id:
            raise HTTPException(422, "Для журнала с формой укажите form_template_id")
        form_version_id = resolve_current_version(body.form_template_id)
    else:
        if body.materials_type not in ("reagent", "so", "precursor"):
            raise HTTPException(422, "materials_type должен быть reagent, so или precursor")

    with SessionLocal() as db:
        if db.query(Journal).filter(Journal.code == body.code.strip()).first():
            raise HTTPException(409, "Журнал с таким шифром уже существует")
        j = Journal(
            code=body.code.strip(),
            title=body.title.strip(),
            kind=body.kind,
            form_template_id=body.form_template_id,
            form_version_id=form_version_id,
            materials_type=body.materials_type,
            owner_id=body.owner_id,
            owner_name=body.owner_name.strip(),
            check_period_weeks=body.check_period_weeks,
            retention_years=body.retention_years,
            daily=body.daily,
        )
        db.add(j)
        db.flush()
        members = {m.user_id: m for m in body.members}
        members[body.owner_id] = MemberIn(user_id=body.owner_id, name=body.owner_name, role="owner")
        for m in members.values():
            if m.role not in ("owner", "worker"):
                raise HTTPException(422, "Роль участника: owner или worker")
            db.add(Member(journal_id=j.id, user_id=m.user_id, name=m.name.strip(), role=m.role))
        db.commit()
        db.refresh(j)
        audit_emit("journal.created", user, "journal", j.id, f"{j.code} {j.title}",
                   {"kind": j.kind, "check_period_weeks": j.check_period_weeks})
        return journal_out(j, get_members(db, j.id), with_members=True)


@app.patch("/journals/{journal_id}")
def patch_journal(journal_id: int, body: JournalPatch, request: Request):
    user = user_from_request(request)
    if user["role"] != "admin":
        raise HTTPException(403, "Журналы настраивает только администратор")
    with SessionLocal() as db:
        j = get_journal_or_404(db, journal_id)
        if body.status is not None:
            if body.status not in ("active", "closed", "archived"):
                raise HTTPException(422, "Статус: active, closed или archived")
            j.status = body.status
        if body.title is not None:
            j.title = body.title.strip()
        if body.check_period_weeks is not None:
            j.check_period_weeks = body.check_period_weeks
        if body.retention_years is not None:
            j.retention_years = body.retention_years
        if body.daily is not None:
            j.daily = body.daily
        if body.owner_id is not None and body.owner_name is not None:
            j.owner_id = body.owner_id
            j.owner_name = body.owner_name.strip()
        if body.form_version_id is not None:
            j.form_version_id = body.form_version_id
        if body.members is not None:
            db.query(Member).filter(Member.journal_id == j.id).delete()
            member_map = {m.user_id: m for m in body.members}
            if body.owner_id is not None and body.owner_name is not None:
                member_map[body.owner_id] = MemberIn(user_id=body.owner_id, name=body.owner_name, role="owner")
            else:
                member_map[j.owner_id] = MemberIn(user_id=j.owner_id, name=j.owner_name, role="owner")
            for m in member_map.values():
                db.add(Member(journal_id=j.id, user_id=m.user_id, name=m.name.strip(), role=m.role))
        db.commit()
        db.refresh(j)
        audit_emit("journal.updated", user, "journal", j.id, f"{j.code} {j.title}",
                   {"status": j.status})
        return journal_out(j, get_members(db, j.id), with_members=True)


@app.get("/journals/{journal_id}/records")
def list_records(
    journal_id: int,
    request: Request,
    date_from: str | None = None,
    date_to: str | None = None,
    status: str | None = None,
    author_id: int | None = None,
    limit: int = 200,
    offset: int = 0,
):
    user = user_from_request(request)
    limit = min(limit, 1000)
    with SessionLocal() as db:
        j = get_journal_or_404(db, journal_id)
        check_view(db, user, j)
        q = db.query(Record).filter(Record.journal_id == journal_id)
        if date_from:
            q = q.filter(Record.created_at >= datetime.fromisoformat(f"{date_from}T00:00:00"))
        if date_to:
            q = q.filter(Record.created_at <= datetime.fromisoformat(f"{date_to}T23:59:59"))
        if status:
            q = q.filter(Record.status == status)
        if author_id is not None:
            q = q.filter(Record.author_id == author_id)
        rows = q.order_by(Record.row_no.desc()).offset(offset).limit(limit).all()
        return [record_out(r, j.code) for r in rows]


@app.post("/journals/{journal_id}/records", status_code=201)
def create_record(journal_id: int, body: RecordCreate, request: Request):
    user = user_from_request(request)
    with SessionLocal() as db:
        j = get_journal_or_404(db, journal_id)
        members = check_view(db, user, j)
        if j.kind != "form":
            raise HTTPException(422, "Записи вносятся только в журналы с формой; журналы материалов ведёт модуль материалов")
        if j.status != "active":
            raise HTTPException(409, f"Журнал {j.status == 'closed' and 'закрыт, новые записи не вносятся' or 'в архиве, только чтение'}")
        if not can_write(user, j, members):
            raise HTTPException(403, "Нет прав на внесение записей в этот журнал")

        columns = fetch_columns(j.form_template_id, j.form_version_id)
        last = (
            db.query(Record).filter(Record.journal_id == j.id)
            .order_by(Record.row_no.desc()).first()
        )
        row_no = (last.row_no + 1) if last else 1
        data = apply_columns(columns, body.data or {}, db, user, row_no)

        r = Record(
            journal_id=j.id, row_no=row_no, data=data,
            author_id=user["id"], author_name=user.get("name") or "",
        )
        db.add(r)
        db.commit()
        db.refresh(r)
        audit_emit("record.created", user, "record", r.id, f"{j.code} №{row_no}",
                   {"journal_code": j.code, "row_no": row_no})
        return record_out(r, j.code)


@app.get("/records/{record_id}")
def get_record(record_id: int, request: Request):
    user = user_from_request(request)
    with SessionLocal() as db:
        r = db.get(Record, record_id)
        if not r:
            raise HTTPException(404, "Запись не найдена")
        j = db.get(Journal, r.journal_id)
        check_view(db, user, j)
        return record_out(r, j.code)


@app.patch("/records/{record_id}")
def correct_record(record_id: int, body: RecordCorrect, request: Request):
    user = user_from_request(request)
    if body.reason_code not in REASON_CODES:
        raise HTTPException(422, "Код причины: V (ошибка вычисления), O (опечатка) или I (иное)")
    with SessionLocal() as db:
        r = db.get(Record, record_id)
        if not r:
            raise HTTPException(404, "Запись не найдена")
        j = db.get(Journal, r.journal_id)
        members = check_view(db, user, j)
        if j.status == "archived":
            raise HTTPException(409, "Журнал в архиве: записи неизменяемы")
        if r.status != "entered":
            raise HTTPException(409, "Закрытая запись не исправляется")
        allowed = user["role"] == "admin" or r.author_id == user["id"] or any(
            m.user_id == user["id"] and m.role == "owner" for m in members
        )
        if not allowed:
            raise HTTPException(403, "Исправлять запись может её автор, ответственный за журнал или администратор")

        columns = fetch_columns(j.form_template_id, j.form_version_id)
        new_data = apply_columns(columns, body.data or {}, db, user, r.row_no, old_data=r.data)

        rev = Revision(
            record_id=r.id, old_data=r.data, new_data=new_data,
            reason_code=body.reason_code, reason_comment=body.reason_comment,
            author_id=user["id"], author_name=user.get("name") or "",
        )
        db.add(rev)
        r.data = new_data
        r.updated_at = datetime.now(timezone.utc)
        db.commit()
        db.refresh(r)
        reason_text = REASON_CODES[body.reason_code] + (f": {body.reason_comment}" if body.reason_comment else "")
        audit_emit("record.corrected", user, "record", r.id, f"{j.code} №{r.row_no}",
                   {"journal_code": j.code, "row_no": r.row_no}, reason=reason_text)
        return record_out(r, j.code)


@app.post("/records/{record_id}/close")
def close_record(record_id: int, request: Request):
    user = user_from_request(request)
    with SessionLocal() as db:
        r = db.get(Record, record_id)
        if not r:
            raise HTTPException(404, "Запись не найдена")
        j = db.get(Journal, r.journal_id)
        members = check_view(db, user, j)
        if j.status == "archived":
            raise HTTPException(409, "Журнал в архиве")
        if not can_close(user, j, members):
            raise HTTPException(403, "Закрыть запись может ответственный за журнал или администратор")
        r.status = "closed"
        r.updated_at = datetime.now(timezone.utc)
        db.commit()
        audit_emit("record.closed", user, "record", r.id, f"{j.code} №{r.row_no}",
                   {"journal_code": j.code, "row_no": r.row_no})
        return record_out(r, j.code)


@app.get("/records/{record_id}/history")
def record_history(record_id: int, request: Request):
    user = user_from_request(request)
    with SessionLocal() as db:
        r = db.get(Record, record_id)
        if not r:
            raise HTTPException(404, "Запись не найдена")
        j = db.get(Journal, r.journal_id)
        check_view(db, user, j)
        revs = (
            db.query(Revision).filter(Revision.record_id == record_id)
            .order_by(Revision.id).all()
        )
        return {
            "record": record_out(r, j.code),
            "revisions": [revision_out(rv) for rv in revs],
        }


@app.post("/journals/{journal_id}/review", status_code=201)
def review_journal(journal_id: int, body: ReviewIn, request: Request):
    user = user_from_request(request)
    if user["role"] not in ("quality_manager", "admin"):
        raise HTTPException(403, "Проверку журнала отмечает менеджер по качеству или администратор")
    with SessionLocal() as db:
        j = get_journal_or_404(db, journal_id)
        check_view(db, user, j)
        if j.status == "archived":
            raise HTTPException(409, "Журнал в архиве не проверяется")
        log = ReviewLog(
            journal_id=j.id, reviewer_id=user["id"],
            reviewer_name=user.get("name") or "", note=body.note,
        )
        db.add(log)
        j.last_review_at = datetime.now(timezone.utc)
        db.commit()
        db.refresh(log)
        audit_emit("journal.reviewed", user, "journal", j.id, f"{j.code} {j.title}",
                   {"note": body.note})
        return {
            "id": log.id, "journal_id": log.journal_id, "reviewer_name": log.reviewer_name,
            "note": log.note, "ts": log.ts.isoformat(),
        }


@app.get("/journals/{journal_id}/reviews")
def list_reviews(journal_id: int, request: Request):
    user = user_from_request(request)
    with SessionLocal() as db:
        j = get_journal_or_404(db, journal_id)
        check_view(db, user, j)
        logs = (
            db.query(ReviewLog).filter(ReviewLog.journal_id == journal_id)
            .order_by(ReviewLog.id.desc()).limit(50).all()
        )
        return [
            {"id": l.id, "reviewer_name": l.reviewer_name, "note": l.note, "ts": l.ts.isoformat()}
            for l in logs
        ]


# ---------- демо-данные ----------

def seed():
    from .seed import run_seed
    run_seed(SessionLocal, Journal, Member, Record, Revision, ReviewLog,
             apply_columns, audit_emit)


seed()
