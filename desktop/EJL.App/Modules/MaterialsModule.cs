using System.Globalization;
using System.Text.Json.Serialization;
using EJL.Data;
using EJL.Util;
using Microsoft.EntityFrameworkCore;

namespace EJL.Modules;

/// <summary>materials-svc: реактивы, СО, прекурсоры; усиленный режим (партии, отдельное право, инвентаризация).</summary>
public static class MaterialsModule
{
    public static readonly Dictionary<string, string> MaterialTypes = new()
    {
        ["reagent"] = "реактив",
        ["so"] = "стандартный образец",
        ["precursor"] = "прекурсор",
    };

    public static readonly Dictionary<string, string> OpTypes = new()
    {
        ["receipt"] = "поступление",
        ["writeoff"] = "списание",
        ["inventory"] = "инвентаризация",
        ["check"] = "проверка пригодности",
    };

    public static void MapMaterials(IEndpointRouteBuilder g)
    {
        g.MapGet("/items", async (
            HttpContext ctx, IDbContextFactory<EjlDbContext> dbf, string? type = null, string? q = null) =>
        {
            ctx.GetUser();
            if (type is not null && !MaterialTypes.ContainsKey(type))
                throw new ApiException(422, "type: reagent, so или precursor");
            await using var db = await dbf.CreateDbContextAsync();
            var query = db.Items.AsNoTracking().Where(i => i.IsActive);
            if (type is not null)
                query = query.Where(i => i.Type == type);
            var items = await query.OrderBy(i => i.Type).ThenBy(i => i.Name).ToListAsync();
            if (!string.IsNullOrEmpty(q))
                items = items.Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            var totals = await db.Batches.AsNoTracking()
                .Where(b => b.Status == "active")
                .ToListAsync();
            return Results.Json(items.Select(it => ItemOut(it, totals)).ToList());
        });

        g.MapPost("/items", async (
            HttpContext ctx, ItemCreate body, IDbContextFactory<EjlDbContext> dbf) =>
        {
            var user = ctx.GetUser();
            if (user.Role != "admin")
                throw new ApiException(403, "Номенклатуру материалов ведёт администратор");
            if (body.Type is null || !MaterialTypes.ContainsKey(body.Type))
                throw new ApiException(422, "type: reagent, so или precursor");
            if (body.Name is null || body.Name.Trim().Length is < 2 or > 128)
                throw new ApiException(422, "name: длина 2–128 символов");
            await using var db = await dbf.CreateDbContextAsync();
            var it = new MaterialItem
            {
                Name = body.Name.Trim(),
                Type = body.Type,
                Unit = (body.Unit ?? "").Trim(),
                StoragePlace = (body.StoragePlace ?? "").Trim(),
            };
            db.Items.Add(it);
            await db.SaveChangesAsync();
            Audit.Emit(db, "material.item_created", user, "material", it.Id, it.Name,
                new() { ["type"] = it.Type });
            await db.SaveChangesAsync();
            return Results.Json(ItemOut(it, await db.Batches.AsNoTracking()
                .Where(b => b.Status == "active").ToListAsync()), statusCode: 201);
        });

        g.MapGet("/batches", async (
            HttpContext ctx, IDbContextFactory<EjlDbContext> dbf, int? item_id = null, int? expiring_days = null) =>
        {
            ctx.GetUser();
            await using var db = await dbf.CreateDbContextAsync();
            var query = db.Batches.AsNoTracking().Where(b => b.Status == "active");
            if (item_id is not null)
                query = query.Where(b => b.ItemId == item_id);
            var batches = await query.ToListAsync();
            if (expiring_days is not null)
            {
                var cutoff = DateOnly.FromDateTime(DateTime.Today).AddDays(expiring_days.Value)
                    .ToString("yyyy-MM-dd");
                batches = batches.Where(b =>
                    b.ExpiresOn is not null && string.Compare(b.ExpiresOn, cutoff) <= 0).ToList();
            }
            batches = batches
                .OrderBy(b => b.ExpiresOn is null ? 0 : 1)
                .ThenBy(b => b.ExpiresOn)
                .ThenBy(b => b.Id)
                .ToList();
            var items = await db.Items.AsNoTracking().ToDictionaryAsync(i => i.Id);
            var result = new List<object>();
            foreach (var b in batches)
                if (items.TryGetValue(b.ItemId, out var it))
                    result.Add(BatchOut(b, it));
            return Results.Json(result);
        });

        g.MapGet("/expiring", async (
            HttpContext ctx, IDbContextFactory<EjlDbContext> dbf, int days = 14) =>
        {
            ctx.GetUser();
            await using var db = await dbf.CreateDbContextAsync();
            var cutoff = DateOnly.FromDateTime(DateTime.Today).AddDays(days).ToString("yyyy-MM-dd");
            var today = DateTime.Today.ToString("yyyy-MM-dd");
            var rows = await db.Batches.AsNoTracking()
                .Where(b => b.Status == "active" && b.Qty > 0 && b.ExpiresOn != null)
                .ToListAsync();
            rows = rows.Where(b => string.Compare(b.ExpiresOn, cutoff) <= 0)
                .OrderBy(b => b.ExpiresOn)
                .ToList();
            var items = await db.Items.AsNoTracking().ToDictionaryAsync(i => i.Id);
            var result = new List<Dictionary<string, object?>>();
            foreach (var b in rows)
            {
                if (!items.TryGetValue(b.ItemId, out var it)) continue;
                var bo = BatchOut(b, it) as Dictionary<string, object?> ?? [];
                bo.Remove("item_type");
                result.Add(bo);
            }
            return Results.Json(result);
        });

        g.MapPost("/receipts", async (
            HttpContext ctx, ReceiptIn body, IDbContextFactory<EjlDbContext> dbf) =>
        {
            var user = ctx.GetUser();
            if (user.Role is not ("admin" or "engineer" or "system"))
                throw new ApiException(403, "Поступление оформляет инженер или администратор");
            DateOnly? expires = null;
            if (!string.IsNullOrEmpty(body.ExpiresOn))
            {
                var exp = body.ExpiresOn.Length > 10 ? body.ExpiresOn[..10] : body.ExpiresOn;
                if (!DateOnly.TryParseExact(exp, "yyyy-MM-dd", out var d))
                    throw new ApiException(422, "Срок годности: дата в формате ГГГГ-ММ-ДД");
                expires = d;
            }
            await using var db = await dbf.CreateDbContextAsync();
            var it = await GetItemOr404(db, body.ItemId);
            var b = new Batch
            {
                ItemId = it.Id,
                BatchNo = (body.BatchNo ?? "").Trim(),
                Qty = Math.Round(body.Qty, 3),
                ExpiresOn = expires?.ToString("yyyy-MM-dd"),
                ReceivedByName = user.Name ?? "",
            };
            db.Batches.Add(b);
            await db.SaveChangesAsync();
            db.Operations.Add(new Operation
            {
                ItemId = it.Id, ItemName = it.Name, ItemType = it.Type, OpType = "receipt",
                BatchId = b.Id, BatchNo = b.BatchNo, Qty = b.Qty, Unit = it.Unit,
                ActorId = user.Id, ActorName = user.Name ?? "",
            });
            Audit.Emit(db, "material.receipt", user, "batch", b.Id, $"{it.Name}, партия {b.BatchNo}",
                new() { ["qty"] = b.Qty, ["unit"] = it.Unit, ["expires_on"] = body.ExpiresOn });
            await db.SaveChangesAsync();
            return Results.Json(BatchOut(b, it), statusCode: 201);
        });

        g.MapPost("/writeoffs", async (
            HttpContext ctx, WriteOffIn body, IDbContextFactory<EjlDbContext> dbf) =>
        {
            var user = ctx.GetUser();
            await using var db = await dbf.CreateDbContextAsync();
            var it = await GetItemOr404(db, body.ItemId);
            if (!CanWriteOff(user, it.Type))
                throw new ApiException(403,
                    $"Списание {MaterialTypes.GetValueOrDefault(it.Type, it.Type)}: требуется отдельное право " +
                    "(обратитесь к администратору)");
            var qty = Math.Round(body.Qty, 3);

            List<Batch> batches;
            if (body.BatchId is { } bid)
            {
                var b = await db.Batches.FindAsync(bid);
                if (b is null || b.ItemId != it.Id || b.Status != "active")
                    throw new ApiException(404, "Партия не найдена");
                batches = [b];
            }
            else
            {
                // FEFO: сначала партии с ближайшим сроком годности (null — в конце)
                batches = await db.Batches
                    .Where(x => x.ItemId == it.Id && x.Status == "active" && x.Qty > 0)
                    .ToListAsync();
                batches = batches
                    .OrderBy(b => b.ExpiresOn is null ? 0 : 1)
                    .ThenBy(b => b.ExpiresOn)
                    .ThenBy(b => b.Id)
                    .ToList();
            }
            var available = Math.Round(batches.Where(b => b is not null).Sum(b => b!.Qty), 3);
            if (available < qty)
                throw new ApiException(422, $"Недостаточно остатка: доступно {available} {it.Unit}");

            // FEFO: сначала партии с ближайшим сроком годности
            var left = qty;
            var used = new List<(Batch b, double taken)>();
            foreach (var b in batches)
            {
                if (b is null || left <= 0) continue;
                var take = Math.Min(b.Qty, left);
                b.Qty = Math.Round(b.Qty - take, 3);
                left = Math.Round(left - take, 3);
                used.Add((b, take));
            }

            db.Operations.Add(new Operation
            {
                ItemId = it.Id, ItemName = it.Name, ItemType = it.Type, OpType = "writeoff",
                BatchId = used.Count > 0 ? used[0].b.Id : null,
                BatchNo = string.Join(", ", used.Select(u => $"{u.b.BatchNo} ({u.taken})")),
                Qty = qty, Unit = it.Unit,
                ActorId = user.Id, ActorName = user.Name ?? "", Note = body.Note,
                TargetJournalCode = body.TargetJournalCode, TargetRecordId = body.TargetRecordId,
            });
            Audit.Emit(db, "material.writeoff", user, "material", it.Id, it.Name,
                new()
                {
                    ["qty"] = qty,
                    ["unit"] = it.Unit,
                    ["item_type"] = it.Type,
                    ["target_journal_code"] = body.TargetJournalCode,
                    ["target_record_id"] = body.TargetRecordId,
                    ["note"] = body.Note,
                });
            await db.SaveChangesAsync();
            return Results.Json(new
            {
                item_id = it.Id,
                item_name = it.Name,
                qty,
                unit = it.Unit,
                batches = used.Select(u => new { batch_no = u.b.BatchNo, taken = Math.Round(u.taken, 3) }).ToList(),
            }, statusCode: 201);
        });

        g.MapPost("/batches/{batchId:int}/inventory", async (
            HttpContext ctx, int batchId, InventoryIn body, IDbContextFactory<EjlDbContext> dbf) =>
        {
            var user = ctx.GetUser();
            if (user.Role != "admin" && !(user.Role == "engineer" && user.Pwp))
                throw new ApiException(403,
                    "Инвентаризацию проводит администратор или сотрудник с правом на прекурсоры");
            await using var db = await dbf.CreateDbContextAsync();
            var b = await db.Batches.FindAsync(batchId);
            if (b is null)
                throw new ApiException(404, "Партия не найдена");
            var it = await db.Items.FindAsync(b.ItemId)
                ?? throw new ApiException(404, "Материал не найден");
            var oldQty = b.Qty;
            b.Qty = Math.Round(body.ActualQty, 3);
            db.Operations.Add(new Operation
            {
                ItemId = it.Id, ItemName = it.Name, ItemType = it.Type, OpType = "inventory",
                BatchId = b.Id, BatchNo = b.BatchNo, Qty = b.Qty, Unit = it.Unit,
                ActorId = user.Id, ActorName = user.Name ?? "",
                Note = body.Note ?? $"Инвентаризация: было {oldQty}, стало {b.Qty}",
            });
            Audit.Emit(db, "material.inventory", user, "batch", b.Id, $"{it.Name}, партия {b.BatchNo}",
                new() { ["old_qty"] = oldQty, ["actual_qty"] = b.Qty, ["note"] = body.Note });
            await db.SaveChangesAsync();
            return Results.Json(BatchOut(b, it), statusCode: 201);
        });

        g.MapPost("/batches/{batchId:int}/suitability", async (
            HttpContext ctx, int batchId, SuitabilityIn body, IDbContextFactory<EjlDbContext> dbf) =>
        {
            var user = ctx.GetUser();
            if (user.Role is not ("admin" or "engineer" or "system"))
                throw new ApiException(403, "Проверку пригодности выполняет инженер или администратор");
            await using var db = await dbf.CreateDbContextAsync();
            var b = await db.Batches.FindAsync(batchId);
            if (b is null)
                throw new ApiException(404, "Партия не найдена");
            var it = await db.Items.FindAsync(b.ItemId)
                ?? throw new ApiException(404, "Материал не найден");
            if (!body.Ok)
                b.Status = "blocked";
            db.Operations.Add(new Operation
            {
                ItemId = it.Id, ItemName = it.Name, ItemType = it.Type, OpType = "check",
                BatchId = b.Id, BatchNo = b.BatchNo, Qty = b.Qty, Unit = it.Unit,
                ActorId = user.Id, ActorName = user.Name ?? "",
                Note = body.Note ?? (body.Ok ? "Пригоден" : "НЕ пригоден, партия заблокирована"),
            });
            Audit.Emit(db, "material.suitability", user, "batch", b.Id, $"{it.Name}, партия {b.BatchNo}",
                new() { ["ok"] = body.Ok, ["note"] = body.Note });
            await db.SaveChangesAsync();
            return Results.Json(BatchOut(b, it), statusCode: 201);
        });

        g.MapGet("/operations", async (
            HttpContext ctx, IDbContextFactory<EjlDbContext> dbf,
            string? item_type = null, string? op_type = null,
            string? date_from = null, string? date_to = null, int limit = 200) =>
        {
            ctx.GetUser();
            limit = Math.Min(limit, 1000);
            await using var db = await dbf.CreateDbContextAsync();
            var q = db.Operations.AsNoTracking().AsQueryable();
            if (item_type is not null) q = q.Where(o => o.ItemType == item_type);
            if (op_type is not null) q = q.Where(o => o.OpType == op_type);
            if (date_from is not null &&
                DateTime.TryParse(date_from + "T00:00:00", null, DateTimeStyles.None, out var fromTs))
                q = q.Where(o => o.Ts >= fromTs);
            if (date_to is not null &&
                DateTime.TryParse(date_to + "T23:59:59", null, DateTimeStyles.None, out var toTs))
                q = q.Where(o => o.Ts <= toTs);
            var rows = await q.OrderByDescending(o => o.Id).Take(limit).ToListAsync();
            return Results.Json(rows.Select(OpOut).ToList());
        });
    }

