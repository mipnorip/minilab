"""Демо-данные records-svc: 16 журналов согласованного перечня + стартовые записи.

Журналы создаются идемпотентно: при каждом старте добавляются только те,
чьих шифров ещё нет в реестре. Периодичности проверки — из перечня СМК
(9.1–9.10 — 4 нед., 9.11–9.15 — 5 нед.).
"""

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


JOURNAL_DEFS = [
    # --- MVP (5) ---
    {"code": "Ж-04/01", "title": "Журнал регистрации и движения образцов",
     "kind": "form", "template": "T-01", "owner": "eng1",
     "members": ["eng1", "lab1", "lab2"], "check": 4, "retention": 5},
    {"code": "Ж-04/02", "title": "Журнал записи результатов испытаний (рабочий)",
     "kind": "form", "template": "T-02", "owner": "eng1",
     "members": ["eng1", "lab1"], "check": 4, "retention": 5},
    {"code": "9.5", "title": "Журнал контроля температуры холодильников",
     "kind": "form", "template": "T-03", "owner": "lab1",
     "members": ["lab1", "lab2"], "check": 4, "retention": 3, "daily": True},
    {"code": "Ж-04/04", "title": "Журнал учёта реактивов",
     "kind": "materials", "materials_type": "reagent", "owner": "eng1",
     "members": ["eng1", "lab1"], "check": 4, "retention": 5},
    {"code": "Ж-04/03", "title": "Журнал регистрации и учёта прекурсоров",
     "kind": "materials", "materials_type": "precursor", "owner": "eng1",
     "members": ["eng1"], "check": 4, "retention": 5},
    # --- Phase 2 (11) ---
    {"code": "9.3", "title": "Журнал регистрации протоколов испытаний",
     "kind": "form", "template": "T-04", "owner": "eng1",
     "members": ["eng1", "lab1"], "check": 4, "retention": 5},
    {"code": "9.4", "title": "Журнал контроля условий окружающей среды",
     "kind": "form", "template": "T-05", "owner": "lab1",
     "members": ["lab1", "lab2"], "check": 4, "retention": 3, "daily": True},
    {"code": "9.7", "title": "Журнал учёта стандартных образцов",
     "kind": "materials", "materials_type": "so", "owner": "eng1",
     "members": ["eng1"], "check": 4, "retention": 5},
    {"code": "Ж-04/05", "title": "Журнал приготовления вспомогательных растворов",
     "kind": "form", "template": "T-06", "owner": "lab1",
     "members": ["lab1", "lab2"], "check": 4, "retention": 5},
    {"code": "Ж-04/06", "title": "Журнал контроля качества титрованных растворов и дистиллированной воды",
     "kind": "form", "template": "T-07", "owner": "eng1",
     "members": ["eng1", "lab1"], "check": 4, "retention": 5},
    {"code": "9.10", "title": "Журнал проверки пригодности реактивов",
     "kind": "materials", "materials_type": "reagent", "owner": "eng1",
     "members": ["eng1"], "check": 4, "retention": 5},
    {"code": "Ж-04/07", "title": "Журнал «Внутренний контроль качества испытаний»",
     "kind": "form", "template": "T-08", "owner": "eng1",
     "members": ["eng1", "lab1"], "check": 5, "retention": 5},
    {"code": "9.12", "title": "Журнал контроля градуировочных характеристик",
     "kind": "form", "template": "T-09", "owner": "eng1",
     "members": ["eng1"], "check": 5, "retention": 5},
    {"code": "9.13", "title": "Записи технического обслуживания оборудования",
     "kind": "form", "template": "T-10", "owner": "eng1",
     "members": ["eng1", "lab1"], "check": 5, "retention": 5},
    {"code": "9.14", "title": "Журнал регистрации несоответствий",
     "kind": "form", "template": "T-11", "owner": "qm",
     "members": ["qm", "eng1"], "check": 5, "retention": 5},
    {"code": "9.15", "title": "Журнал регистрации жалоб, претензий и апелляций",
     "kind": "form", "template": "T-12", "owner": "qm",
     "members": ["qm"], "check": 5, "retention": 5},
]


