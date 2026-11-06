using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DeployIt.DTOs;
using DeployIt.Models;
using DeployIt.Services;
using Microsoft.EntityFrameworkCore;

namespace DeployIt.Tests;

public sealed class RecoveryTests
{
    [Fact]
    public async Task EmptyPanelRecoversOriginalIdentityAndCommandsWithoutExecutingAnything()
    {
        using var fixture = new DeploymentTests.Fixture();
        var id = Guid.NewGuid();
        var metadata = new RecoveryMetadata(1, id, "Original app", "1", "fixture/repo1", "https://github.com/fixture/repo1.git",
            "main", "printf ORIGINAL_DEPLOY", "printf ORIGINAL_RESTART", "printf ORIGINAL_STOP", 60);
        var discovery = new FakeDiscovery(new DiscoveredProject(id, "/opt/apps/first", metadata.RepositoryUrl, "main", metadata));
        var input = Input();
        var result = await fixture.Service.RecoverProjectsAsync(input, discovery);
        Assert.True(Assert.Single(result.Projects).Added);
        var project = await fixture.Service.GetTargetAsync(id);
        Assert.Equal(id, project.Id);
        Assert.True(project.RequiresReview);
        Assert.Equal("printf ORIGINAL_DEPLOY", project.DeployCommand);
        Assert.Equal(60, project.TimeoutMinutes);
        Assert.Equal("ssh-password", fixture.Secrets.Unprotect(project.ProtectedPassword));
        Assert.DoesNotContain("ssh-password", JsonSerializer.Serialize(result));
        Assert.Empty(await fixture.Service.HistoryAsync(id));
        Assert.Equal(0, fixture.Storage.Calls);
        input.Password = "different-password";
        Assert.False(Assert.Single((await fixture.Service.RecoverProjectsAsync(input, discovery)).Projects).Added);
        Assert.Equal(project.ProtectedPassword, (await fixture.Service.GetTargetAsync(id)).ProtectedPassword);

        // Neither a forged API request nor a UI button can run guessed commands or delete data.
        await Conflict(() => fixture.Service.EnqueueAsync(id));
        await Conflict(() => fixture.Service.EnqueueRestartAsync(id));
        await Conflict(() => fixture.Service.EnqueueStopAsync(id));
        await Conflict(() => fixture.Service.DeleteProjectAsync(id));
        await Conflict(() => fixture.Service.CleanReleasesAsync(id));
        await Conflict(() => fixture.Service.DeleteReleaseAsync(id, Guid.NewGuid()));
        await Conflict(() => fixture.Service.SaveEnvironmentAsync(id, new() { Content = "BAD=1", Version = "version" }));
        Assert.Equal(0, fixture.Storage.Calls);

    }

    [Fact]
    public async Task SavingReviewedSettingsUnlocksImportedProjectWithoutDeployingIt()
    {
        using var fixture = new DeploymentTests.Fixture();
        var id = Guid.NewGuid();
        await fixture.Service.RecoverProjectsAsync(Input(), new FakeDiscovery(new DiscoveredProject(id, "/opt/apps/first", "https://github.com/fixture/repo1.git", "main", null)));
        var connection = await fixture.Connect();
        var input = fixture.Input(connection.Id);
        input.WorkingDirectory = "/opt/apps/first";
        input.AuthenticationType = SshAuthenticationType.Password;
        var reviewed = await fixture.Service.SaveProjectAsync(input, id);
        Assert.False(reviewed.RequiresReview);
        Assert.Equal(id, reviewed.Id);
        Assert.Empty(await fixture.Service.HistoryAsync(id));
        Assert.Equal("ssh-password", fixture.Secrets.Unprotect((await fixture.Service.GetTargetAsync(id)).ProtectedPassword));
        await fixture.Service.EnqueueRestartAsync(id);
        Assert.Single(await fixture.Service.HistoryAsync(id));
    }

    [Fact]
    public async Task DiscoveryFailureDoesNotPartiallyImportAnything()
    {
        using var fixture = new DeploymentTests.Fixture();
        var scan = new FakeDiscovery { Fail = true };
        await Assert.ThrowsAsync<DomainException>(() => fixture.Service.RecoverProjectsAsync(Input(), scan));
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Empty(await db.Projects.ToListAsync());
        Assert.Empty(await db.Connections.ToListAsync());
        Assert.Equal(0, fixture.Storage.Calls);
    }

    [Fact]
    public async Task AmbiguousIdsAndNestedPathsAreSkippedWithoutOverwritingExistingSettings()
    {
        using var fixture = new DeploymentTests.Fixture();
        var connection = await fixture.Connect();
        var original = await fixture.Service.SaveProjectAsync(fixture.Input(connection.Id));
        var duplicateId = Guid.NewGuid();
        var scan = new FakeDiscovery(
            new(original.Id, original.WorkingDirectory, original.RepositoryUrl, "main", null),
            new(original.Id, "/opt/apps/other-location", original.RepositoryUrl, "main", null),
            new(duplicateId, "/opt/apps/a", "https://github.com/fixture/a.git", "main", null),
            new(duplicateId, "/opt/apps/b", "https://github.com/fixture/b.git", "main", null),
            new(Guid.NewGuid(), "/opt/apps/c", "https://github.com/fixture/c.git", "main", null),
            new(Guid.NewGuid(), "/opt/apps/c/child", "https://github.com/fixture/child.git", "main", null));
        var result = await fixture.Service.RecoverProjectsAsync(Input(), scan);
        Assert.All(result.Projects, p => Assert.False(p.Added));
        Assert.Null(result.Projects.Single(p => p.WorkingDirectory == "/opt/apps/other-location").Id);
        Assert.Single(await fixture.Service.ProjectsAsync());
        Assert.False((await fixture.Service.ProjectAsync(original.Id)).RequiresReview);
    }

