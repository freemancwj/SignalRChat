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

    public record SyncUserRequest(string Username, string? Password, string? Sign, string? Avatar);

    /// <summary>
    /// 同步用户：用户名不存在则创建（自动互加好友、加入所有群），已存在则更新签名/头像/密码。
    /// 适用于业务系统增删改账号时同步聊天账号。
    /// </summary>
    [HttpPost("users")]
    public async Task<IActionResult> SyncUser([FromBody] SyncUserRequest req)
    {
        var username = (req.Username ?? "").Trim();
        if (username.Length < 2 || username.Length > 20)
        {
            return Ok(new { code = 1, msg = "用户名长度需在 2-20 个字符之间" });
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        var created = false;

        if (user == null)
        {
            // 未提供密码时默认 123456
            var password = string.IsNullOrEmpty(req.Password) ? "123456" : req.Password;
            user = await UserProvisioning.CreateWithDefaultsAsync(_db, username, password);
            created = true;
        }

        if (!created)
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
        }

        return Ok(new { code = 0, msg = "", data = new { id = user.Id, username = user.Username, created } });
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
