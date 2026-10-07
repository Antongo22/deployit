using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeployIt.DTOs;
using DeployIt.Models;

namespace DeployIt.Services;

public sealed class GitProviderService(IHttpClientFactory clients, SecretProtector secrets)
{
    public static string NormalizeBaseUrl(GitProvider provider, string value)
    {
        if (!Enum.IsDefined(provider))
            throw new DomainException("Выберите Git-сервис. Например: GitHub.");
        var example = provider == GitProvider.GitHub ? "https://github.com" : "https://gitlab.com";
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "")
            throw new DomainException($"В поле «Адрес сервиса» укажите только HTTPS-адрес сайта, без логина, пароля, параметров и пути к репозиторию. Пример: {example}. Репозиторий выбирается позже, при создании проекта.");
        if (provider == GitProvider.GitHub && uri.GetLeftPart(UriPartial.Authority) != "https://github.com")
            throw new DomainException("Поддерживается GitHub.com. В поле «Адрес сервиса» укажите https://github.com. Пример: https://github.com, а не адрес вашего репозитория.");
        if (uri.AbsolutePath != "/")
            throw new DomainException($"В поле «Адрес сервиса» указан адрес страницы или репозитория. Нужен только адрес сайта. Пример: {uri.GetLeftPart(UriPartial.Authority)}. Репозиторий выбирается позже, при создании проекта.");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public async Task<(string Account, string UserId)> InspectAsync(GitConnection connection, CancellationToken ct)
    {
        if (!connection.PublicOnly)
        {
            using var json = await GetAsync(connection, "/user", ct);
            var root = json.RootElement;
            return (root.GetProperty(connection.Provider == GitProvider.GitHub ? "login" : "username").GetString()!,
                root.GetProperty("id").ToString());
        }
        if (!Regex.IsMatch(connection.Account, "^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,99}$"))
            throw new DomainException("В поле «Имя аккаунта» укажите только имя пользователя, без ссылки и названия репозитория. " + AccountExample(connection.Provider));
        using var user = await GetAsync(connection, connection.Provider == GitProvider.GitHub
            ? $"/users/{Uri.EscapeDataString(connection.Account)}"
            : $"/users?username={Uri.EscapeDataString(connection.Account)}", ct);
        var item = connection.Provider == GitProvider.GitHub ? user.RootElement
            : user.RootElement.EnumerateArray().FirstOrDefault();
        if (item.ValueKind != JsonValueKind.Object)
            throw new DomainException("Аккаунт не найден. Проверьте поле «Имя аккаунта»: нужно имя пользователя, а не название репозитория. " + AccountExample(connection.Provider));
        return (item.GetProperty(connection.Provider == GitProvider.GitHub ? "login" : "username").GetString()!,
            item.GetProperty("id").ToString());
    }

    public async Task<List<RepositoryView>> RepositoriesAsync(GitConnection connection, CancellationToken ct)
    {
        var result = new List<RepositoryView>();
        for (var page = 1; page <= 100; page++)
        {
            var path = connection.Provider == GitProvider.GitHub
                ? (connection.PublicOnly ? $"/users/{Uri.EscapeDataString(connection.Account)}/repos?"
                    : "/user/repos?affiliation=owner,collaborator,organization_member&")
                : (connection.PublicOnly ? $"/users/{connection.ExternalUserId}/projects?visibility=public&"
                    : "/projects?membership=true&");
            using var document = await GetAsync(connection, $"{path}per_page=100&page={page}", ct);
            var items = document.RootElement.EnumerateArray().ToArray();
            result.AddRange(items.Select(item => Map(connection, item)));
            if (items.Length < 100)
                return result.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        throw new DomainException("Слишком много репозиториев. Ограничьте доступ токена нужными проектами.");
    }

    public async Task<RepositoryView> RepositoryAsync(GitConnection connection, string id, CancellationToken ct)
    {
        if (!Regex.IsMatch(id, "^[0-9]{1,20}$"))
            throw new DomainException("Выберите репозиторий из списка.");
        using var json = await GetAsync(connection,
            connection.Provider == GitProvider.GitHub ? $"/repositories/{id}" : $"/projects/{id}", ct);
        return Map(connection, json.RootElement);
    }

    private static RepositoryView Map(GitConnection connection, JsonElement item)
    {
        var github = connection.Provider == GitProvider.GitHub;
        var url = item.GetProperty(github ? "clone_url" : "http_url_to_repo").GetString()!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != ""
            || uri.GetLeftPart(UriPartial.Authority) != connection.BaseUrl)
            throw new DomainException("Git-сервис вернул неподдерживаемый адрес репозитория.");
        return new(item.GetProperty("id").ToString(),
            item.GetProperty(github ? "full_name" : "path_with_namespace").GetString()!, url,
            item.TryGetProperty("default_branch", out var branch) ? branch.GetString() ?? "main" : "main");
    }

