using System.Net;
using System.Reflection;
using System.Text.Json;
using EJL;
using EJL.Data;
using EJL.Modules;
using EJL.Util;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

// ════════════════════════════════════════════════════════════════════════
// ЭЖЛ desktop: один exe = веб-сервер (Kestrel) + SPA + SQLite.
// Запуск: поднимаем сервер на 127.0.0.1, открываем окно приложения (Edge
// в app-режиме), живём, пока окно открыто (пульс /api/ping).
// Данные: EJL_DATA или %LOCALAPPDATA%\EJL (БД, секрет, лог, резервные копии).
// ════════════════════════════════════════════════════════════════════════

static bool Flag(string name) =>
    Environment.GetEnvironmentVariable(name) is "1" or "true" or "True" or "TRUE";

var dataDir = Environment.GetEnvironmentVariable("EJL_DATA")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EJL");
Directory.CreateDirectory(dataDir);
var paths = new AppPaths(
    DataDir: dataDir,
    DbPath: Path.Combine(dataDir, "ejl.db"),
    SecretPath: Path.Combine(dataDir, "secret.key"),
    LogPath: Path.Combine(dataDir, "ejl.log"),
    UrlFile: Path.Combine(dataDir, "server.url"));

// ── единственный экземпляр: эксклюзивная блокировка файла ───────────────
FileStream? instanceLock;
try
{
    instanceLock = new FileStream(
        Path.Combine(dataDir, "server.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
}
catch (IOException)
{
    if (File.Exists(paths.UrlFile))
    {
        BrowserLauncher.OpenApp(File.ReadAllText(paths.UrlFile).Trim());
        return 0; // окно уже работающего приложения
    }
    Console.Error.WriteLine("Приложение уже запущено, но адрес его окна не найден.");
    return 1;
}

var builder = WebApplication.CreateBuilder(args);
var port = int.TryParse(Environment.GetEnvironmentVariable("EJL_PORT"), out var fixedPort)
    ? fixedPort
    : Tcp.FreePort();
builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));

var secret = JwtService.LoadOrCreateSecret(paths.SecretPath);
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(new JwtService(secret));
builder.Services.AddDbContextFactory<EjlDbContext>(o =>
    o.UseSqlite($"Data Source={paths.DbPath};Default Timeout=15"));
builder.Services.AddHostedService<SchedulerService>();
builder.Services.ConfigureHttpJsonOptions(o => Json.Configure(o.SerializerOptions));
builder.Logging.AddProvider(new FileLoggerProvider(paths.LogPath));

var app = builder.Build();
var url = $"http://127.0.0.1:{port}/";

// ── база данных: схема + WAL + демо-данные при первом запуске ───────────
var dbFactory = app.Services.GetRequiredService<IDbContextFactory<EjlDbContext>>();
using (var db = await dbFactory.CreateDbContextAsync())
{
    await db.Database.EnsureCreatedAsync();
    var conn = new SqliteConnection($"Data Source={paths.DbPath}");
    await conn.OpenAsync();
    try
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        await cmd.ExecuteNonQueryAsync();
    }
    finally
    {
        await conn.DisposeAsync();
    }
    var demoPassword = Environment.GetEnvironmentVariable("EJL_DEMO_PASSWORD") ?? "demo123";
    await Seeder.SeedIfEmpty(db, demoPassword);
}

// ── ошибки в формате FastAPI {"detail": "..."} — так их парсит фронтенд ─
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async ctx =>
    {
        var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
        var (status, detail) = ex switch
        {
            ApiException api => (api.Status, api.Message),
            BadHttpRequestException => (400, "Некорректный запрос"),
            JsonException => (422, "Некорректное тело запроса"),
            _ => (500, "Внутренняя ошибка сервера"),
        };
        if (status == 500)
            app.Logger.LogError(ex, "Необработанная ошибка: {Path}", ctx.Request.Path);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { detail }, Json.Opts));
    });
});

// ── SPA: статика вшита в exe (EmbeddedResource), отдаём из манифеста ─────
var assembly = typeof(EjlDbContext).Assembly;
var wwwroot = new ManifestEmbeddedFileProvider(assembly, "wwwroot");
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = wwwroot });
app.UseStaticFiles(new StaticFileOptions { FileProvider = wwwroot });

// ── API (маршруты как у nginx-gateway: /api/<service>/...) ───────────────
var api = app.MapGroup("/api");
AuthModule.MapAuth(api.MapGroup("/auth"));
FormsModule.MapForms(api.MapGroup("/forms"));
RecordsModule.MapRecords(api.MapGroup("/records"));
MaterialsModule.MapMaterials(api.MapGroup("/materials"));
NotificationsModule.MapNotifications(api.MapGroup("/notifications"));
AuditModule.MapAudit(api.MapGroup("/audit"));

// пульс окна: пока фронтенд жив, живёт и приложение
api.MapPost("/ping", () =>
{
    Liveness.Touch();
    return Results.Json(new { ok = true });
});

// резервная копия: онлайн-бэкап SQLite в data/backup
api.MapPost("/backup", async (HttpContext ctx) =>
{
    ctx.GetUser();
    var backupDir = Path.Combine(paths.DataDir, "backup");
    Directory.CreateDirectory(backupDir);
    var file = Path.Combine(backupDir, $"ejl-{DateTime.Now:yyyyMMdd-HHmmss}.db");
    await using var dst = new SqliteConnection($"Data Source={file}");
    await dst.OpenAsync();
    using var src = new SqliteConnection($"Data Source={paths.DbPath}");
    await src.OpenAsync();
    try { src.BackupDatabase(dst); }
    finally { await src.DisposeAsync(); }
    app.Logger.LogInformation("Резервная копия: {File}", file);
    return Results.Json(new { ok = true, path = file });
});

// SPA-маршруты React (/journals, /login, ...) — отдаём index.html
app.MapFallback(async (HttpContext ctx) =>
{
    var path = ctx.Request.Path.Value?.TrimStart('/') ?? "";
    if (path.StartsWith("api", StringComparison.OrdinalIgnoreCase) || path.StartsWith("api/"))
    {
        ctx.Response.StatusCode = 404;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync("{\"detail\":\"Не найдено\"}");
        return;
    }
    var file = wwwroot.GetFileInfo("index.html");
    if (!file.Exists)
    {
        ctx.Response.StatusCode = 404;
        return;
    }
    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "text/html; charset=utf-8";
    await using var fs = file.CreateReadStream();
    await fs.CopyToAsync(ctx.Response.Body);
});

// ── запуск: адрес в файл, окно браузера, наблюдение за пульсом ──────────
await File.WriteAllTextAsync(paths.UrlFile, url);
if (!Flag("EJL_NO_BROWSER"))
    BrowserLauncher.OpenApp(url);

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
if (!Flag("EJL_NO_LIVENESS"))
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(90)); // окно может открываться небыстро
        while (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            var idleMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Liveness.LastMs;
            if (idleMs > 45_000)
            {
                app.Logger.LogInformation("Окно приложения закрыто (пульс прекратился) — завершаемся.");
                lifetime.StopApplication();
                return;
            }
        }
    });
}

lifetime.ApplicationStopping.Register(() =>
{
    try { SqliteConnection.ClearAllPools(); } catch { /* ignore */ }
    try { instanceLock.Dispose(); } catch { /* ignore */ }
    try { File.Delete(paths.UrlFile); } catch { /* ignore */ }
});

app.Logger.LogInformation("ЭЖЛ запущен: {Url} · данные: {DataDir}", url, dataDir);
app.Run();
return 0;
