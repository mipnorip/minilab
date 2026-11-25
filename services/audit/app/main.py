"""audit-svc — append-only журнал аудита: «кто — когда — что — почему».

Схема БД: audit. Изменять и удалять события нельзя (API не предоставляет
таких операций). Пишут события все сервисы через X-Internal-Key или JWT.
"""

import os
from datetime import datetime, timezone

import jwt
from fastapi import FastAPI, HTTPException, Request
from pydantic import BaseModel
from sqlalchemy import BigInteger, DateTime, Integer, String, create_engine, text
from sqlalchemy.dialects.postgresql import JSONB
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column, sessionmaker

JWT_SECRET = os.getenv("JWT_SECRET", "dev-jwt-secret")
INTERNAL_KEY = os.getenv("INTERNAL_KEY", "dev-internal-key")
DB_URL = os.getenv("DATABASE_URL", "postgresql+psycopg://minilab:minilab@db:5432/minilab")

engine = create_engine(DB_URL, pool_pre_ping=True)
with engine.begin() as conn:
    conn.execute(text("CREATE SCHEMA IF NOT EXISTS audit"))
SessionLocal = sessionmaker(bind=engine, expire_on_commit=False)


class Base(DeclarativeBase):
    pass


class Event(Base):
    __tablename__ = "events"
    __table_args__ = {"schema": "audit"}

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True, autoincrement=True)
    ts: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), default=lambda: datetime.now(timezone.utc)
    )
    actor_id: Mapped[int | None] = mapped_column(Integer, nullable=True)
    actor_name: Mapped[str] = mapped_column(String(128))
    action: Mapped[str] = mapped_column(String(64))
    entity_type: Mapped[str] = mapped_column(String(64))
    entity_id: Mapped[str] = mapped_column(String(64))
    entity_title: Mapped[str] = mapped_column(String(256), nullable=True)
    reason: Mapped[str] = mapped_column(String(512), nullable=True)
    payload: Mapped[dict] = mapped_column(JSONB, default=dict)


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
        "name": payload.get("name"),
        "role": payload.get("role"),
        "pwp": bool(payload.get("pwp")),
    }


app = FastAPI(title="audit-svc", version="1.0")


class EventIn(BaseModel):
    actor_id: int | None = None
    actor_name: str = "system"
    action: str
    entity_type: str
    entity_id: str
    entity_title: str | None = None
    reason: str | None = None
    payload: dict = {}


def to_out(e: Event) -> dict:
    return {
        "id": e.id,
        "ts": e.ts.isoformat(),
        "actor_id": e.actor_id,
        "actor_name": e.actor_name,
        "action": e.action,
        "entity_type": e.entity_type,
        "entity_id": e.entity_id,
        "entity_title": e.entity_title,
        "reason": e.reason,
        "payload": e.payload,
    }


@app.get("/health")
def health():
    return {"status": "ok", "service": "audit"}


@app.post("/events", status_code=201)
def create_event(body: EventIn, request: Request):
    user_from_request(request)  # JWT или внутренний ключ
    with SessionLocal() as db:
        e = Event(
            actor_id=body.actor_id,
            actor_name=body.actor_name,
            action=body.action,
            entity_type=body.entity_type,
            entity_id=str(body.entity_id)[:64],
            entity_title=body.entity_title,
            reason=body.reason,
            payload=body.payload or {},
        )
        db.add(e)
        db.commit()
        db.refresh(e)
        return to_out(e)


@app.get("/events")
def list_events(
    request: Request,
    entity_type: str | None = None,
    entity_id: str | None = None,
    actor_id: int | None = None,
    action: str | None = None,
    date_from: str | None = None,
    date_to: str | None = None,
    limit: int = 200,
):
    user = user_from_request(request)
    if user["role"] not in ("admin", "head", "quality_manager"):
        raise HTTPException(403, "Журнал аудита доступен руководителю, менеджеру по качеству и администратору")
    limit = min(limit, 1000)
    with SessionLocal() as db:
        q = db.query(Event)
        if entity_type:
            q = q.filter(Event.entity_type == entity_type)
        if entity_id:
            q = q.filter(Event.entity_id == entity_id)
        if actor_id is not None:
            q = q.filter(Event.actor_id == actor_id)
        if action:
            q = q.filter(Event.action == action)
        if date_from:
            q = q.filter(Event.ts >= datetime.fromisoformat(f"{date_from}T00:00:00"))
        if date_to:
            q = q.filter(Event.ts <= datetime.fromisoformat(f"{date_to}T23:59:59"))
        q = q.order_by(Event.id.desc()).limit(limit)
        return [to_out(e) for e in q.all()]