def run_seed(SessionLocal, Journal, Member, Record, Revision, ReviewLog, apply_columns, audit_emit):
    from .main import fetch_columns  # лениво: main к этому моменту инициализирован

    if not _wait_service(FORMS_URL) or not _wait_service(AUTH_URL):
        print("[records-seed] соседние сервисы недоступны — демо-данные пропущены")
        return

    with SessionLocal() as db:
        try:
            templates = {
                t["code"]: t
                for t in httpx.get(
                    f"{FORMS_URL}/templates", headers={"X-Internal-Key": INTERNAL_KEY}, timeout=5,
                ).json()
            }
            users = {
                u["login"]: u
                for u in httpx.get(
                    f"{AUTH_URL}/internal/users", headers={"X-Internal-Key": INTERNAL_KEY}, timeout=5,
                ).json()
            }
        except Exception as exc:
            print(f"[records-seed] справочники недоступны: {exc}")
            return

        needed_users = {d["owner"] for d in JOURNAL_DEFS} | {
            m for d in JOURNAL_DEFS for m in d["members"]
        }
        needed_templates = {d["template"] for d in JOURNAL_DEFS if d["kind"] == "form"}
        if not needed_users <= set(users) or not needed_templates <= set(templates):
            print("[records-seed] нет нужных пресетов или пользователей — пропускаю")
            return

        def add_journal(d):
            template = templates.get(d["template"]) if d["kind"] == "form" else None
            owner = users[d["owner"]]
            j = Journal(
                code=d["code"], title=d["title"], kind=d["kind"],
                form_template_id=template["id"] if template else None,
                form_version_id=template["current_version_id"] if template else None,
                materials_type=d.get("materials_type"),
                owner_id=owner["id"], owner_name=owner["full_name"],
                check_period_weeks=d.get("check", 4),
                retention_years=d.get("retention", 5),
                daily=d.get("daily", False),
            )
            db.add(j)
            db.flush()
            member_map = {users[m]["id"]: users[m] for m in d["members"]}
            member_map[owner["id"]] = owner
            for u in member_map.values():
                role = "owner" if u["id"] == owner["id"] else "worker"
                db.add(Member(journal_id=j.id, user_id=u["id"], name=u["full_name"], role=role))
            return j

        existing_codes = {j.code for j in db.query(Journal).all()}
        created = []
        for d in JOURNAL_DEFS:
            if d["code"] in existing_codes:
                continue
            created.append(add_journal(d))
        if created:
            db.commit()
            seed_actor = {"id": 0, "name": "Система (демо-данные)"}
            for j in created:
                audit_emit("journal.created", seed_actor, "journal", j.id, f"{j.code} {j.title}",
                           {"kind": j.kind, "seed": True})
            print(f"[records-seed] создано журналов: {len(created)} (всего {len(existing_codes) + len(created)})")

        # --- демо-записи: только один раз, пока записей нет ---
        if db.query(Record).count() == 0:
            today = date.today()
            yesterday = today - timedelta(days=1)

            def by_code(code):
                return db.query(Journal).filter(Journal.code == code).first()

            j01 = by_code("Ж-04/01")
            j02 = by_code("Ж-04/02")
            j95 = by_code("9.5")
            lab1, lab2, qm = users["lab1"], users["lab2"], users["qm"]

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

            if j01 and j02 and j95:
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

                # вчерашняя запись температуры: сегодняшней нет — scheduler покажет уведомление
                add_record(j95, {
                    "date": yesterday.isoformat(),
                    "fridge": "Холодильник №1",
                    "t_morning": 4.0,
                    "t_evening": 4.3,
                }, lab1, 1,
                    created_at=datetime.combine(yesterday, dt_time(9, 30), tzinfo=timezone.utc),
                    auto_overrides={"date": yesterday.isoformat()})

                review_ts = datetime.now(timezone.utc) - timedelta(days=10)
                db.add(ReviewLog(
                    journal_id=j01.id, reviewer_id=qm["id"], reviewer_name=qm["full_name"],
                    note="Журнал проверен, замечаний нет", ts=review_ts,
                ))
                j01.last_review_at = review_ts
                db.commit()

                seed_actor = {"id": 0, "name": "Система (демо-данные)"}
                audit_emit("journal.reviewed", {"id": qm["id"], "name": qm["full_name"]},
                           "journal", j01.id, f"{j01.code} {j01.title}",
                           {"note": "Журнал проверен, замечаний нет"})
                for r in db.query(Record).all():
                    audit_emit("record.created", seed_actor, "record", r.id, f"Ж-04/{r.journal_id} №{r.row_no}",
                               {"seed": True, "row_no": r.row_no})
                print("[records-seed] демо-записи созданы: 4 записи, 1 проверка")
