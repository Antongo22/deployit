using DeployIt.DTOs;
using DeployIt.Models;

namespace DeployIt.Components;

public static class Ui
{
    public static bool Active(DeploymentStatus? status) => status is DeploymentStatus.Queued or DeploymentStatus.Running;
    public static string Operation(DeploymentOperation operation) => operation switch {
        DeploymentOperation.Restart => "Перезапуск", DeploymentOperation.Stop => "Остановка", _ => "Деплой"
    };
    public static bool Stopped(DeploymentView? run) => run is { Operation: DeploymentOperation.Stop, Status: DeploymentStatus.Succeeded };
    public static string Status(DeploymentStatus? status) => status switch {
        DeploymentStatus.Queued => "В очереди", DeploymentStatus.Running => "Выполняется",
        DeploymentStatus.Succeeded => "Успешно", DeploymentStatus.Failed => "Ошибка",
        DeploymentStatus.Interrupted => "Прерван", DeploymentStatus.Canceled => "Отменён", _ => "Нет запусков"
    };
    public static string Time(DateTimeOffset? time) => time?.ToOffset(TimeSpan.FromHours(3)).ToString("dd.MM.yyyy HH:mm:ss") ?? "—";
    public static string RepositoryLink(string url) => url.EndsWith(".git", StringComparison.Ordinal) ? url[..^4] : url;
    public static string Commit(string? sha) => sha is { Length: >= 8 } ? sha[..8] : "—";
    public static string Error(Exception error) => error is DomainException ? error.Message : "Не удалось выполнить действие. Обновите страницу и повторите попытку.";
}
