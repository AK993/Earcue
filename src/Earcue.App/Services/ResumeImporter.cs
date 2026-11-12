using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Earcue.App.Services;

/// <summary>把简历文件读成纯文本：支持 .txt / .md / .docx；PDF 请复制文本后粘贴。</summary>
public static class ResumeImporter
{
    private const long MaxFileBytes = 10 * 1024 * 1024;

    public static string Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("文件不存在", path);
        }

        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes)
        {
            throw new InvalidDataException("文件超过 10 MB，请精简后再导入。");
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".txt" or ".md" or ".markdown" => File.ReadAllText(path),
            ".docx" => ReadDocx(path),
            _ => throw new NotSupportedException("支持 .txt / .md / .docx；PDF 请打开后复制文本，粘贴到简历框。")
        };
    }

    private static string ReadDocx(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("word/document.xml")
                    ?? throw new InvalidDataException("不是有效的 .docx 文件（缺少 word/document.xml）。");

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        var builder = new StringBuilder();
        foreach (var paragraph in document.Descendants(w + "p"))
        {
            var line = new StringBuilder();
            foreach (var node in paragraph.Descendants())
            {
                if (node.Name == w + "t")
                {
                    line.Append(node.Value);
                }
                else if (node.Name == w + "tab")
                {
                    line.Append('\t');
                }
                else if (node.Name == w + "br" || node.Name == w + "cr")
                {
                    line.Append('\n');
                }
            }

            var text = line.ToString().TrimEnd();
            if (text.Length > 0)
            {
                builder.AppendLine(text);
            }
        }

        return builder.ToString().Trim();
    }
}
