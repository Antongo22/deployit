using DeployIt.DTOs;
using DeployIt.Models;
using DeployIt.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet;

namespace DeployIt.Tests;

[Collection("SSH integration")]
public sealed class ParallelDeploymentTests
{
    [SshFact]
    public async Task WorkerRunsProjectsInParallelAndKeepsLimitsCancellationAndResumeIndependent()
    {
        using var fixture = new DeploymentTests.Fixture();
        var ssh = new SshDeploymentService(fixture.Secrets);
        var keyText = await File.ReadAllTextAsync("/fixture/client");
        var fingerprint = await ssh.ProbeAsync("ssh-target", 22);
        using var key = new PrivateKeyFile(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(keyText)));
        SshClient Client() {
            var client = new SshClient("ssh-target", "deploy", key) { KeepAliveInterval = TimeSpan.FromSeconds(15) };
            client.HostKeyReceived += (_, e) => e.CanTrust = "SHA256:" + e.FingerPrintSHA256 == fingerprint;
            return client;
        }
        var root = "/opt/apps/parallel-" + Guid.NewGuid().ToString("N");
        var connection = new GitConnection { Name = "parallel fixture", PublicOnly = true };
        var projects = Enumerable.Range(0, 4).Select(i => new DeploymentProject {
            Name = "project-" + i, ConnectionId = connection.Id, Host = "ssh-target", Username = "deploy",
            HostFingerprint = fingerprint, ProtectedPrivateKey = fixture.Secrets.Protect(keyText),
            RepositoryUrl = root + "/repo-" + i, Branch = "main", WorkingDirectory = root + "/app-" + i,
            TimeoutMinutes = 1, StopCommand = "true",
            DeployCommand = "printf x >> \"$DEPLOYIT_PROJECT_DIR/executions\"; printf 'PARALLEL_RUNNING\\n'; "
                + "while [ ! -f \"$DEPLOYIT_PROJECT_DIR/release\" ]; do sleep 0.1; done; "
                + (i == 1 ? "exit 7" : "printf 'PARALLEL_FINISHED\\n'")
        }).ToArray();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Deployments:MaxParallelDeployments"] = "2"
        }).Build();
        DeploymentWorker CreateWorker() => new(fixture.Factory, ssh, fixture.Secrets,
            NullLogger<DeploymentWorker>.Instance, configuration);
        DeploymentWorker? worker = null;
        DeploymentView[] runs = [];
        try {
            using (var setup = Client()) {
                await setup.ConnectAsync(default);
                foreach (var project in projects) {
                    var script = "set -e\nmkdir -p -- " + SshDeploymentService.Quote(project.RepositoryUrl)
                        + "\ncd -- " + SshDeploymentService.Quote(project.RepositoryUrl)
                        + "\ngit init -q --initial-branch=main\nprintf fixture > README\ngit add README"
                        + "\ngit -c user.name=test -c user.email=test@example.invalid commit -qm fixture";
                    using var create = setup.CreateCommand("bash -c " + SshDeploymentService.Quote(script));
                    await create.ExecuteAsync(default);
                    Assert.Equal(0, create.ExitStatus);
                }
            }
            await using (var db = fixture.Factory.CreateDbContext()) {
                await db.InitializeAsync();
                db.Connections.Add(connection); db.Projects.AddRange(projects);
                await db.SaveChangesAsync();
            }
            var queued = new List<DeploymentView>();
            foreach (var project in projects) queued.Add(await fixture.Service.EnqueueAsync(project.Id));
            runs = queued.ToArray();
            Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => fixture.Service.EnqueueAsync(projects[0].Id))).StatusCode);
            worker = CreateWorker();
            await worker.StartAsync(default);
            await WaitUntil(async () => (await Run(0)).Log!.Contains("PARALLEL_RUNNING")
                && (await Run(1)).Log!.Contains("PARALLEL_RUNNING"), "Two distinct remote commands must run before either one finishes.");
            Assert.Equal(DeploymentStatus.Running, (await Run(0)).Status);
            Assert.Equal(DeploymentStatus.Running, (await Run(1)).Status);
            Assert.Equal(DeploymentStatus.Queued, (await Run(2)).Status);
            Assert.Null((await Run(2)).StartedAt);
            Assert.Equal(DeploymentStatus.Canceled, (await fixture.Service.CancelDeploymentAsync(runs[3].Id)).Status);

            await fixture.Service.CancelDeploymentAsync(runs[0].Id);
            await WaitUntil(async () => (await Run(0)).Status == DeploymentStatus.Canceled, "Only the selected run must be canceled.");
            await WaitUntil(async () => (await Run(2)).Log!.Contains("PARALLEL_RUNNING"), "A released slot must start the next project.");
            Assert.Equal(DeploymentStatus.Running, (await Run(1)).Status);
            await Release(1);
            await WaitUntil(async () => (await Run(1)).Status == DeploymentStatus.Failed, "The second project must retain its own failure status.");
            Assert.Equal(7, (await Run(1)).ExitCode);
            Assert.Equal(DeploymentStatus.Running, (await Run(2)).Status);

            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15))) await worker.StopAsync(timeout.Token);
            worker.Dispose(); worker = null;
            await using (var db = fixture.Factory.CreateDbContext()) {
                var saved = await db.Deployments.AsNoTracking().SingleAsync(d => d.Id == runs[2].Id);
                Assert.Equal(DeploymentStatus.Running, saved.Status);
                Assert.True(saved.RemoteLogOffset > 0);
                Assert.NotEmpty(saved.ProtectedSnapshot);
            }
            worker = CreateWorker(); await worker.StartAsync(default);
            await WaitUntil(async () => (await Run(2)).Log!.Contains("Панель перезапущена"), "The running job must resume after worker restart.");
            await Release(2);
            await WaitUntil(async () => (await Run(2)).Status == DeploymentStatus.Succeeded, "The resumed project must finish successfully.");
            Assert.Single((await Run(2)).Log!.Split('\n'), line => line == "PARALLEL_RUNNING");
            Assert.Equal(DeploymentStatus.Canceled, (await Run(3)).Status);
            Assert.Null((await Run(3)).StartedAt);
            using var verify = Client(); await verify.ConnectAsync(default);
            foreach (var project in projects.Take(3)) {
                using var count = verify.CreateCommand("cat " + SshDeploymentService.Quote(project.WorkingDirectory + "/executions"));
                await count.ExecuteAsync(default);
                Assert.Equal("x", count.Result);
            }
        }
        finally {
            if (worker is not null) {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await worker.StopAsync(timeout.Token); worker.Dispose();
            }
            foreach (var index in Enumerable.Range(0, Math.Min(3, runs.Length))) {
                try { await ssh.StopRunAsync(projects[index], runs[index].Id, default); } catch { }
            }
            using var cleanup = Client(); await cleanup.ConnectAsync(default);
            using var remove = cleanup.CreateCommand("rm -rf -- " + SshDeploymentService.Quote(root));
            await remove.ExecuteAsync(default);
        }

        Task<DeploymentView> Run(int index) => fixture.Service.DeploymentAsync(runs[index].Id);
        async Task Release(int index) {
            using var client = Client(); await client.ConnectAsync(default);
            using var release = client.CreateCommand("touch -- " + SshDeploymentService.Quote(projects[index].WorkingDirectory + "/release"));
            await release.ExecuteAsync(default); Assert.Equal(0, release.ExitStatus);
        }
    }

    private static async Task WaitUntil(Func<Task<bool>> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline) {
            if (await condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail(message);
    }
}
