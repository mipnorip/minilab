using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace EJL.Util;

/// <summary>Запускает окно приложения: Edge в app-режиме на Windows, браузер по умолчанию — иначе.</summary>
public static class BrowserLauncher
{
    public static void OpenApp(string url)
    {
        if (OperatingSystem.IsWindows())
        {
            var edge = FindEdge();
            if (edge is not null)
            {
                Start(edge, $"--app={url} --window-size=1360,860");
                return;
            }
        }
        Open(url);
    }

    public static void Open(string url)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS())
            Start("open", $"\"{url}\"");
        else
            Start("xdg-open", url);
    }

    private static void Start(string fileName, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = false });
        }
        catch
        {
            // браузер не нашёлся — окно откроет пользователь вручную по адресу из лога
        }
    }

    private static string? FindEdge()
    {
        var candidates = new[]
        {
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';'))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "msedge.exe");
                if (File.Exists(p)) return p;
            }
            catch { /* ignore */ }
        }
        return null;
    }
}

/// <summary>Пульс фронтенда: пока открытое окно шлёт /api/ping — сервер жив.
/// Пропал пульс (окно закрыли) — завершаем процесс и корректно закрываем БД.</summary>
public static class Liveness
{
    private static long _lastMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static readonly long StartMs = _lastMs;

    public static void Touch() => Interlocked.Exchange(ref _lastMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    public static long LastMs => Interlocked.Read(ref _lastMs);
    public static long StartedAtMs => StartMs;
}

public static class Tcp
{
    /// <summary>Свободный порт для localhost (TcpListener :0).</summary>
    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
