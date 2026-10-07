using DeployIt.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DeployIt.Data;

public sealed class DeployItDbContext(DbContextOptions<DeployItDbContext> options) : DbContext(options)
{
    public DbSet<GitConnection> Connections => Set<GitConnection>();
    public DbSet<DeploymentProject> Projects => Set<DeploymentProject>();
    public DbSet<Deployment> Deployments => Set<Deployment>();

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await Database.EnsureCreatedAsync(ct);
        await Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);

        // EnsureCreated doesn't upgrade existing SQLite databases. Add only the new columns,
        // retaining projects, history and queued snapshots from the key-only version.
        await Database.OpenConnectionAsync(ct);
        await using var transaction = await Database.BeginTransactionAsync(ct);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = "PRAGMA table_info(\"Projects\");";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) columns.Add(reader.GetString(1));
        }
        if (!columns.Contains("AuthenticationType"))
            await Database.ExecuteSqlRawAsync("ALTER TABLE \"Projects\" ADD COLUMN \"AuthenticationType\" INTEGER NOT NULL DEFAULT 0;", ct);
        if (!columns.Contains("ProtectedPassword"))
            await Database.ExecuteSqlRawAsync("ALTER TABLE \"Projects\" ADD COLUMN \"ProtectedPassword\" TEXT NOT NULL DEFAULT '';", ct);
        if (!columns.Contains("RestartCommand"))
            await Database.ExecuteSqlRawAsync("ALTER TABLE \"Projects\" ADD COLUMN \"RestartCommand\" TEXT NOT NULL DEFAULT 'docker compose up -d --force-recreate --no-build';", ct);
        await using (var command = Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = "PRAGMA table_info(\"Deployments\");";
            await using var reader = await command.ExecuteReaderAsync(ct);
            columns.Clear();
            while (await reader.ReadAsync(ct)) columns.Add(reader.GetString(1));
        }
        if (!columns.Contains("Operation"))
            await Database.ExecuteSqlRawAsync("ALTER TABLE \"Deployments\" ADD COLUMN \"Operation\" INTEGER NOT NULL DEFAULT 0;", ct);
        if (!columns.Contains("CancelRequested"))
            await Database.ExecuteSqlRawAsync("ALTER TABLE \"Deployments\" ADD COLUMN \"CancelRequested\" INTEGER NOT NULL DEFAULT 0;", ct);
        if (!columns.Contains("Stage"))
            await Database.ExecuteSqlRawAsync("ALTER TABLE \"Deployments\" ADD COLUMN \"Stage\" TEXT NOT NULL DEFAULT '';", ct);
        if (!columns.Contains("ErrorMessage"))
            await Database.ExecuteSqlRawAsync("ALTER TABLE \"Deployments\" ADD COLUMN \"ErrorMessage\" TEXT NULL;", ct);
        await transaction.CommitAsync(ct);
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<DeploymentProject>().Property(p => p.AuthenticationType).HasDefaultValue(SshAuthenticationType.PrivateKey);
        model.Entity<DeploymentProject>().Property(p => p.ProtectedPassword).HasDefaultValue("");
        model.Entity<DeploymentProject>().Property(p => p.RestartCommand).HasDefaultValue("docker compose up -d --force-recreate --no-build");
        model.Entity<Deployment>().Property(d => d.Operation).HasDefaultValue(DeploymentOperation.Deploy);
        model.Entity<DeploymentProject>().HasIndex(p => p.RepositoryUrl).IsUnique();
        model.Entity<DeploymentProject>().HasIndex(p => new { p.Host, p.Port }).IsUnique();
        model.Entity<DeploymentProject>().HasOne(p => p.Connection).WithMany()
            .HasForeignKey(p => p.ConnectionId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Deployment>().Property(d => d.Status).HasConversion<string>();
        model.Entity<Deployment>().HasIndex(d => d.ProjectId).IsUnique()
            .HasFilter("\"Status\" IN ('Queued', 'Running')");
        model.Entity<Deployment>().HasOne(d => d.Project).WithMany(p => p.Deployments)
            .HasForeignKey(d => d.ProjectId).OnDelete(DeleteBehavior.Cascade);
        // SQLite can't order DateTimeOffset values directly. Store Unix milliseconds.
        model.Entity<Deployment>().Property(d => d.CreatedAt)
            .HasConversion(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v));
    }
}
