"""forms-svc — конструктор форм журналов: шаблоны и их версии.

Схема БД: forms. Шаблон — набор колонок; версии неизменяемы,
записи в records-svc привязаны к конкретной версии шаблона.
"""

import os
import re
from datetime import datetime, timezone

import httpx
import jwt
from fastapi import FastAPI, HTTPException, Request
from pydantic import BaseModel, Field
from sqlalchemy import JSON, Boolean, Integer, String, create_engine, text
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column, sessionmaker

JWT_SECRET = os.getenv("JWT_SECRET", "dev-jwt-secret")
INTERNAL_KEY = os.getenv("INTERNAL_KEY", "dev-internal-key")
AUDIT_URL = os.getenv("AUDIT_URL", "http://audit-svc:8006")
DB_URL = os.getenv("DATABASE_URL", "postgresql+psycopg://minilab:minilab@db:5432/minilab")

COLUMN_TYPES = {"text", "number", "date", "bool", "enum", "ref_journal", "ref_material", "formula", "auto"}
AUTO_KINDS = {"row", "date", "author"}

engine = create_engine(DB_URL, pool_pre_ping=True)
with engine.begin() as conn:
    conn.execute(text("CREATE SCHEMA IF NOT EXISTS forms"))
SessionLocal = sessionmaker(bind=engine, expire_on_commit=False)


class Base(DeclarativeBase):
    pass


class Template(Base):
    __tablename__ = "templates"
    __table_args__ = {"schema": "forms"}

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    code: Mapped[str] = mapped_column(String(32), unique=True, index=True)
    title: Mapped[str] = mapped_column(String(128))
    is_preset: Mapped[bool] = mapped_column(Boolean, default=False)
    current_version_id: Mapped[int | None] = mapped_column(Integer, nullable=True)
    created_at: Mapped[datetime] = mapped_column(default=lambda: datetime.now(timezone.utc))


class TemplateVersion(Base):
    __tablename__ = "template_versions"
    __table_args__ = {"schema": "forms"}

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    template_id: Mapped[int] = mapped_column(Integer, index=True)
    version: Mapped[int] = mapped_column(Integer)
    columns: Mapped[list] = mapped_column(JSON)
    comment: Mapped[str | None] = mapped_column(String(256), nullable=True)
    created_by: Mapped[str] = mapped_column(Integer, nullable=True)
    created_at: Mapped[datetime] = mapped_column(default=lambda: datetime.now(timezone.utc))


Base.metadata.create_all(engine)


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


def require_admin(user: dict):
    if user["role"] != "admin":
        raise HTTPException(403, "Шаблоны форм создаёт и изменяет только администратор")


def audit_emit(action: str, actor: dict, entity_type: str, entity_id, entity_title=None, payload=None):
    try:
        httpx.post(
            f"{AUDIT_URL}/events",
            json={
                "actor_id": actor.get("id"), "actor_name": actor.get("name"),
                "action": action, "entity_type": entity_type, "entity_id": str(entity_id),
                "entity_title": entity_title, "payload": payload or {},
            },
            headers={"X-Internal-Key": INTERNAL_KEY}, timeout=3,
        )
    except Exception:
        pass


# ---------- валидация колонок ----------

def validate_columns(columns: list) -> list:
    if not isinstance(columns, list) or not columns:
        raise HTTPException(422, "Шаблон должен содержать хотя бы одну колонку")
    keys: set[str] = set()
    numeric_keys: set[str] = set()
    for c in columns:
        if not isinstance(c, dict):
            raise HTTPException(422, "Каждая колонка должна быть объектом")
        key = c.get("key", "")
        if not re.fullmatch(r"[a-z_][a-z0-9_]*", str(key)):
            raise HTTPException(422, f"Ключ колонки «{key}»: латиница в нижнем регистре, цифры, _")
        if key in keys:
            raise HTTPException(422, f"Дублирующийся ключ колонки: {key}")
        keys.add(key)
        title = str(c.get("title", "")).strip()
        if not title:
            raise HTTPException(422, f"Колонка «{key}»: не задано название")
        ctype = c.get("type")
        if ctype not in COLUMN_TYPES:
            raise HTTPException(422, f"Колонка «{title}»: неизвестный тип {ctype}")
        if ctype == "enum" and not c.get("options"):
            raise HTTPException(422, f"Колонка «{title}»: задайте варианты списка")
        if ctype == "ref_journal" and not c.get("ref_code"):
            raise HTTPException(422, f"Колонка «{title}»: задайте шифр журнала-источника")
        if ctype == "auto":
            if c.get("auto") not in AUTO_KINDS:
                raise HTTPException(422, f"Колонка «{title}»: тип auto требует auto=row|date|author")
        if ctype == "formula":
            formula = str(c.get("formula") or "").strip()
            if not formula:
                raise HTTPException(422, f"Колонка «{title}»: задайте формулу")
            refs = set(re.findall(r"[A-Za-z_][A-Za-z0-9_]*", formula))
            unknown = refs - keys
            if unknown:
                raise HTTPException(422, f"Колонка «{title}»: в формуле неизвестные поля: {', '.join(sorted(unknown))}")
            numeric_keys.add(key)
        if ctype == "number":
            numeric_keys.add(key)
    return [
        {
            "key": c["key"],
            "title": str(c["title"]).strip(),
            "type": c["type"],
            "required": bool(c.get("required", False)),
            "unit": str(c.get("unit") or "").strip() or None,
            "options": [str(o).strip() for o in (c.get("options") or []) if str(o).strip()] or None,
            "formula": str(c.get("formula") or "").strip() or None,
            "ref_code": str(c.get("ref_code") or "").strip() or None,
            "auto": c.get("auto"),
        }
        for c in columns
    ]


