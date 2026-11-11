using System.IO;

namespace HookshotBuilder;

/// <summary>Per-installation version preference. The user folder is preserved across builds.</summary>
static class VersionPolicy
{
    static string PolicyPath(string folder) => Path.Combine(folder, "user", "hookshot_update_policy.txt");

    public static string? PinnedTag(string folder)
    {
        try
        {
            var lines = File.ReadAllLines(PolicyPath(folder));
            if (!lines.Any(line => line.Trim() == "mode=pinned")) return null;
            var line = lines.FirstOrDefault(item => item.StartsWith("version=", StringComparison.Ordinal));
            var tag = line == null ? null : line[8..].Trim();
            return !string.IsNullOrEmpty(tag) && tag.Length <= 128 && tag.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') ? tag : null;
        }
        catch (Exception) { return null; }
    }

    public static string BackupBeforeSwitch(string folder)
    {
        var backups = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HookshotBuilder", "version-backups");
        var dest = Path.Combine(backups, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dest);
        var saveDir = Path.Combine(folder, "user", "save");
        if (Directory.Exists(saveDir))
        {
            foreach (var source in Directory.EnumerateFiles(saveDir, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(dest, "save", Path.GetRelativePath(saveDir, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }
        }
        foreach (var name in new[] { "hookshot_options.ini", "hookshot_env.txt" })
        {
            var source = Path.Combine(folder, name);
            if (File.Exists(source)) File.Copy(source, Path.Combine(dest, name));
        }
        return dest;
    }

    public static void SetPinnedTag(string folder, string? tag)
    {
        var user = Path.Combine(folder, "user");
        Directory.CreateDirectory(user);
        var policy = PolicyPath(folder);
        if (tag == null)
        {
            if (File.Exists(policy)) File.Delete(policy);
            return;
        }
        if (tag.Length > 128 || tag.Length == 0 || !char.IsAsciiLetterOrDigit(tag[0]) ||
            !tag.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            throw new InvalidDataException("Invalid version tag.");
        File.WriteAllText(policy, $"mode=pinned\nversion={tag}\n");
    }
}
