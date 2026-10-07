using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using DeployIt.Data;
using DeployIt.DTOs;
using DeployIt.Models;
using DeployIt.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DeployIt.Tests;

public sealed class DeploymentTests
{
    [Fact]
    public void SecretsAreEncryptedAndRecoverable()
    {
        using var fixture = new Fixture();
        var protectedValue = fixture.Secrets.Protect("private-token");
        Assert.DoesNotContain("private-token", protectedValue);
        Assert.Equal("private-token", fixture.Secrets.Unprotect(protectedValue));
        Assert.Equal("", fixture.Secrets.Unprotect(fixture.Secrets.Protect("")));
    }

    [Fact]
    public async Task PublicDtosNeverExposeCredentials()
    {
        using var fixture = new Fixture();
        var connection = await fixture.Connect();
        var project = await fixture.Service.SaveProjectAsync(fixture.Input(connection.Id));
        var publicJson = JsonSerializer.Serialize(new { connection, project });
        Assert.DoesNotContain("private-token", publicJson);
        Assert.DoesNotContain("PRIVATE KEY", publicJson);
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Equal("private-token", fixture.Secrets.Unprotect((await db.Connections.SingleAsync()).ProtectedToken));
        Assert.DoesNotContain("PRIVATE KEY", (await db.Projects.SingleAsync()).ProtectedPrivateKey);
    }

