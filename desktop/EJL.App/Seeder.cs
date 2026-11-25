using System.Reflection;
using System.Text.Json;
using EJL.Data;
using EJL.Domain;
using EJL.Util;
using Microsoft.EntityFrameworkCore;

namespace EJL;

/// <summary>Демо-данные при первом запуске (порт сидов auth/forms/records/materials):
/// 6 пользователей, 12 пресетов форм, 16 журналов согласованного перечня,
/// стартовые записи, материалы, события аудита.</summary>
public static class Seeder
{
    // журнал перечня: MVP (5) + Phase 2 (11); периодичность проверки — из перечня СМК
    private sealed record JournalDef(
        string Code, string Title, string Kind, string? Template, string Owner,
        string[] Members, int Check = 4, int Retention = 5, bool Daily = false,
        string? MaterialsType = null);

    private static readonly JournalDef[] JournalDefs =
    [
        new("Ж-04/01", "Журнал регистрации и движения образцов", "form", "T-01", "eng1",
            ["eng1", "lab1", "lab2"]),
        new("Ж-04/02", "Журнал записи результатов испытаний (рабочий)", "form", "T-02", "eng1",
            ["eng1", "lab1"]),
        new("9.5", "Журнал контроля температуры холодильников", "form", "T-03", "lab1",
            ["lab1", "lab2"], Retention: 3, Daily: true),
        new("Ж-04/04", "Журнал учёта реактивов", "materials", null, "eng1",
            ["eng1", "lab1"], MaterialsType: "reagent"),
        new("Ж-04/03", "Журнал регистрации и учёта прекурсоров", "materials", null, "eng1",
            ["eng1"], MaterialsType: "precursor"),

        new("9.3", "Журнал регистрации протоколов испытаний", "form", "T-04", "eng1",
            ["eng1", "lab1"]),
        new("9.4", "Журнал контроля условий окружающей среды", "form", "T-05", "lab1",
            ["lab1", "lab2"], Retention: 3, Daily: true),
        new("9.7", "Журнал учёта стандартных образцов", "materials", null, "eng1",
            ["eng1"], MaterialsType: "so"),
        new("Ж-04/05", "Журнал приготовления вспомогательных растворов", "form", "T-06", "lab1",
            ["lab1", "lab2"]),
        new("Ж-04/06", "Журнал контроля качества титрованных растворов и дистиллированной воды",
            "form", "T-07", "eng1", ["eng1", "lab1"]),
        new("9.10", "Журнал проверки пригодности реактивов", "materials", null, "eng1",
            ["eng1"], MaterialsType: "reagent"),
        new("Ж-04/07", "Журнал «Внутренний контроль качества испытаний»", "form", "T-08", "eng1",
            ["eng1", "lab1"], Check: 5),
        new("9.12", "Журнал контроля градуировочных характеристик", "form", "T-09", "eng1",
            ["eng1"], Check: 5),
        new("9.13", "Записи технического обслуживания оборудования", "form", "T-10", "eng1",
            ["eng1", "lab1"], Check: 5),
        new("9.14", "Журнал регистрации несоответствий", "form", "T-11", "qm",
            ["qm", "eng1"], Check: 5),
        new("9.15", "Журнал регистрации жалоб, претензий и апелляций", "form", "T-12", "qm",
            ["qm"], Check: 5),
    ];

