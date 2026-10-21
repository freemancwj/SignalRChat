namespace LayimChat.Models;

/// <summary>聊天用户</summary>
public class User
{
    /// <summary>用户ID（LayIM 使用字符串 ID）</summary>
    public string Id { get; set; } = "";

    public string Username { get; set; } = "";

    /// <summary>PBKDF2 密码哈希（Base64）</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>密码盐（Base64）</summary>
    public string Salt { get; set; } = "";

    public string Avatar { get; set; } = "";

    /// <summary>签名</summary>
    public string Sign { get; set; } = "";

    /// <summary>online / hide</summary>
    public string Status { get; set; } = "online";

    /// <summary>是否超级管理员（可新建群组、删除群组、移除群成员）</summary>
    public bool IsAdmin { get; set; }

    /// <summary>最后离线时间（UTC，用于上线时推送离线期间的消息）</summary>
    public DateTime? LastSeenAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<FriendGroup> FriendGroups { get; set; } = new();
}

/// <summary>好友分组（每个用户自己的分组）</summary>
public class FriendGroup
{
    public int Id { get; set; }

    public string GroupName { get; set; } = "";

    /// <summary>所属用户</summary>
    public string OwnerId { get; set; } = "";

    public User? Owner { get; set; }

    public List<Friendship> Friends { get; set; } = new();
}

/// <summary>好友关系</summary>
public class Friendship
{
    public int Id { get; set; }

    /// <summary>拥有者（谁的好友列表）</summary>
    public string OwnerId { get; set; } = "";

    public string FriendId { get; set; } = "";

    public int FriendGroupId { get; set; }

    public FriendGroup? FriendGroup { get; set; }
}

/// <summary>群组</summary>
public class ChatGroup
{
    /// <summary>群ID（LayIM 使用字符串 ID）</summary>
    public string Id { get; set; } = "";

    public string GroupName { get; set; } = "";

    public string Avatar { get; set; } = "";

    public string OwnerId { get; set; } = "";

    public List<GroupMember> Members { get; set; } = new();
}

/// <summary>群成员</summary>
public class GroupMember
{
    public long Id { get; set; }

    public string GroupId { get; set; } = "";

    public ChatGroup? Group { get; set; }

    public string UserId { get; set; } = "";

    public User? User { get; set; }
}

/// <summary>聊天消息</summary>
public class Message
{
    public long Id { get; set; }

    /// <summary>发送者用户ID</summary>
    public string FromUserId { get; set; } = "";

    /// <summary>friend / group</summary>
    public string ToType { get; set; } = "";

    /// <summary>接收者：好友用户ID 或 群ID</summary>
    public string ToId { get; set; } = "";

    public string Content { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
