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
        if (!Enum.IsDefined(provider) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != ""
            || uri.AbsolutePath != "/")
            throw new DomainException("Укажите HTTPS-адрес Git-сервиса без пути и пароля.");
        if (provider == GitProvider.GitHub && uri.GetLeftPart(UriPartial.Authority) != "https://github.com")
            throw new DomainException("Для GitHub используется https://github.com.");
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
            throw new DomainException("Для публичного подключения укажите имя аккаунта GitHub/GitLab.");
        using var user = await GetAsync(connection, connection.Provider == GitProvider.GitHub
            ? $"/users/{Uri.EscapeDataString(connection.Account)}"
            : $"/users?username={Uri.EscapeDataString(connection.Account)}", ct);
        var item = connection.Provider == GitProvider.GitHub ? user.RootElement
            : user.RootElement.EnumerateArray().FirstOrDefault();
        if (item.ValueKind != JsonValueKind.Object)
            throw new DomainException("Аккаунт не найден.");
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
                throw new DomainException($"{connection.Provider}: HTTP {(int)response.StatusCode}. Проверьте аккаунт, токен и права доступа.");
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(body, cancellationToken: ct);
        }
        catch (HttpRequestException) { throw new DomainException("Git-сервис недоступен. Проверьте адрес и соединение."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new DomainException("Git-сервис не ответил вовремя."); }
    }
}
