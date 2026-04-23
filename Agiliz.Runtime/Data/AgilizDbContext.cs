using Microsoft.EntityFrameworkCore;
using Agiliz.Runtime.Data.Entities;

namespace Agiliz.Runtime.Data;

public class AgilizDbContext : DbContext
{
    public DbSet<SchedulingUser> Users { get; set; } = null!;
    public DbSet<Appointment> Appointments { get; set; } = null!;

    public AgilizDbContext(DbContextOptions<AgilizDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SchedulingUser>()
            .HasKey(u => new { u.Phone, u.TenantId });

        modelBuilder.Entity<Appointment>()
            .HasKey(a => a.Id);

        modelBuilder.Entity<Appointment>()
            .HasIndex(a => new { a.Phone, a.TenantId });
    }
}