    // ---------- права ----------

    private static bool CanWriteOff(CurrentUser user, string itemType)
    {
        if (user.Role == "admin") return true;
        if (user.Role != "engineer") return false;
        if (itemType == "precursor") return user.Pwp;
        return true;
    }

    private static async Task<MaterialItem> GetItemOr404(EjlDbContext db, int itemId)
    {
        var it = await db.Items.FindAsync(itemId);
        if (it is null || !it.IsActive)
            throw new ApiException(404, "Материал не найден");
        return it;
    }

    // ---------- сериализация ----------

    public static object ItemOut(MaterialItem it, List<Batch> activeBatches)
    {
        var total = activeBatches.Where(b => b.ItemId == it.Id).Sum(b => b.Qty);
        var min = activeBatches
            .Where(b => b.ItemId == it.Id && b.Qty > 0 && b.ExpiresOn is not null)
            .Select(b => b.ExpiresOn)
            .DefaultIfEmpty()
            .Min();
        return new
        {
            id = it.Id,
            name = it.Name,
            type = it.Type,
            type_title = MaterialTypes.GetValueOrDefault(it.Type, it.Type),
            unit = it.Unit,
            storage_place = it.StoragePlace,
            is_active = it.IsActive,
            total_qty = Math.Round(total, 3),
            min_expires = min,
        };
    }

