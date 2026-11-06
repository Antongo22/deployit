using DeployIt.Models;
using DeployIt.Services;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DeployIt.Tests;

[Collection("SSH integration")]
public sealed class SshResilienceTests
{
    [SshFact]
    public async Task RealSshDisconnectAndPanelRestartKeepOneRemoteExecution()
    {
        using var fixture = new DeploymentTests.Fixture();
        var service = new SshDeploymentService(fixture.Secrets);
        var keyText = await File.ReadAllTextAsync("/fixture/client");
        var fingerprint = await service.ProbeAsync("ssh-target", 22);
        using var key = new PrivateKeyFile(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(keyText)));
        SshClient Client() {
            var client = new SshClient("ssh-target", "deploy", key);
            client.HostKeyReceived += (_, e) => e.CanTrust = "SHA256:" + e.FingerPrintSHA256 == fingerprint;
            return client;
        }
        using (var setup = Client()) {
            await setup.ConnectAsync(default);
            using var command = setup.CreateCommand("bash -c 'set -e; mkdir -p /opt/apps/resilience-source; cd /opt/apps/resilience-source; git init -q --initial-branch=main; printf fixture > README; git add README; if ! git diff --cached --quiet; then git -c user.name=test -c user.email=test@example.invalid commit -qm fixture; fi'");
            await command.ExecuteAsync(default);
            Assert.Equal(0, command.ExitStatus);
        }
        for (var mode = 0; mode < 2; mode++) {
            var project = new DeploymentProject { Host = "ssh-target", Username = "deploy", HostFingerprint = fingerprint,
                ProtectedPrivateKey = fixture.Secrets.Protect(keyText), RepositoryUrl = "/opt/apps/resilience-source",
                WorkingDirectory = "/opt/apps/resilience-" + Guid.NewGuid().ToString("N"), TimeoutMinutes = 1,
                DeployCommand = "printf x >> \"$DEPLOYIT_PROJECT_DIR/executions\"; printf 'INTEGRATION_RUNNING\\n'; sleep 10; printf 'INTEGRATION_FINISHED\\n'",
                StopCommand = "true" };
            var snapshot = new DeploymentSnapshot(project, new GitConnection { PublicOnly = true });
            var id = Guid.NewGuid();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lines = new List<string>();
            long offset = 0;
            Task Log(string line) { lines.Add(line); if (line == "INTEGRATION_RUNNING") started.TrySetResult(); return Task.CompletedTask; }
            Task Checkpoint(long value) { offset = value; return Task.CompletedTask; }
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            try {
                var running = service.DeployAsync(snapshot, id, Log, cancellation.Token, checkpoint: Checkpoint);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
                if (mode == 0) {
                    // Only the isolated fixture's SSH sessions are killed. setsid workers stay alive.
                    using var interrupt = Client();
                    await interrupt.ConnectAsync(default);
                    using var kill = interrupt.CreateCommand("pkill -u deploy -x sshd");
                    try { await kill.ExecuteAsync(default); }
                    catch (SshConnectionException) { }
                    Assert.Equal(0, await running);
                    Assert.Contains(lines, line => line.Contains("Связь по SSH потеряна"));
                    Assert.Contains(lines, line => line.Contains("SSH-соединение восстановлено"));
                } else {
                    // The panel process stops monitoring; a new instance resumes at the checkpoint.
                    cancellation.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
                    Assert.True(offset > 0);
                    Assert.Equal(0, await service.ResumeAsync(snapshot, id, Log, default, offset, Checkpoint));
                    Assert.Single(lines, line => line == "INTEGRATION_RUNNING");
                }
                using var verify = Client();
                await verify.ConnectAsync(default);
                using var count = verify.CreateCommand("cat " + SshDeploymentService.Quote(project.WorkingDirectory + "/executions"));
                await count.ExecuteAsync(default);
                Assert.Equal("x", count.Result);
                Assert.Contains("INTEGRATION_FINISHED", lines);
            }
            finally {
                await service.DeleteAsync(project, [id], default);
            }
        }
    }
}

public sealed class SshFactAttribute : FactAttribute
{
    public SshFactAttribute() {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("DEPLOYIT_SSH_INTEGRATION") != "1")
            Skip = "Explicit isolated ssh-target fixture is required.";
    }
}
