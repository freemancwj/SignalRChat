using LayimChat.Data;
using LayimChat.Models;
using Microsoft.EntityFrameworkCore;

namespace LayimChat.Services;

/// <summary>
/// 头像显示约定：用户未设置自定义头像（Users.Avatar 为空）时统一回落到默认头像。
/// 所有对外返回用户头像的接口都经过这里，避免前端渲染出 &lt;img src=""&gt; 变成破图。
/// 注意：该类方法只能在内存中调用；EF 表达式投影里请先 ToListAsync 再映射。
/// </summary>
public static class Avatars
{
    /// <summary>默认头像。用站点绝对路径，保证主面板与三层目录下的 chatlog iframe 都能正确解析</summary>
    public const string Default = "/dist/layim-assets/images/default.png";

    /// <summary>头像为空时返回默认头像</summary>
    public static string OrDefault(string? avatar)
        => string.IsNullOrWhiteSpace(avatar) ? Default : avatar!;
}

/// <summary>
/// 用户开通辅助：创建用户并执行默认动作（创建默认好友分组、按参数加入群组）。
/// 注册接口、第三方用户同步接口、SSO 自动开户共用。
/// 注意：不自动与所有用户互加好友——企业用户之间可直接单聊（见 ChatHub.CanChatAsync），
/// 好友关系仅用于上下线提醒等场景，不影响通讯录展示与发消息权限。
/// </summary>
public static class UserProvisioning
{
    /// <summary>
    /// 创建用户（不再自动互加好友，新用户好友列表为空）。
    /// groupId 指定时只加入该群组；
    /// groupId 为空时：joinAllGroupsIfNoGroupId=true 加入所有群组（SSO/集成同步的默认行为），
    /// false 则不属于任何群组（注册时未选群组）。
    /// </summary>
    public static async Task<User> CreateWithDefaultsAsync(LayimDbContext db, string username, string password,
        string? groupId = null, bool joinAllGroupsIfNoGroupId = true)
    {
        // 生成新用户ID（在最大数字ID之上递增）
        var allIds = await db.Users.Select(u => u.Id).ToListAsync();
        var maxId = allIds
            .Where(id => id.All(char.IsDigit))
            .Select(long.Parse)
            .DefaultIfEmpty(100000)
            .Max();

        var (hash, salt) = PasswordCrypto.Hash(password);
        var user = new User
        {
            Id = (maxId + 1).ToString(),
            Username = username,
            PasswordHash = hash,
            Salt = salt,
            Avatar = "",
            Sign = "",
            Status = "online"
        };
        db.Users.Add(user);

        // 默认好友分组（「我的好友」选项卡的数据来源，初始为空，由用户显式加好友填充）
        var myGroup = new FriendGroup { GroupName = "我的好友", OwnerId = user.Id };
        db.FriendGroups.Add(myGroup);

        var groupIds = groupId != null
            ? new List<string> { groupId }
            : joinAllGroupsIfNoGroupId
                ? await db.ChatGroups.Select(g => g.Id).ToListAsync()
                : new List<string>();

        foreach (var gid in groupIds)
        {
            db.GroupMembers.Add(new GroupMember { GroupId = gid, UserId = user.Id });
        }

        await db.SaveChangesAsync();
        return user;
    }
}
