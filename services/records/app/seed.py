"""Демо-данные records-svc: 5 журналов MVP, стартовые записи, отметка проверки."""

import os
import time
from datetime import date, datetime, time as dt_time, timedelta, timezone

import httpx

FORMS_URL = os.getenv("FORMS_URL", "http://forms-svc:8002")
AUTH_URL = os.getenv("AUTH_URL", "http://auth-svc:8001")
INTERNAL_KEY = os.getenv("INTERNAL_KEY", "dev-internal-key")


def _wait_service(url, attempts=60, delay=1.0):
    for _ in range(attempts):
        try:
            if httpx.get(f"{url}/health", timeout=2).status_code == 200:
                return True
        except Exception:
            pass
        time.sleep(delay)
    return False


def run_seed(SessionLocal, Journal, Member, Record, Revision, ReviewLog, apply_columns, audit_emit):
    from .main import fetch_columns  # лениво: main к этому моменту инициализирован

    if not _wait_service(FORMS_URL) or not _wait_service(AUTH_URL):
        print("[records-seed] соседние сервисы недоступны — демо-данные пропущены")
        return

    with SessionLocal() as db:
        if db.query(Journal).count():
            return

        try:
            templates = {
                t["code"]: t
                for t in httpx.get(
                    f"{FORMS_URL}/templates",
                    headers={"X-Internal-Key": INTERNAL_KEY}, timeout=5,
                ).json()
            }
            users = {
                u["login"]: u
                for u in httpx.get(
                    f"{AUTH_URL}/internal/users",
                    headers={"X-Internal-Key": INTERNAL_KEY}, timeout=5,
                ).json()
            }
        except Exception as exc:
            print(f"[records-seed] справочники недоступны: {exc}")
            return

        eng1 = users.get("eng1")
        lab1 = users.get("lab1")
        lab2 = users.get("lab2")
        qm = users.get("qm")
        t01, t02, t03 = templates.get("T-01"), templates.get("T-02"), templates.get("T-03")
        if not all([eng1, lab1, lab2, qm, t01, t02, t03]):
            print("[records-seed] нет нужных пресетов или пользователей — пропускаю")
            return

        today = date.today()
        yesterday = today - timedelta(days=1)
        seed_actor = {"id": 0, "name": "Система (демо-данные)"}

        def add_journal(code, title, kind, owner, members, template=None,
                        materials_type=None, check_weeks=4, retention=5, daily=False):
            j = Journal(
                code=code, title=title, kind=kind,
                form_template_id=template["id"] if template else None,
                form_version_id=template["current_version_id"] if template else None,
                materials_type=materials_type,
                owner_id=owner["id"], owner_name=owner["full_name"],
                check_period_weeks=check_weeks, retention_years=retention, daily=daily,
            )
            db.add(j)
            db.flush()
            member_map = {u["id"]: (u, role) for u, role in members}
            member_map[owner["id"]] = (owner, "owner")
            for u, role in member_map.values():
                db.add(Member(journal_id=j.id, user_id=u["id"], name=u["full_name"], role=role))
            return j

        def add_record(journal, raw, user, row_no, created_at=None, auto_overrides=None):
            columns = fetch_columns(journal.form_template_id, journal.form_version_id)
            data = apply_columns(columns, raw, db, user, row_no, old_data=auto_overrides)
            now = created_at or datetime.now(timezone.utc)
            r = Record(
                journal_id=journal.id, row_no=row_no, data=data,
                author_id=user["id"], author_name=user["full_name"],
                created_at=now, updated_at=now,
            )
            db.add(r)
            db.flush()
            return r

        # --- журналы MVP (5 шт.) ---
        j01 = add_journal(
            "Ж-04/01", "Журнал регистрации и движения образцов", "form", eng1,
            [(eng1, "owner"), (lab1, "worker"), (lab2, "worker")],
            template=t01,
        )
        j02 = add_journal(
            "Ж-04/02", "Журнал записи результатов испытаний (рабочий)", "form", eng1,
            [(eng1, "owner"), (lab1, "worker")],
            template=t02,
        )
        j95 = add_journal(
            "9.5", "Журнал контроля температуры холодильников", "form", lab1,
            [(lab1, "owner"), (lab2, "worker")],
            template=t03, retention=3, daily=True,
        )
        j04 = add_journal(
            "Ж-04/04", "Журнал учёта реактивов", "materials", eng1,
            [(eng1, "owner"), (lab1, "worker")],
            materials_type="reagent",
        )
        add_journal(
            "Ж-04/03", "Журнал регистрации и учёта прекурсоров", "materials", eng1,
            [(eng1, "owner")],
            materials_type="precursor",
        )

        # --- записи ---
        sample1 = add_record(j01, {
            "date_in": (today - timedelta(days=2)).isoformat(),
            "sample_code": "В-1024",
            "object_name": "Вода питьевая, скважина №3",
            "customer": "ЦКП «КУЭП»",
            "test_type": "химический анализ",
            "storage": "Холодильник №2",
        }, lab1, 1)
        add_record(j01, {
            "date_in": yesterday.isoformat(),
            "sample_code": "В-1025",
            "object_name": "Вода дистиллированная",
            "customer": "Лаборатория",
            "test_type": "контроль качества",
            "storage": "Шкаф №1",
        }, lab2, 2)

        add_record(j02, {
            "date": yesterday.isoformat(),
            "sample": sample1.id,
            "method": "ГОСТ 31954-2012 (общая жёсткость)",
            "mass_g": 0.5,
            "volume_ml": 100,
            "norm": "до 7,0 °Ж",
            "verdict": "соответствует",
        }, lab1, 1)

        # Вчерашняя запись температуры: сегодняшней нет — scheduler покажет уведомление
        add_record(j95, {
            "date": yesterday.isoformat(),
            "fridge": "Холодильник №1",
            "t_morning": 4.0,
            "t_evening": 4.3,
        }, lab1, 1,
            created_at=datetime.combine(yesterday, dt_time(9, 30), tzinfo=timezone.utc))

        # --- отметка проверки Ж-04/01 (10 дней назад) ---
        review_ts = datetime.now(timezone.utc) - timedelta(days=10)
        db.add(ReviewLog(
            journal_id=j01.id, reviewer_id=qm["id"], reviewer_name=qm["full_name"],
            note="Журнал проверен, замечаний нет", ts=review_ts,
        ))
        j01.last_review_at = review_ts

        db.commit()

        # события аудита для наполнения журнала аудита
        for j in (j01, j02, j95, j04):
            audit_emit("journal.created", seed_actor, "journal", j.id, f"{j.code} {j.title}",
                       {"kind": j.kind, "seed": True})
        audit_emit("journal.reviewed", {"id": qm["id"], "name": qm["full_name"]},
                   "journal", j01.id, f"{j01.code} {j01.title}", {"note": "Журнал проверен, замечаний нет"})
        for r in db.query(Record).all():
            audit_emit("record.created", seed_actor, "record", r.id, f"Ж-04/{r.journal_id} №{r.row_no}",
                       {"seed": True, "row_no": r.row_no})

        print("[records-seed] демо-данные созданы: 5 журналов, 4 записи, 1 проверка")
