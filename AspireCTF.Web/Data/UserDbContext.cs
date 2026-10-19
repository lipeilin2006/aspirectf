using Microsoft.EntityFrameworkCore;

namespace AspireCTF.Web.Data;

public sealed class UserDbContext(DbContextOptions<UserDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var user = modelBuilder.Entity<AppUser>();
        user.HasKey(x => x.Id);
        user.HasIndex(x => x.Username).IsUnique();
        user.Property(x => x.Username).HasMaxLength(64).IsRequired();
        user.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
    }
}
