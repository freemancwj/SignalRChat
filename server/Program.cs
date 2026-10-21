using LayimChat.Data;
using LayimChat.Hubs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var contentRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
var webRoot = Path.GetFullPath(Path.Combine(contentRoot, ".."));

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = contentRoot,
    WebRootPath = webRoot
});

// 数据库提供程序切换：appsettings.json → Database:Provider = Sqlite / SqlServer / MySql
// 也可通过环境变量 Database__Provider 覆盖
var dbSection = builder.Configuration.GetSection("Database");
var dbProvider = (dbSection["Provider"] ?? "Sqlite").Trim();

builder.Services.AddDbContext<LayimDbContext>(options =>
{
    switch (dbProvider.ToLowerInvariant())
    {
        case "sqlserver":
            // 本机为 SQL Server 2008 R2（兼容级别 100），需告知 EF 生成兼容语法
            // 否则 EF Core 8 默认按高版本生成 CTE/OPENJSON 等新语法会报 "关键字 'WITH' 附近有语法错误"
            options.UseSqlServer(dbSection["SqlServer"], o => o.UseCompatibilityLevel(100));
            break;
        case "mysql":
            var connStr = dbSection["MySql"]!;
            // AutoDetect 会在启动时连接数据库探测版本
            options.UseMySql(connStr, ServerVersion.AutoDetect(connStr));
            break;
        default:
            options.UseSqlite(dbSection["Sqlite"]);
            break;
    }
});
Console.WriteLine($"[LayimChat] 数据库提供程序: {dbProvider}");

builder.Services.AddSignalR();

builder.Services.AddControllers();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = ".layim.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        // API 场景：未认证时直接返回 401，不做 302 跳转
        options.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization();

// 将 DataProtection 密钥持久化到项目目录（避免依赖用户目录权限，且重启后登录态不失效）
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(contentRoot, "keys")))
    .SetApplicationName("LayimChat");

var app = builder.Build();

app.UseDefaultFiles();
// 静态文件禁用启发式缓存，避免修改后浏览器仍用旧页面
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers["Cache-Control"] = "no-cache, max-age=0";

        // /uploads 中可执行的文件类型（网页/脚本/SVG）强制按附件下载，防止在本站域内被执行（XSS）
        var ext = Path.GetExtension(ctx.File.Name);
        if (ctx.Context.Request.Path.StartsWithSegments("/uploads") &&
            ext is ".html" or ".htm" or ".js" or ".svg")
        {
            ctx.Context.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{Uri.EscapeDataString(ctx.File.Name)}\"";
            ctx.Context.Response.ContentType = "application/octet-stream";
        }
    }
});

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<ChatHub>("/chathub");

DbInitializer.Initialize(app);

app.Run();
