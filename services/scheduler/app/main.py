"""scheduler-svc — планировщик напоминаний.

Следит за тремя видами событий и складывает уведомления пользователям:
  * material_expiring — истекает срок годности материалов (materials-svc);
  * daily_missing     — не внесена ежедневная запись (журналы с daily=True);
  * review_due        — истекла плановая проверка журнала (records-svc).

Каждое уведомление создаётся не чаще раза в сутки (dedupe-ключ).
Ручной запуск проверки: POST /tick (администратор) — удобно для демонстрации.
"""

import asyncio
import os
from contextlib import asynccontextmanager
from datetime import date, datetime, timezone

import httpx
import jwt
from fastapi import FastAPI, HTTPException, Request
from sqlalchemy import BigInteger, Boolean, DateTime, Integer, String, create_engine, text
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column, sessionmaker

JWT_SECRET = os.getenv("JWT_SECRET", "dev-jwt-secret")
INTERNAL_KEY = os.getenv("INTERNAL_KEY", "dev-internal-key")
AUTH_URL = os.getenv("AUTH_URL", "http://auth-svc:8001")
RECORDS_URL = os.getenv("RECORDS_URL", "http://records-svc:8003")
MATERIALS_URL = os.getenv("MATERIALS_URL", "http://materials-svc:8004")
DB_URL = os.getenv("DATABASE_URL", "postgresql+psycopg://minilab:minilab@db:5432/minilab")

CHECK_INTERVAL = int(os.getenv("SCHED_INTERVAL_SECONDS", "60"))
EXPIRING_DAYS = int(os.getenv("MATERIAL_EXPIRING_DAYS", "14"))
DAILY_CHECK_HOUR = int(os.getenv("DAILY_CHECK_HOUR", "12"))

engine = create_engine(DB_URL, pool_pre_ping=True)
with engine.begin() as conn:
    conn.execute(text("CREATE SCHEMA IF NOT EXISTS scheduler"))
SessionLocal = sessionmaker(bind=engine, expire_on_commit=False)

IK = {"X-Internal-Key": INTERNAL_KEY}


class Base(DeclarativeBase):
    pass


class Notification(Base):
    __tablename__ = "notifications"
    __table_args__ = {"schema": "scheduler"}

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True, autoincrement=True)
    ts: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), default=lambda: datetime.now(timezone.utc)
    )
    user_id: Mapped[int] = mapped_column(Integer, index=True)
    user_name: Mapped[str] = mapped_column(String(128))
    type: Mapped[str] = mapped_column(String(32))
    title: Mapped[str] = mapped_column(String(256))
    body: Mapped[str] = mapped_column(String(512))
    entity_type: Mapped[str | None] = mapped_column(String(32), nullable=True)
    entity_id: Mapped[str | None] = mapped_column(String(64), nullable=True)
    is_read: Mapped[bool] = mapped_column(Boolean, default=False)
    dedupe_key: Mapped[str] = mapped_column(String(128), unique=True, index=True)


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
    return {"id": int(payload["sub"]), "name": payload.get("name"), "role": payload.get("role")}


# ---------- логика проверок ----------

def notify(db, user: dict, ntype: str, title: str, body: str,
           entity_type: str | None, entity_id, dedupe_key: str) -> int:
    key = f"{dedupe_key}:u{user['id']}"
    if db.query(Notification).filter(Notification.dedupe_key == key).first():
        return 0
    db.add(Notification(
        user_id=user["id"], user_name=user["full_name"], type=ntype,
        title=title[:256], body=body[:512],
        entity_type=entity_type, entity_id=str(entity_id) if entity_id is not None else None,
        dedupe_key=key,
    ))
    return 1