    public static async Task SeedIfEmpty(EjlDbContext db, string demoPassword)
    {
        if (await db.Users.AnyAsync()) return;

        // ── пользователи (идентификаторы 1–6, как в web-версии) ────────────
        User NewUser(string login, string name, string role, bool pwp) => new()
        {
            Login = login, FullName = name, Role = role,
            CanWfPrecursors = pwp, PasswordHash = PasswordHasher.Hash(demoPassword),
        };
        db.Users.AddRange(
            NewUser("admin", "Администратор системы", "admin", false),
            NewUser("head", "Заведующий лабораторией", "head", false),
            NewUser("qm", "Менеджер по качеству", "quality_manager", false),
            NewUser("eng1", "Иванов И. И. (инженер)", "engineer", true),
            NewUser("lab1", "Петрова П. П. (лаборант)", "engineer", false),
            NewUser("lab2", "Сидоров С. С. (лаборант)", "engineer", false));
        await db.SaveChangesAsync();

        // ── пресеты форм (T-01…T-12, идентификаторы 1–12) ──────────────────
        foreach (var p in LoadPresets())
        {
            var t = new Template { Code = p.Code, Title = p.Title, IsPreset = true };
            db.Templates.Add(t);
            await db.SaveChangesAsync();
            var v = new TemplateVersion { TemplateId = t.Id, Version = 1, Columns = p.Columns };
            db.TemplateVersions.Add(v);
            await db.SaveChangesAsync();
            t.CurrentVersionId = v.Id;
            await db.SaveChangesAsync();
        }
        var templates = await db.Templates.ToDictionaryAsync(t => t.Code);

        // ── журналы (16): идемпотентно — только отсутствующие шифры ────────
        var users = await db.Users.ToDictionaryAsync(u => u.Login);
        var existingCodes = (await db.Journals.Select(j => j.Code).ToListAsync()).ToHashSet();
        var created = new List<Journal>();
        foreach (var d in JournalDefs)
        {
            if (existingCodes.Contains(d.Code)) continue;
            var template = d.Kind == "form" ? templates[d.Template!] : null;
            var owner = users[d.Owner];
            var j = new Journal
            {
                Code = d.Code, Title = d.Title, Kind = d.Kind,
                FormTemplateId = template?.Id,
                FormVersionId = template?.CurrentVersionId,
                MaterialsType = d.MaterialsType,
                OwnerId = owner.Id, OwnerName = owner.FullName,
                CheckPeriodWeeks = d.Check, RetentionYears = d.Retention, Daily = d.Daily,
            };
            db.Journals.Add(j);
            await db.SaveChangesAsync();
            var memberMap = d.Members.Select(m => users[m]).DistinctBy(u => u.Id).ToList();
            memberMap.RemoveAll(u => u.Id == owner.Id);
            memberMap.Add(owner);
            foreach (var u in memberMap)
                db.JournalMembers.Add(new JournalMember
                {
                    JournalId = j.Id, UserId = u.Id, Name = u.FullName,
                    Role = u.Id == owner.Id ? "owner" : "worker",
                });
            await db.SaveChangesAsync();
            created.Add(j);
        }

        var seedActor = new CurrentUser(0, "system", "Система (демо-данные)", "system", true);
        foreach (var j in created)
            Audit.Emit(db, "journal.created", seedActor, "journal", j.Id, $"{j.Code} {j.Title}",
                new() { ["kind"] = j.Kind, ["seed"] = true });

        // ── демо-записи: только один раз, пока записей нет ─────────────────
        if (await db.Records.AnyAsync()) { await db.SaveChangesAsync(); return; }
        var byCode = await db.Journals.ToDictionaryAsync(j => j.Code);
        var j01 = byCode["Ж-04/01"];
        var j02 = byCode["Ж-04/02"];
        var j95 = byCode["9.5"];
        var lab = users["lab1"];
        var lab2 = users["lab2"];
        var qm = users["qm"];
        var today = DateTime.Today;
        var yesterday = today.AddDays(-1);

        async Task<RecordEntity> AddRecord(Journal j, Dictionary<string, object?> raw, User u, int rowNo,
            DateTime? createdAt = null)
        {
            var columns = (await db.TemplateVersions.FindAsync(j.FormVersionId!.Value))!.Columns;
            var data = Columns.Apply(columns, raw, db,
                new CurrentUser(u.Id, u.Login, u.FullName, u.Role, u.CanWfPrecursors), rowNo);
            var now = createdAt ?? DateTime.UtcNow;
            var r = new RecordEntity
            {
                JournalId = j.Id, RowNo = rowNo, Data = data,
                AuthorId = u.Id, AuthorName = u.FullName,
                CreatedAt = now, UpdatedAt = now,
            };
            db.Records.Add(r);
            await db.SaveChangesAsync();
            return r;
        }

        var sample1 = await AddRecord(j01, new()
        {
            ["date_in"] = today.AddDays(-2).ToString("yyyy-MM-dd"),
            ["sample_code"] = "В-1024",
            ["object_name"] = "Вода питьевая, скважина №3",
            ["customer"] = "ЦКП «КУЭП»",
            ["test_type"] = "химический анализ",
            ["storage"] = "Холодильник №2",
        }, lab, 1);
        await AddRecord(j01, new()
        {
            ["date_in"] = yesterday.ToString("yyyy-MM-dd"),
            ["sample_code"] = "В-1025",
            ["object_name"] = "Вода дистиллированная",
            ["customer"] = "Лаборатория",
            ["test_type"] = "контроль качества",
            ["storage"] = "Шкаф №1",
        }, lab2, 2);

        await AddRecord(j02, new()
        {
            ["date"] = yesterday.ToString("yyyy-MM-dd"),
            ["sample"] = sample1.Id,
            ["method"] = "ГОСТ 31954-2012 (общая жёсткость)",
            ["mass_g"] = 0.5,
            ["volume_ml"] = 100.0,
            ["norm"] = "до 7,0 °Ж",
            ["verdict"] = "соответствует",
        }, lab, 1);

        // вчерашняя температура: сегодняшней нет — планировщик покажет уведомление
        await AddRecord(j95, new()
        {
            ["date"] = yesterday.ToString("yyyy-MM-dd"),
            ["fridge"] = "Холодильник №1",
            ["t_morning"] = 4.0,
            ["t_evening"] = 4.3,
        }, lab, 1, createdAt: DateTime.UtcNow.Date.AddDays(-1).AddHours(9).AddMinutes(30));

        // ── отметка проверки Ж-04/01 (10 дней назад) ────────────────────────
        var reviewTs = DateTime.UtcNow.AddDays(-10);
        db.ReviewLogs.Add(new ReviewLog
        {
            JournalId = j01.Id, ReviewerId = qm.Id, ReviewerName = qm.FullName,
            Note = "Журнал проверен, замечаний нет", Ts = reviewTs,
        });
        j01.LastReviewAt = reviewTs;
        await db.SaveChangesAsync();

        Audit.Emit(db, "journal.reviewed",
            new CurrentUser(qm.Id, qm.Login, qm.FullName, qm.Role, qm.CanWfPrecursors),
            "journal", j01.Id, $"{j01.Code} {j01.Title}",
            new() { ["note"] = "Журнал проверен, замечаний нет" });
        foreach (var r in await db.Records.OrderBy(r => r.Id).ToListAsync())
            Audit.Emit(db, "record.created", seedActor, "record", r.Id, $"Ж-04/{r.JournalId} №{r.RowNo}",
                new() { ["seed"] = true, ["row_no"] = r.RowNo });

        // ── материалы: 6 наименований, 6 партий, списание ──────────────────
        MaterialItem AddItem(string name, string type, string unit, string storage)
        {
            var it = new MaterialItem { Name = name, Type = type, Unit = unit, StoragePlace = storage };
            db.Items.Add(it);
            return it;
        }

        var eng = users["eng1"];
        var nacl = AddItem("Натрий хлористый, хч", "reagent", "г", "Шкаф №3");
        var hcl = AddItem("Кислота соляная (фиканал)", "reagent", "амп.", "Шкаф №1");
        var nitrate = AddItem("Стандарт-раствор нитратов, 10 мг/л", "reagent", "мл", "Холодильник №1");
        var gso = AddItem("ГСО 2213-81 (состав хлоридов)", "so", "амп.", "Шкаф СО");
        var kmno4 = AddItem("Калий марганцевокислый", "precursor", "г", "Сейф №1");
        var h2so4 = AddItem("Кислота серная концентрированная", "precursor", "мл", "Сейф №1");
        await db.SaveChangesAsync();

        async Task AddReceipt(MaterialItem item, string batchNo, double qty, int? expiresInDays,
            User actor, int daysAgo = 30)
        {
            var ts = DateTime.UtcNow.AddDays(-daysAgo);
            var b = new Batch
            {
                ItemId = item.Id, BatchNo = batchNo, Qty = qty,
                ExpiresOn = expiresInDays is null
                    ? null
                    : today.AddDays(expiresInDays.Value).ToString("yyyy-MM-dd"),
                ReceivedAt = ts, ReceivedByName = actor.FullName,
            };
            db.Batches.Add(b);
            await db.SaveChangesAsync();
            db.Operations.Add(new Operation
            {
                Ts = ts, ItemId = item.Id, ItemName = item.Name, ItemType = item.Type,
                OpType = "receipt", BatchId = b.Id, BatchNo = batchNo, Qty = qty, Unit = item.Unit,
                ActorId = actor.Id, ActorName = actor.FullName, Note = "Поступление (демо)",
            });
            await db.SaveChangesAsync();
        }

        await AddReceipt(nacl, "2023-44", 500.0, 365, eng);
        await AddReceipt(hcl, "Ф-250", 3.0, 180, eng);
        await AddReceipt(nitrate, "СР-10", 200.0, 10, eng); // истекает — уведомление
        await AddReceipt(gso, "2213-81/24", 2.0, 365, eng);
        await AddReceipt(kmno4, "2024-11", 100.0, 500, eng, daysAgo: 45);
        await AddReceipt(h2so4, "2024-7", 1000.0, 900, eng, daysAgo: 45);

        // одно списание в истории
        db.Operations.Add(new Operation
        {
            Ts = DateTime.UtcNow.AddDays(-5),
            ItemId = nacl.Id, ItemName = nacl.Name, ItemType = nacl.Type,
            OpType = "writeoff", BatchNo = "2023-44", Qty = 50.0, Unit = nacl.Unit,
            ActorId = lab.Id, ActorName = lab.FullName,
            Note = "Навески для испытаний", TargetJournalCode = "Ж-04/02",
        });
        var naclBatch = await db.Batches.FirstAsync(b => b.ItemId == nacl.Id);
        naclBatch.Qty = 450.0;
        await db.SaveChangesAsync();

        foreach (var it in (MaterialItem[])[nacl, hcl, nitrate, gso, kmno4, h2so4])
            Audit.Emit(db, "material.item_created", seedActor, "material", it.Id, it.Name,
                new() { ["seed"] = true });
        await db.SaveChangesAsync();
    }

    // ---------- пресеты из ресурсов ----------

    private sealed record Preset(string Code, string Title, List<FormColumn> Columns);

    private static List<Preset> LoadPresets()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("presets.json"))
            ?? throw new InvalidOperationException("presets.json не найден в ресурсах");
        using var stream = asm.GetManifestResourceStream(name)!;
        var result = new List<Preset>();
        using var doc = JsonSerializer.Deserialize<JsonDocument>(stream) ?? throw new InvalidOperationException();
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            var columnsJson = p.GetProperty("columns").GetRawText();
            var columns = JsonSerializer.Deserialize<List<FormColumn>>(columnsJson) ?? [];
            result.Add(new Preset(
                p.GetProperty("code").GetString()!,
                p.GetProperty("title").GetString()!,
                columns));
        }
        return result;
    }
}
