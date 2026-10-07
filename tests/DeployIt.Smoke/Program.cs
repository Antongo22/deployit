using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

if (args.Contains("--install")) {
    Environment.Exit(Microsoft.Playwright.Program.Main(["install", "--with-deps", "chromium"]));
    return;
}
var baseUrl = Environment.GetEnvironmentVariable("SMOKE_BASE_URL") ?? "http://deployit-web:8080";
var username = Environment.GetEnvironmentVariable("SMOKE_ADMIN_USERNAME") ?? "admin";
var password = Environment.GetEnvironmentVariable("SMOKE_ADMIN_PASSWORD");
if (string.IsNullOrEmpty(password)) password = File.ReadAllText("/panel-data/admin-password").Trim();
Directory.CreateDirectory("/artifacts");
File.Delete("/artifacts/smoke-result.json");
using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) {
    BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(60) };
var anonymous = await client.GetAsync("/api/projects");
Check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "API rejects unauthenticated requests");
var login = await client.GetStringAsync("/login");
var tokenMatch = Regex.Match(login, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
Check(tokenMatch.Success, "SSR login contains CSRF token");
using var loginResponse = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string,string> {
    ["username"] = username, ["password"] = password!,
    ["__RequestVerificationToken"] = WebUtility.HtmlDecode(tokenMatch.Groups[1].Value) }));
