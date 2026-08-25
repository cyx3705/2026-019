using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HistoryVulcan.Core.Commands;

namespace HistoryDiana;

/// <summary>
/// 跨项目说明书只从各项目 z-Publish 根候选读取，避免把历史包或旧发布区当成当前真值。
/// 现场扫描生成通道，避免手维护清单漂移。
/// </summary>
internal static class DianaZDocCommands
{
    private const int MaximumDocumentBytes = 512 * 1024;
    private const int OutlineWithoutHeadingBytes = 12 * 1024;
    private static readonly Regex VersionToken = new(@"^\d+\.\d+\.\d+\b", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt",
    };

    public static void Register(CommandRegistry registry, Func<string> projectLibraryRoot)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(projectLibraryRoot);

        var channels = Discover(projectLibraryRoot);
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
                var snapshot = Discover(projectLibraryRoot);
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
                Summary = $"查看 {captured.Module} 已发布 z 快照中的 Markdown；省略 file 只列出本通道",
                Example = captured.Documents.Count == 0
                    ? $"diana.docs.{captured.Id}"
                    : $"diana.docs.{captured.Id} file={captured.Documents[0].Path}",
                Readonly = true,
                Parameters =
                [
                    new ParameterSpec
                    {
                        Name = "file",
                        Description = "z 内相对路径或唯一文件名；省略则只列出本通道文档，不读正文",
                        Position = 0,
                    },
                    new ParameterSpec
                    {
                        Name = "heading",
                        Description = "只返回该 Markdown 标题的一节，或 x.y.z 版本条目；长文应带上以免整篇进对话",
                    },
                ],
                Handler = CommandDescriptor.Sync(context => OpenChannel(
                    projectLibraryRoot, captured.Id, context.GetString("file"), context.GetString("heading"))),
            });
        }
    }

    private static CommandResult OpenChannel(Func<string> projectLibraryRoot, string channelId, string? file, string? heading)
    {
        var channel = Discover(projectLibraryRoot).FirstOrDefault(item =>
            item.Id.Equals(channelId, StringComparison.OrdinalIgnoreCase));
        if (channel == null)
            return CommandResult.Fail($"z 通道不存在或已消失: {channelId}。请先执行 diana.docs.catalog。");

        var relative = (file ?? "").Trim().Replace('\\', '/');
        if (relative.Length == 0)
        {
            return CommandResult.Ok(RenderChannel(channel), channel);
        }

        var (document, resolveError) = ResolveDocument(channel, relative);
        if (document == null)
            return CommandResult.Fail(resolveError ?? $"{channel.Id} 没有登记 {relative}。");

        var fullPath = ResolveInside(channel.PackagePath, document.Path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            return CommandResult.Fail($"文件不在 z 快照中: {document.Path}");
        if (info.Length > MaximumDocumentBytes)
            return CommandResult.Fail($"{document.Path} 超过 512 KiB 上限");

        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath)));
        if (!actual.Equals(document.Sha256, StringComparison.OrdinalIgnoreCase))
            return CommandResult.Fail($"{document.Path} 的 SHA-256 与 SHA256SUMS 不一致，已拒绝读取");

        var markdown = File.ReadAllText(fullPath);
        var headingValue = (heading ?? "").Trim();
        if (headingValue.Length > 0)
        {
            var section = ExtractHeading(markdown, headingValue);
            if (section == null)
            {
                var headings = ListHeadings(markdown);
                var versions = ListVersions(markdown);
                var available = headings.Count == 0
                    ? "没有 Markdown 标题"
                    : $"可用章节: {string.Join("、", headings)}";
                if (versions.Count > 0)
                    available += $"。可用版本: {string.Join("、", versions)}";
                return CommandResult.Fail($"{document.Path} 没有标题或版本「{headingValue}」。{available}");
            }

            return CommandResult.Ok(section, new
            {
                channel.Id,
                channel.Module,
                channel.Version,
                Path = document.Path,
                Heading = headingValue,
                Sha256 = document.Sha256,
                Content = section,
            });
        }

        if (info.Length > OutlineWithoutHeadingBytes)
        {
            var outline = RenderOutline(document.Path, info.Length, markdown);
            return CommandResult.Ok(outline, new
            {
                channel.Id,
                channel.Module,
                channel.Version,
                Path = document.Path,
                Outline = true,
                Headings = ListHeadings(markdown),
                Sha256 = document.Sha256,
            });
        }

        return CommandResult.Ok(markdown, new
        {
            channel.Id,
            channel.Module,
            channel.Version,
            Path = document.Path,
            Sha256 = document.Sha256,
            Content = markdown,
        });
    }

    private static (ZDocFile? Document, string? Error) ResolveDocument(ZDocChannel channel, string relative)
    {
        var exact = channel.Documents.FirstOrDefault(item =>
            item.Path.Equals(relative, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return (exact, null);

        var name = Path.GetFileName(relative);
        var matches = channel.Documents
            .Where(item => Path.GetFileName(item.Path).Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 1)
            return (matches[0], null);
        if (matches.Count > 1)
        {
            return (null,
                $"{channel.Id} 有多份同名文件 {name}，请用 catalog 里的完整路径: {string.Join(", ", matches.Select(item => item.Path))}");
        }

        return (null,
            $"{channel.Id} 没有登记 {relative}。本通道文件: {string.Join(", ", channel.Documents.Select(item => item.Path))}");
    }

    private static IReadOnlyList<string> ListHeadings(string markdown)
    {
        var headings = new List<string>();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (!TryReadHeading(raw, out _, out var title) || title.Length == 0)
                continue;
            headings.Add(title);
        }

        return headings;
    }

    private static IReadOnlyList<string> ListVersions(string markdown)
    {
        var versions = new List<string>();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string? version = null;
            if (TryReadVersionBullet(raw, out var bulletVersion))
                version = bulletVersion;
            else if (TryReadHeading(raw, out _, out var title) && VersionToken.IsMatch(title))
                version = VersionToken.Match(title).Value;
            if (version == null || versions.Contains(version, StringComparer.Ordinal))
                continue;
            versions.Add(version);
        }

        return versions;
    }

    private static string? ExtractHeading(string markdown, string heading)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var start = -1;
        var startLevel = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!TryReadHeading(lines[i], out var level, out var title))
                continue;
            if (start < 0)
            {
                if (HeadingMatches(title, heading))
                {
                    start = i;
                    startLevel = level;
                }

                continue;
            }

            if (level <= startLevel)
                return string.Join('\n', lines[start..i]).TrimEnd();
        }

        if (start >= 0)
            return string.Join('\n', lines[start..]).TrimEnd();
        return ExtractVersionItems(lines, heading);
    }

    private static bool HeadingMatches(string title, string heading)
    {
        if (title.Equals(heading, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!VersionToken.IsMatch(heading))
            return false;
        if (!title.StartsWith(heading, StringComparison.OrdinalIgnoreCase))
            return false;
        return title.Length == heading.Length || title[heading.Length] is ' ' or '\t' or ':' or '：';
    }

    private static string? ExtractVersionItems(string[] lines, string heading)
    {
        if (!VersionToken.IsMatch(heading))
            return null;

        var collected = new List<string>();
        var capturing = false;
        foreach (var line in lines)
        {
            if (TryReadVersionBullet(line, out var itemVersion))
                capturing = itemVersion.Equals(heading, StringComparison.Ordinal);
            if (capturing)
                collected.Add(line);
        }

        return collected.Count == 0 ? null : string.Join('\n', collected).TrimEnd();
    }

    private static bool TryReadVersionBullet(string line, out string version)
    {
        version = "";
        var trimmed = line.TrimStart();
        if (trimmed.Length < 3 || trimmed[0] != '-' || trimmed[1] is not ' ' and not '\t')
            return false;
        var body = trimmed[2..].TrimStart();
        if (body.StartsWith("**", StringComparison.Ordinal))
            body = body[2..].TrimStart();
        var match = VersionToken.Match(body);
        if (!match.Success)
            return false;
        version = match.Value;
        return true;
    }

    private static bool TryReadHeading(string line, out int level, out string title)
    {
        level = 0;
        title = "";
        if (line.Length == 0 || line[0] != '#')
            return false;
        while (level < line.Length && line[level] == '#')
            level++;
        if (level is < 1 or > 6 || (level < line.Length && line[level] is not ' ' and not '\t'))
            return false;
        title = line[level..].Trim();
        return title.Length > 0;
    }

    private static string RenderOutline(string path, long bytes, string markdown)
    {
        var headings = ListHeadings(markdown);
        var builder = new System.Text.StringBuilder();
        builder.Append(path);
        builder.Append(" 共 ");
        builder.Append(bytes);
        builder.Append(" 字节。不要整篇灌进对话，加 heading=章节标题或版本号 只读一节。");
        if (headings.Count > 0)
        {
            builder.Append("\n章节:");
            foreach (var heading in headings)
            {
                builder.Append("\n  ");
                builder.Append(heading);
            }
        }

        var versions = ListVersions(markdown);
        if (versions.Count > 0)
        {
            builder.Append("\n版本:");
            foreach (var version in versions)
            {
                builder.Append("\n  ");
                builder.Append(version);
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyList<ZDocChannel> Discover(Func<string> projectLibraryRoot)
    {
        var rootValue = projectLibraryRoot();
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
            foreach (var package in DiscoverPackages(project))
                TryAddChannel(package, project, Path.GetFileName(project), channels);
        }

        return channels
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(item => item.Documents.Count == 0 ? 1 : 0)
                .ThenBy(item => item.PackagePath, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> DiscoverPackages(string project)
    {
        var publishRoot = Path.Combine(project, "z-Publish");
        foreach (var package in EnumerateCurrentPackages(publishRoot))
            yield return package;
    }

    private static IEnumerable<string> EnumerateCurrentPackages(string publishRoot)
    {
        if (!Directory.Exists(publishRoot))
            yield break;

        var flatHost = Path.Combine(publishRoot, "manifest.json");
        if (File.Exists(flatHost)
            && File.Exists(Path.Combine(publishRoot, "SHA256SUMS"))
            && File.Exists(Path.Combine(publishRoot, "host", "HistoryVulcan.exe")))
        {
            yield return publishRoot;
        }

        foreach (var directory in Directory.GetDirectories(publishRoot, "History*-v*", SearchOption.TopDirectoryOnly))
        {
            if (File.Exists(Path.Combine(directory, "module.manifest.json"))
                && File.Exists(Path.Combine(directory, "SHA256SUMS")))
            {
                yield return directory;
            }
        }
    }

    private static void TryAddChannel(
        string package,
        string project,
        string projectDirectory,
        List<ZDocChannel> channels)
    {
        var sumsPath = Path.Combine(package, "SHA256SUMS");
        if (!File.Exists(sumsPath))
            return;

        var moduleName = ReadIdentity(package, Path.GetFileName(package));
        var id = ToDomain(moduleName);
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
        var relativeFolder = Path.GetRelativePath(project, package).Replace('\\', '/');
        channels.Add(new ZDocChannel(
            id,
            moduleName,
            version,
            projectDirectory,
            relativeFolder,
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

    private static string ToDomain(string moduleName)
    {
        var value = (moduleName ?? string.Empty).Trim();
        if (value.StartsWith("History", StringComparison.OrdinalIgnoreCase))
            value = value["History".Length..];
        return value.Length == 0 ? "history" : value.ToLowerInvariant();
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
        builder.AppendLine("Z 文档通道（扫描各项目 z-Publish 当前候选：模块为 History*-v*，Vulcan 5.1.2 宿主为平铺 host，现场读取 SHA256SUMS）");
        builder.AppendLine("跨项目读说明书：先把本索引留在对话中，再调用其中一个 diana.docs.<通道>。省略 file 只列出该通道。");
        builder.AppendLine("file 可用 catalog 路径或唯一文件名。超过 12 KiB 的正文必须带 heading=章节标题或版本号（如 5.1.2），否则只返回目录。");
        builder.AppendLine("若列出了通道但命令尚未登记，执行 vulcan.module.reload 让 Diana 按当前运行区重新附着。");
        if (channels.Count == 0)
        {
            builder.Append("当前项目库下没有合规的 z-Publish 当前候选（模块版本目录或平铺 host 宿主）。");
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
