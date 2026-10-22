using System.Security.Claims;
using LayimChat.Data;
using LayimChat.Hubs;
using LayimChat.Models;
using LayimChat.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace LayimChat.Controllers;

public record AuthRequest(string Username, string Password);

public record RegisterRequest(string Username, string Password, string? GroupId);

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly LayimDbContext _db;
    private readonly IConfiguration _config;
    private readonly IHubContext<ChatHub> _hub;

    public AuthController(LayimDbContext db, IConfiguration config, IHubContext<ChatHub> hub)
    {
        _db = db;
        _config = config;
        _hub = hub;
    }

    /// <summary>当前登录用户</summary>
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (uid == null)
        {
            return Ok(new { code = 1, msg = "未登录" });
        }

        var user = await _db.Users.FindAsync(uid);
        if (user == null)
        {
            return Ok(new { code = 1, msg = "用户不存在" });
        }

        return Ok(new
        {
            code = 0,
            msg = "",
            data = new
            {
                id = user.Id,
                username = user.Username,
                avatar = Avatars.OrDefault(user.Avatar),
                sign = user.Sign,
                admin = user.IsAdmin,
                // 客服身份标识：前端据此进入客服工作台模式（无群组、联系人=访客）
                kefu = user.Id == ServiceAgent.Id,
                guest = ServiceAgent.IsGuestName(user.Username)
            }
        });
    }

    /// <summary>群组列表（注册页选择所属群组用，无需登录）</summary>
    [HttpGet("groups")]
    public async Task<IActionResult> Groups()
    {
        var groups = await _db.ChatGroups
            .OrderBy(g => g.Id)
            .Select(g => new { id = g.Id, groupname = g.GroupName })
            .ToListAsync();
        return Ok(new { code = 0, msg = "", data = groups });
    }

    /// <summary>注册（群组可选：不选则不属于任何群，登录后可自行加群；注册后自动与现有用户互加好友）</summary>
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req)
    {
        var username = req.Username?.Trim() ?? "";
        var password = req.Password ?? "";
        var groupId = string.IsNullOrWhiteSpace(req.GroupId) ? null : req.GroupId.Trim();

        if (username.Length < 2 || username.Length > 20)
        {
            return Ok(new { code = 1, msg = "用户名长度需在 2-20 个字符之间" });
        }
        if (password.Length < 6 || password.Length > 32)
        {
            return Ok(new { code = 1, msg = "密码长度需在 6-32 个字符之间" });
        }
        if (groupId != null && !await _db.ChatGroups.AnyAsync(g => g.Id == groupId))
        {
            return Ok(new { code = 1, msg = "所选群组不存在" });
        }
        if (await _db.Users.AnyAsync(u => u.Username == username))
        {
            return Ok(new { code = 1, msg = "用户名已被占用" });
        }

        // joinAllGroupsIfNoGroupId: false —— 注册未选群组时保持"无群组"，而不是加入全部群组
        var user = await UserProvisioning.CreateWithDefaultsAsync(_db, username, password, groupId, joinAllGroupsIfNoGroupId: false);
        await SignInAsync(user);
        return Ok(new { code = 0, msg = "", data = new { id = user.Id, username = user.Username } });
    }

    /// <summary>
    /// 网站访客免登录入口：已带访客/正式登录态的直接返回当前用户；
    /// 否则创建临时访客账号（随机用户名，只与客服互为好友）并写入会话 Cookie。
    /// </summary>
    [HttpPost("guest")]
    public async Task<IActionResult> Guest()
    {
        var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (uid != null)
        {
            var cur = await _db.Users.FindAsync(uid);
            // 只有当前身份本身就是访客时才复用；若浏览器带着正式员工的登录 Cookie，
            // 仍以访客身份进入（客服面板的定位是“对外接待”，身份以访客为准）
            if (cur != null && ServiceAgent.IsGuestName(cur.Username))
            {
                return Ok(new
                {
                    code = 0,
                    msg = "",
                    data = new { id = cur.Id, username = cur.Username, guest = true }
                });
            }
        }

        var guest = await ServiceAgent.CreateGuestAsync(_db);
        await SignInAsync(guest);

        // 通知在线客服有新访客到访，客服端据此把访客加入联系人列表
        await _hub.Clients.User(ServiceAgent.Id).SendAsync("NewGuest", new
        {
            id = guest.Id,
            username = guest.Username,
            avatar = Avatars.OrDefault(guest.Avatar),
            sign = guest.Sign
        });

        return Ok(new
        {
            code = 0,
            msg = "",
            data = new { id = guest.Id, username = guest.Username, guest = true }
        });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] AuthRequest req)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == (req.Username ?? "").Trim());
        if (user == null || !PasswordCrypto.Verify(req.Password ?? "", user.PasswordHash, user.Salt))
        {
            return Ok(new { code = 1, msg = "用户名或密码错误" });
        }

        await SignInAsync(user);
        return Ok(new
        {
            code = 0,
            msg = "",
            data = new
            {
                id = user.Id,
                username = user.Username,
                admin = user.IsAdmin,
                kefu = user.Id == ServiceAgent.Id
            }
        });
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(new { code = 0, msg = "" });
    }

    public record UpdateProfileRequest(string? Username, string? NewPassword);

    /// <summary>修改当前登录用户的用户名 / 登录密码（新密码留空表示不修改）</summary>
    [Authorize]
    [HttpPost("profile")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest req)
    {
        var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (uid == null) return Ok(new { code = 1, msg = "未登录" });

        var user = await _db.Users.FindAsync(uid);
        if (user == null) return Ok(new { code = 1, msg = "用户不存在" });

        var username = req.Username?.Trim() ?? "";
        if (username.Length < 2 || username.Length > 20)
        {
            return Ok(new { code = 1, msg = "用户名长度需在 2-20 个字符之间" });
        }
        if (await _db.Users.AnyAsync(u => u.Username == username && u.Id != uid))
        {
            return Ok(new { code = 1, msg = "用户名已被占用" });
        }

        var password = req.NewPassword ?? "";
        if (password.Length > 0 && (password.Length < 6 || password.Length > 32))
        {
            return Ok(new { code = 1, msg = "密码长度需在 6-32 个字符之间" });
        }

        user.Username = username;
        if (password.Length > 0)
        {
            var (hash, salt) = PasswordCrypto.Hash(password);
            user.PasswordHash = hash;
            user.Salt = salt;
        }
        await _db.SaveChangesAsync();

        return Ok(new
        {
            code = 0,
            msg = "",
            data = new { id = user.Id, username = user.Username, passwordChanged = password.Length > 0 }
        });
    }

    /// <summary>
    /// 第三方系统单点登录（SSO）。
    /// ticket = Base64($"{username}|{unixSeconds}|{hmacHex}")，其中
    /// hmacHex = HMACSHA256(Integration:SsoSecret, $"{username}|{unixSeconds}") 的小写十六进制。
    /// 时间戳有效期 5 分钟。校验通过后建立 Cookie 登录态并跳转聊天首页；用户不存在时自动开户。
    /// </summary>
    [HttpGet("sso")]
    public async Task<IActionResult> Sso([FromQuery] string ticket)
    {
        IActionResult Fail(string msg) => Content(
            $"<html><head><meta charset=\"utf-8\"></head><body style=\"font-family:sans-serif;text-align:center;padding-top:80px\">" +
            $"<h3>单点登录失败</h3><p>{System.Net.WebUtility.HtmlEncode(msg)}</p></body></html>",
            "text/html");

        var secret = _config["Integration:SsoSecret"];
        if (string.IsNullOrEmpty(secret))
        {
            return Fail("服务端未配置 Integration:SsoSecret");
        }

        string raw;
        try
        {
            // 兼容 URL 安全 Base64（- 代 +，_ 代 /），并补齐 padding
            var b64 = (ticket ?? "").Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight((b64.Length + 3) / 4 * 4, '=');
            raw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        catch
        {
            return Fail("ticket 不是合法的 Base64");
        }

        var parts = raw.Split('|');
        if (parts.Length != 3)
        {
            return Fail("ticket 格式错误，应为 base64(用户名|时间戳|签名)");
        }
        var (username, tsStr, sign) = (parts[0].Trim(), parts[1].Trim(), parts[2].Trim());

        if (!long.TryParse(tsStr, out var ts) ||
            Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) > 300)
        {
            return Fail("ticket 已过期（有效期 5 分钟），请从业务系统重新进入");
        }

        var expected = HmacHex(secret, $"{username}|{tsStr}");
        if (!FixedTimeEquals(expected, sign))
        {
            return Fail("ticket 签名无效");
        }

        if (username.Length < 2 || username.Length > 20)
        {
            return Fail("用户名长度需在 2-20 个字符之间");
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null)
        {
            // 自动开户：随机密码（SSO 用户无需本地密码），自动加好友/入群
            user = await UserProvisioning.CreateWithDefaultsAsync(_db, username, Guid.NewGuid().ToString("N"));
        }

        await SignInAsync(user);
        // embed=1：内嵌场景，跳转后在 iframe 中铺满面板
        var target = HttpContext.Request.Query["embed"].ToString() == "1"
            ? "/index.html?embed=1"
            : "/index.html";
        return Redirect(target);
    }

    private static string HmacHex(string secret, string message)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(message))).ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = System.Text.Encoding.UTF8.GetBytes(a);
        var bb = System.Text.Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private async Task SignInAsync(User user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.Username)
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        // IsPersistent=false：写入会话级 Cookie（不设置 Expires），关闭浏览器后登录态即失效，
        // 下次打开站点需要重新登录；同一浏览器的其它标签页在关闭前仍共享该登录态。
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false });
    }
}
