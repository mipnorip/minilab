"""materials-svc — модуль «Материалы»: реактивы, стандартные образцы, прекурсоры.

Схема БД: materials. Усиленный режим для прекурсоров (Ж-04/03):
списание требует отдельного права (can_wf_precursors), партии, инвентаризация.
Журналы Ж-04/04 и Ж-04/03 формируются из операций автоматически.
"""

import os
import time
from datetime import date, datetime, timedelta, timezone

import httpx
import jwt
from fastapi import FastAPI, HTTPException, Request
from pydantic import BaseModel, Field
from sqlalchemy import (
    BigInteger, Boolean, Date, DateTime, Float, Integer, String, create_engine, func, text,
)
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column, sessionmaker

JWT_SECRET = os.getenv("JWT_SECRET", "dev-jwt-secret")
INTERNAL_KEY = os.getenv("INTERNAL_KEY", "dev-internal-key")
AUDIT_URL = os.getenv("AUDIT_URL", "http://audit-svc:8006")
AUTH_URL = os.getenv("AUTH_URL", "http://auth-svc:8001")
DB_URL = os.getenv("DATABASE_URL", "postgresql+psycopg://minilab:minilab@db:5432/minilab")

MATERIAL_TYPES = {"reagent": "реактив", "so": "стандартный образец", "precursor": "прекурсор"}
OP_TYPES = {
    "receipt": "поступление",
    "writeoff": "списание",
    "inventory": "инвентаризация",
    "check": "проверка пригодности",
}

engine = create_engine(DB_URL, pool_pre_ping=True)
with engine.begin() as conn:
    conn.execute(text("CREATE SCHEMA IF NOT EXISTS materials"))
SessionLocal = sessionmaker(bind=engine, expire_on_commit=False)


class Base(DeclarativeBase):
    pass


class Item(Base):
    __tablename__ = "items"
    __table_args__ = {"schema": "materials"}

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    name: Mapped[str] = mapped_column(String(128))
    type: Mapped[str] = mapped_column(String(16))  # reagent | so | precursor
    unit: Mapped[str] = mapped_column(String(16))
    storage_place: Mapped[str] = mapped_column(String(64))
    is_active: Mapped[bool] = mapped_column(Boolean, default=True)


class Batch(Base):
    __tablename__ = "batches"
    __table_args__ = {"schema": "materials"}

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    item_id: Mapped[int] = mapped_column(Integer, index=True)
    batch_no: Mapped[str] = mapped_column(String(64))
    qty: Mapped[float] = mapped_column(Float, default=0)
    expires_on: Mapped[date | None] = mapped_column(Date, nullable=True)
    received_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), default=lambda: datetime.now(timezone.utc)
    )
    received_by_name: Mapped[str] = mapped_column(String(128))
    status: Mapped[str] = mapped_column(String(16), default="active")  # active | blocked


class Operation(Base):
    __tablename__ = "operations"
    __table_args__ = {"schema": "materials"}

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True, autoincrement=True)
    ts: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), default=lambda: datetime.now(timezone.utc)
    )
    item_id: Mapped[int] = mapped_column(Integer, index=True)
    item_name: Mapped[str] = mapped_column(String(128))
    item_type: Mapped[str] = mapped_column(String(16))
    op_type: Mapped[str] = mapped_column(String(16))
    batch_id: Mapped[int | None] = mapped_column(Integer, nullable=True)
    batch_no: Mapped[str | None] = mapped_column(String(64), nullable=True)
    qty: Mapped[float] = mapped_column(Float)
    unit: Mapped[str] = mapped_column(String(16))
    actor_id: Mapped[int | None] = mapped_column(Integer, nullable=True)
    actor_name: Mapped[str] = mapped_column(String(128))
    note: Mapped[str | None] = mapped_column(String(256), nullable=True)
    target_journal_code: Mapped[str | None] = mapped_column(String(32), nullable=True)
    target_record_id: Mapped[int | None] = mapped_column(Integer, nullable=True)


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


def can_write_off(user: dict, item_type: str) -> bool:
    if user["role"] == "admin":
        return True
    if user["role"] != "engineer":
        return False
    if item_type == "precursor":
        return bool(user.get("pwp"))
    return True


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


# ---------- сериализация ----------