    [Fact]
    public async Task RepositoryAndServerMappingsAreOneToOne()
    {
        using var fixture = new Fixture();
        var connection = await fixture.Connect();
        await fixture.Service.SaveProjectAsync(fixture.Input(connection.Id));
        var duplicateRepo = fixture.Input(connection.Id); duplicateRepo.Host = "other-server";
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => fixture.Service.SaveProjectAsync(duplicateRepo))).StatusCode);
        var duplicateHost = fixture.Input(connection.Id); duplicateHost.RepositoryId = "2";
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => fixture.Service.SaveProjectAsync(duplicateHost))).StatusCode);
    }

    [Fact]
    public async Task OnlyOneActiveDeploymentCanBeQueuedConcurrently()
    {
        using var fixture = new Fixture();
        var connection = await fixture.Connect();
        var project = await fixture.Service.SaveProjectAsync(fixture.Input(connection.Id));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ => {
            try { await fixture.Service.EnqueueAsync(project.Id); return true; }
            catch (DomainException e) when (e.StatusCode == 409) { return false; }
        }));
        Assert.Single(results, value => value);
        Assert.Single(await fixture.Service.HistoryAsync(project.Id));
    }

    [Fact]
    public async Task ActiveProjectCannotBeChangedOrDeleted()
    {
        using var fixture = new Fixture();
        var connection = await fixture.Connect(); var input = fixture.Input(connection.Id);
        var project = await fixture.Service.SaveProjectAsync(input);
        await fixture.Service.EnqueueAsync(project.Id);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => fixture.Service.DeleteProjectAsync(project.Id))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => fixture.Service.SaveProjectAsync(input, project.Id))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => fixture.Service.DeleteConnectionAsync(connection.Id))).StatusCode);
    }

    [Fact]
    public async Task QueuePersistsEncryptedConfigurationSnapshot()
    {
        using var fixture = new Fixture();
        var connection = await fixture.Connect(); var input = fixture.Input(connection.Id);
        input.DeployCommand = "printf original";
        var project = await fixture.Service.SaveProjectAsync(input);
        var run = await fixture.Service.EnqueueAsync(project.Id);
        await using var db = fixture.Factory.CreateDbContext();
        var saved = await db.Deployments.SingleAsync();
        Assert.Equal(run.Id, saved.Id); Assert.DoesNotContain("printf original", saved.ProtectedSnapshot);
        var snapshot = JsonSerializer.Deserialize<DeploymentSnapshot>(fixture.Secrets.Unprotect(saved.ProtectedSnapshot))!;
        Assert.Equal("printf original", snapshot.Project.DeployCommand);
        Assert.Equal("private-token", fixture.Secrets.Unprotect(snapshot.Connection.ProtectedToken));
    }

    [Theory]
    [InlineData(GitProvider.GitHub, true, "https://github.com")]
    [InlineData(GitProvider.GitLab, false, "https://gitlab.com")]
    [InlineData(GitProvider.GitLab, true, "https://gitlab.com")]
    public async Task GitProviderPayloadsSupportPublicAndPrivateAccounts(GitProvider provider, bool publicOnly, string baseUrl)
    {
        using var fixture = new Fixture();
        var connection = await fixture.Service.AddConnectionAsync(new ConnectionInput {
            Name = "provider", Provider = provider, BaseUrl = baseUrl, PublicOnly = publicOnly,
            Account = "fixture", Token = publicOnly ? "" : "private-token" });
        var repositories = await fixture.Service.RepositoriesAsync(connection.Id);
        var repository = Assert.Single(repositories);
        Assert.StartsWith(baseUrl, repository.CloneUrl);
        Assert.Equal("fixture", connection.Account);
        var project = await fixture.Service.SaveProjectAsync(fixture.Input(connection.Id));
        Assert.Equal(repository.CloneUrl, project.RepositoryUrl);
    }

    [Fact]
    public async Task LastSuccessfulCommitSurvivesLaterFailure()
    {
        using var fixture = new Fixture();
        var connection = await fixture.Connect();
        var project = await fixture.Service.SaveProjectAsync(fixture.Input(connection.Id));
        var first = await fixture.Service.EnqueueAsync(project.Id);
        await using (var db = fixture.Factory.CreateDbContext()) {
            var run = await db.Deployments.FindAsync(first.Id);
            run!.Status = DeploymentStatus.Succeeded; run.CommitSha = new string('a', 40);
            run.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        var second = await fixture.Service.EnqueueAsync(project.Id);
        await using (var db = fixture.Factory.CreateDbContext()) {
            var run = await db.Deployments.FindAsync(second.Id);
            run!.Status = DeploymentStatus.Failed; run.CommitSha = new string('b', 40);
            await db.SaveChangesAsync();
        }
        var view = await fixture.Service.ProjectAsync(project.Id);
        Assert.Equal(DeploymentStatus.Failed, view.LatestDeployment!.Status);
        Assert.Equal(new string('a', 40), view.LastSuccessfulDeployment!.CommitSha);
    }

    [Theory]
    [InlineData("../main")]
    [InlineData("--upload-pack=malicious")]
    [InlineData("main; touch /tmp/injected")]
    [InlineData("refs/heads/.hidden")]
    public async Task UnsafeBranchNamesAreRejected(string branch)
    {
        using var fixture = new Fixture();
        var connection = await fixture.Connect(); var input = fixture.Input(connection.Id); input.Branch = branch;
        await Assert.ThrowsAsync<DomainException>(() => fixture.Service.SaveProjectAsync(input));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/opt/../app")]
    [InlineData("relative/path")]
    public async Task UnsafeWorkingDirectoriesAreRejected(string path)
    {
        using var fixture = new Fixture();
        var connection = await fixture.Connect(); var input = fixture.Input(connection.Id); input.WorkingDirectory = path;
        await Assert.ThrowsAsync<DomainException>(() => fixture.Service.SaveProjectAsync(input));
    }

    [Fact]
    public void GitServiceRejectsCredentialsInUrlsAndNonHttps()
    {
        Assert.Throws<DomainException>(() => GitProviderService.NormalizeBaseUrl(GitProvider.GitLab, "http://gitlab.com"));
        Assert.Throws<DomainException>(() => GitProviderService.NormalizeBaseUrl(GitProvider.GitLab, "https://token@gitlab.com"));
        Assert.Throws<DomainException>(() => GitProviderService.NormalizeBaseUrl(GitProvider.GitHub, "https://other.example"));
    }

    [LinuxFact]
    public async Task ShellDeploysExactCommitAndPreservesCurrentAfterFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "deployit-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            var repository = Path.Combine(directory, "source"); Directory.CreateDirectory(repository);
            await Shell($"git -C {SshDeploymentService.Quote(repository)} init --initial-branch=main");
            await File.WriteAllTextAsync(Path.Combine(repository, "README"), "fixture");
            await Shell($"git -C {SshDeploymentService.Quote(repository)} add README && git -C {SshDeploymentService.Quote(repository)} -c user.name=test -c user.email=test@example.invalid commit -m fixture");
            var project = new DeploymentProject { RepositoryUrl = repository, Branch = "main", TimeoutMinutes = 1,
                WorkingDirectory = Path.Combine(directory, "app's folder"), DeployCommand = "test -f README && printf SHELL_OK" };
            var first = Guid.NewGuid();
            var success = await Shell(SshDeploymentService.BuildCommand(project, first, false));
            Assert.Equal(0, success.ExitCode); Assert.Contains("SHELL_OK", success.Output);
            Assert.Matches("DEPLOYIT_COMMIT=[a-f0-9]{40}", success.Output);
            var current = new DirectoryInfo(Path.Combine(project.WorkingDirectory, "current"));
            Assert.EndsWith(first.ToString("N"), current.LinkTarget);
            project.DeployCommand = "printf FAIL >&2; exit 7";
            var failure = await Shell(SshDeploymentService.BuildCommand(project, Guid.NewGuid(), false));
            Assert.Equal(7, failure.ExitCode); Assert.Contains("FAIL", failure.Output);
            current.Refresh(); Assert.EndsWith(first.ToString("N"), current.LinkTarget);
        } finally { Directory.Delete(directory, true); }
    }

    private static async Task<(int ExitCode, string Output)> Shell(string command)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("bash") {
            RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("-c"); process.StartInfo.ArgumentList.Add(command);
        process.Start(); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); return (process.ExitCode, await stdout + await stderr);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), "deployit-tests-" + Guid.NewGuid().ToString("N"));
        public TestFactory Factory { get; }
        public SecretProtector Secrets { get; }
        public ProjectService Service { get; }
        private readonly string key;
        public Fixture() {
            Directory.CreateDirectory(path);
            Factory = new(new DbContextOptionsBuilder<DeployItDbContext>().UseSqlite($"Data Source={path}/test.db;Default Timeout=30").Options);
            using var db = Factory.CreateDbContext(); db.Database.EnsureCreated();
            Secrets = new(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(path, "keys"))));
            Service = new(Factory, new GitProviderService(new ClientFactory(), Secrets), Secrets);
            using var rsa = RSA.Create(2048); key = rsa.ExportRSAPrivateKeyPem();
        }
        public Task<ConnectionView> Connect() => Service.AddConnectionAsync(new ConnectionInput {
            Name = "fixture", Provider = GitProvider.GitHub, BaseUrl = "https://github.com", Token = "private-token" });
        public ProjectInput Input(Guid connection) => new() { Name = "fixture", ConnectionId = connection,
            RepositoryId = "1", Host = "server", Branch = "main", PrivateKey = key,
            HostFingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData([1])).TrimEnd('=') };
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(path, true); }
    }
    private sealed class ClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler());
        private sealed class Handler : HttpMessageHandler {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
                var uri = request.RequestUri!;
                var path = uri.AbsolutePath;
                var id = path.EndsWith("/2") ? 2 : 1;
                var gitlab = uri.Host == "gitlab.com";
                var user = new Dictionary<string, object> { [gitlab ? "username" : "login"] = "fixture", ["id"] = 1 };
                var repository = new Dictionary<string, object> { ["id"] = id, ["default_branch"] = "main",
                    [gitlab ? "path_with_namespace" : "full_name"] = $"fixture/repo{id}",
                    [gitlab ? "http_url_to_repo" : "clone_url"] = $"https://{uri.Host.Replace("api.github.com", "github.com")}/fixture/repo{id}.git" };
                object payload = repository;
                if (path.EndsWith("/user") || path == "/users/fixture") payload = user;
                else if (path == "/api/v4/users") payload = new[] { user };
                else if (path.EndsWith("/repos") || path.EndsWith("/projects")) payload = new[] { repository };
                var body = JsonSerializer.Serialize(payload);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            }
        }
    }
    public sealed class TestFactory(DbContextOptions<DeployItDbContext> options) : IDbContextFactory<DeployItDbContext>
    { public DeployItDbContext CreateDbContext() => new(options); }
}

public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute() { if (!OperatingSystem.IsLinux()) Skip = "Linux server commands are checked in Docker."; }
}
