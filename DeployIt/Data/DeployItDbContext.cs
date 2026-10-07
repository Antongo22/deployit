using DeployIt.Models;
using Microsoft.EntityFrameworkCore;

namespace DeployIt.Data;

public sealed class DeployItDbContext(DbContextOptions<DeployItDbContext> options) : DbContext(options)
{
    public DbSet<GitConnection> Connections => Set<GitConnection>();
    public DbSet<DeploymentProject> Projects => Set<DeploymentProject>();
    public DbSet<Deployment> Deployments => Set<Deployment>();

    protected override void OnModelCreating(ModelBuilder model)
    {
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
