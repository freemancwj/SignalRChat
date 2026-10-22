using LayimChat.Data;
using LayimChat.Hubs;
using LayimChat.Models;
using LayimChat.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace LayimChat.Controllers;

/// <summary>
/// 第三方系统（如公司管理系统）集成接口。
/// 全部接口需在请求头携带 X-Api-Key（与配置 Integration:ApiKey 一致）。
/// </summary>
[ApiController]
[Route("api/integration")]
[ApiKey]
public class IntegrationController : ControllerBase
{
    private readonly LayimDbContext _db;
    private readonly IHubContext<ChatHub> _hub;

    public IntegrationController(LayimDbContext db, IHubContext<ChatHub> hub)
    {
        _db = db;
        _hub = hub;
    }

    /// <summary>用户列表（供第三方系统对照账号）</summary>
    [HttpGet("users")]
    public async Task<IActionResult> Users()
    {
        var users = await _db.Users
            .OrderBy(u => u.Id.Length).ThenBy(u => u.Id)
            .Select(u => new { u.Id, u.Username, u.Sign, u.Avatar, u.Status })
            .ToListAsync();
        return Ok(new { code = 0, msg = "", data = users });
    }

    public record SyncUserRequest(string Username, string? Password, string? Sign, string? Avatar, string[]? Groups);

