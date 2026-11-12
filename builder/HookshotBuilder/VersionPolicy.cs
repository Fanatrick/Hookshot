using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace HookshotBuilder;

/// The launcher's version list and "Stay on this version" (MainWindow.xaml.cs, the launcher): which version its button
/// installs, the pin a game folder keeps, and the copy of the saves and settings made before a switch to another
/// version. No window here: tests/launcher_versions_test.cs runs these as they are
static class VersionPolicy
{
    /// The first release with the version list and the pin, and so the oldest the list offers. Going back further is not
    /// offered: a builder before it opens as the folder's launcher with no list to come back from and no pin, and offers
    /// its update at every start; one before v2.0.6 cannot be installed by version at all (its setup takes no --tag: it
    /// installs the latest release, and the update then stops at "The update installed ..., not ..."); and none before
    /// it knows a portable builder's folder. It is the release this ships in, the next minor version after v2.1.0: if
    /// this is released in a later one, that version goes here (and in docs/versioning.md, Staying on a version)
    public const string Since = "v2.2.0";

    static string PolicyPath(string folder) => Path.Combine(folder, "user", "hookshot_update_policy.txt");

    /// The version a game folder's pin names, null without one: user\hookshot_update_policy.txt, beside the saves and
    /// kept by every build, "mode=pinned" and "version=<the version>". Read as host/hos_update.c reads it (read_value):
    /// the first line of each name that has a value, of the length the game takes
    public static string? PinnedTag(string folder)
    {
        try
        {
            string? mode = null, version = null;
            foreach (var line in File.ReadAllLines(PolicyPath(folder)))
            {
                mode ??= Value(line, "mode=", 32);
                version ??= Value(line, "version=", 128);
            }
            return mode == "pinned" ? version : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static string? Value(string line, string name, int cap) =>
        line.StartsWith(name, StringComparison.Ordinal) && line[name.Length..].TrimEnd(' ', '\t', '\r', '\n') is { Length: > 0 } value &&
        value.Length < cap ? value : null;

    /// The pin holds only while the version it names is the one installed (the game's rule too, host/hos_update.c
    /// pin_holds): one left from another version is as none
    public static bool Pinned(string folder, string? installed) => installed != null && PinnedTag(folder) == installed;

    /// The pin written for a version, or taken away (null)
    public static void SetPinnedTag(string folder, string? tag)
    {
        var policy = PolicyPath(folder);
        if (string.IsNullOrEmpty(tag))
        {
            if (File.Exists(policy)) File.Delete(policy);   // (File.Delete alone throws when there is no user folder yet)
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(policy)!);
        File.WriteAllText(policy, $"mode=pinned\nversion={tag}\n");
    }

    /// A build of the folder ended well, on the version installed: pinned to it when the switch asked for that. A pin
    /// that names another version goes: the folder has left that version, and going back to it one day would bring the
    /// pin back unasked. Nothing is written before a build or after one that stopped, so a download or a build that
    /// fails leaves the folder's pin as it was
    public static void Settle(string folder, string? installed, bool pin)
    {
        if (pin && installed != null) SetPinnedTag(folder, installed);
        else if (PinnedTag(folder) is { } left && left != installed) SetPinnedTag(folder, null);
    }

    internal enum Step { None, Update, Switch }

    /// What the launcher's button does with the version picked: nothing with the one installed, or in a folder that
    /// keeps no game files to build from; the update as it always was with the track's newest (Updater.Target); a
    /// switch, asked first and with the saves copied, with any other
    internal static Step StepFor(string? installed, bool rebuildable, string? target, string? picked) =>
        picked == null || !rebuildable || picked == installed ? Step.None : picked == target ? Step.Update : Step.Switch;

    /// The version picked when the launcher opens: the track's newest, so the page is the one it was ("Update
    /// available" and Update, or "Up to date"), unless the player stays on the version installed
    internal static string? Preselected(string? installed, bool rebuildable, string? target, bool pinned) =>
        pinned || !rebuildable || target == null ? installed : target;

    /// The version the launcher's tick is for: the one picked to switch to, else the one installed. So on the page of an
    /// update that is out, the tick is "not this update": it keeps the folder where it is
    internal static string? PinSubject(string? installed, bool rebuildable, string? target, string? picked) =>
        StepFor(installed, rebuildable, target, picked) == Step.Switch ? picked : installed;

    /// The game's own check (host/hos_update.c: the game started without its launcher) keeps to the pin from Since on. One
    /// built by a release or release candidate before it still asks there, and the tick says so. (a nightly's version names
    /// the release before it, so it does not tell)
    internal static bool GameAsksAnyway(string? installed) =>
        installed != null && Updater.TrackOf(installed) != Updater.Nightly && Updater.Newer(Since, installed.Split('-')[0]);

    /// A release the list offers: vX.Y.Z from Since on
    internal static bool Offers(string tag) => Updater.IsRelease(tag) && !Updater.Newer(Since, tag);

    /// The launcher's list, newest first: the releases GitHub listed (Updater.ReleaseTags, in that order), with the
    /// track's newest and the version installed put in where their versions belong (one that is not a version: last)
    internal static List<string> Offered(string? installed, string? target, IEnumerable<string> releases)
    {
        var list = releases.Distinct().ToList();
        foreach (var extra in new[] { target, installed })
        {
            if (extra == null || list.Contains(extra)) continue;
            var at = list.FindIndex(listed => Updater.Newer(extra, listed));
            list.Insert(at < 0 ? list.Count : at, extra);
        }
        return list;
    }

    // ---- the copy of the saves and settings before a switch ----

    /// How many copies a game folder keeps: the oldest go when a new one is made
    public const int Backups = 5;

    /// Where a game folder's copies are: a folder of its own in the builder's (BuilderHome.Base: a portable builder's stay
    /// with it), named after the game folder and told from another of that name by its path
    public static string BackupHome(string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        // (a portable builder's game on its own drive: the path it is saved by, which stays when the drive's letter changes)
        var key = (BuilderHome.SavePath(full) ?? full).ToUpperInvariant();
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8].ToLowerInvariant();
        var name = Safe(Path.GetFileName(full));
        return Path.Combine(BuilderHome.Base, "version-backups", name.Length > 0 ? $"{name}-{id}" : id);
    }

    /// (a folder's name, or a version, as part of a folder name)
    static string Safe(string text)
    {
        var kept = string.Concat(text.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '+' ? c : '_')).Trim('.', '_');
        return kept.Length > 40 ? kept[..40] : kept;
    }