    [Fact]
    public async Task SearchValidationRejectsMissingCredentialsAndMalformedFingerprint()
    {
        using var fixture = new DeploymentTests.Fixture();
        var scan = new FakeDiscovery();
        var input = Input(); input.Password = "";
        await Assert.ThrowsAsync<DomainException>(() => fixture.Service.RecoverProjectsAsync(input, scan));
        input = Input(); input.HostFingerprint = "bad";
        await Assert.ThrowsAsync<DomainException>(() => fixture.Service.RecoverProjectsAsync(input, scan));
        Assert.Equal(0, scan.Calls);
    }

    [LinuxFact]
    public async Task SearchReadsProjectsAndMetadataWithoutChangingServerFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "deployit-discovery-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "app with 'quote'");
        var releaseId = Guid.NewGuid();
        var release = Path.Combine(root, "releases", releaseId.ToString("N"));
        Directory.CreateDirectory(release);
        try {
            var project = new DeploymentProject { Name = "Recovered", WorkingDirectory = root,
                RepositoryId = "1", RepositoryUrl = "https://github.com/fixture/repo1.git", Branch = "main",
                DeployCommand = "exit 99", ProtectedPassword = "MUST_NOT_EXPORT" };
            await Shell($"git -C {SshDeploymentService.Quote(release)} init --initial-branch=main");
            await Shell($"git -C {SshDeploymentService.Quote(release)} remote add origin {SshDeploymentService.Quote(project.RepositoryUrl)}");
            await File.WriteAllTextAsync(Path.Combine(root, ".deployit-project"), project.Id.ToString("N"));
            await File.WriteAllTextAsync(Path.Combine(root, ".deployit-project.json"), JsonSerializer.Serialize(RecoveryMetadata.From(project)));
            await File.WriteAllTextAsync(Path.Combine(root, ".env"), "KEEP_SECRET=private");
            Directory.CreateSymbolicLink(Path.Combine(root, "current"), release);
            var before = Snapshot(directory);
            var result = await Shell(SshDeploymentService.BuildDiscoveryCommand(directory));
            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("KEEP_SECRET", result.Output);
            Assert.DoesNotContain("MUST_NOT_EXPORT", result.Output);
            var found = Assert.Single(SshDeploymentService.ParseDiscovery(result.Output).Projects);
            Assert.Equal(project.Id, found.Id);
            Assert.Equal(project.RepositoryUrl, found.RepositoryUrl);
            Assert.Equal("exit 99", found.Metadata!.DeployCommand);
            Assert.Equal(before, Snapshot(directory));

            // Legacy marker-only layout still recovers ID and Git repository.
            File.Delete(Path.Combine(root, ".deployit-project.json"));
            result = await Shell(SshDeploymentService.BuildDiscoveryCommand(directory));
            Assert.Equal(0, result.ExitCode);
            found = Assert.Single(SshDeploymentService.ParseDiscovery(result.Output).Projects);
            Assert.Equal(project.Id, found.Id);
            Assert.Null(found.Metadata);

            // Older installations can recover identity from Compose labels if no marker exists.
            File.Delete(Path.Combine(root, ".deployit-project"));
            var bin = Path.Combine(directory, "bin"); Directory.CreateDirectory(bin);
            var docker = Path.Combine(bin, "docker");
            await File.WriteAllTextAsync(docker, $"#!/bin/bash\ncase \"$1\" in\nps) printf 'container-id\\n' ;;\ninspect) printf '%s\\t%s\\n' deployit-{project.Id:N} {SshDeploymentService.Quote(release)} ;;\nesac\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(docker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            result = await Shell("export PATH=" + SshDeploymentService.Quote(bin) + ":\"$PATH\"; " + SshDeploymentService.BuildDiscoveryCommand(directory));
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(project.Id, Assert.Single(SshDeploymentService.ParseDiscovery(result.Output).Projects).Id);

            // Never follow current outside the project's releases directory.
            Directory.Delete(Path.Combine(root, "current"));
            Directory.CreateSymbolicLink(Path.Combine(root, "current"), directory);
            result = await Shell(SshDeploymentService.BuildDiscoveryCommand(directory));
            var unsafeScan = SshDeploymentService.ParseDiscovery(result.Output);
            Assert.Empty(unsafeScan.Projects);
            Assert.NotEmpty(unsafeScan.Warnings);
        } finally { Directory.Delete(directory, true); }
    }

    private static Dictionary<string, string> Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => path, path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
    private static async Task Conflict(Func<Task> operation) => Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(operation)).StatusCode);
    private static RecoveryInput Input() => new() { Host = "server", Username = "root", Password = "ssh-password",
        HostFingerprint = "SHA256:" + new string('A', 43) };
    private sealed class FakeDiscovery(params DiscoveredProject[] projects) : IProjectDiscovery
    {
        public bool Fail { get; set; }
        public int Calls { get; private set; }
        public Task<DiscoveryScan> DiscoverAsync(DeploymentProject target, string searchDirectory, CancellationToken ct)
        {
            Calls++;
            if (Fail) throw new DomainException("SSH unavailable");
            return Task.FromResult(new DiscoveryScan(projects.ToList(), []));
        }
    }
    private static async Task<(int ExitCode, string Output)> Shell(string command)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("-c"); process.StartInfo.ArgumentList.Add(command);
        process.Start(); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }
}