def item_out(db, it: Item) -> dict:
    totals = dict(
        db.query(Batch.item_id, func.sum(Batch.qty))
        .filter(Batch.status == "active")
        .group_by(Batch.item_id)
        .all()
    )
    soon = (
        db.query(func.min(Batch.expires_on))
        .filter(Batch.item_id == it.id, Batch.status == "active", Batch.qty > 0)
        .scalar()
    )
    return {
        "id": it.id,
        "name": it.name,
        "type": it.type,
        "type_title": MATERIAL_TYPES.get(it.type, it.type),
        "unit": it.unit,
        "storage_place": it.storage_place,
        "is_active": it.is_active,
        "total_qty": round(float(totals.get(it.id) or 0), 3),
        "min_expires": soon.isoformat() if soon else None,
    }


def batch_out(b: Batch, it: Item) -> dict:
    days_left = (b.expires_on - date.today()).days if b.expires_on else None
    return {
        "id": b.id,
        "item_id": b.item_id,
        "item_name": it.name,
        "item_type": it.type,
        "unit": it.unit,
        "batch_no": b.batch_no,
        "qty": round(b.qty, 3),
        "expires_on": b.expires_on.isoformat() if b.expires_on else None,
        "days_left": days_left,
        "status": b.status,
        "received_at": b.received_at.isoformat(),
        "received_by_name": b.received_by_name,
    }


def op_out(o: Operation) -> dict:
    return {
        "id": o.id,
        "ts": o.ts.isoformat(),
        "item_id": o.item_id,
        "item_name": o.item_name,
        "item_type": o.item_type,
        "op_type": o.op_type,
        "op_type_title": OP_TYPES.get(o.op_type, o.op_type),
        "batch_no": o.batch_no,
        "qty": round(o.qty, 3),
        "unit": o.unit,
        "actor_name": o.actor_name,
        "note": o.note,
        "target_journal_code": o.target_journal_code,
        "target_record_id": o.target_record_id,
    }


# ---------- API ----------

app = FastAPI(title="materials-svc", version="1.0")


class ItemCreate(BaseModel):
    name: str = Field(min_length=2, max_length=128)
    type: str
    unit: str = Field(min_length=1, max_length=16)
    storage_place: str = Field(min_length=1, max_length=64)


class ReceiptIn(BaseModel):
    item_id: int
    qty: float = Field(gt=0)
    batch_no: str = Field(min_length=1, max_length=64)
    expires_on: str | None = None


class WriteOffIn(BaseModel):
    item_id: int
    qty: float = Field(gt=0)
    batch_id: int | None = None
    target_journal_code: str | None = None
    target_record_id: int | None = None
    note: str | None = None


class InventoryIn(BaseModel):
    actual_qty: float = Field(ge=0)
    note: str | None = None


class SuitabilityIn(BaseModel):
    ok: bool
    note: str | None = None


def get_item_or_404(db, item_id: int) -> Item:
    it = db.get(Item, item_id)
    if not it or not it.is_active:
        raise HTTPException(404, "Материал не найден")
    return it


@app.get("/health")
def health():
    return {"status": "ok", "service": "materials"}


@app.get("/items")
def list_items(request: Request, type: str | None = None, q: str | None = None):
    user_from_request(request)
    with SessionLocal() as db:
        query = db.query(Item).filter(Item.is_active.is_(True))
        if type:
            if type not in MATERIAL_TYPES:
                raise HTTPException(422, "type: reagent, so или precursor")
            query = query.filter(Item.type == type)
        if q:
            query = query.filter(Item.name.ilike(f"%{q}%"))
        return [item_out(db, it) for it in query.order_by(Item.type, Item.name).all()]


@app.post("/items", status_code=201)
def create_item(body: ItemCreate, request: Request):
    user = user_from_request(request)
    if user["role"] != "admin":
        raise HTTPException(403, "Номенклатуру материалов ведёт администратор")
    if body.type not in MATERIAL_TYPES:
        raise HTTPException(422, "type: reagent, so или precursor")
    with SessionLocal() as db:
        it = Item(name=body.name.strip(), type=body.type, unit=body.unit.strip(),
                  storage_place=body.storage_place.strip())
        db.add(it)
        db.commit()
        db.refresh(it)
        audit_emit("material.item_created", user, "material", it.id, it.name, {"type": it.type})
        return item_out(db, it)


