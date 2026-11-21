using Microsoft.EntityFrameworkCore;
using Agiliz.Runtime.Data.Entities;

namespace Agiliz.Runtime.Data;

public class AgilizDbContext : DbContext
{
    public DbSet<SchedulingUser> Users { get; set; } = null!;
    public DbSet<Appointment> Appointments { get; set; } = null!;
    public DbSet<ConversationState> ConversationStates { get; set; } = null!;
    public DbSet<CostRecord> CostRecords { get; set; } = null!;
    public DbSet<ContactActivity> ContactActivities { get; set; } = null!;

    public AgilizDbContext(DbContextOptions<AgilizDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SchedulingUser>()
            .HasKey(u => new { u.Phone, u.TenantId });

        modelBuilder.Entity<Appointment>()
            .HasKey(a => a.Id);

        modelBuilder.Entity<Appointment>()
            .HasIndex(a => new { a.Phone, a.TenantId });

        modelBuilder.Entity<Appointment>()
            .HasIndex(a => new { a.TenantId, a.ScheduledTime });

        modelBuilder.Entity<ConversationState>()
            .HasKey(s => new { s.Phone, s.TenantId });

        modelBuilder.Entity<CostRecord>(cost =>
        {
            cost.HasKey(c => c.Id);
            cost.Property(c => c.Id).ValueGeneratedNever();
            cost.Property(c => c.TenantId).HasMaxLength(64).IsRequired();
            cost.Property(c => c.Type).HasConversion<string>().HasMaxLength(32);
            cost.Property(c => c.Description).HasMaxLength(CostRecord.DescriptionMaxLength).IsRequired();
            cost.Property(c => c.AmountUsd).HasPrecision(18, 8);
            cost.Property(c => c.IntegrationId).HasMaxLength(64);
            cost.HasIndex(c => new { c.TenantId, c.Timestamp });
            cost.HasIndex(c => c.Timestamp);
        });

        modelBuilder.Entity<ContactActivity>(usage =>
        {
            usage.HasKey(u => new { u.TenantId, u.Month, u.Phone });
            usage.Property(u => u.TenantId).HasMaxLength(64);
            usage.Property(u => u.Month).HasMaxLength(7);
            usage.Property(u => u.Phone).HasMaxLength(32);
        });
    }
}
