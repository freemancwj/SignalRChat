using System.Security.Claims;
using LayimChat.Data;
using LayimChat.Hubs;
using LayimChat.Models;
using LayimChat.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace LayimChat.Controllers;

/// <summary>超级管理员接口：新建群组、删除群组、移除群成员</summary>
[Authorize]
[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly LayimDbContext _db;
    private readonly IHubContext<ChatHub> _hub;

    public AdminController(LayimDbContext db, IHubContext<ChatHub> hub)
    {
        _db = db;
        _hub = hub;
    }

    private string MyId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private async Task<bool> IsAdminAsync()
    {
        var me = await _db.Users.FindAsync(MyId);
        return me?.IsAdmin == true;
    }

    private IActionResult Forbidden() => Ok(new { code = 403, msg = "需要超级管理员权限" });

    public record CreateGroupRequest(string? Name);

    public record RenameGroupRequest(string? Name);

    /// <summary>修改群名称：保存后实时通知在线成员更新界面</summary>
    [HttpPut("groups/{id}")]
    public async Task<IActionResult> RenameGroup(string id, [FromBody] RenameGroupRequest req)
    {
        if (!await IsAdminAsync()) return Forbidden();

        var group = await _db.ChatGroups.FindAsync(id);
        if (group == null)
        {
            return Ok(new { code = 1, msg = "群组不存在" });
        }

        var name = req.Name?.Trim() ?? "";
        if (name.Length == 0 || name.Length > 30)
        {
            return Ok(new { code = 1, msg = "群名称长度需在 1-30 个字符之间" });
        }

        group.GroupName = name;
        await _db.SaveChangesAsync();

        await _hub.Clients.Group(ChatHub.GroupName(id)).SendAsync("GroupRenamed", new { id, name });

        return Ok(new { code = 0, msg = "", data = new { id, name } });
    }

    /// <summary>新建群组：管理员自动成为该群成员（便于直接进入群聊）</summary>
    [HttpPost("groups")]
    public async Task<IActionResult> CreateGroup([FromBody] CreateGroupRequest req)
    {
        if (!await IsAdminAsync()) return Forbidden();

        var name = req.Name?.Trim() ?? "";
        if (name.Length == 0 || name.Length > 30)
        {
            return Ok(new { code = 1, msg = "群名称长度需在 1-30 个字符之间" });
        }

        // 群ID 沿用递增数字（101、102…），避免与既有 ID 冲突
        var ids = await _db.ChatGroups.Select(g => g.Id).ToListAsync();
        var next = ids
            .Select(id => int.TryParse(id, out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;

        var group = new ChatGroup
        {
            Id = next.ToString(),
            GroupName = name,
            Avatar = "",
            OwnerId = MyId
        };
        _db.ChatGroups.Add(group);
        _db.GroupMembers.Add(new GroupMember { GroupId = group.Id, UserId = MyId });
        await _db.SaveChangesAsync();

        // 管理员当前在线的连接立即加入 SignalR 组，无需刷新即可收发群消息
        foreach (var connId in ChatHub.ConnectionIdsOf(MyId))
        {
            await _hub.Groups.AddToGroupAsync(connId, ChatHub.GroupName(group.Id));
        }

        return Ok(new
        {
            code = 0,
            msg = "",
            data = new { id = group.Id, groupname = group.GroupName, avatar = Avatars.OrDefault(group.Avatar) }
        });
    }

    /// <summary>删除群组：连带删除成员关系与该群聊天记录，并通知在线成员的客户端移除该群</summary>
    [HttpDelete("groups/{id}")]
    public async Task<IActionResult> DeleteGroup(string id)
    {
        if (!await IsAdminAsync()) return Forbidden();

        var group = await _db.ChatGroups.FindAsync(id);
        if (group == null)
        {
            return Ok(new { code = 1, msg = "群组不存在" });
        }

        var payload = new { id = group.Id, name = group.GroupName };

        // 先通知在线成员，再删除数据（成员此时仍在 SignalR 组内）
        await _hub.Clients.Group(ChatHub.GroupName(id)).SendAsync("GroupDeleted", payload);

        await _db.GroupMembers.Where(m => m.GroupId == id).ExecuteDeleteAsync();
        await _db.Messages.Where(m => m.ToType == "group" && m.ToId == id).ExecuteDeleteAsync();
        _db.ChatGroups.Remove(group);
        await _db.SaveChangesAsync();

        return Ok(new { code = 0, msg = "" });
    }

    /// <summary>移除群成员：删除成员关系、将其连接移出 SignalR 组，并通知本人客户端移除该群</summary>
    [HttpDelete("groups/{id}/members/{userId}")]
    public async Task<IActionResult> RemoveMember(string id, string userId)
    {
        if (!await IsAdminAsync()) return Forbidden();

        var group = await _db.ChatGroups.FindAsync(id);
        if (group == null)
        {
            return Ok(new { code = 1, msg = "群组不存在" });
        }

        var member = await _db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == userId);
        if (member == null)
        {
            return Ok(new { code = 1, msg = "该用户不在此群中" });
        }

        _db.GroupMembers.Remove(member);
        await _db.SaveChangesAsync();

        foreach (var connId in ChatHub.ConnectionIdsOf(userId))
        {
            await _hub.Groups.RemoveFromGroupAsync(connId, ChatHub.GroupName(id));
        }

        // 通知被移除者（其所有在线标签页）移除该群
        await _hub.Clients.User(userId).SendAsync("RemovedFromGroup", new { id = group.Id, name = group.GroupName });

        return Ok(new { code = 0, msg = "" });
    }
}