    /// The saves and settings of a game folder copied before it is built with another version, which may write them its
    /// own way: the saves (user\save), the files beside them (not the launcher's own two, the pin and "Skip this
    /// version"), and the folder's hookshot_options.ini, hookshot_env.txt and language.txt. Not the game's caches
    /// (user\cache, user\temp): the game makes them again. Returns the copy's folder; the oldest beyond Backups go
    public static string Backup(string folder, string? from, string to, DateTime? now = null)
    {
        var home = BackupHome(folder);
        var stamp = (now ?? DateTime.UtcNow).ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var name = $"{stamp} {Safe(from ?? "unknown")} to {Safe(to)}";
        var dest = Path.Combine(home, name);
        for (var again = 2; Directory.Exists(dest); again++) dest = Path.Combine(home, $"{name} ({again})");
        // (made complete under another name first: a copy that stopped half way is never taken for one, nor counted)
        var part = dest + ".part";
        if (Directory.Exists(part)) Directory.Delete(part, recursive: true);
        Directory.CreateDirectory(part);
        var user = Path.Combine(folder, "user");
        if (Directory.Exists(Path.Combine(user, "save"))) CopyTree(Path.Combine(user, "save"), Path.Combine(part, "user", "save"));
        if (Directory.Exists(user))
            foreach (var source in Directory.EnumerateFiles(user))
                if (Path.GetFileName(source) is not ("hookshot_update_policy.txt" or "hookshot_update_skip.txt"))
                    Copy(source, Path.Combine(part, "user", Path.GetFileName(source)));
        foreach (var setting in new[] { "hookshot_options.ini", "hookshot_env.txt", "language.txt" })
            if (File.Exists(Path.Combine(folder, setting))) Copy(Path.Combine(folder, setting), Path.Combine(part, setting));
        Directory.Move(part, dest);
        Prune(home);
        return dest;
    }

    static void Copy(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
    }

    /// A folder's files and folders. A link to another folder inside it (a junction, a symbolic link) is not followed:
    /// what it points at is not this game's to copy, and a link back to a folder above would never end
    static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from)) Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var inner in Directory.EnumerateDirectories(from))
            if (!File.GetAttributes(inner).HasFlag(FileAttributes.ReparsePoint))
                CopyTree(inner, Path.Combine(to, Path.GetFileName(inner)));
    }

    /// The newest Backups copies stay (their names begin with their time, so by name), and no copy that stopped half
    /// way; one that cannot be deleted now stays until the next copy is made
    static void Prune(string home)
    {
        var copies = Directory.EnumerateDirectories(home).ToList();
        var stopped = copies.Where(d => d.EndsWith(".part", StringComparison.Ordinal)).ToList();
        var old = copies.Except(stopped).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).Skip(Backups);
        foreach (var gone in stopped.Concat(old))
            try { Directory.Delete(gone, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
