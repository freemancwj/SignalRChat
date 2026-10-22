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

[Authorize]
[ApiController]
[Route("api/layim")]
public class LayimController : ControllerBase
{
    private readonly LayimDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly IHubContext<ChatHub> _hub;

    public LayimController(LayimDbContext db, IWebHostEnvironment env, IHubContext<ChatHub> hub)
    {
        _db = db;
        _env = env;
        _hub = hub;
    }

    private string MyId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    /// <summary>LayIM 初始化数据（好友列表 + 我的群组 + 我的资料）</summary>
    [HttpGet("init")]
    public async Task<IActionResult> Init()
    {
        var me = await _db.Users.FindAsync(MyId);
        if (me == null)
        {
            return Unauthorized();
        }

        // 网站访客（免登录临时用户）：通讯录只有固定客服一个联系人，没有任何群组
        if (ServiceAgent.IsGuestName(me.Username))
        {
            var agent = await _db.Users.FindAsync(ServiceAgent.Id);
            if (agent == null)
            {
                return Ok(new { code = 1, msg = "客服账号不存在" });
            }
            return Ok(new
            {
                code = 0,
                msg = "",
                data = new
                {
                    mine = new
                    {
                        username = me.Username,
                        id = me.Id,
                        status = me.Status,
                        sign = me.Sign,
                        avatar = Avatars.OrDefault(me.Avatar),
                        admin = false,
                        guest = true // 前端据此进入访客模式（精简界面、自动打开客服会话）
                    },
                    friend = new[]
                    {
                        new
                        {
                            groupname = "在线客服",
                            id = "kefu",
                            list = new[]
                            {
                                new
                                {
                                    username = ServiceAgent.DisplayName,
                                    id = agent.Id,
                                    avatar = Avatars.OrDefault(agent.Avatar),
                                    sign = agent.Sign,
                                    status = "online" // 客服对访客恒在线
                                }
                            }
                        }
                    },
                    group = Array.Empty<object>()
                }
            });
        }

        // 网站客服：联系人是全部访客（最近有消息的排在前面），没有群组
        if (me.Id == ServiceAgent.Id)
        {
            var guests = await _db.Users
                .Where(u => u.Username.StartsWith(ServiceAgent.GuestPrefix))
                .ToListAsync();

            // 按「最近一条消息时间」排序访客，方便客服优先处理新会话
            var recent = await _db.Messages
                .Where(m => m.ToType == "friend"
                    && (m.FromUserId == ServiceAgent.Id || m.ToId == ServiceAgent.Id))
                .Select(m => new { m.FromUserId, m.ToId, m.CreatedAt })
                .ToListAsync();
            var lastTimes = recent
                .Select(m => new
                {
                    Partner = m.FromUserId == ServiceAgent.Id ? m.ToId : m.FromUserId,
                    m.CreatedAt
                })
                .GroupBy(x => x.Partner)
                .ToDictionary(g => g.Key, g => g.Max(x => x.CreatedAt));

            var guestList = guests
                .OrderByDescending(u => lastTimes.TryGetValue(u.Id, out var t) ? t : DateTime.MinValue)
                .ThenBy(u => u.Username)
                .Select(u => new
                {
                    username = u.Username,
                    id = u.Id,
                    avatar = Avatars.OrDefault(u.Avatar),
                    sign = u.Sign,
                    status = ChatHub.IsOnline(u.Id) ? "online" : "offline"
                })
                .ToList();

            return Ok(new
            {
                code = 0,
                msg = "",
                data = new
                {
                    mine = new
                    {
                        username = me.Username,
                        id = me.Id,
                        status = me.Status,
                        sign = me.Sign,
                        avatar = Avatars.OrDefault(me.Avatar),
                        admin = me.IsAdmin,
                        kefu = true // 前端据此进入客服工作台模式（无群组）
                    },
                    friend = new[]
                    {
                        new { groupname = "访客", id = "guests", list = guestList }
                    },
                    group = Array.Empty<object>()
                }
            });
        }

        // 通讯录按群组（部门）组织：第一级为群组，展开显示该群组下的所有成员
        var allGroups = await _db.ChatGroups
            .Include(g => g.Members)
            .OrderBy(g => g.Id)
            .ToListAsync();

        // 一次性取所有成员用户信息，避免 N+1 查询
        var memberIds = allGroups.SelectMany(g => g.Members.Select(m => m.UserId)).Distinct().ToList();
        var memberUsers = await _db.Users
            .Where(u => memberIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id);

        var myGroups = await _db.GroupMembers
            .Where(m => m.UserId == MyId)
            .Join(_db.ChatGroups, m => m.GroupId, g => g.Id, (m, g) => g)
            .ToListAsync();

        return Ok(new
        {
            code = 0,
            msg = "",
            data = new
            {
                mine = new
                {
                    username = me.Username,
                    id = me.Id,
                    status = me.Status,
                    sign = me.Sign,
                    avatar = Avatars.OrDefault(me.Avatar),
                    // 前端据此显示「群组管理」入口
                    admin = me.IsAdmin
                },
                friend = allGroups.Select(g => new
                {
                    groupname = g.GroupName,
                    id = g.Id,
                    list = g.Members
                        .Where(m => m.UserId != MyId && memberUsers.ContainsKey(m.UserId))
                        .Select(m =>
                        {
                            var u = memberUsers[m.UserId];
                            return new
                            {
                                username = u.Username,
                                id = u.Id,
                                avatar = Avatars.OrDefault(u.Avatar),
                                sign = u.Sign,
                                // online/offline：前端据此渲染在线标识，离线好友头像置灰
                                status = ChatHub.IsOnline(u.Id) ? "online" : "offline"
                            };
                        })
                        // 在线用户排在所有离线用户之前
                        .OrderByDescending(m => m.status == "online")
                        .ThenBy(m => m.username)
                }),
                group = myGroups.Select(g => new { groupname = g.GroupName, id = g.Id, avatar = Avatars.OrDefault(g.Avatar) })
            }
        });
    }

