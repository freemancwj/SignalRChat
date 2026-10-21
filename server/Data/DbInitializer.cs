using LayimChat.Models;
using LayimChat.Services;
using Microsoft.EntityFrameworkCore;

namespace LayimChat.Data;

public static class DbInitializer
{
    /// <summary>演示账号的初始密码</summary>
    public const string DemoPassword = "123456";

    /// <summary>超级管理员账号（每次启动确保存在，可建群/删群/移除群成员）</summary>
    public const string AdminUsername = "admin";

    /// <summary>超级管理员密码</summary>
    public const string AdminPassword = "cwjaixy1314";

    private const string AdminId = "999999";

    /// <summary>
    /// 轻量 schema 补丁：为既有数据库补充后加的列（EnsureCreated 不会修改已存在的表）。
    /// 各数据库的标识符引用语法不同：SqlServer 用 []，MySql 用 ``，Sqlite 两种都认。
    /// </summary>
    private static void PatchSchema(LayimDbContext db, string provider)
    {
        var (probeLastSeen, alterLastSeen, backfillLastSeen) = provider switch
        {
            "SqlServer" => (
                "SELECT TOP 1 [LastSeenAt] FROM [Users]",
                "ALTER TABLE [Users] ADD [LastSeenAt] datetime2 NULL",
                "UPDATE [Users] SET [LastSeenAt] = GETUTCDATE() WHERE [LastSeenAt] IS NULL"),
            "MySql" => (
                "SELECT `LastSeenAt` FROM `Users` LIMIT 1",
                "ALTER TABLE `Users` ADD COLUMN `LastSeenAt` datetime NULL",
                "UPDATE `Users` SET `LastSeenAt` = UTC_TIMESTAMP() WHERE `LastSeenAt` IS NULL"),
            _ => (
                "SELECT [LastSeenAt] FROM [Users] LIMIT 1",
                "ALTER TABLE [Users] ADD COLUMN [LastSeenAt] TEXT NULL",
                "UPDATE [Users] SET [LastSeenAt] = CURRENT_TIMESTAMP WHERE [LastSeenAt] IS NULL")
        };

        // LastSeenAt：离线消息水位；不存在则补列并回填，避免老用户首次上线收到全部历史
        if (!ColumnExists(db, probeLastSeen))
        {
            db.Database.ExecuteSqlRaw(alterLastSeen);
            db.Database.ExecuteSqlRaw(backfillLastSeen);
        }

        var (probeAdmin, alterAdmin) = provider switch
        {
            "SqlServer" => (
                "SELECT TOP 1 [IsAdmin] FROM [Users]",
                "ALTER TABLE [Users] ADD [IsAdmin] bit NOT NULL DEFAULT 0"),
            "MySql" => (
                "SELECT `IsAdmin` FROM `Users` LIMIT 1",
                "ALTER TABLE `Users` ADD COLUMN `IsAdmin` tinyint(1) NOT NULL DEFAULT 0"),
            _ => (
                "SELECT [IsAdmin] FROM [Users] LIMIT 1",
                "ALTER TABLE [Users] ADD COLUMN [IsAdmin] INTEGER NOT NULL DEFAULT 0")
        };

        if (!ColumnExists(db, probeAdmin))
        {
            db.Database.ExecuteSqlRaw(alterAdmin);
        }
    }

