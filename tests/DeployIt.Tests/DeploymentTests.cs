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
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => fixture.Service.CleanReleasesAsync(project.Id))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => fixture.Service.ReleasesAsync(project.Id))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => fixture.Service.DeleteReleaseAsync(project.Id, Guid.NewGuid()))).StatusCode);
        Assert.Equal(0, fixture.Storage.Calls);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => fixture.Service.SaveProjectAsync(input, project.Id))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => fixture.Service.DeleteConnectionAsync(connection.Id))).StatusCode);
    }

    [Fact]
    public async Task DeletionKeepsConfigurationOnRemoteFailureAndCascadesHistoryOnlyAfterSuccess()
    {
        using var fixture = new Fixture();
        var connection = await fixture.Connect();
        var project = await fixture.Service.SaveProjectAsync(fixture.Input(connection.Id));
        var run = await fixture.Service.EnqueueAsync(project.Id);
        await using (var db = fixture.Factory.CreateDbContext()) {
            (await db.Deployments.FindAsync(run.Id))!.Status = DeploymentStatus.Succeeded;
            await db.SaveChangesAsync();
        }
        fixture.Storage.Fail = true;
        await Assert.ThrowsAsync<DomainException>(() => fixture.Service.DeleteProjectAsync(project.Id));
        Assert.Equal(project.Id, (await fixture.Service.ProjectAsync(project.Id)).Id);
        Assert.Single(await fixture.Service.HistoryAsync(project.Id));
        fixture.Storage.Fail = false;
        await fixture.Service.CleanReleasesAsync(project.Id);
        Assert.Single(await fixture.Service.HistoryAsync(project.Id));
        await fixture.Service.DeleteProjectAsync(project.Id);
        await using var after = fixture.Factory.CreateDbContext();
        Assert.Empty(await after.Projects.ToListAsync());
        Assert.Empty(await after.Deployments.ToListAsync());
        Assert.Single(await after.Connections.ToListAsync());
        Assert.Equal(3, fixture.Storage.Calls);
        Assert.Equal(run.Id, Assert.Single(fixture.Storage.KnownReleases));
        Assert.Equal(404, (await Assert.ThrowsAsync<DomainException>(() => fixture.Service.SaveEnvironmentAsync(
            project.Id, new() { Content = "STALE=1", Version = "old" }))).StatusCode);
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

    [Fact]
    public async Task ReleaseDeletionUsesProjectHistoryAndKeepsProjectAndLogs()
    {
        using var fixture = new Fixture();
        var connection = await fixture.Connect();
        var project = await fixture.Service.SaveProjectAsync(fixture.Input(connection.Id));
        var run = await fixture.Service.EnqueueAsync(project.Id);
        await using (var db = fixture.Factory.CreateDbContext()) {
            (await db.Deployments.FindAsync(run.Id))!.Status = DeploymentStatus.Failed;
            await db.SaveChangesAsync();
        }
        fixture.Storage.ReleaseItems.Add(new(run.Id, DateTimeOffset.UtcNow, 4096, false, false));
        Assert.Equal(run.Id, Assert.Single(await fixture.Service.ReleasesAsync(project.Id)).Id);
        await fixture.Service.DeleteReleaseAsync(project.Id, run.Id);
        Assert.Equal(run.Id, fixture.Storage.DeletedRelease);
        Assert.Equal(run.Id, Assert.Single(fixture.Storage.KnownReleases));
        Assert.Single(await fixture.Service.HistoryAsync(project.Id));
        Assert.Equal(project.Id, (await fixture.Service.ProjectAsync(project.Id)).Id);
        Assert.Equal(404, (await Assert.ThrowsAsync<DomainException>(() => fixture.Service.DeleteReleaseAsync(Guid.NewGuid(), run.Id))).StatusCode);
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
            for (var attempt = 0; attempt < 4; attempt++)
                Assert.Equal(7, (await Shell(SshDeploymentService.BuildCommand(project, Guid.NewGuid(), false))).ExitCode);
            Assert.Equal(3, Directory.GetDirectories(Path.Combine(project.WorkingDirectory, "releases")).Length);
            Assert.True(Directory.Exists(Path.Combine(project.WorkingDirectory, "releases", first.ToString("N"))));
        } finally { Directory.Delete(directory, true); }
    }

    [LinuxFact]
    public async Task ManualCleanupSupportsLegacyProjectsAndPreservesUnmanagedFilesAndSymlinks()
    {
        var directory = Path.Combine(Path.GetTempPath(), "deployit-clean-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "app's folder");
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        Directory.CreateDirectory(root);
        try {
            for (var i = 0; i < ids.Length; i++) {
                var release = Path.Combine(root, "releases", ids[i].ToString("N"));
                Directory.CreateDirectory(release);
                Directory.SetLastWriteTimeUtc(release, DateTime.UtcNow.AddMinutes(i - 10));
            }
            var outside = Path.Combine(directory, "outside"); Directory.CreateDirectory(outside);
            await File.WriteAllTextAsync(Path.Combine(outside, "keep"), "keep");
            Directory.CreateSymbolicLink(Path.Combine(root, "current"), Path.Combine(root, "releases", ids[0].ToString("N")));
            Directory.CreateSymbolicLink(Path.Combine(root, "releases", Guid.NewGuid().ToString("N")), outside);
            Directory.CreateDirectory(Path.Combine(root, "releases", "user-files"));
            await File.WriteAllTextAsync(Path.Combine(root, ".env"), "KEEP=1");
            var project = new DeploymentProject { WorkingDirectory = root, TimeoutMinutes = 1 };
            var result = await Shell(SshDeploymentService.BuildStorageCommand(project, ids, false));
            Assert.Equal(0, result.ExitCode);
            foreach (var index in new[] { 0, 4, 5 }) Assert.True(Directory.Exists(Path.Combine(root, "releases", ids[index].ToString("N"))));
            foreach (var index in new[] { 1, 2, 3 }) Assert.False(Directory.Exists(Path.Combine(root, "releases", ids[index].ToString("N"))));
            Assert.True(Directory.Exists(Path.Combine(root, "releases", "user-files")));
            Assert.Equal("KEEP=1", await File.ReadAllTextAsync(Path.Combine(root, ".env")));
            Assert.True(File.Exists(Path.Combine(outside, "keep")));

            var mounted = Path.Combine(root, "releases", ids[1].ToString("N"));
            Directory.CreateDirectory(mounted);
            Directory.SetLastWriteTimeUtc(mounted, DateTime.UtcNow.AddDays(-1));
            var bin = Path.Combine(directory, "bin"); Directory.CreateDirectory(bin);
            var docker = Path.Combine(bin, "docker");
            await File.WriteAllTextAsync(docker, $$"""
                #!/bin/bash
                case "$1" in
                    ps) printf 'mounted-container\n' ;;
                    inspect) printf '%s\n' {{SshDeploymentService.Quote(mounted + "/uploads")}} ;;
                esac
                """);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(docker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var cleanup = "export PATH=" + SshDeploymentService.Quote(bin) + ":\"$PATH\"; " + SshDeploymentService.BuildStorageCommand(project, ids, false);
            Assert.Equal(0, (await Shell(cleanup)).ExitCode);
            Assert.True(Directory.Exists(mounted));
            // Docker failure must prevent deleting files whose use cannot be checked.
            await File.WriteAllTextAsync(docker, "#!/bin/bash\nexit 1\n");
            Assert.NotEqual(0, (await Shell(cleanup)).ExitCode);
            Assert.True(Directory.Exists(mounted));
        } finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/opt")]
    [InlineData("/home/user")]
    [InlineData("/var/lib")]
    [InlineData("/var/lib/docker")]
    [InlineData("/etc/my-app")]
    [InlineData("/opt/../apps")]
    public void StorageCleanupRejectsSystemDirectories(string root)
        => Assert.Throws<DomainException>(() => SshDeploymentService.BuildStorageCommand(
            new DeploymentProject { WorkingDirectory = root }, [], true));

    [LinuxFact]
    public async Task DeletionRejectsUnownedRootsAndLinksOutsideProject()
    {
        var directory = Path.Combine(Path.GetTempPath(), "deployit-guard-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "project"); Directory.CreateDirectory(root);
        try {
            await File.WriteAllTextAsync(Path.Combine(root, "keep"), "keep");
            var project = new DeploymentProject { WorkingDirectory = root, TimeoutMinutes = 1 };
            Assert.Equal(77, (await Shell(SshDeploymentService.BuildStorageCommand(project, [], true))).ExitCode);
            Assert.True(File.Exists(Path.Combine(root, "keep")));
            await File.WriteAllTextAsync(Path.Combine(root, ".deployit-project"), project.Id.ToString("N"));
            Directory.CreateSymbolicLink(Path.Combine(root, "releases"), directory);
            Assert.Equal(76, (await Shell(SshDeploymentService.BuildStorageCommand(project, [], true))).ExitCode);
            Directory.Delete(Path.Combine(root, "releases"));
            Directory.CreateSymbolicLink(Path.Combine(root, "current"), directory);
            Assert.Equal(76, (await Shell(SshDeploymentService.BuildStorageCommand(project, [], true))).ExitCode);
            Assert.True(File.Exists(Path.Combine(root, "keep")));
        } finally { Directory.Delete(directory, true); }
    }

    [LinuxFact]
    public async Task DeletionRemovesOnlyLabeledDockerResourcesAndProjectDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "deployit-delete-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "project"); Directory.CreateDirectory(root);
        var bin = Path.Combine(directory, "bin"); Directory.CreateDirectory(bin);
        try {
            var project = new DeploymentProject { WorkingDirectory = root, TimeoutMinutes = 1 };
            await File.WriteAllTextAsync(Path.Combine(root, ".deployit-project"), project.Id.ToString("N"));
            await File.WriteAllTextAsync(Path.Combine(root, ".env"), "DELETE=1");
            var log = Path.Combine(directory, "docker.log");
            var docker = Path.Combine(bin, "docker");
            await File.WriteAllTextAsync(docker, $$"""
                #!/bin/bash
                printf '%s\n' "$*" >> {{SshDeploymentService.Quote(log)}}
                case "$1:$2" in
                    ps:-aq) printf 'owned-container\n' ;;
                    network:ls) printf 'owned-network\n' ;;
                    volume:ls) printf 'owned-volume\n' ;;
                esac
                """);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(docker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var command = "export PATH=" + SshDeploymentService.Quote(bin) + ":\"$PATH\"; " + SshDeploymentService.BuildStorageCommand(project, [], true);
            Assert.Equal(0, (await Shell(command)).ExitCode);
            Assert.False(Directory.Exists(root));
            var output = await File.ReadAllTextAsync(log);
            Assert.Contains("label=com.docker.compose.project=deployit-" + project.Id.ToString("N"), output);
            Assert.Contains("rm -fv owned-container", output);
            Assert.Contains("network rm owned-network", output);
            Assert.Contains("volume rm owned-volume", output);
            Assert.DoesNotContain("prune", output);
            // A retry after filesystem deletion still clears any remaining project Docker resources.
            Assert.Equal(0, (await Shell(command)).ExitCode);
        } finally { Directory.Delete(directory, true); }
    }

    [LinuxFact]
    public async Task ReleaseListShowsSizesAndOnlyUnusedCopiesCanBeDeleted()
    {
        var directory = Path.Combine(Path.GetTempPath(), "deployit-release-ui-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "app's folder");
        var project = new DeploymentProject { WorkingDirectory = root, TimeoutMinutes = 1 };
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        Directory.CreateDirectory(root);
        try {
            Assert.Equal("[]", (await Shell(SshDeploymentService.BuildReleaseCommand(project, [], null))).Output);
            await File.WriteAllTextAsync(Path.Combine(root, ".deployit-project"), project.Id.ToString("N"));
            await File.WriteAllTextAsync(Path.Combine(root, ".env"), "KEEP=1");
            foreach (var id in ids) {
                var release = Path.Combine(root, "releases", id.ToString("N")); Directory.CreateDirectory(release);
                await File.WriteAllTextAsync(Path.Combine(release, "source"), new string('x', 5000));
            }
            Directory.CreateSymbolicLink(Path.Combine(root, "current"), Path.Combine(root, "releases", ids[0].ToString("N")));
            Directory.CreateDirectory(Path.Combine(root, "releases", "user-files"));
            var bin = Path.Combine(directory, "bin"); Directory.CreateDirectory(bin);
            var docker = Path.Combine(bin, "docker");
            await File.WriteAllTextAsync(docker, $$"""
                #!/bin/bash
                case "$1" in
                    ps) printf 'fixture-container\n' ;;
                    inspect) printf '%s\n' {{SshDeploymentService.Quote(Path.Combine(root, "releases", ids[1].ToString("N"), "uploads"))}} ;;
                esac
                """);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(docker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string Command(Guid? id) => "export PATH=" + SshDeploymentService.Quote(bin) + ":\"$PATH\"; " + SshDeploymentService.BuildReleaseCommand(project, ids, id);
            var listed = await Shell(Command(null)); Assert.Equal(0, listed.ExitCode);
            using var json = JsonDocument.Parse(listed.Output);
            Assert.Equal(3, json.RootElement.GetArrayLength());
            var rows = json.RootElement.EnumerateArray().ToDictionary(r => Guid.Parse(r.GetProperty("id").GetString()!));
            Assert.True(rows[ids[0]].GetProperty("isCurrent").GetBoolean());
            Assert.True(rows[ids[1]].GetProperty("inUse").GetBoolean());
            Assert.False(rows[ids[2]].GetProperty("inUse").GetBoolean());
            Assert.All(rows.Values, r => Assert.True(r.GetProperty("sizeBytes").GetInt64() >= 5000));
            Assert.All(rows.Values, r => Assert.True(r.GetProperty("modifiedAtUnix").GetInt64() > 0));
            Assert.Equal(79, (await Shell(Command(ids[0]))).ExitCode);
            Assert.Equal(80, (await Shell(Command(ids[1]))).ExitCode);
            Assert.Equal(0, (await Shell(Command(ids[2]))).ExitCode);
            Assert.False(Directory.Exists(Path.Combine(root, "releases", ids[2].ToString("N"))));
            Assert.Equal(81, (await Shell(Command(ids[2]))).ExitCode);
            Assert.Equal("KEEP=1", await File.ReadAllTextAsync(Path.Combine(root, ".env")));
            Assert.True(Directory.Exists(Path.Combine(root, "releases", "user-files")));
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
        public FakeStorage Storage { get; } = new();
        private readonly string key;
        public Fixture() {
            Directory.CreateDirectory(path);
            Factory = new(new DbContextOptionsBuilder<DeployItDbContext>().UseSqlite($"Data Source={path}/test.db;Default Timeout=30").Options);
            using var db = Factory.CreateDbContext(); db.Database.EnsureCreated();
            Secrets = new(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(path, "keys"))));
            Service = new(Factory, new GitProviderService(new ClientFactory(), Secrets), Secrets, Storage);
            using var rsa = RSA.Create(2048); key = rsa.ExportRSAPrivateKeyPem();
        }
        public Task<ConnectionView> Connect() => Service.AddConnectionAsync(new ConnectionInput {
            Name = "fixture", Provider = GitProvider.GitHub, BaseUrl = "https://github.com", Token = "private-token" });
        public ProjectInput Input(Guid connection) => new() { Name = "fixture", ConnectionId = connection,
            RepositoryId = "1", Host = "server", Branch = "main", PrivateKey = key,
            HostFingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData([1])).TrimEnd('=') };
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(path, true); }
    }
    private sealed class FakeStorage : IProjectStorage
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public IReadOnlyCollection<Guid> KnownReleases { get; private set; } = [];
        public List<ReleaseView> ReleaseItems { get; } = [];
        public Guid? DeletedRelease { get; private set; }
        public Task<List<ReleaseView>> ReleasesAsync(DeploymentProject project, CancellationToken ct) => Task.FromResult(ReleaseItems);
        public Task DeleteReleaseAsync(DeploymentProject project, IReadOnlyCollection<Guid> knownReleases, Guid releaseId, CancellationToken ct)
        { DeletedRelease = releaseId; return DeleteAsync(project, knownReleases, ct); }
        public Task<EnvironmentFileView> SaveEnvironmentAsync(DeploymentProject project, EnvironmentFileInput input, CancellationToken ct)
            => Task.FromResult(new EnvironmentFileView(project.WorkingDirectory + "/.env", true, input.Content, input.Version));
        public Task CleanReleasesAsync(DeploymentProject project, IReadOnlyCollection<Guid> knownReleases, CancellationToken ct)
            => DeleteAsync(project, knownReleases, ct);
        public Task DeleteAsync(DeploymentProject project, IReadOnlyCollection<Guid> knownReleases, CancellationToken ct)
        {
            Calls++;
            KnownReleases = knownReleases;
            if (Fail) throw new DomainException("SSH unavailable", 409);
            return Task.CompletedTask;
        }
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
