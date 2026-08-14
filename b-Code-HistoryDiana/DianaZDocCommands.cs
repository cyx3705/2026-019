using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Storage;

namespace HistoryDiana;

/// <summary>
/// 跨项目说明书只从各项目 <c>z-*</c> 读取。现场扫描生成通道，避免手维护清单漂移。
/// </summary>
internal static class DianaZDocCommands
{
    private const int MaximumDocumentBytes = 512 * 1024;
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt",
    };

    public static void Register(CommandRegistry registry, ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(settings);

        var channels = Discover(settings);
        registry.Register(new CommandDescriptor
        {
            Name = "diana.docs.catalog",
            Domain = "HistoryDiana",
            CommandClass = "docs",
            Summary = "列出全部 z 文档通道与文件名；跨项目读文档前必须先执行本命令，把索引留在对话里",
            Example = "diana.docs.catalog",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var snapshot = Discover(settings);
                return CommandResult.Ok(RenderCatalog(snapshot), snapshot);
            }),
        });

        foreach (var channel in channels)
        {
            var captured = channel;
            registry.Register(new CommandDescriptor
            {
                Name = $"diana.docs.{captured.Id}",
                Domain = "HistoryDiana",
                CommandClass = "docs",
                Summary = $"查看 {captured.Module} 正式 z 快照中的已发布 Markdown；省略 file 只列出本通道",
                Example = captured.Documents.Count == 0
                    ? $"diana.docs.{captured.Id}"
                    : $"diana.docs.{captured.Id} file={captured.Documents[0].Path}",
                Readonly = true,
                Parameters =
                [
                    new ParameterSpec
                    {
                        Name = "file",
                        Description = "z 内相对路径；省略则只列出本通道文档，不读正文",
                        Position = 0,
                    },
                ],
                Handler = CommandDescriptor.Sync(context => OpenChannel(settings, captured.Id, context.GetString("file"))),
            });
        }
    }

    private static CommandResult OpenChannel(ISettingsService settings, string channelId, string? file)
    {
        var channel = Discover(settings).FirstOrDefault(item =>
            item.Id.Equals(channelId, StringComparison.OrdinalIgnoreCase));
        if (channel == null)
            return CommandResult.Fail($"z 通道不存在或已消失: {channelId}。请先执行 diana.docs.catalog。");

        var relative = (file ?? "").Trim().Replace('\\', '/');
        if (relative.Length == 0)
        {
            return CommandResult.Ok(RenderChannel(channel), channel);
        }

        var document = channel.Documents.FirstOrDefault(item =>
            item.Path.Equals(relative, StringComparison.OrdinalIgnoreCase));
        if (document == null)
        {
            return CommandResult.Fail(
                $"{channel.Id} 没有登记 {relative}。本通道文件: {string.Join(", ", channel.Documents.Select(item => item.Path))}");
        }

        var fullPath = ResolveInside(channel.PackagePath, relative);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            return CommandResult.Fail($"文件不在 z 快照中: {relative}");
        if (info.Length > MaximumDocumentBytes)
            return CommandResult.Fail($"{relative} 超过 512 KiB 上限");

        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath)));
        if (!actual.Equals(document.Sha256, StringComparison.OrdinalIgnoreCase))
            return CommandResult.Fail($"{relative} 的 SHA-256 与 SHA256SUMS 不一致，已拒绝读取");

        return CommandResult.Ok(relative, new
        {
            channel.Id,
            channel.Module,
            channel.Version,
            Path = relative,
            Sha256 = document.Sha256,
            Content = File.ReadAllText(fullPath),
        });
    }

    private static IReadOnlyList<ZDocChannel> Discover(ISettingsService settings)
    {
        var rootValue = DianaLibraryRoot.Resolve(settings);
        if (!Directory.Exists(rootValue))
            return [];

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootValue));
        var channels = new List<ZDocChannel>();
        IEnumerable<string> projects;
        try
        {
            projects = Directory.GetDirectories(root, "*-History*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        foreach (var project in projects)
        {
            IEnumerable<string> packages;
            try
            {
                packages = Directory.GetDirectories(project, "z-*");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var package in packages)
                TryAddChannel(package, Path.GetFileName(project), channels);
        }

        var duplicates = channels
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return channels
            .Where(item => !duplicates.Contains(item.Id))
            .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void TryAddChannel(string package, string projectDirectory, List<ZDocChannel> channels)
    {
        var sumsPath = Path.Combine(package, "SHA256SUMS");
        if (!File.Exists(sumsPath))
            return;

        var moduleName = ReadIdentity(package, Path.GetFileName(package));
        var id = ModuleDomainNaming.ToDomain(moduleName);
        if (string.IsNullOrWhiteSpace(id))
            return;

        Dictionary<string, string> hashes;
        try
        {
            hashes = ReadChecksums(sumsPath);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var documents = new List<ZDocFile>();
        foreach (var (relative, expected) in hashes)
        {
            if (!DocumentExtensions.Contains(Path.GetExtension(relative)))
                continue;
            string fullPath;
            try
            {
                fullPath = ResolveInside(package, relative);
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            if (!File.Exists(fullPath))
                continue;
            documents.Add(new ZDocFile(relative, expected, new FileInfo(fullPath).Length));
        }

        var version = ReadVersion(package);
        channels.Add(new ZDocChannel(
            id,
            moduleName,
            version,
            projectDirectory,
            Path.GetFileName(package),
            Path.GetFullPath(package),
            documents.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList()));
    }

    private static string ReadIdentity(string package, string folderName)
    {
        var moduleManifest = Path.Combine(package, "module.manifest.json");
        if (File.Exists(moduleManifest))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(moduleManifest));
                if (document.RootElement.TryGetProperty("name", out var name)
                    && name.GetString() is { Length: > 0 } moduleName)
                {
                    return moduleName;
                }
            }
            catch (JsonException)
            {
            }
        }

        var hostManifest = Path.Combine(package, "manifest.json");
        if (File.Exists(hostManifest))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(hostManifest));
                if (document.RootElement.TryGetProperty("product", out var product)
                    && product.GetString() is { Length: > 0 } productName)
                {
                    return productName;
                }
            }
            catch (JsonException)
            {
            }
        }

        return folderName.StartsWith("z-", StringComparison.OrdinalIgnoreCase)
            ? folderName[2..]
            : folderName;
    }

    private static string ReadVersion(string package)
    {
        foreach (var (file, key) in new[]
        {
            ("module.manifest.json", "version"),
            ("manifest.json", "version"),
        })
        {
            var path = Path.Combine(package, file);
            if (!File.Exists(path))
                continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty(key, out var version)
                    && version.GetString() is { Length: > 0 } value)
                {
                    return value;
                }
            }
            catch (JsonException)
            {
            }
        }

        return "unknown";
    }

    private static Dictionary<string, string> ReadChecksums(string sumsPath)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(sumsPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var match = Regex.Match(line, "^(?<hash>[0-9A-Fa-f]{64}) [ *](?<file>.+)$");
            if (!match.Success)
                throw new InvalidOperationException($"Invalid SHA256SUMS line: {line}");
            var file = match.Groups["file"].Value.Trim().Replace('\\', '/');
            if (hashes.ContainsKey(file))
                throw new InvalidOperationException($"Duplicate SHA256SUMS entry: {file}");
            hashes[file] = match.Groups["hash"].Value.ToUpperInvariant();
        }

        return hashes;
    }

    private static string ResolveInside(string package, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)
            || Path.IsPathRooted(relative)
            || relative.Split('/', '\\').Any(part => part is "." or ".."))
        {
            throw new InvalidOperationException($"路径必须是 z 目录内的相对路径: {relative}");
        }

        var root = Path.GetFullPath(package).TrimEnd(Path.DirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"路径越出 Z 目录: {relative}");
        return resolved;
    }

    private static string RenderCatalog(IReadOnlyList<ZDocChannel> channels)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("Z 文档通道（现场扫描 SHA256SUMS，不是手维护清单）");
        builder.AppendLine("跨项目读说明书：先把本索引留在对话中，再调用其中一个 diana.docs.<通道>。省略 file 只列出该通道。");
        builder.AppendLine("若列出了通道但命令尚未登记，执行 vulcan.module.reload 让 Diana 按当前 z 重新附着。");
        if (channels.Count == 0)
        {
            builder.Append("当前工作树根下没有含 SHA256SUMS 的 z-* 快照。");
            return builder.ToString();
        }

        foreach (var channel in channels)
        {
            builder.AppendLine();
            builder.Append($"- {channel.Id}  diana.docs.{channel.Id}  {channel.Folder}  {channel.Module} {channel.Version}");
            if (channel.Documents.Count == 0)
            {
                builder.AppendLine("  （无可读 Markdown）");
                continue;
            }

            builder.AppendLine();
            foreach (var document in channel.Documents)
                builder.AppendLine($"    - {document.Path}");
        }

        return builder.ToString();
    }

    private static string RenderChannel(ZDocChannel channel)
    {
        if (channel.Documents.Count == 0)
            return $"{channel.Id}: {channel.Folder} 没有已登记的 Markdown。";
        return $"{channel.Id} 可按 file 读取:{Environment.NewLine}" +
               string.Join(Environment.NewLine, channel.Documents.Select(item => $"  {item.Path}"));
    }

    private sealed record ZDocFile(string Path, string Sha256, long Bytes);

    private sealed record ZDocChannel(
        string Id,
        string Module,
        string Version,
        string Project,
        string Folder,
        string PackagePath,
        IReadOnlyList<ZDocFile> Documents);
}
