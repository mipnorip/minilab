"""auth-svc — пользователи, роли, выпуск JWT-токенов.

Схема БД: auth. Роли: admin | head | quality_manager | engineer.
Право can_wf_precursors — отдельное право на списание прекурсоров (Ж-04/03).
"""

import hashlib
import os
import secrets
from datetime import datetime, timedelta, timezone

import httpx
import jwt
from fastapi import FastAPI, HTTPException, Request
from pydantic import BaseModel, Field
from sqlalchemy import Boolean, Integer, String, create_engine, text
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column, sessionmaker

JWT_SECRET = os.getenv("JWT_SECRET", "dev-jwt-secret")
JWT_TTL_HOURS = int(os.getenv("JWT_TTL_HOURS", "12"))
INTERNAL_KEY = os.getenv("INTERNAL_KEY", "dev-internal-key")
AUDIT_URL = os.getenv("AUDIT_URL", "http://audit-svc:8006")
DB_URL = os.getenv("DATABASE_URL", "postgresql+psycopg://minilab:minilab@db:5432/minilab")

ROLES = {"admin", "head", "quality_manager", "engineer"}
ROLE_TITLES = {
    "admin": "Администратор",
    "head": "Заведующий лабораторией",
    "quality_manager": "Менеджер по качеству",
    "engineer": "Инженер/лаборант",
}

engine = create_engine(DB_URL, pool_pre_ping=True)
with engine.begin() as conn:
    conn.execute(text("CREATE SCHEMA IF NOT EXISTS auth"))
SessionLocal = sessionmaker(bind=engine, expire_on_commit=False)


class Base(DeclarativeBase):
    pass


class User(Base):
    __tablename__ = "users"
    __table_args__ = {"schema": "auth"}

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    login: Mapped[str] = mapped_column(String(64), unique=True, index=True)
    full_name: Mapped[str] = mapped_column(String(128))
    password_hash: Mapped[str] = mapped_column(String(256))
    role: Mapped[str] = mapped_column(String(32))
    can_wf_precursors: Mapped[bool] = mapped_column(Boolean, default=False)
    is_active: Mapped[bool] = mapped_column(Boolean, default=True)


Base.metadata.create_all(engine)


# ---------- пароли (stdlib, без внешних зависимостей) ----------

def hash_password(password: str) -> str:
    salt = secrets.token_hex(16)
    digest = hashlib.pbkdf2_hmac("sha256", password.encode(), salt.encode(), 100_000).hex()
    return f"{salt}${digest}"


def verify_password(password: str, stored: str) -> bool:
    try:
        salt, digest = stored.split("$")
    except ValueError:
        return False
    candidate = hashlib.pbkdf2_hmac("sha256", password.encode(), salt.encode(), 100_000).hex()
    return secrets.compare_digest(candidate, digest)


# ---------- вспомогательное ----------

def audit_emit(action: str, actor: dict, entity_type: str, entity_id, entity_title=None, payload=None):
    """Лучшее усилие: событие в audit-svc. Не роняет бизнес-операцию."""
    try:
        httpx.post(
            f"{AUDIT_URL}/events",
            json={
                "actor_id": actor.get("id"),
                "actor_name": actor.get("name"),
                "action": action,
                "entity_type": entity_type,
                "entity_id": str(entity_id),
                "entity_title": entity_title,
                "payload": payload or {},
            },
            headers={"X-Internal-Key": INTERNAL_KEY},
            timeout=3,
        )
    except Exception:
        pass


def user_from_request(request: Request) -> dict:
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


def require_role(user: dict, *roles: str):
    if user["role"] not in roles:
        raise HTTPException(403, "Недостаточно прав")


def require_internal(request: Request):
    key = request.headers.get("X-Internal-Key")
    if not key or key != INTERNAL_KEY:
        raise HTTPException(401, "Неверный внутренний ключ")


def to_out(u: User) -> dict:
    return {
        "id": u.id,
        "login": u.login,
        "full_name": u.full_name,
        "role": u.role,
        "role_title": ROLE_TITLES.get(u.role, u.role),
        "can_wf_precursors": u.can_wf_precursors,
        "is_active": u.is_active,
    }


# ---------- API ----------

app = FastAPI(title="auth-svc", version="1.0")


class LoginIn(BaseModel):
    login: str
    password: str


class UserCreate(BaseModel):
    login: str = Field(min_length=2, max_length=64)
    full_name: str = Field(min_length=2, max_length=128)
    password: str = Field(min_length=4)
    role: str
    can_wf_precursors: bool = False