@app.get("/batches")
def list_batches(request: Request, item_id: int | None = None, expiring_days: int | None = None):
    user_from_request(request)
    with SessionLocal() as db:
        query = db.query(Batch).filter(Batch.status == "active")
        if item_id is not None:
            query = query.filter(Batch.item_id == item_id)
        if expiring_days is not None:
            query = query.filter(
                Batch.expires_on.is_not(None),
                Batch.expires_on <= date.today() + timedelta(days=expiring_days),
            )
        out = []
        for b in query.order_by(Batch.expires_on.is_(None), Batch.expires_on, Batch.id).all():
            it = db.get(Item, b.item_id)
            if it:
                out.append(batch_out(b, it))
        return out


@app.get("/expiring")
def expiring(request: Request, days: int = 14):
    """Партии с истекающим/истёкшим сроком годности (для scheduler-svc)."""
    user_from_request(request)
    with SessionLocal() as db:
        rows = (
            db.query(Batch)
            .filter(
                Batch.status == "active", Batch.qty > 0,
                Batch.expires_on.is_not(None),
                Batch.expires_on <= date.today() + timedelta(days=days),
            )
            .order_by(Batch.expires_on)
            .all()
        )
        out = []
        for b in rows:
            it = db.get(Item, b.item_id)
            if it:
                bo = batch_out(b, it)
                bo.pop("item_type", None)
                out.append(bo)
        return out


@app.post("/receipts", status_code=201)
def receipt(body: ReceiptIn, request: Request):
    user = user_from_request(request)
    if user["role"] not in ("admin", "engineer", "system"):
        raise HTTPException(403, "Поступление оформляет инженер или администратор")
    expires_on = None
    if body.expires_on:
        try:
            expires_on = date.fromisoformat(body.expires_on[:10])
        except ValueError:
            raise HTTPException(422, "Срок годности: дата в формате ГГГГ-ММ-ДД")
    with SessionLocal() as db:
        it = get_item_or_404(db, body.item_id)
        b = Batch(
            item_id=it.id, batch_no=body.batch_no.strip(), qty=round(body.qty, 3),
            expires_on=expires_on, received_by_name=user.get("name") or "",
        )
        db.add(b)
        db.flush()
        db.add(Operation(
            item_id=it.id, item_name=it.name, item_type=it.type, op_type="receipt",
            batch_id=b.id, batch_no=b.batch_no, qty=round(body.qty, 3), unit=it.unit,
            actor_id=user["id"], actor_name=user.get("name") or "",
        ))
        db.commit()
        db.refresh(b)
        audit_emit("material.receipt", user, "batch", b.id, f"{it.name}, партия {b.batch_no}",
                   {"qty": b.qty, "unit": it.unit, "expires_on": body.expires_on})
        return batch_out(b, it)


@app.post("/writeoffs", status_code=201)
def write_off(body: WriteOffIn, request: Request):
    user = user_from_request(request)
    with SessionLocal() as db:
        it = get_item_or_404(db, body.item_id)
        if not can_write_off(user, it.type):
            raise HTTPException(
                403,
                f"Списание {MATERIAL_TYPES.get(it.type, it.type)}: требуется отдельное право "
                "(обратитесь к администратору)",
            )
        qty = round(body.qty, 3)

        if body.batch_id:
            batches = [db.get(Batch, body.batch_id)]
            if not batches[0] or batches[0].item_id != it.id or batches[0].status != "active":
                raise HTTPException(404, "Партия не найдена")
        else:
            batches = (
                db.query(Batch)
                .filter(Batch.item_id == it.id, Batch.status == "active", Batch.qty > 0)
                .order_by(Batch.expires_on.is_(None), Batch.expires_on, Batch.id)
                .all()
            )
        available = round(sum(b.qty for b in batches if b), 3)
        if available < qty:
            raise HTTPException(422, f"Недостаточно остатка: доступно {available} {it.unit}")

        left = qty
        used = []
        for b in batches:
            if not b or left <= 0:
                continue
            take = min(b.qty, left)
            b.qty = round(b.qty - take, 3)
            left = round(left - take, 3)
            used.append((b, take))

        db.add(Operation(
            item_id=it.id, item_name=it.name, item_type=it.type, op_type="writeoff",
            batch_id=used[0][0].id, batch_no=", ".join(f"{b.batch_no} ({t})" for b, t in used),
            qty=qty, unit=it.unit,
            actor_id=user["id"], actor_name=user.get("name") or "", note=body.note,
            target_journal_code=body.target_journal_code, target_record_id=body.target_record_id,
        ))
        db.commit()
        audit_emit(
            "material.writeoff", user, "material", it.id, it.name,
            {
                "qty": qty, "unit": it.unit, "item_type": it.type,
                "target_journal_code": body.target_journal_code,
                "target_record_id": body.target_record_id, "note": body.note,
            },
        )
        return {
            "item_id": it.id, "item_name": it.name, "qty": qty, "unit": it.unit,
            "batches": [{"batch_no": b.batch_no, "taken": round(t, 3)} for b, t in used],
        }


