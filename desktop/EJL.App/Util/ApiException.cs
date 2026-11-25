namespace EJL.Util;

/// <summary>Бизнес-ошибка API: HTTP-код + detail (формат FastAPI {\"detail\": \"...\"}, как ждёт фронтенд).</summary>
public sealed class ApiException(int status, string detail) : Exception(detail)
{
    public int Status { get; } = status;
}
