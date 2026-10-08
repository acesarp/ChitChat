using Microsoft.EntityFrameworkCore;

namespace SampleApp.Server;

public class ChatDbContext(DbContextOptions<ChatDbContext> options) : DbContext(options)
{
    public DbSet<ChatMessageEntity> Messages => Set<ChatMessageEntity>();
    public DbSet<PrivateMessageEntity> PrivateMessages => Set<PrivateMessageEntity>();
    public DbSet<AvatarEntity> Avatars => Set<AvatarEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // UserName isn't named "Id", so EF won't infer it as the key by convention.
        modelBuilder.Entity<AvatarEntity>().HasKey(a => a.UserName);
    }
}