@app.post("/batches/{batch_id}/inventory", status_code=201)
def inventory(batch_id: int, body: InventoryIn, request: Request):
    """Инвентаризация партии (усиленный режим прекурсоров)."""
    user = user_from_request(request)
    if not (user["role"] == "admin" or (user["role"] == "engineer" and user.get("pwp"))):
        raise HTTPException(403, "Инвентаризацию проводит администратор или сотрудник с правом на прекурсоры")
    with SessionLocal() as db:
        b = db.get(Batch, batch_id)
        if not b:
            raise HTTPException(404, "Партия не найдена")
        it = db.get(Item, b.item_id)
        old_qty = b.qty
        b.qty = round(body.actual_qty, 3)
        db.add(Operation(
            item_id=it.id, item_name=it.name, item_type=it.type, op_type="inventory",
            batch_id=b.id, batch_no=b.batch_no, qty=b.qty, unit=it.unit,
            actor_id=user["id"], actor_name=user.get("name") or "",
            note=body.note or f"Инвентаризация: было {old_qty}, стало {b.qty}",
        ))
        db.commit()
        audit_emit("material.inventory", user, "batch", b.id, f"{it.name}, партия {b.batch_no}",
                   {"old_qty": old_qty, "actual_qty": b.qty, "note": body.note})
        return batch_out(b, it)


@app.post("/batches/{batch_id}/suitability", status_code=201)
def suitability(batch_id: int, body: SuitabilityIn, request: Request):
    """Проверка пригодности реактива (журнал 9.10)."""
    user = user_from_request(request)
    if user["role"] not in ("admin", "engineer", "system"):
        raise HTTPException(403, "Проверку пригодности выполняет инженер или администратор")
    with SessionLocal() as db:
        b = db.get(Batch, batch_id)
        if not b:
            raise HTTPException(404, "Партия не найдена")
        it = db.get(Item, b.item_id)
        if not body.ok:
            b.status = "blocked"
        db.add(Operation(
            item_id=it.id, item_name=it.name, item_type=it.type, op_type="check",
            batch_id=b.id, batch_no=b.batch_no, qty=b.qty, unit=it.unit,
            actor_id=user["id"], actor_name=user.get("name") or "",
            note=body.note or ("Пригоден" if body.ok else "НЕ пригоден, партия заблокирована"),
        ))
        db.commit()
        audit_emit("material.suitability", user, "batch", b.id, f"{it.name}, партия {b.batch_no}",
                   {"ok": body.ok, "note": body.note})
        return batch_out(b, it)


@app.get("/operations")
def list_operations(
    request: Request,
    item_type: str | None = None,
    op_type: str | None = None,
    date_from: str | None = None,
    date_to: str | None = None,
    limit: int = 200,
):
    """Строки журналов учёта материалов (Ж-04/04, Ж-04/03 и др.)."""
    user_from_request(request)
    limit = min(limit, 1000)
    with SessionLocal() as db:
        q = db.query(Operation)
        if item_type:
            q = q.filter(Operation.item_type == item_type)
        if op_type:
            q = q.filter(Operation.op_type == op_type)
        if date_from:
            q = q.filter(Operation.ts >= datetime.fromisoformat(f"{date_from}T00:00:00"))
        if date_to:
            q = q.filter(Operation.ts <= datetime.fromisoformat(f"{date_to}T23:59:59"))
        rows = q.order_by(Operation.id.desc()).limit(limit).all()
        return [op_out(o) for o in rows]