    private async Task<JsonDocument> GetAsync(GitConnection connection, string path, CancellationToken ct)
    {
        var apiBase = connection.Provider == GitProvider.GitHub ? "https://api.github.com" : connection.BaseUrl + "/api/v4";
        using var request = new HttpRequestMessage(HttpMethod.Get, apiBase + path);
        request.Headers.UserAgent.ParseAdd("DeployIt/1.0");
        request.Headers.Accept.ParseAdd("application/json");
        if (!connection.PublicOnly)
        {
            var token = secrets.Unprotect(connection.ProtectedToken);
            if (connection.Provider == GitProvider.GitHub)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            else
                request.Headers.Add("PRIVATE-TOKEN", token);
        }
        try
        {
            using var response = await clients.CreateClient("git").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new DomainException(ResponseError(connection, path, (int)response.StatusCode));
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(body, cancellationToken: ct);
        }
        catch (HttpRequestException) { throw new DomainException($"Git-сервис недоступен. Проверьте интернет и поле «Адрес сервиса». Пример: {(connection.Provider == GitProvider.GitHub ? "https://github.com" : "https://gitlab.com")}. Для собственного GitLab нужен доступ к его HTTPS-адресу с сервера панели."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new DomainException("Git-сервис не ответил вовремя. Повторите подключение через минуту. Для собственного GitLab проверьте, что его HTTPS-адрес открывается с сервера панели."); }
    }

    private static string ResponseError(GitConnection connection, string path, int status)
    {
        var prefix = $"{connection.Provider}: HTTP {status}. ";
        var permissions = connection.Provider == GitProvider.GitHub
            ? "Пример прав fine-grained токена GitHub: доступ к нужным репозиториям, Metadata: Read и Contents: Read."
            : "Пример прав токена GitLab: read_api и read_repository.";
        return prefix + (status switch
        {
            401 when connection.PublicOnly => "Сервис требует авторизацию. Отключите «Только публичные репозитории, без токена» и укажите access token. " + permissions,
            401 => "Токен не принят. Вставьте действующий personal access token в поле «Access token»; пароль аккаунта сюда не подходит. " + permissions,
            403 when connection.PublicOnly => "Сервис запретил запрос без токена или ограничил частоту запросов. Повторите позже либо отключите «Только публичные репозитории, без токена» и укажите access token. " + permissions,
            403 => "Сервис запретил доступ. Проверьте права токена и ограничения API; при исчерпанном лимите повторите запрос позже. " + permissions,
            404 when connection.PublicOnly && path.StartsWith("/users", StringComparison.Ordinal) => "Аккаунт не найден. В поле «Имя аккаунта» нужно имя пользователя, без ссылки и репозитория. " + AccountExample(connection.Provider),
            404 => "Аккаунт или репозиторий не найден либо недоступен этому токену. Проверьте, что аккаунт существует и токен имеет доступ к нужному репозиторию. " + permissions,
            429 => "Превышен лимит запросов Git-сервиса. Подождите и повторите запрос позже.",
            >= 500 => "Ошибка на стороне Git-сервиса. Повторите запрос позже; для собственного GitLab проверьте состояние сервера.",
            _ => "Проверьте адрес сервиса, аккаунт и доступ токена. " + permissions
        });
    }

    private static string AccountExample(GitProvider provider) => provider == GitProvider.GitHub
        ? "Пример: octocat для https://github.com/octocat/Hello-World."
        : "Пример: alex для https://gitlab.com/alex/my-app.";
}
