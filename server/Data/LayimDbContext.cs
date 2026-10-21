using LayimChat.Models;
using Microsoft.EntityFrameworkCore;

namespace LayimChat.Data;

public class LayimDbContext : DbContext
{
    public LayimDbContext(DbContextOptions<LayimDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<FriendGroup> FriendGroups => Set<FriendGroup>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<ChatGroup> ChatGroups => Set<ChatGroup>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<Message> Messages => Set<Message>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<User>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => x.Username).IsUnique();
        });

        mb.Entity<Friendship>(e =>
        {
            e.HasIndex(x => new { x.OwnerId, x.FriendId }).IsUnique();
        });

        mb.Entity<GroupMember>(e =>
        {
            e.HasIndex(x => new { x.GroupId, x.UserId }).IsUnique();
        });

        mb.Entity<Message>(e =>
        {
            e.HasIndex(x => new { x.ToType, x.ToId, x.CreatedAt });
        });
    }
}