    public static object BatchOut(Batch b, MaterialItem it)
    {
        int? daysLeft = null;
        if (b.ExpiresOn is not null && DateOnly.TryParseExact(b.ExpiresOn, "yyyy-MM-dd", out var d))
            daysLeft = d.DayNumber - DateOnly.FromDateTime(DateTime.Today).DayNumber;
        return new Dictionary<string, object?>
        {
            ["id"] = b.Id,
            ["item_id"] = b.ItemId,
            ["item_name"] = it.Name,
            ["item_type"] = it.Type,
            ["unit"] = it.Unit,
            ["batch_no"] = b.BatchNo,
            ["qty"] = Math.Round(b.Qty, 3),
            ["expires_on"] = b.ExpiresOn,
            ["days_left"] = daysLeft,
            ["status"] = b.Status,
            ["received_at"] = b.ReceivedAt,
            ["received_by_name"] = b.ReceivedByName,
        };
    }

    private static object OpOut(Operation o) => new
    {
        id = o.Id,
        ts = o.Ts,
        item_id = o.ItemId,
        item_name = o.ItemName,
        item_type = o.ItemType,
        op_type = o.OpType,
        op_type_title = OpTypes.GetValueOrDefault(o.OpType, o.OpType),
        batch_no = o.BatchNo,
        qty = Math.Round(o.Qty, 3),
        unit = o.Unit,
        actor_name = o.ActorName,
        note = o.Note,
        target_journal_code = o.TargetJournalCode,
        target_record_id = o.TargetRecordId,
    };
}

// ---------- DTO ----------

public sealed record ItemCreate(
    string? Name,
    string? Type,
    string? Unit,
    [property: JsonPropertyName("storage_place")] string? StoragePlace);

public sealed record ReceiptIn(
    [property: JsonPropertyName("item_id")] int ItemId,
    double Qty,
    [property: JsonPropertyName("batch_no")] string? BatchNo,
    [property: JsonPropertyName("expires_on")] string? ExpiresOn = null);

public sealed record WriteOffIn(
    [property: JsonPropertyName("item_id")] int ItemId,
    double Qty,
    [property: JsonPropertyName("batch_id")] int? BatchId = null,
    [property: JsonPropertyName("target_journal_code")] string? TargetJournalCode = null,
    [property: JsonPropertyName("target_record_id")] int? TargetRecordId = null,
    string? Note = null);

public sealed record InventoryIn(
    [property: JsonPropertyName("actual_qty")] double ActualQty,
    string? Note = null);

public sealed record SuitabilityIn(bool Ok, string? Note = null);