# ---------- демо-данные ----------

def _wait_auth(attempts=60, delay=1.0):
    for _ in range(attempts):
        try:
            if httpx.get(f"{AUTH_URL}/health", timeout=2).status_code == 200:
                return True
        except Exception:
            pass
        time.sleep(delay)
    return False


def seed():
    with SessionLocal() as db:
        if db.query(Item).count():
            return
        _wait_auth()
        today = date.today()
        eng = {"id": None, "name": "Иванов И. И. (инженер)"}
        lab = {"id": None, "name": "Петрова П. П. (лаборант)"}
        try:
            users = {
                u["login"]: u
                for u in httpx.get(
                    f"{AUTH_URL}/internal/users",
                    headers={"X-Internal-Key": INTERNAL_KEY}, timeout=5,
                ).json()
            }
            if "eng1" in users:
                eng = {"id": users["eng1"]["id"], "name": users["eng1"]["full_name"]}
            if "lab1" in users:
                lab = {"id": users["lab1"]["id"], "name": users["lab1"]["full_name"]}
        except Exception:
            pass
        seed_actor = {"id": 0, "name": "Система (демо-данные)"}

        def add_item(name, mtype, unit, storage):
            it = Item(name=name, type=mtype, unit=unit, storage_place=storage)
            db.add(it)
            db.flush()
            return it

        def add_receipt(item, batch_no, qty, expires, actor, days_ago=30):
            ts = datetime.now(timezone.utc) - timedelta(days=days_ago)
            b = Batch(item_id=item.id, batch_no=batch_no, qty=qty, expires_on=expires,
                      received_at=ts, received_by_name=actor["name"])
            db.add(b)
            db.flush()
            db.add(Operation(
                ts=ts, item_id=item.id, item_name=item.name, item_type=item.type,
                op_type="receipt", batch_id=b.id, batch_no=batch_no, qty=qty, unit=item.unit,
                actor_id=actor["id"], actor_name=actor["name"], note="Поступление (демо)",
            ))
            return b

        nacl = add_item("Натрий хлористый, хч", "reagent", "г", "Шкаф №3")
        hcl = add_item("Кислота соляная (фиканал)", "reagent", "амп.", "Шкаф №1")
        nitrate = add_item("Стандарт-раствор нитратов, 10 мг/л", "reagent", "мл", "Холодильник №1")
        gso = add_item("ГСО 2213-81 (состав хлоридов)", "so", "амп.", "Шкаф СО")
        kmno4 = add_item("Калий марганцевокислый", "precursor", "г", "Сейф №1")
        h2so4 = add_item("Кислота серная концентрированная", "precursor", "мл", "Сейф №1")

        add_receipt(nacl, "2023-44", 500, today + timedelta(days=365), eng)
        add_receipt(hcl, "Ф-250", 3, today + timedelta(days=180), eng)
        add_receipt(nitrate, "СР-10", 200, today + timedelta(days=10), eng)  # истекает — уведомление
        add_receipt(gso, "2213-81/24", 2, today + timedelta(days=365), eng)
        add_receipt(kmno4, "2024-11", 100, today + timedelta(days=500), eng, days_ago=45)
        add_receipt(h2so4, "2024-7", 1000, today + timedelta(days=900), eng, days_ago=45)

        # одно списание в истории
        db.add(Operation(
            ts=datetime.now(timezone.utc) - timedelta(days=5),
            item_id=nacl.id, item_name=nacl.name, item_type=nacl.type,
            op_type="writeoff", batch_no="2023-44", qty=50, unit=nacl.unit,
            actor_id=lab["id"], actor_name=lab["name"],
            note="Навески для испытаний", target_journal_code="Ж-04/02",
        ))
        nacl_batches = db.query(Batch).filter(Batch.item_id == nacl.id).all()
        if nacl_batches:
            nacl_batches[0].qty = 450

        db.commit()
        for it in (nacl, hcl, nitrate, gso, kmno4, h2so4):
            audit_emit("material.item_created", seed_actor, "material", it.id, it.name, {"seed": True})
        print("[materials-seed] демо-данные созданы: 6 материалов, 6 партий, 7 операций")


seed()
