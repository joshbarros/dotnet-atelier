using Atelier.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Atelier.Workshops;

public class WorkshopsDbContext(DbContextOptions<WorkshopsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "workshops";

    public DbSet<Workshop> Workshops => Set<Workshop>();
    public DbSet<SeatHold> Holds => Set<SeatHold>();
    public DbSet<WorkshopAttendee> Attendees => Set<WorkshopAttendee>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Workshop>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(20);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Workshops_Remaining", "\"SeatsLeft\" >= 0 AND \"SeatsLeft\" <= \"SeatsTotal\"");
                t.HasCheckConstraint("CK_Workshops_Kits", "\"KitsLeft\" >= 0 AND \"KitsLeft\" <= \"KitsTotal\"");
                t.HasCheckConstraint("CK_Workshops_Cap", "\"MaxPerAttendee\" >= 1");
            });
            e.Property(x => x.MaxPerAttendee).HasDefaultValue(2);
        });

        b.Entity<WorkshopAttendee>(e =>
        {
            e.ToTable("WorkshopAttendees", t => t.HasCheckConstraint("CK_WorkshopAttendees_Seats", "\"Seats\" >= 0"));
            e.HasKey(x => new { x.WorkshopId, x.OwnerId });
            e.HasOne<Workshop>().WithMany().HasForeignKey(x => x.WorkshopId);
        });

        b.Entity<SeatHold>(e =>
        {
            e.ToTable("SeatHolds");
            e.HasKey(x => x.OrderId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne<Workshop>().WithMany().HasForeignKey(x => x.WorkshopId);
        });
    }
}
