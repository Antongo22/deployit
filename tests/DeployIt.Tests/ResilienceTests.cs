using System.Diagnostics;
using System.Text;
using DeployIt.Models;
using DeployIt.Services;

namespace DeployIt.Tests;

public sealed class ResilienceTests
{
    [LinuxFact]
    public async Task DetachedRunSurvivesSessionExitAndRepeatedLaunchDoesNotExecuteTwice()
    {
        var root = Path.Combine(Path.GetTempPath(), "deployit-durable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var project = new DeploymentProject { WorkingDirectory = root, TimeoutMinutes = 1 };
        var id = Guid.NewGuid();
        try {
            var operation = "printf x >> " + SshDeploymentService.Quote(root + "/executions")
                + "; printf 'START\\n'; sleep 1; printf 'ошибка\\n' >&2; printf 'ж%.0s' {1..40000}; printf '\\nEND\\n'; exit 7";
            var command = "bash --noprofile --norc -c " + SshDeploymentService.Quote(operation);
            var launch = SshDeploymentService.BuildLaunchCommand(project, id, command);
            Assert.Equal(0, (await Shell(launch)).ExitCode);
            // A new SSH shell retries the same request after the original shell has disappeared.
            Assert.Equal(0, (await Shell(launch)).ExitCode);
            var data = new List<byte>();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            string state = "";
            while (DateTime.UtcNow < deadline) {
                var read = await Shell(SshDeploymentService.BuildReadRunCommand(project, id, data.Count));
                Assert.Equal(0, read.ExitCode);
                var parts = read.Output.Split('\n', 2);
                var header = parts[0].Split('|');
                state = header[0];
                data.AddRange(Convert.FromBase64String(parts.ElementAtOrDefault(1)?.Trim() ?? ""));
                if (state.StartsWith("done:") && data.Count >= long.Parse(header[1])) break;
                await Task.Delay(50);
            }
            Assert.Equal("done:7", state);
            var output = Encoding.UTF8.GetString(data.ToArray());
            Assert.Contains("START", output);
            Assert.Contains("[stderr] ошибка", output);
            Assert.Contains(new string('ж', 40000), output);
            Assert.Contains("END", output);
            Assert.Equal("x", await File.ReadAllTextAsync(root + "/executions"));
            Assert.Equal(0, (await Shell(launch)).ExitCode);
            Assert.Equal("x", await File.ReadAllTextAsync(root + "/executions"));
        }
        finally { Directory.Delete(root, true); }
    }

    [LinuxFact]
    public async Task MissingResultIsReportedAsUnknownAndNotRelaunched()
    {
        var root = Path.Combine(Path.GetTempPath(), "deployit-lost-run-" + Guid.NewGuid().ToString("N"));
        var project = new DeploymentProject { WorkingDirectory = root };
        var id = Guid.NewGuid();
        var job = Path.Combine(root, ".deployit-runs", id.ToString("N"));
        Directory.CreateDirectory(job);
        try {
            // A machine reboot/OOM can leave the claim without a live process or exit status.
            await File.WriteAllTextAsync(Path.Combine(job, "worker.sh"), "old claimed execution");
            var read = await Shell(SshDeploymentService.BuildReadRunCommand(project, id, 0));
            Assert.StartsWith("unknown|0", read.Output);
            Assert.Equal(0, (await Shell(SshDeploymentService.BuildLaunchCommand(project, id,
                "touch " + SshDeploymentService.Quote(root + "/MUST_NOT_RUN")))).ExitCode);
            Assert.False(File.Exists(root + "/MUST_NOT_RUN"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RunLogOffsetCannotBeNegative() => Assert.Throws<ArgumentOutOfRangeException>(() =>
        SshDeploymentService.BuildReadRunCommand(new() { WorkingDirectory = "/opt/apps/test" }, Guid.NewGuid(), -1));

    private static async Task<(int ExitCode, string Output)> Shell(string command)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("-c"); process.StartInfo.ArgumentList.Add(command);
        process.Start(); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }
}
