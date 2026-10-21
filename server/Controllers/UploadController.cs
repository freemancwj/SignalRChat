using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LayimChat.Controllers;

[Authorize]
[ApiController]
[Route("api/upload")]
public class UploadController : ControllerBase
{
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };

    // “发送文件”允许的类型：视频、音频、图片、常用文档/压缩包
    private static readonly HashSet<string> FileExt = new(StringComparer.OrdinalIgnoreCase)
    {
        // 视频
        ".mp4", ".webm", ".mov", ".avi", ".mkv", ".flv", ".wmv", ".m4v",
        // 音频
        ".mp3", ".wav", ".ogg", ".m4a", ".flac", ".aac",
        // 图片
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".svg",
        // 文档/压缩包
        ".zip", ".rar", ".7z", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".txt", ".md",
        // 网页/脚本
        ".html", ".htm", ".js"
    };

    private const long ImageMaxSize = 10L * 1024 * 1024;   // 图片 10MB
    private const long FileMaxSize = 200L * 1024 * 1024;   // 文件（含视频）200MB

    [HttpPost("image")]
    [RequestSizeLimit(ImageMaxSize)]
    public async Task<IActionResult> Image(IFormFile file)
    {
        return await SaveAsync(file, ImageExt, ImageMaxSize);
    }

    [HttpPost("file")]
    [RequestSizeLimit(FileMaxSize)]
    [RequestFormLimits(MultipartBodyLengthLimit = FileMaxSize)]
    public async Task<IActionResult> File(IFormFile file)
    {
        return await SaveAsync(file, FileExt, FileMaxSize);
    }

    private async Task<IActionResult> SaveAsync(IFormFile? file, HashSet<string> allowedExt, long maxSize)
    {
        if (file == null || file.Length == 0)
        {
            return Ok(new { code = 1, msg = "未收到文件" });
        }
        if (file.Length > maxSize)
        {
            return Ok(new { code = 1, msg = $"文件大小超过限制（{maxSize / 1024 / 1024}MB）" });
        }

        var ext = Path.GetExtension(file.FileName);
        if (!allowedExt.Contains(ext))
        {
            return Ok(new { code = 1, msg = "不支持的文件类型" });
        }

        var dir = Path.Combine(HttpContext.RequestServices
            .GetRequiredService<IWebHostEnvironment>().WebRootPath ?? ".", "uploads");
        Directory.CreateDirectory(dir);

        var name = $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        await using (var stream = System.IO.File.Create(Path.Combine(dir, name)))
        {
            await file.CopyToAsync(stream);
        }

        return Ok(new
        {
            code = 0,
            msg = "",
            data = new { src = $"/uploads/{name}", name = file.FileName }
        });
    }
}
