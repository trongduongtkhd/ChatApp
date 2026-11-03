using System.IO.Compression;
using System.Text;

namespace ChatApp.ChatService.Services;

public sealed class AttachmentException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

public static class AttachmentValidation
{
    public const long MaxFile = 25L * 1024 * 1024;
    public const long MaxImage = 10L * 1024 * 1024;
    public const long MaxTotal = 50L * 1024 * 1024;
    public static string CleanName(string name)
    {
        name = name.Replace('\\', '/').Split('/').Last().Trim();
        name = new string(name.Where(c => !char.IsControl(c)).ToArray());
        if (string.IsNullOrWhiteSpace(name) || name.Length > 180)
            throw new AttachmentException("Tên file không hợp lệ hoặc dài quá 180 ký tự.");
        return name;
    }
    public static async Task<(string Type, string Kind)> ValidateAsync(Stream data, string name, CancellationToken ct)
    {
        if (data.Length is <= 0 or > MaxFile) throw new AttachmentException("File phải có dữ liệu và không vượt 25 MiB.", 413);
        var head = new byte[16]; data.Position = 0; var read = await data.ReadAsync(head, ct); data.Position = 0;
        var ext = Path.GetExtension(name).ToLowerInvariant();
        var type = ext switch
        {
            ".jpg" or ".jpeg" when read >= 3 && head[0] == 255 && head[1] == 216 && head[2] == 255 => "image/jpeg",
            ".png" when read >= 8 && head.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}) => "image/png",
            ".webp" when read >= 12 && Encoding.ASCII.GetString(head, 0, 4) == "RIFF" && Encoding.ASCII.GetString(head, 8, 4) == "WEBP" => "image/webp",
            ".pdf" when read >= 5 && Encoding.ASCII.GetString(head, 0, 5) == "%PDF-" => "application/pdf",
            ".txt" => "text/plain",
            ".zip" or ".docx" or ".xlsx" or ".pptx" => "zip",
            _ => throw new AttachmentException("Định dạng không hỗ trợ hoặc nội dung không khớp loại file.")
        };
        if (type == "text/plain")
        {
            try
            {
                using var reader = new StreamReader(data, new UTF8Encoding(false, true), false, 4096, true);
                var buffer = new char[4096]; int n;
                while ((n = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
                    if (buffer.AsSpan(0, n).Contains('\0')) throw new AttachmentException("File TXT chứa dữ liệu nhị phân.");
            }
            catch (DecoderFallbackException) { throw new AttachmentException("File TXT phải dùng UTF-8."); }
        }
        if (type == "zip")
        {
            try
            {
                using var zip = new ZipArchive(data, ZipArchiveMode.Read, true);
                if (zip.Entries.Count > 10000) throw new AttachmentException("File nén có quá nhiều mục.");
                var entry = ext switch { ".docx" => "word/document.xml", ".xlsx" => "xl/workbook.xml", ".pptx" => "ppt/presentation.xml", _ => null };
                if (entry is not null && (zip.GetEntry("[Content_Types].xml") is null || zip.GetEntry(entry) is null))
                    throw new AttachmentException("Nội dung không đúng định dạng Office.");
            }
            catch (InvalidDataException) { throw new AttachmentException("File nén/Office không hợp lệ."); }
            type = ext switch
            {
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                _ => "application/zip"
            };
        }
        var kind = type.StartsWith("image/") ? "image" : "file";
        if (kind == "image" && data.Length > MaxImage) throw new AttachmentException("Ảnh không vượt 10 MiB.", 413);
        data.Position = 0; return (type, kind);
    }
}