# ---------- API ----------

app = FastAPI(title="forms-svc", version="1.0")


class TemplateCreate(BaseModel):
    code: str = Field(min_length=2, max_length=32)
    title: str = Field(min_length=2, max_length=128)
    columns: list
    is_preset: bool = False


class VersionCreate(BaseModel):
    columns: list
    comment: str | None = None


def template_out(db, t: Template) -> dict:
    version_no = None
    if t.current_version_id:
        v = db.get(TemplateVersion, t.current_version_id)
        version_no = v.version if v else None
    return {
        "id": t.id, "code": t.code, "title": t.title, "is_preset": t.is_preset,
        "current_version_id": t.current_version_id, "version": version_no,
    }


def version_out(v: TemplateVersion, columns: bool = False) -> dict:
    out = {
        "id": v.id, "template_id": v.template_id, "version": v.version,
        "comment": v.comment, "created_at": v.created_at.isoformat(),
    }
    if columns:
        out["columns"] = v.columns
    return out


@app.get("/health")
def health():
    return {"status": "ok", "service": "forms"}


@app.get("/templates")
def list_templates(request: Request, presets_only: bool = False):
    user_from_request(request)
    with SessionLocal() as db:
        q = db.query(Template).order_by(Template.code)
        if presets_only:
            q = q.filter(Template.is_preset.is_(True))
        return [template_out(db, t) for t in q.all()]


@app.get("/templates/{template_id}")
def get_template(template_id: int, request: Request):
    user_from_request(request)
    with SessionLocal() as db:
        t = db.get(Template, template_id)
        if not t:
            raise HTTPException(404, "Шаблон не найден")
        out = template_out(db, t)
        if t.current_version_id:
            v = db.get(TemplateVersion, t.current_version_id)
            if v:
                out["columns"] = v.columns
        return out


@app.get("/templates/{template_id}/versions")
def list_versions(template_id: int, request: Request):
    user_from_request(request)
    with SessionLocal() as db:
        if not db.get(Template, template_id):
            raise HTTPException(404, "Шаблон не найден")
        vs = (
            db.query(TemplateVersion)
            .filter(TemplateVersion.template_id == template_id)
            .order_by(TemplateVersion.version)
            .all()
        )
        return [version_out(v) for v in vs]


@app.get("/templates/{template_id}/versions/{version_id}")
def get_version(template_id: int, version_id: int, request: Request):
    user_from_request(request)
    with SessionLocal() as db:
        v = db.get(TemplateVersion, version_id)
        if not v or v.template_id != template_id:
            raise HTTPException(404, "Версия не найдена")
        return version_out(v, columns=True)


@app.post("/templates", status_code=201)
def create_template(body: TemplateCreate, request: Request):
    user = user_from_request(request)
    require_admin(user)
    columns = validate_columns(body.columns)
    with SessionLocal() as db:
        if db.query(Template).filter(Template.code == body.code.strip().upper()).first():
            raise HTTPException(409, "Код шаблона уже занят")
        t = Template(code=body.code.strip().upper(), title=body.title.strip(), is_preset=body.is_preset)
        db.add(t)
        db.flush()
        v = TemplateVersion(template_id=t.id, version=1, columns=columns, created_by=user["id"])
        db.add(v)
        db.flush()
        t.current_version_id = v.id
        db.commit()
        db.refresh(t)
        audit_emit("form.created", user, "form", t.id, f"{t.code} {t.title}", {"columns": len(columns)})
        return template_out(db, t)


@app.post("/templates/{template_id}/versions", status_code=201)
def create_version(template_id: int, body: VersionCreate, request: Request):
    user = user_from_request(request)
    require_admin(user)
    columns = validate_columns(body.columns)
    with SessionLocal() as db:
        t = db.get(Template, template_id)
        if not t:
            raise HTTPException(404, "Шаблон не найден")
        last = (
            db.query(TemplateVersion)
            .filter(TemplateVersion.template_id == template_id)
            .order_by(TemplateVersion.version.desc())
            .first()
        )
        v = TemplateVersion(
            template_id=template_id,
            version=(last.version + 1) if last else 1,
            columns=columns,
            comment=body.comment,
            created_by=user["id"],
        )
        db.add(v)
        db.flush()
        t.current_version_id = v.id
        db.commit()
        db.refresh(v)
        audit_emit("form.versioned", user, "form", t.id, f"{t.code} {t.title}",
                   {"version": v.version, "columns": len(columns)})
        return version_out(v, columns=True)


# ---------- пресеты ----------

def seed():
    from .presets import PRESETS
    with SessionLocal() as db:
        if db.query(Template).count():
            return
        for p in PRESETS:
            t = Template(code=p["code"], title=p["title"], is_preset=True)
            db.add(t)
            db.flush()
            v = TemplateVersion(template_id=t.id, version=1, columns=p["columns"])
            db.add(v)
            db.flush()
            t.current_version_id = v.id
        db.commit()


seed()