Check(loginResponse.IsSuccessStatusCode, "Admin login works");
var csrf = await Api("GET", "/api/security/csrf");
client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.GetProperty("requestToken").GetString());
using (var noCsrf = new HttpRequestMessage(HttpMethod.Post, "/api/projects")) {
    // Temporarily remove the client header to verify the server's CSRF check.
    var header = client.DefaultRequestHeaders.GetValues("X-CSRF-TOKEN").Single();
    client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
    noCsrf.Content = JsonContent.Create(new { });
    using var response = await client.SendAsync(noCsrf);
    Check(response.StatusCode == HttpStatusCode.BadRequest, "API rejects missing CSRF token");
    client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", header);
}
using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } });
var page = await context.NewPageAsync();
page.SetDefaultTimeout(60000);
var browserErrors = new List<string>();
page.PageError += (_, message) => browserErrors.Add(message);
page.Console += (_, message) => { if (message.Type == "error") Console.WriteLine("BROWSER: " + message.Text); };
page.RequestFailed += (_, request) => Console.WriteLine("REQUEST FAILED: " + request.Url + " " + request.Failure);
page.Response += (_, response) => { if (response.Status >= 400) Console.WriteLine($"HTTP {response.Status}: {response.Url}"); };
Guid? connectionId = null, projectId = null;
try {
    await page.GotoAsync(baseUrl + "/login");
    await page.GetByLabel("Имя пользователя").FillAsync(username);
    await page.GetByLabel("Пароль", new() { Exact = true }).FillAsync(password!);
    await page.GetByRole(AriaRole.Button, new() { Name = "Войти в панель" }).ClickAsync();
    await Ready();
    await Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("Ваши проекты") })).ToBeVisibleAsync();
    await page.ScreenshotAsync(new() { Path = "/artifacts/dashboard.png", FullPage = true });
    await page.GotoAsync(baseUrl + "/connections"); await Ready();
    await page.GetByLabel("Название подключения").FillAsync("Compose smoke GitHub");
    await page.GetByLabel("Только публичные репозитории, без токена").CheckAsync();
    await page.GetByLabel("Имя аккаунта").FillAsync("octocat");
    await page.GetByRole(AriaRole.Button, new() { Name = "Подключить аккаунт" }).ClickAsync();
    await Expect(page.GetByText("Аккаунт подключён. Теперь можно создать проект.")).ToBeVisibleAsync();
    var connections = await Api("GET", "/api/connections");
    connectionId = connections.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "Compose smoke GitHub").GetProperty("id").GetGuid();
    var repositories = await Api("GET", $"/api/connections/{connectionId}/repositories");
    var repository = repositories.EnumerateArray().Single(r => r.GetProperty("name").GetString() == "octocat/Hello-World");
    Check(repository.GetProperty("cloneUrl").GetString()!.StartsWith("https://github.com/"), "Real GitHub repositories loaded");
    await page.ScreenshotAsync(new() { Path = "/artifacts/connections.png", FullPage = true });
    await page.GotoAsync(baseUrl + "/projects/new"); await Ready();
    await page.GetByLabel("Git-подключение").SelectOptionAsync(connectionId.Value.ToString());
    await page.GetByLabel("Репозиторий").SelectOptionAsync(repository.GetProperty("id").GetString()!);
    await page.GetByLabel("Название проекта").FillAsync("Compose SSH smoke");
    await page.GetByLabel("Адрес сервера").FillAsync("ssh-target");
    await page.GetByRole(AriaRole.Button, new() { Name = "Получить отпечаток" }).ClickAsync();
    await Expect(page.GetByLabel("Отпечаток SSH-ключа сервера")).ToHaveValueAsync(new Regex("^SHA256:"));
    await page.GetByLabel("Приватный SSH-ключ").FillAsync(File.ReadAllText("/fixture/client"));
    await page.GetByLabel("Корневой каталог на сервере").FillAsync("/opt/apps/smoke");
    await page.GetByLabel("Команда деплоя").FillAsync("test -f README && printf 'COMPOSE_SSH_OK\\n' && sleep 5");
    await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить проект" }).ClickAsync();
    await page.WaitForURLAsync(new Regex("/projects/[a-f0-9-]+$"));
    projectId = Guid.Parse(new Uri(page.Url).Segments.Last());
    await page.GetByRole(AriaRole.Button, new() { Name = "Проверить SSH" }).ClickAsync();
    await Expect(page.GetByText("SSH подключён. Git, bash, timeout и flock доступны.")).ToBeVisibleAsync();
    await page.GetByRole(AriaRole.Button, new() { Name = "Развернуть", Exact = false }).ClickAsync();
    await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Деплой в работе…" })).ToBeVisibleAsync();
    var history = await Api("GET", $"/api/projects/{projectId}/deployments");
    var firstId = history[0].GetProperty("id").GetGuid();
    await Api("POST", $"/api/projects/{projectId}/deploy", null, HttpStatusCode.Conflict);
    var first = await WaitRun(firstId);
    Check(first.GetProperty("status").GetString() == "Succeeded", "One-click SSH deployment succeeded");
    Check(first.GetProperty("log").GetString()!.Contains("COMPOSE_SSH_OK"), "Remote command output persisted");
    Check(first.GetProperty("commitSha").GetString()!.Length == 40, "Git commit SHA recorded");
    var link = new DirectoryInfo("/target-data/smoke/current").LinkTarget;
    Check(link?.EndsWith(firstId.ToString("N")) == true, "Successful release becomes current");
    await Expect(page.Locator(".terminal .badge")).ToHaveTextAsync("Успешно");
    await page.ScreenshotAsync(new() { Path = "/artifacts/deployment.png", FullPage = true });
    await page.GetByRole(AriaRole.Link, new() { Name = "Настройки", Exact = true }).ClickAsync();
    await Expect(page.GetByLabel("Приватный SSH-ключ")).ToHaveValueAsync("");
    await page.GetByLabel("Команда деплоя").FillAsync("printf 'EXPECTED_FAILURE\\n' >&2; exit 7");
    await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить проект" }).ClickAsync();
    await page.WaitForURLAsync(new Regex("/projects/[a-f0-9-]+$"));
    var second = await Api("POST", $"/api/projects/{projectId}/deploy", null, HttpStatusCode.Accepted);
    var failed = await WaitRun(second.GetProperty("id").GetGuid());
    Check(failed.GetProperty("status").GetString() == "Failed" && failed.GetProperty("exitCode").GetInt32() == 7, "Nonzero exit marks deployment failed");
    Check(failed.GetProperty("log").GetString()!.Contains("[stderr] EXPECTED_FAILURE"), "stderr is captured");
    Check(new DirectoryInfo("/target-data/smoke/current").LinkTarget == link, "Failed deployment preserves previous current release");
    var project = await Api("GET", $"/api/projects/{projectId}");
    var raw = project.GetRawText();
    Check(!raw.Contains("PRIVATE KEY") && !raw.Contains("protectedPrivateKey"), "Project API does not expose SSH credentials");
    var openapi = await Api("GET", "/openapi/v1.json");
    Check(openapi.GetProperty("paths").TryGetProperty("/api/projects/{id}/deploy", out _), "OpenAPI documents deployment endpoint");
    await page.GotoAsync(baseUrl + "/swagger");
    await Expect(page.Locator(".swagger-ui .opblock").First).ToBeVisibleAsync();
    await Expect(page.GetByText("Failed to load API definition.")).Not.ToBeVisibleAsync();
    var swaggerMutationStatus = await page.EvaluateAsync<int>("""
        async (id) => {
            const request = await window.ui.getConfigs().requestInterceptor({ method: 'DELETE', headers: {} });
            const response = await fetch('/api/connections/' + id, { method: 'DELETE', headers: request.headers });
            return response.status;
        }
        """, connectionId.Value.ToString());
    Check(swaggerMutationStatus == 409, "Swagger mutation passes CSRF and respects connection dependencies");
    await page.ScreenshotAsync(new() { Path = "/artifacts/swagger.png", FullPage = true });
    await page.GotoAsync(baseUrl + "/"); await Ready();
    await page.SetViewportSizeAsync(390, 844);
    await page.ScreenshotAsync(new() { Path = "/artifacts/mobile.png", FullPage = true });
    var overflow = await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > window.innerWidth");
    Check(!overflow, "Mobile layout fits viewport");
    Check(browserErrors.Count == 0, "Blazor pages have no JavaScript exceptions");
    await File.WriteAllTextAsync("/artifacts/smoke-result.json", JsonSerializer.Serialize(new {
        passed = true, firstRun = firstId, commit = first.GetProperty("commitSha").GetString(),
        failureExitCode = 7, browserErrors, checks = "Blazor UI, GitHub, SSH, queue, history, stderr, release promotion, CSRF, Swagger, mobile" }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("PASS: Compose + Blazor + GitHub + real SSH deployment and failure handling.");
} catch {
    Console.WriteLine("BROWSER ERRORS: " + string.Join("; ", browserErrors));
    await page.ScreenshotAsync(new() { Path = "/artifacts/smoke-failure.png", FullPage = true,
        Mask = [page.Locator("textarea, input[type='password']")] });
    throw;
} finally {
    if (projectId.HasValue) {
        try { await Api("DELETE", $"/api/projects/{projectId}", null, HttpStatusCode.NoContent); } catch { }
    }
    if (connectionId.HasValue) {
        try { await Api("DELETE", $"/api/connections/{connectionId}", null, HttpStatusCode.NoContent); } catch { }
    }
}

async Task Ready() => await page.Locator(".shell[data-interactive='true']").WaitForAsync();
async Task<JsonElement> Api(string method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK) {
    using var request = new HttpRequestMessage(new HttpMethod(method), path);
    if (body is not null) request.Content = JsonContent.Create(body);
    using var response = await client.SendAsync(request);
    var json = await response.Content.ReadAsStringAsync();
    if (response.StatusCode != expected) throw new Exception($"{method} {path}: expected {(int)expected}, got {(int)response.StatusCode}: {json}");
    return json.Length == 0 ? default : JsonDocument.Parse(json).RootElement.Clone();
}
async Task<JsonElement> WaitRun(Guid id) {
    var deadline = DateTime.UtcNow.AddMinutes(2);
    while (DateTime.UtcNow < deadline) {
        var run = await Api("GET", $"/api/deployments/{id}");
        if (run.GetProperty("status").GetString() is not ("Queued" or "Running")) return run;
        await Task.Delay(1000);
    }
    throw new Exception("SSH deployment did not finish within two minutes");
}
static void Check(bool condition, string message) {
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS: " + message);
}