class UserPatch(BaseModel):
    full_name: str | None = None
    role: str | None = None
    can_wf_precursors: bool | None = None
    is_active: bool | None = None
    password: str | None = None


@app.get("/health")
def health():
    return {"status": "ok", "service": "auth"}


@app.post("/login")
def login(body: LoginIn):
    with SessionLocal() as db:
        u = db.query(User).filter(User.login == body.login.strip().lower()).first()
        if not u or not verify_password(body.password, u.password_hash) or not u.is_active:
            raise HTTPException(401, "Неверный логин или пароль")
        payload = {
            "sub": str(u.id),
            "login": u.login,
            "name": u.full_name,
            "role": u.role,
            "pwp": u.can_wf_precursors,
            "exp": datetime.now(timezone.utc) + timedelta(hours=JWT_TTL_HOURS),
        }
        token = jwt.encode(payload, JWT_SECRET, algorithm="HS256")
        audit_emit("login", {"id": u.id, "name": u.full_name}, "user", u.id, u.full_name)
        return {"token": token, "user": to_out(u)}


@app.get("/me")
def me(request: Request):
    current = user_from_request(request)
    with SessionLocal() as db:
        u = db.get(User, current["id"])
        if not u or not u.is_active:
            raise HTTPException(403, "Пользователь недоступен")
        return to_out(u)


@app.get("/users")
def list_users(request: Request):
    current = user_from_request(request)
    require_role(current, "admin", "head", "quality_manager")
    with SessionLocal() as db:
        return [to_out(u) for u in db.query(User).order_by(User.id).all()]


@app.post("/users", status_code=201)
def create_user(body: UserCreate, request: Request):
    current = user_from_request(request)
    require_role(current, "admin")
    if body.role not in ROLES:
        raise HTTPException(422, f"Роль должна быть одной из: {', '.join(sorted(ROLES))}")
    with SessionLocal() as db:
        if db.query(User).filter(User.login == body.login.strip().lower()).first():
            raise HTTPException(409, "Логин уже занят")
        u = User(
            login=body.login.strip().lower(),
            full_name=body.full_name.strip(),
            password_hash=hash_password(body.password),
            role=body.role,
            can_wf_precursors=body.can_wf_precursors,
        )
        db.add(u)
        db.commit()
        db.refresh(u)
        audit_emit("user.created", current, "user", u.id, u.full_name, {"role": u.role, "login": u.login})
        return to_out(u)


@app.patch("/users/{user_id}")
def patch_user(user_id: int, body: UserPatch, request: Request):
    current = user_from_request(request)
    require_role(current, "admin")
    with SessionLocal() as db:
        u = db.get(User, user_id)
        if not u:
            raise HTTPException(404, "Пользователь не найден")
        if body.role is not None:
            if body.role not in ROLES:
                raise HTTPException(422, "Недопустимая роль")
            u.role = body.role
        if body.full_name is not None:
            u.full_name = body.full_name.strip()
        if body.can_wf_precursors is not None:
            u.can_wf_precursors = body.can_wf_precursors
        if body.is_active is not None:
            u.is_active = body.is_active
        if body.password:
            u.password_hash = hash_password(body.password)
        db.commit()
        audit_emit("user.updated", current, "user", u.id, u.full_name)
        return to_out(u)


@app.get("/internal/users")
def internal_users(request: Request):
    """Для межсервисных вызовов (scheduler): активные пользователи."""
    require_internal(request)
    with SessionLocal() as db:
        return [to_out(u) for u in db.query(User).filter(User.is_active.is_(True)).all()]


# ---------- демо-данные ----------

def seed():
    with SessionLocal() as db:
        if db.query(User).count():
            return
        demo_password = os.getenv("DEMO_PASSWORD", "demo123")
        demo_users = [
            ("admin", "Администратор системы", "admin", False),
            ("head", "Заведующий лабораторией", "head", False),
            ("qm", "Менеджер по качеству", "quality_manager", False),
            ("eng1", "Иванов И. И. (инженер)", "engineer", True),
            ("lab1", "Петрова П. П. (лаборант)", "engineer", False),
            ("lab2", "Сидоров С. С. (лаборант)", "engineer", False),
        ]
        for login, name, role, pwp in demo_users:
            db.add(User(
                login=login, full_name=name, role=role,
                can_wf_precursors=pwp, password_hash=hash_password(demo_password),
            ))
        db.commit()


seed()