    /// <summary>
    /// 同步用户：用户名不存在则创建，已存在则更新签名/头像/密码/群组成员关系。
    /// Groups 为办公系统的部门（群组）ID 列表：
    /// - 传入（含空数组）时按此列表精确设置成员关系，空数组表示不属于任何群；
    /// - 不传（null）时保持旧行为：新建用户自动加入所有群，已存在用户群关系不变。
    /// </summary>
    [HttpPost("users")]
    public async Task<IActionResult> SyncUser([FromBody] SyncUserRequest req)
    {
        var username = (req.Username ?? "").Trim();
        if (username.Length < 2 || username.Length > 20)
        {
            return Ok(new { code = 1, msg = "用户名长度需在 2-20 个字符之间" });
        }

        if (req.Groups != null)
        {
            var allGroupIds = await _db.ChatGroups.Select(g => g.Id).ToListAsync();
            var invalid = req.Groups.Select(g => (g ?? "").Trim()).Where(g => g.Length > 0 && !allGroupIds.Contains(g)).ToList();
            if (invalid.Count > 0)
            {
                return Ok(new { code = 1, msg = $"群组不存在：{string.Join(",", invalid)}，请先创建群组" });
            }
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        var created = false;

        if (user == null)
        {
            var password = string.IsNullOrEmpty(req.Password) ? "123456" : req.Password;
            if (req.Groups == null)
            {
                // 未指定群组：沿用旧行为，自动加入所有群
                user = await UserProvisioning.CreateWithDefaultsAsync(_db, username, password);
            }
            else
            {
                user = await UserProvisioning.CreateWithDefaultsAsync(_db, username, password,
                    joinAllGroupsIfNoGroupId: false);
                foreach (var gid in req.Groups.Distinct())
                {
                    _db.GroupMembers.Add(new GroupMember { GroupId = gid, UserId = user.Id });
                }
                await _db.SaveChangesAsync();
            }
            created = true;
        }
        else
        {
            if (req.Sign != null) user.Sign = req.Sign;
            if (req.Avatar != null) user.Avatar = req.Avatar;
            if (!string.IsNullOrEmpty(req.Password))
            {
                var (hash, salt) = PasswordCrypto.Hash(req.Password);
                user.PasswordHash = hash;
                user.Salt = salt;
            }
            await _db.SaveChangesAsync();

            if (req.Groups != null)
            {
                await SetMembershipsAsync(user.Id, req.Groups);
            }
        }

        return Ok(new { code = 0, msg = "", data = new { id = user.Id, username = user.Username, created } });
    }

    /// <summary>删除用户：连带清理好友关系、群成员关系与该用户的消息</summary>
    [HttpDelete("users/{user}")]
    public async Task<IActionResult> DeleteUser(string user)
    {
        var target = await ResolveUser(user);
        if (target == null)
        {
            return Ok(new { code = 1, msg = $"用户不存在：{user}" });
        }

        await _db.Friendships
            .Where(f => f.OwnerId == target.Id || f.FriendId == target.Id)
            .ExecuteDeleteAsync();
        await _db.GroupMembers.Where(m => m.UserId == target.Id).ExecuteDeleteAsync();
        await _db.Messages
            .Where(m => m.FromUserId == target.Id || (m.ToType == "friend" && m.ToId == target.Id))
            .ExecuteDeleteAsync();
        _db.Users.Remove(target);
        await _db.SaveChangesAsync();

        return Ok(new { code = 0, msg = "" });
    }

    // ==================== 群组（部门）同步 ====================

    /// <summary>群组列表（含成员），供办公系统对账</summary>
    [HttpGet("groups")]
    public async Task<IActionResult> Groups()
    {
        var groups = await _db.ChatGroups.OrderBy(g => g.Id).ToListAsync();
        var members = await _db.GroupMembers.ToListAsync();
        var nameMap = await _db.Users.ToDictionaryAsync(u => u.Id, u => u.Username);

        var data = groups.Select(g => new
        {
            id = g.Id,
            name = g.GroupName,
            ownerId = g.OwnerId,
            members = members
                .Where(m => m.GroupId == g.Id)
                .Select(m => new { id = m.UserId, username = nameMap.TryGetValue(m.UserId, out var n) ? n : "" })
                .ToList()
        });

        return Ok(new { code = 0, msg = "", data });
    }

    public record SyncGroupRequest(string? Id, string? Name);

    /// <summary>
    /// 创建或更新群组：建议直接用办公系统的部门ID作为群ID（稳定不变）。
    /// 已存在时仅更新名称；名称变化会实时通知在线成员刷新界面。
    /// </summary>
    [HttpPost("groups")]
    public async Task<IActionResult> SyncGroup([FromBody] SyncGroupRequest req)
    {
        var name = (req.Name ?? "").Trim();
        if (name.Length == 0 || name.Length > 30)
        {
            return Ok(new { code = 1, msg = "群名称长度需在 1-30 个字符之间" });
        }

        var id = (req.Id ?? "").Trim();
        if (id.Length == 0)
        {
            // 未指定ID时自动生成递增数字ID（与网页端管理界面一致）
            var ids = await _db.ChatGroups.Select(g => g.Id).ToListAsync();
            id = (ids.Select(v => int.TryParse(v, out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1).ToString();
        }
        if (id.Length > 50)
        {
            return Ok(new { code = 1, msg = "群组ID长度不能超过 50" });
        }

        var group = await _db.ChatGroups.FindAsync(id);
        var created = false;
        if (group == null)
        {
            group = new ChatGroup { Id = id, GroupName = name, Avatar = "", OwnerId = "" };
            _db.ChatGroups.Add(group);
            created = true;
        }
        else
        {
            group.GroupName = name;
        }
        await _db.SaveChangesAsync();

        if (!created)
        {
            await _hub.Clients.Group(ChatHub.GroupName(id)).SendAsync("GroupRenamed", new { id, name });
        }

        return Ok(new { code = 0, msg = "", data = new { id, name, created } });
    }

    /// <summary>删除群组：连带删除成员关系与该群聊天记录，并通知在线成员移除界面上的群</summary>
    [HttpDelete("groups/{id}")]
    public async Task<IActionResult> DeleteGroup(string id)
    {
        var group = await _db.ChatGroups.FindAsync(id);
        if (group == null)
        {
            return Ok(new { code = 1, msg = $"群组不存在：{id}" });
        }

        await _hub.Clients.Group(ChatHub.GroupName(id))
            .SendAsync("GroupDeleted", new { id = group.Id, name = group.GroupName });

        await _db.GroupMembers.Where(m => m.GroupId == id).ExecuteDeleteAsync();
        await _db.Messages.Where(m => m.ToType == "group" && m.ToId == id).ExecuteDeleteAsync();
        _db.ChatGroups.Remove(group);
        await _db.SaveChangesAsync();

        return Ok(new { code = 0, msg = "" });
    }

    public record MemberRequest(string? User);

    /// <summary>向群组添加成员（User 传用户ID或用户名），可重复调用</summary>
    [HttpPost("groups/{id}/members")]
    public async Task<IActionResult> AddMember(string id, [FromBody] MemberRequest req)
    {
        if (await _db.ChatGroups.FindAsync(id) == null)
        {
            return Ok(new { code = 1, msg = $"群组不存在：{id}" });
        }
        var user = await ResolveUser(req.User);
        if (user == null)
        {
            return Ok(new { code = 1, msg = $"用户不存在：{req.User}" });
        }

        var exists = await _db.GroupMembers.AnyAsync(m => m.GroupId == id && m.UserId == user.Id);
        if (!exists)
        {
            _db.GroupMembers.Add(new GroupMember { GroupId = id, UserId = user.Id });
            await _db.SaveChangesAsync();
            foreach (var connId in ChatHub.ConnectionIdsOf(user.Id))
            {
                await _hub.Groups.AddToGroupAsync(connId, ChatHub.GroupName(id));
            }
        }

        return Ok(new { code = 0, msg = "", data = new { groupId = id, userId = user.Id, added = !exists } });
    }

    /// <summary>从群组移除成员（路径中的 users 传用户ID或用户名，含中文需 URL 编码）</summary>
    [HttpDelete("groups/{id}/members/{user}")]
    public async Task<IActionResult> RemoveMember(string id, string user)
    {
        var group = await _db.ChatGroups.FindAsync(id);
        if (group == null)
        {
            return Ok(new { code = 1, msg = $"群组不存在：{id}" });
        }
        var target = await ResolveUser(user);
        if (target == null)
        {
            return Ok(new { code = 1, msg = $"用户不存在：{user}" });
        }

        var member = await _db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == target.Id);
        if (member == null)
        {
            return Ok(new { code = 1, msg = "该用户不在此群中" });
        }

        _db.GroupMembers.Remove(member);
        await _db.SaveChangesAsync();
        foreach (var connId in ChatHub.ConnectionIdsOf(target.Id))
        {
            await _hub.Groups.RemoveFromGroupAsync(connId, ChatHub.GroupName(id));
        }
        await _hub.Clients.User(target.Id)
            .SendAsync("RemovedFromGroup", new { id = group.Id, name = group.GroupName });

        return Ok(new { code = 0, msg = "" });
    }

    /// <summary>按列表精确设置用户所属群组，并同步其在线连接的 SignalR 组成员关系</summary>
    private async Task SetMembershipsAsync(string userId, string[] groupIds)
    {
        var wanted = groupIds.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct().ToList();

        var current = await _db.GroupMembers.Where(m => m.UserId == userId).ToListAsync();
        _db.GroupMembers.RemoveRange(current.Where(m => !wanted.Contains(m.GroupId)));
        var toAdd = wanted.Where(g => current.All(m => m.GroupId != g)).ToList();
        foreach (var gid in toAdd)
        {
            _db.GroupMembers.Add(new GroupMember { GroupId = gid, UserId = userId });
        }
        await _db.SaveChangesAsync();

        var allGroupIds = await _db.ChatGroups.Select(g => g.Id).ToListAsync();
        foreach (var connId in ChatHub.ConnectionIdsOf(userId))
        {
            foreach (var gid in allGroupIds)
            {
                if (wanted.Contains(gid)) await _hub.Groups.AddToGroupAsync(connId, ChatHub.GroupName(gid));
                else await _hub.Groups.RemoveFromGroupAsync(connId, ChatHub.GroupName(gid));
            }
        }
    }

    public record SendMessageRequest(string FromUser, string ToType, string ToUser, string Content);

    /// <summary>
    /// 发送消息（系统通知 / 业务提醒推送）。
    /// FromUser / ToUser 支持传用户ID或用户名；ToType 为 friend 或 group。
    /// 消息会落库，且接收方在线时实时推送到聊天窗口。
    /// 典型用法：先在聊天系统里建一个"系统通知"账号作为 FromUser。
    /// </summary>
    [HttpPost("messages")]
    public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest req)
    {
        var toType = (req.ToType ?? "").Trim().ToLower();
        if (toType is not ("friend" or "group"))
        {
            return Ok(new { code = 1, msg = "ToType 必须是 friend 或 group" });
        }
        if (string.IsNullOrWhiteSpace(req.Content))
        {
            return Ok(new { code = 1, msg = "消息内容不能为空" });
        }

        var from = await ResolveUser(req.FromUser);
        if (from == null)
        {
            return Ok(new { code = 1, msg = $"发送者不存在：{req.FromUser}" });
        }

        // 群消息时校验群存在；好友消息时校验接收者存在
        if (toType == "group")
        {
            if (await _db.ChatGroups.FindAsync(req.ToUser.Trim()) == null)
            {
                return Ok(new { code = 1, msg = $"群组不存在：{req.ToUser}" });
            }
        }
        else
        {
            if (await ResolveUser(req.ToUser) == null)
            {
                return Ok(new { code = 1, msg = $"接收者不存在：{req.ToUser}" });
            }
        }

        var toId = toType == "friend"
            ? (await ResolveUser(req.ToUser))!.Id
            : req.ToUser.Trim();

        var message = new Message
        {
            FromUserId = from.Id,
            ToType = toType,
            ToId = toId,
            Content = req.Content,
            CreatedAt = DateTime.UtcNow
        };
        _db.Messages.Add(message);
        await _db.SaveChangesAsync();

        // 推送规则与 ChatHub 保持一致：好友消息 id=发送者ID，群消息 id=群ID
        var payload = new
        {
            username = from.Username,
            avatar = Avatars.OrDefault(from.Avatar),
            id = toType == "group" ? toId : from.Id,
            type = toType,
            content = req.Content,
            timestamp = new DateTimeOffset(DateTime.SpecifyKind(message.CreatedAt, DateTimeKind.Utc)).ToUnixTimeMilliseconds()
        };

        if (toType == "group")
        {
            await _hub.Clients.Group(ChatHub.GroupName(toId)).SendAsync("ReceiveMessage", payload);
        }
        else
        {
            await _hub.Clients.User(toId).SendAsync("ReceiveMessage", payload);
        }

        return Ok(new { code = 0, msg = "", data = new { id = message.Id } });
    }

    /// <summary>按ID或用户名解析用户</summary>
    private async Task<User?> ResolveUser(string? idOrName)
    {
        var key = (idOrName ?? "").Trim();
        if (key.Length == 0) return null;
        return await _db.Users.FirstOrDefaultAsync(u => u.Id == key || u.Username == key);
    }
}