    /// <summary>群成员列表</summary>
    [HttpGet("members")]
    public async Task<IActionResult> Members([FromQuery] string id)
    {
        // 先落库查询，再在内存中归一化头像（Avatars.OrDefault 无法被 EF 翻译成 SQL）
        var rows = await _db.GroupMembers
            .Where(m => m.GroupId == id)
            .Join(_db.Users, m => m.UserId, u => u.Id, (m, u) => u)
            .Select(u => new { username = u.Username, id = u.Id, avatar = u.Avatar, sign = u.Sign })
            .ToListAsync();

        var members = rows
            .Select(u => new { u.username, u.id, avatar = Avatars.OrDefault(u.avatar), u.sign })
            .ToList();

        return Ok(new { code = 0, msg = "", data = new { list = members } });
    }

    /// <summary>历史消息（chatlog 弹窗使用）；带 keyword 时按关键字搜索</summary>
    [HttpGet("chatlog")]
    public async Task<IActionResult> ChatLog([FromQuery] string id, [FromQuery] string type, [FromQuery] string? keyword)
    {

        IQueryable<Message> query;
        if (type == "friend")
        {
            var me = MyId;
            query = _db.Messages.Where(m =>
                m.ToType == "friend" &&
                ((m.FromUserId == me && m.ToId == id) || (m.FromUserId == id && m.ToId == me)));
        }
        else if (type == "group")
        {
            // 仅群成员可查看群历史
            var isMember = await _db.GroupMembers.AnyAsync(m => m.GroupId == id && m.UserId == MyId);
            if (!isMember)
            {
                return Ok(new { code = 1, msg = "不是该群成员" });
            }
            query = _db.Messages.Where(m => m.ToType == "group" && m.ToId == id);
        }
        else
        {
            return Ok(new { code = 1, msg = "不支持的会话类型" });
        }

        // 搜索模式：按关键字过滤整段会话（历史不止最近200条，搜索时放宽区间）
        var kw = keyword?.Trim();
        var isSearch = !string.IsNullOrEmpty(kw);
        if (isSearch)
        {
            query = query.Where(m => m.Content.Contains(kw!));
        }

        var total = await query.CountAsync(); // 匹配总数（仅在搜索时展示给用户）
        var take = isSearch ? 300 : 200;

        // 取最近 N 条，按时间升序返回
        var rows = await query
            .OrderByDescending(m => m.CreatedAt)
            .Take(take)
            .OrderBy(m => m.CreatedAt)
            .Join(_db.Users, m => m.FromUserId, u => u.Id, (m, u) => new
            {
                username = u.Username,
                id = u.Id,
                avatar = u.Avatar,
                timestamp = new DateTimeOffset(DateTime.SpecifyKind(m.CreatedAt, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                content = m.Content
            })
            .ToListAsync();

        // 内存中归一化头像（上面是 EF 投影，无法直接调用 Avatars.OrDefault）
        var messages = rows
            .Select(m => new
            {
                m.username,
                m.id,
                avatar = Avatars.OrDefault(m.avatar),
                m.timestamp,
                m.content
            })
            .ToList();

        return Ok(new { code = 0, msg = "", data = messages, total });
    }

    /// <summary>修改签名</summary>
    [HttpPost("sign")]
    public async Task<IActionResult> Sign([FromBody] SignRequest req)
    {
        var user = await _db.Users.FindAsync(MyId);
        if (user == null)
        {
            return Unauthorized();
        }
        user.Sign = req.Sign?.Trim() ?? "";
        await _db.SaveChangesAsync();
        return Ok(new { code = 0, msg = "" });
    }

    /// <summary>更换头像（multipart 上传图片，更新 Users.Avatar）</summary>
    [HttpPost("avatar")]
    public async Task<IActionResult> Avatar(IFormFile file)
    {
        if (file == null || file.Length == 0)
        {
            return Ok(new { code = 1, msg = "请选择图片文件" });
        }
        if (file.Length > 2 * 1024 * 1024)
        {
            return Ok(new { code = 1, msg = "头像图片不能超过 2MB" });
        }
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        string[] allowed = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };
        if (!allowed.Contains(ext))
        {
            return Ok(new { code = 1, msg = "仅支持 jpg/png/gif/bmp/webp 图片" });
        }

        var user = await _db.Users.FindAsync(MyId);
        if (user == null)
        {
            return Unauthorized();
        }

        // 保存新头像
        var dir = Path.Combine(_env.WebRootPath, "uploads");
        Directory.CreateDirectory(dir);
        var name = $"avatar-{MyId}-{DateTime.Now:yyyyMMddHHmmss}{ext}";
        var fullPath = Path.Combine(dir, name);
        await using (var fs = System.IO.File.Create(fullPath))
        {
            await file.CopyToAsync(fs);
        }

        // 删除旧头像文件（仅清理本系统上传的头像）
        if (!string.IsNullOrEmpty(user.Avatar) && user.Avatar.Contains("/uploads/avatar-"))
        {
            var oldPath = Path.Combine(dir, Path.GetFileName(user.Avatar));
            if (System.IO.File.Exists(oldPath))
            {
                System.IO.File.Delete(oldPath);
            }
        }

        user.Avatar = "/uploads/" + name;
        await _db.SaveChangesAsync();
        return Ok(new { code = 0, msg = "", data = new { src = user.Avatar } });
    }

    /// <summary>恢复默认头像（清空自定义头像，并删除已上传的头像文件）</summary>
    [HttpPost("avatar/reset")]
    public async Task<IActionResult> ResetAvatar()
    {
        var user = await _db.Users.FindAsync(MyId);
        if (user == null)
        {
            return Unauthorized();
        }

        if (!string.IsNullOrEmpty(user.Avatar) && user.Avatar.Contains("/uploads/avatar-"))
        {
            var oldPath = Path.Combine(_env.WebRootPath, "uploads", Path.GetFileName(user.Avatar));
            if (System.IO.File.Exists(oldPath))
            {
                System.IO.File.Delete(oldPath);
            }
        }

        user.Avatar = "";
        await _db.SaveChangesAsync();
        return Ok(new { code = 0, msg = "" });
    }

    public record SignRequest(string Sign);

    public record GroupRequest(string Id);

    /// <summary>全部群组（含是否已加入、成员数），供主面板「加群/退群」使用</summary>
    [HttpGet("groups")]
    public async Task<IActionResult> AllGroups()
    {
        var myId = MyId;
        var rows = await _db.ChatGroups
            .OrderBy(g => g.Id)
            .Select(g => new
            {
                id = g.Id,
                groupname = g.GroupName,
                avatar = g.Avatar,
                memberCount = g.Members.Count,
                joined = g.Members.Any(m => m.UserId == myId)
            })
            .ToListAsync();

        return Ok(new
        {
            code = 0,
            msg = "",
            data = rows.Select(g => new
            {
                g.id,
                g.groupname,
                avatar = Avatars.OrDefault(g.avatar),
                g.memberCount,
                g.joined
            })
        });
    }

    /// <summary>加入群组：写 GroupMembers，并把当前用户所有在线连接加入 SignalR 组（立即生效，无需重连）</summary>
    [HttpPost("groups/join")]
    public async Task<IActionResult> JoinGroup([FromBody] GroupRequest req)
    {
        var myId = MyId;
        var groupId = req.Id ?? "";
        var group = await _db.ChatGroups.FindAsync(groupId);
        if (group == null)
        {
            return Ok(new { code = 1, msg = "群组不存在" });
        }

        if (!await _db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == myId))
        {
            _db.GroupMembers.Add(new GroupMember { GroupId = groupId, UserId = myId });
            await _db.SaveChangesAsync();
        }

        foreach (var connId in ChatHub.ConnectionIdsOf(myId))
        {
            await _hub.Groups.AddToGroupAsync(connId, ChatHub.GroupName(groupId));
        }

        return Ok(new
        {
            code = 0,
            msg = "",
            data = new { id = group.Id, groupname = group.GroupName, avatar = Avatars.OrDefault(group.Avatar) }
        });
    }

    /// <summary>退出群组：删除 GroupMembers 记录，并把当前用户所有在线连接移出 SignalR 组（立即停止接收群消息）</summary>
    [HttpPost("groups/leave")]
    public async Task<IActionResult> LeaveGroup([FromBody] GroupRequest req)
    {
        var myId = MyId;
        var groupId = req.Id ?? "";

        var member = await _db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == myId);
        if (member != null)
        {
            _db.GroupMembers.Remove(member);
            await _db.SaveChangesAsync();
        }

        foreach (var connId in ChatHub.ConnectionIdsOf(myId))
        {
            await _hub.Groups.RemoveFromGroupAsync(connId, ChatHub.GroupName(groupId));
        }

        return Ok(new { code = 0, msg = "" });
    }
}
