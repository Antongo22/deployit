using System.Net.Sockets;
using DeployIt.DTOs;
using Renci.SshNet.Common;

namespace DeployIt.Services;

public static class DeploymentDiagnostics
{
    public static string StageName(string stage) => stage switch {
        "ssh" => "Подключение по SSH", "credentials" => "Передача доступа к Git",
        "prepare" => "Подготовка каталога", "clone" => "Получение репозитория",
        "environment" => "Подключение .env", "deploy-command" => "Команда деплоя",
        "restart-command" => "Команда перезапуска", "publish" => "Обновление текущего релиза",
        "complete" => "Завершено", "" => "Ожидание запуска", _ => stage
    };

    public static string InferStage(string log)
    {
        if (log.Contains("Выполнение команды деплоя")) return "deploy-command";
        if (log.Contains("Перезапуск текущего релиза")) return "restart-command";
        if (log.Contains("Клонирование выбранной ветки")) return "clone";
        return "ssh";
    }

    public static string Failure(int? exit, string stage, string output, int? timeoutMinutes = null)
    {
        var prefix = $"Этап «{StageName(stage)}». ";
        if (exit == 124)
            return prefix + $"Превышен таймаут{(timeoutMinutes.HasValue ? $" {timeoutMinutes} мин." : " выполнения")} (код {exit}). "
                + "Проверьте последние строки вывода. Если сборка ещё работала, увеличьте «Таймаут» в настройках проекта, например до 20 минут.";
        if (exit == 137)
            return prefix + "Процесс принудительно завершён сигналом KILL (код 137). Возможен таймаут или нехватка памяти. Проверьте последние строки, таймаут проекта и журнал ядра: journalctl -k --since '-10 min'.";
        if (exit == 143) return prefix + "Команда завершена сигналом TERM (код 143). Если остановку не запрашивали, проверьте SSH-соединение и журнал сервера.";
        if (exit == 75 && stage == "prepare") return prefix + "Каталог занят другим запуском или сохранением .env. Дождитесь его завершения.";
        if (exit == 66 && stage == "prepare") return prefix + "Текущий релиз не найден. Сначала выполните успешный деплой.";
        if (exit == 127) return prefix + "Команда или программа не найдена на сервере. Например, проверьте: docker compose version; git --version.";
        if (exit == 126) return prefix + "Команду нельзя запустить. Проверьте права и путь к исполняемому файлу; например: chmod +x deploy.sh.";
        var tail = output.Length > 16000 ? output[^16000..] : output;
        if (tail.Contains("port is already allocated", StringComparison.OrdinalIgnoreCase)
            || tail.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
            return prefix + "Порт уже занят на сервере. Проверьте docker ps и порт публикации в compose.yaml/.env, например APP_PORT=8081.";
        if (tail.Contains("no space left on device", StringComparison.OrdinalIgnoreCase))
            return prefix + "На сервере закончилось место. Проверьте df -h и docker system df.";
        if (tail.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase))
            return prefix + "Docker daemon недоступен. Проверьте: systemctl status docker.";
        if (tail.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
            return prefix + "Отказано в доступе. Проверьте указанный в последних строках файл или Docker socket и права SSH-пользователя.";
        if (tail.Contains("authentication failed", StringComparison.OrdinalIgnoreCase)
            || tail.Contains("repository not found", StringComparison.OrdinalIgnoreCase))
            return prefix + "Git не дал доступ к репозиторию. Проверьте адрес репозитория, токен подключения и доступ к выбранной ветке.";
        if (tail.Contains("failed to solve", StringComparison.OrdinalIgnoreCase)
            || tail.Contains("error CS", StringComparison.OrdinalIgnoreCase))
            return prefix + $"Сборка приложения завершилась ошибкой (код {exit}). Конкретная ошибка Docker/.NET показана ниже в последних строках вывода.";
        if (stage == "clone") return prefix + $"Git завершился с кодом {exit}. Проверьте ветку, токен и доступ сервера к GitHub/GitLab; причина показана в последних строках.";
        return prefix + $"Команда завершилась с кодом {exit?.ToString() ?? "не получен"}. Смотрите последние строки вывода ниже. "
            + "Для воспроизведения откройте SSH-терминал и выполните команду из настроек проекта.";
    }

    public static string ExceptionFailure(Exception error, string stage, int timeoutMinutes)
    {
        if (error is DomainException) return $"Этап «{StageName(stage)}». {error.Message}";
        if (error is SshOperationTimeoutException)
            return $"Этап «{StageName(stage)}»: SSH не дождался ответа ({error.GetType().Name}). "
                + $"Таймаут проекта — {timeoutMinutes} мин. Проверьте SSH, сеть и последние строки вывода; результат удалённой команды может быть неизвестен.";
        if (error is SshAuthenticationException)
            return "SSH отклонил учётные данные. Проверьте пользователя и выбранный ключ или пароль в настройках проекта.";
        if (error is SftpPermissionDeniedException)
            return "Не удалось передать доступ к Git: SFTP отказал в записи. Проверьте права SSH-пользователя на /tmp.";
        if (error is SocketException || error is SshConnectionException)
            return $"Этап «{StageName(stage)}»: SSH-соединение не установлено или оборвалось ({error.GetType().Name}). Проверьте адрес, порт, доступность сервера и отпечаток ключа.";
        return $"Этап «{StageName(stage)}»: {error.GetType().Name}. Операция не завершена. Проверьте последние строки вывода и SSH-подключение.";
    }

    public static string LastOutput(string? log) => string.Join('\n', (log ?? "").Split('\n').TakeLast(35));
}