    /// <summary>探测列是否存在（查询成功即存在）</summary>
    private static bool ColumnExists(LayimDbContext db, string probeSql)
    {
        try
        {
            db.Database.ExecuteSqlRaw(probeSql);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>确保超级管理员账号存在（已有同名账号时只补齐管理员标记）</summary>
    private static void EnsureAdmin(LayimDbContext db)
    {
        // 兼容管理员账号被改名的情况：按内置 ID 兜底查找，避免重启时重复创建导致主键冲突
        var admin = db.Users.FirstOrDefault(u => u.Username == AdminUsername)
            ?? db.Users.FirstOrDefault(u => u.Id == AdminId);
        if (admin == null)
        {
            var existingUsers = db.Users.Where(u => u.Username != AdminUsername).ToList();
            var (hash, salt) = PasswordCrypto.Hash(AdminPassword);

            admin = new User
            {
                Id = AdminId,
                Username = AdminUsername,
                PasswordHash = hash,
                Salt = salt,
                Avatar = "",
                Sign = "系统管理员",
                Status = "online",
                IsAdmin = true,
                CreatedAt = DateTime.UtcNow
            };
            db.Users.Add(admin);

            var myGroup = new FriendGroup { GroupName = "我的好友", OwnerId = admin.Id };
            admin.FriendGroups.Add(myGroup);
            db.FriendGroups.Add(myGroup);

            // 与其他用户互加好友，保证管理员可直接单聊任何用户
            foreach (var other in existingUsers)
            {
                db.Friendships.Add(new Friendship { OwnerId = admin.Id, FriendId = other.Id, FriendGroup = myGroup });

                var otherGroupId = db.FriendGroups
                    .Where(g => g.OwnerId == other.Id)
                    .OrderBy(g => g.Id)
                    .Select(g => (int?)g.Id)
                    .FirstOrDefault();
                if (otherGroupId != null)
                {
                    db.Friendships.Add(new Friendship { OwnerId = other.Id, FriendId = admin.Id, FriendGroupId = otherGroupId.Value });
                }
            }
        }
        else if (!admin.IsAdmin)
        {
            admin.IsAdmin = true;
        }

        db.SaveChanges();
    }

    public static void Initialize(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LayimDbContext>();

        db.Database.EnsureCreated();

        // 为既有数据库补充 Users 后加的列（EnsureCreated 不会修改已有表）
        PatchSchema(db, app.Configuration["Database:Provider"] ?? "Sqlite");

        // 上传文件目录
        var uploadsDir = Path.Combine(app.Environment.WebRootPath ?? ".", "uploads");
        Directory.CreateDirectory(uploadsDir);

        if (!db.Users.Any())
        {
            SeedDemoData(db);
        }

        // 每次启动都确保超级管理员存在（老库升级后也能补上）
        EnsureAdmin(db);
    }

    private static void SeedDemoData(LayimDbContext db)
    {
        var now = DateTime.UtcNow;

        // ---------- 种子用户 ----------
        var users = new List<User>();
        for (var i = 1; i <= 5; i++)
        {
            var (hash, salt) = PasswordCrypto.Hash(DemoPassword);
            users.Add(new User
            {
                Id = (100000 + i).ToString(),
                Username = $"测试{i}",
                PasswordHash = hash,
                Salt = salt,
                Avatar = "",
                Sign = $"测试内容{i}",
                Status = "online",
                CreatedAt = now
            });
        }
        db.Users.AddRange(users);

        // ---------- 好友关系（5 个用户互为好友，各自一个“我的好友”分组） ----------
        var groups = new List<FriendGroup>();
        foreach (var user in users)
        {
            var fg = new FriendGroup { GroupName = "我的好友", OwnerId = user.Id };
            groups.Add(fg);
            user.FriendGroups.Add(fg);
        }
        db.FriendGroups.AddRange(groups);

        foreach (var owner in users)
        {
            foreach (var friend in users.Where(u => u.Id != owner.Id))
            {
                db.Friendships.Add(new Friendship
                {
                    OwnerId = owner.Id,
                    FriendId = friend.Id,
                    FriendGroup = groups.First(g => g.OwnerId == owner.Id)
                });
            }
        }

        // ---------- 群组 ----------
        var g1 = new ChatGroup { Id = "101", GroupName = "测试群组一", Avatar = "", OwnerId = "100001" };
        var g2 = new ChatGroup { Id = "102", GroupName = "测试群组二", Avatar = "", OwnerId = "100001" };
        db.ChatGroups.AddRange(g1, g2);

        foreach (var u in users)
        {
            db.GroupMembers.Add(new GroupMember { GroupId = g1.Id, UserId = u.Id });
        }
        foreach (var u in users.Take(3))
        {
            db.GroupMembers.Add(new GroupMember { GroupId = g2.Id, UserId = u.Id });
        }

        // ---------- 演示历史消息 ----------
        db.Messages.AddRange(
            new Message { FromUserId = "100001", ToType = "friend", ToId = "100002", Content = "你好，我是测试1 face[微笑] ", CreatedAt = now.AddMinutes(-30) },
            new Message { FromUserId = "100002", ToType = "friend", ToId = "100001", Content = "你好呀！face[心] 这是一条真实的数据库消息", CreatedAt = now.AddMinutes(-29) },
            new Message { FromUserId = "100002", ToType = "friend", ToId = "100003", Content = "测试3，周末一起爬山吗？", CreatedAt = now.AddMinutes(-20) },
            new Message { FromUserId = "100002", ToType = "group", ToId = "101", Content = "大家下午好 face[哈哈] ", CreatedAt = now.AddMinutes(-15) },
            new Message { FromUserId = "100003", ToType = "group", ToId = "101", Content = "下午好！今天天气不错 face[太阳] ", CreatedAt = now.AddMinutes(-14) }
        );

        db.SaveChanges();
    }
}