def run_checks() -> int:
    now = datetime.now(timezone.utc)
    today = now.date()
    created = 0

    try:
        users = httpx.get(f"{AUTH_URL}/internal/users", headers=IK, timeout=5).json()
    except Exception as exc:
        print(f"[scheduler] auth-svc недоступен: {exc}")
        return 0
    staff = [u for u in users if u["role"] in ("admin", "quality_manager", "engineer")]
    managers = [u for u in users if u["role"] in ("admin", "quality_manager")]

    with SessionLocal() as db:
        # 1) истекающие материалы
        try:
            expiring = httpx.get(
                f"{MATERIALS_URL}/expiring", params={"days": EXPIRING_DAYS},
                headers=IK, timeout=5,
            ).json()
        except Exception:
            expiring = []
        for b in expiring:
            days = b.get("days_left")
            until = b.get("expires_on")
            when = "истёк" if days is not None and days < 0 else f"осталось {days} дн."
            for u in staff:
                created += notify(
                    db, u, "material_expiring",
                    f"Истекает срок годности: {b['item_name']}",
                    f"Партия {b['batch_no']} — {b['qty']} {b['unit']}, {when} (до {until}).",
                    "batch", b["id"], f"exp:{b['id']}:{today}",
                )

        # 2) ежедневные записи не внесены
        if now.hour >= DAILY_CHECK_HOUR:
            try:
                dailies = httpx.get(
                    f"{RECORDS_URL}/journals", params={"daily": "true"},
                    headers=IK, timeout=5,
                ).json()
            except Exception:
                dailies = []
            for j in dailies:
                try:
                    rows = httpx.get(
                        f"{RECORDS_URL}/journals/{j['id']}/records",
                        params={"date_from": today.isoformat(), "date_to": today.isoformat(),
                                "limit": 1},
                        headers=IK, timeout=5,
                    ).json()
                except Exception:
                    continue
                if rows:
                    continue
                try:
                    detail = httpx.get(f"{RECORDS_URL}/journals/{j['id']}", headers=IK, timeout=5).json()
                    member_ids = {m["user_id"] for m in detail.get("members", [])}
                except Exception:
                    member_ids = set()
                targets = [u for u in users if u["id"] in member_ids or u["role"] in ("admin", "quality_manager")]
                for u in targets:
                    created += notify(
                        db, u, "daily_missing",
                        f"Не внесена запись: {j['title']}",
                        f"Журнал {j['code']} требует ежедневной записи за {today.isoformat()}.",
                        "journal", j["id"], f"daily:{j['id']}:{today}",
                    )

        # 3) просроченные проверки журналов
        try:
            due = httpx.get(
                f"{RECORDS_URL}/journals/review-due", params={"before": now.isoformat()},
                headers=IK, timeout=5,
            ).json()
        except Exception:
            due = []
        for j in due:
            member_ids = {m["user_id"] for m in j.get("members", [])}
            targets = [u for u in users if u["id"] in member_ids or u in managers]
            since = (j.get("last_review_at") or "")[:10] or "создания"
            for u in targets:
                created += notify(
                    db, u, "review_due",
                    f"Просрочена проверка журнала: {j['code']}",
                    f"Журнал «{j['title']}» не проверялся с {since}. "
                    f"Периодичность — {j['check_period_weeks']} нед.",
                    "journal", j["id"], f"review:{j['id']}:{today}",
                )

        db.commit()
    return created


async def _loop():
    # первая проверка почти сразу после старта, дальше — по интервалу
    await asyncio.sleep(5)
    while True:
        try:
            await asyncio.to_thread(run_checks)
        except Exception as exc:
            print(f"[scheduler] ошибка цикла проверок: {exc}")
        await asyncio.sleep(CHECK_INTERVAL)


@asynccontextmanager
async def lifespan(app: FastAPI):
    task = asyncio.create_task(_loop())
    yield
    task.cancel()


app = FastAPI(title="scheduler-svc", version="1.0", lifespan=lifespan)


def notif_out(n: Notification) -> dict:
    return {
        "id": n.id,
        "ts": n.ts.isoformat(),
        "type": n.type,
        "title": n.title,
        "body": n.body,
        "entity_type": n.entity_type,
        "entity_id": n.entity_id,
        "is_read": n.is_read,
    }


@app.get("/health")
def health():
    return {"status": "ok", "service": "scheduler"}


@app.get("/list")
def list_notifications(request: Request, limit: int = 100):
    user = user_from_request(request)
    limit = min(limit, 500)
    with SessionLocal() as db:
        rows = (
            db.query(Notification)
            .filter(Notification.user_id == user["id"])
            .order_by(Notification.is_read, Notification.id.desc())
            .limit(limit)
            .all()
        )
        return [notif_out(n) for n in rows]


@app.post("/{notif_id}/read")
def mark_read(notif_id: int, request: Request):
    user = user_from_request(request)
    with SessionLocal() as db:
        n = db.get(Notification, notif_id)
        if not n or n.user_id != user["id"]:
            raise HTTPException(404, "Уведомление не найдено")
        n.is_read = True
        db.commit()
        return notif_out(n)


@app.post("/read-all")
def mark_all_read(request: Request):
    user = user_from_request(request)
    with SessionLocal() as db:
        for n in db.query(Notification).filter(
            Notification.user_id == user["id"], Notification.is_read.is_(False)
        ).all():
            n.is_read = True
        db.commit()
        return {"ok": True}


@app.post("/tick")
async def tick(request: Request):
    """Ручной запуск проверок (для демонстрации)."""
    user = user_from_request(request)
    if user["role"] not in ("admin", "system"):
        raise HTTPException(403, "Проверку запускает администратор")
    created = await asyncio.to_thread(run_checks)
    return {"created": created}
