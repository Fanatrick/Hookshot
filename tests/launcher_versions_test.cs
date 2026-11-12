using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HookshotBuilder;

// The launcher's version list and "Stay on this version": the real Updater.cs, VersionPolicy.cs and BuilderHome.cs,
// compiled by tests/test_launcher_versions.py. Only the outside is authored here: GitHub's answers, the process a
// finished download starts, the command line, and the window the updater reports to.
namespace VersionsFixture
{
    static class FakeEnvironment
    {
        public static string[] Args = Array.Empty<string>();
        public static string[] GetCommandLineArgs() => Args;
    }

    static class UnusedPortableArchive
    {
        public static string Install(string zip, string home, string tag) =>
            throw new InvalidOperationException("A standard update took the portable archive's way.");
    }

    static class FakeProcess
    {
        public static void Start(ProcessStartInfo info) => State.Current.Starts.Add(info);
    }

    sealed class FakeHttpClient : HttpClient
    {
        public FakeHttpClient() : base(State.Current.Handler, disposeHandler: false) { }
    }

    sealed class Handler : HttpMessageHandler
    {
        public readonly List<string> Requests = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => throw new Exception("No authored answer for this request.");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Respond(request));
        }
    }

    sealed class State
    {
        public static State Current = null!;
        public static string Temp = "";
        static int count;
        public readonly string Root;
        public readonly Handler Handler = new();
        public readonly List<ProcessStartInfo> Starts = new();
        public readonly MainWindow Window = new();
        public readonly TaskCompletionSource<string> Outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly byte[] Setup = Encoding.ASCII.GetBytes("authored setup bytes, never run");

        public State()
        {
            Current = this;
            Root = Path.Combine(Temp, "case " + ++count);
            Directory.CreateDirectory(Root);
            FakeEnvironment.Args = Array.Empty<string>();
            Tools.Installed = "v2.3.0";
        }

        /// A game folder as a build leaves it, as far as the launcher looks: saves, settings, caches and the game itself
        public string Game(string name = "Hookshot")
        {
            var game = Path.Combine(Root, name);
            foreach (var (path, text) in new[]
            {
                ("user/save/0000000000000001/slot1.bin", "save one"), ("user/save/0000000000000001/extra/deep.bin", "save two"),
                ("user/present.txt", "present"), ("user/hookshot_update_skip.txt", "skip=v9.9.9\n"),
                ("user/cache/0/big.bin", "a cache"), ("user/temp/scratch.bin", "scratch"),
                ("hookshot_options.ini", "vsync=1\n"), ("hookshot_env.txt", "RX_TEST=1\n"), ("language.txt", "en-US\n"),
                ("image.bin", "the game"), ("romfs/data.bin", "the game's data"), ("hookshot_version.txt", "version=v2.3.0\n"),
            })
            {
                var file = Path.Combine(game, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, text);
            }
            return game;
        }

        static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        /// GitHub's answer for one release (releases/latest, releases/tags/<tag>) and for its setup's download
        public void OneRelease(string tag)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(new
            {
                tag_name = tag,
                assets = new object[]
                {
                    new { name = "HookshotBuilder.exe", browser_download_url = "https://fixture.invalid/setup", url = "https://fixture.invalid/api",
                          size = Setup.Length, digest = Hash(Setup) },
                    new { name = "hookshot-setup.txt" },
                }
            });
            Handler.Respond = request => new(HttpStatusCode.OK)
                { Content = new ByteArrayContent(request.RequestUri!.Host == "api.github.com" ? json : Setup) };
        }

        /// GitHub's release list, a page of it a request
        public void Pages(params object[][] pages)
        {
            Handler.Respond = request =>
            {
                var query = request.RequestUri!.Query;
                var page = int.Parse(query[(query.IndexOf("&page=", StringComparison.Ordinal) + 6)..]);
                var body = page <= pages.Length ? pages[page - 1] : Array.Empty<object>();
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body)) };
            };
        }

        public async Task<string> Folder(string game, string? tag, bool fetched = false, bool named = false, bool pin = false)
        {
            Updater.UpdateFolder(Window, game, tag, fetched, named, pin);
            return await Outcome.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }

        public async Task<string> CommandLine(params string[] args)
        {
            FakeEnvironment.Args = new[] { "HookshotBuilder.exe" }.Concat(args).ToArray();
            Updater.FromCommandLine(Window);
            return await Outcome.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }

        public string[] Started => Starts.Single().ArgumentList.ToArray();
    }

    static class Program
    {
        const string Api = "https://api.github.com/repos/NotRexed/Hookshot/releases";
        static int cases, failures;

        static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }

        static void Same(IEnumerable<string> got, params string[] wanted)
        {
            var have = got.ToArray();
            Check(have.SequenceEqual(wanted), $"got [{string.Join(", ", have)}], not [{string.Join(", ", wanted)}]");
        }

        static async Task Case(string name, Func<State, Task> test)
        {
            cases++;
            try { await test(new State()); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
        }

        static void Case(string name, Action<State> test) =>
            Case(name, state => { test(state); return Task.CompletedTask; }).GetAwaiter().GetResult();

        /// One release of GitHub's list
        static object Listed(string tag, bool setup = true, bool list = true, bool draft = false, bool prerelease = false)
        {
            var assets = new List<object> { new { name = "SHA256SUMS" } };
            if (setup) assets.Add(new { name = "HookshotBuilder.exe" });
            if (list) assets.Add(new { name = "hookshot-setup.txt" });
            return new { tag_name = tag, draft, prerelease, assets };
        }

        static object[] Full(IEnumerable<object> first, string filler)
        {
            var page = first.ToList();
            for (var n = 1; page.Count < 100; n++) page.Add(Listed($"{filler}.{n}"));
            return page.ToArray();
        }

        static IEnumerable<string> Files(string folder) =>
            Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/')).OrderBy(file => file, StringComparer.Ordinal);

        static string Policy(string game) => Path.Combine(game, "user", "hookshot_update_policy.txt");

        static async Task<int> Main(string[] args)
        {
            State.Temp = args[0];
            const VersionPolicy.Step None = VersionPolicy.Step.None, Update = VersionPolicy.Step.Update, Switch = VersionPolicy.Step.Switch;

            // ---- the version list ----

            Case("a release's name", _ =>
            {
                foreach (var tag in new[] { "v2.2.0", "v10.20.30", "v2.0.14" }) Check(Updater.IsRelease(tag), tag + " is a release");
                foreach (var tag in new string?[] { null, "", "v2.2.0-rc.1", "v2.1.0-rc.1-3-g1a2b3c4", "v2.2.0-3-g1a2b3c4-dirty", "nightly", "rc",
                                                    "builder-parts", "2.2.0", "v2.2", "v2.2.0.1", "v2.2.0\n", " v2.2.0", "V2.2.0" })
                    Check(!Updater.IsRelease(tag), $"'{tag}' was taken for a release");
                Check(VersionPolicy.Offers(VersionPolicy.Since), "the first release with the list is not on it");
                Check(VersionPolicy.Offers("v99.0.0"), "a later release is not on the list");
                Check(Updater.IsRelease(VersionPolicy.Since), "VersionPolicy.Since is not a release's name");
                foreach (var tag in new[] { "v2.1.0", "v2.0.14", "v2.0.5", "v2.0.0", "v99.0.0-rc.1", "nightly" })
                    Check(!VersionPolicy.Offers(tag), tag + " is on the list");
            });

            await Case("the list: releases with the setup and its list, from the first with the list on, newest first", async s =>
            {
                s.Pages(
                    Full(new[]
                    {
                        Listed("nightly", prerelease: true), Listed("rc", prerelease: true), Listed("builder-parts"), Listed("new-installer"),
                        Listed("v99.1.0-rc.1"), Listed("v99.1.0", draft: true), Listed("v99.0.9", prerelease: true),
                        Listed("v99.0.1"), Listed("v99.0.3"), Listed("v99.0.2", list: false), Listed("v99.0.4", setup: false), Listed("v99.0.1"),
                    }, "v99.1.0-rc"),
                    Full(new[] { Listed(VersionPolicy.Since), Listed("v2.1.0"), Listed("v2.0.14"), Listed("v2.0.3") }, "v2.0.8-rc"),
                    Full(new[] { Listed("v99.0.5") }, "v2.0.7-rc"));
                Same(await Updater.ReleaseTags(VersionPolicy.Since), "v99.0.3", "v99.0.1", VersionPolicy.Since);
                // (a page of 100 at a time, and no further than the page with a release from before the list)
                Same(s.Handler.Requests, Api + "?per_page=100&page=1", Api + "?per_page=100&page=2");
            });

            await Case("the list: a page that is not full is the last", async s =>
            {
                s.Pages(new[] { Listed("v99.0.1"), Listed("v99.0.0") });
                Same(await Updater.ReleaseTags(VersionPolicy.Since), "v99.0.1", "v99.0.0");
                Check(s.Handler.Requests.Count == 1, "another page was asked for after one that was not full");
            });

            await Case("the list: more than a hundred releases, and no more than ten pages asked for", async s =>
            {
                var pages = Enumerable.Range(1, 12).Select(page => Full(new[] { Listed($"v99.{page}.0") }, $"v99.{page}.1-rc")).ToArray();
                s.Pages(pages);
                var tags = await Updater.ReleaseTags(VersionPolicy.Since);
                Check(s.Handler.Requests.Count == 10, s.Handler.Requests.Count + " pages were asked for");
                Same(tags, Enumerable.Range(1, 10).Reverse().Select(page => $"v99.{page}.0").ToArray());   // (the second page's release too)
            });

            await Case("the list: GitHub's refusal is an error, not an empty list", async s =>
            {
                s.Handler.Respond = _ => new(HttpStatusCode.Forbidden) { ReasonPhrase = "rate limit exceeded" };
                try { await Updater.ReleaseTags(VersionPolicy.Since); Check(false, "a refused list was taken for an empty one"); }
                catch (HttpRequestException error) { Check(error.Message.Contains("403"), error.Message); }
                Check(s.Handler.Requests.Count == 1, "a refusal was asked again");
            });

            await Case("the list: entries with parts missing are passed over", async s =>
            {
                s.Pages(new object[] { new { }, new { tag_name = (string?)null }, new { tag_name = "v99.0.2" }, new { tag_name = "v99.0.1", assets = "none" },
                                       new { tag_name = "v99.0.0", assets = new object[] { new { }, new { name = "HookshotBuilder.exe" }, new { name = "hookshot-setup.txt" } } } });
                Same(await Updater.ReleaseTags(VersionPolicy.Since), "v99.0.0");
            });

            Case("the list's order: the newest and the version installed where their versions belong", _ =>
            {
                Same(VersionPolicy.Offered("v2.2.1", "v2.3.0", new[] { "v2.3.0", "v2.2.1", "v2.2.0" }), "v2.3.0", "v2.2.1", "v2.2.0");
                Same(VersionPolicy.Offered("v2.2.0", "v2.3.0", Array.Empty<string>()), "v2.3.0", "v2.2.0");
                Same(VersionPolicy.Offered("v2.2.0", "v2.2.0", Array.Empty<string>()), "v2.2.0");
                Same(VersionPolicy.Offered("v2.1.0", "v2.4.0-rc.1", new[] { "v2.3.0", "v2.2.0" }), "v2.4.0-rc.1", "v2.3.0", "v2.2.0", "v2.1.0");
                Same(VersionPolicy.Offered("v2.2.0-3-g1a2b3c4", "v2.3.0", new[] { "v2.3.0", "v2.2.0" }), "v2.3.0", "v2.2.0-3-g1a2b3c4", "v2.2.0");
                Same(VersionPolicy.Offered("a-test-build", null, new[] { "v2.3.0" }), "v2.3.0", "a-test-build");
                Same(VersionPolicy.Offered(null, null, Array.Empty<string>()));
            });

            // ---- what the launcher's button does ----

            Case("the page opens as it was: Update for the track's newest, nothing when it is installed", _ =>
            {
                // (an update is out: the newest is picked, and the button is the update, not a switch)
                Check(VersionPolicy.Preselected("v2.2.0", true, "v2.3.0", pinned: false) == "v2.3.0", "an update that is out is not what the page opens on");
                Check(VersionPolicy.StepFor("v2.2.0", true, "v2.3.0", "v2.3.0") == Update, "the track's newest is not the update");
                // (a switch of track's older newest too: Updater.Target, and OlderOk asks)
                Check(VersionPolicy.StepFor("v2.3.0-rc.1", true, "v2.2.0", "v2.2.0") == Update, "a track's older newest is not the update");
                // (up to date)
                Check(VersionPolicy.Preselected("v2.3.0", true, "v2.3.0", pinned: false) == "v2.3.0", "up to date");
                Check(VersionPolicy.StepFor("v2.3.0", true, "v2.3.0", "v2.3.0") == None, "the version installed has a button");
                // (GitHub did not answer)
                Check(VersionPolicy.Preselected("v2.2.0", true, null, pinned: false) == "v2.2.0", "no answer");
                Check(VersionPolicy.StepFor("v2.2.0", true, null, "v2.2.0") == None, "no answer has a button");
                // (a folder that keeps no game files: nothing to build from, whatever is picked)
                Check(VersionPolicy.Preselected("v2.2.0", false, "v2.3.0", pinned: false) == "v2.2.0", "a folder with no game files");
                foreach (var picked in new string?[] { "v2.3.0", "v2.2.1", "v2.2.0", null })
                    Check(VersionPolicy.StepFor("v2.2.0", false, "v2.3.0", picked) == None, "a folder with no game files has a button");
                Check(VersionPolicy.StepFor("v2.2.0", true, "v2.3.0", null) == None, "nothing picked has a button");
            });

            Case("another version than the newest is a switch, and staying opens on the version installed", _ =>
            {
                Check(VersionPolicy.StepFor("v2.3.0", true, "v2.3.0", "v2.2.0") == Switch, "an older release is not a switch");
                Check(VersionPolicy.StepFor("v2.2.0", true, "v2.4.0", "v2.3.0") == Switch, "a release between is not a switch");
                Check(VersionPolicy.Preselected("v2.2.0", true, "v2.3.0", pinned: true) == "v2.2.0", "staying on a version still opens on the update");
                Check(VersionPolicy.StepFor("v2.2.0", true, "v2.3.0", "v2.2.0") == None, "the version stayed on has a button");
                // (the tick is for the version picked to switch to, else for the one installed: on the page of an update that is
                // out it is "not this update")
                Check(VersionPolicy.PinSubject("v2.2.0", true, "v2.3.0", "v2.3.0") == "v2.2.0", "the tick on the update's page");
                Check(VersionPolicy.PinSubject("v2.2.0", true, "v2.3.0", "v2.2.0") == "v2.2.0", "the tick on the version installed");
                Check(VersionPolicy.PinSubject("v2.3.0", true, "v2.3.0", "v2.2.0") == "v2.2.0", "the tick on a version to switch to");
                Check(VersionPolicy.PinSubject("v2.3.0", true, null, "v2.2.0") == "v2.2.0", "the tick with no answer from GitHub");
                // (a game built before the pin still asks when it is started without the launcher)
                Check(VersionPolicy.GameAsksAnyway("v2.1.0") && VersionPolicy.GameAsksAnyway("v2.1.0-rc.3"), "a game from before the pin");
                Check(!VersionPolicy.GameAsksAnyway(VersionPolicy.Since) && !VersionPolicy.GameAsksAnyway(VersionPolicy.Since + "-rc.1") &&
                      !VersionPolicy.GameAsksAnyway("v99.0.0") && !VersionPolicy.GameAsksAnyway("v2.1.0-rc.3-250-g1a2b3c4") &&
                      !VersionPolicy.GameAsksAnyway(null), "a game with the pin, or one whose version does not tell");
            });

            // ---- the pin ----

            Case("the pin: two lines beside the saves, and it holds only for the version installed", s =>
            {
                var game = s.Game();
                Check(VersionPolicy.PinnedTag(game) == null && !VersionPolicy.Pinned(game, "v2.3.0"), "a folder with no pin is pinned");
                VersionPolicy.SetPinnedTag(game, "v2.3.0");
                Check(File.ReadAllBytes(Policy(game)).SequenceEqual(Encoding.ASCII.GetBytes("mode=pinned\nversion=v2.3.0\n")), "the pin's file is not the two lines");
                Check(VersionPolicy.PinnedTag(game) == "v2.3.0" && VersionPolicy.Pinned(game, "v2.3.0"), "the pin written is not read");
                Check(!VersionPolicy.Pinned(game, "v2.4.0") && !VersionPolicy.Pinned(game, "v2.3") && !VersionPolicy.Pinned(game, null),
                      "a pin left from another version holds");
                foreach (var (text, pinned) in new[]
                {
                    ("﻿mode=pinned\r\nversion=v2.3.0\r\n", "v2.3.0"), ("version=v2.3.0\nmode=pinned\n", "v2.3.0"),
                    ("mode=pinned  \nversion=v2.3.0\t\n", "v2.3.0"), ("# a note\nmode=pinned\nversion=v2.2.0\nversion=v2.3.0\n", "v2.2.0"),
                    ("mode=pinned\nversion=\nversion=v2.3.0\n", "v2.3.0"),   // (a line with no value is passed over, as the game does)
                    ("mode=latest\nversion=v2.3.0\n", null), ("version=v2.3.0\n", null), ("mode=pinned\n", null), ("mode=pinned\nversion=\n", null),
                    ("mode=pinned\nversion=" + new string('9', 128) + "\n", null), ("", null),
                })
                {
                    File.WriteAllText(Policy(game), text);
                    Check(VersionPolicy.PinnedTag(game) == pinned, $"'{text}' reads as '{VersionPolicy.PinnedTag(game)}'");
                }
                VersionPolicy.SetPinnedTag(game, null);
                Check(!File.Exists(Policy(game)), "the pin taken away is still there");
                VersionPolicy.SetPinnedTag(game, null);   // (twice: no error)
                var bare = Path.Combine(s.Root, "no user folder");
                Directory.CreateDirectory(bare);
                VersionPolicy.SetPinnedTag(bare, null);
                Check(!Directory.Exists(Path.Combine(bare, "user")), "taking no pin away made a user folder");
                VersionPolicy.SetPinnedTag(bare, "v2.3.0");
                Check(VersionPolicy.Pinned(bare, "v2.3.0"), "a pin in a folder with no user folder yet");
            });

            Case("the pin after a build: set when asked, and one left from another version goes", s =>
            {
                var game = s.Game();
                VersionPolicy.Settle(game, "v2.3.0", pin: false);
                Check(!File.Exists(Policy(game)), "a build that asked for no pin left one");
                VersionPolicy.Settle(game, "v2.2.0", pin: true);
                Check(VersionPolicy.Pinned(game, "v2.2.0"), "the switch's pin was not kept");
                VersionPolicy.Settle(game, "v2.2.0", pin: false);   // (built again as the same version)
                Check(VersionPolicy.Pinned(game, "v2.2.0"), "building the same version again took its pin");
                VersionPolicy.Settle(game, "v2.3.0", pin: false);   // (updated: the folder left the version it stayed on)
                Check(VersionPolicy.PinnedTag(game) == null, "a pin stayed for a version the folder left");
                VersionPolicy.SetPinnedTag(game, "v2.2.0");
                VersionPolicy.Settle(game, "v2.3.0", pin: true);
                Check(VersionPolicy.Pinned(game, "v2.3.0"), "a new pin did not replace the old one");
                VersionPolicy.Settle(game, null, pin: true);   // (no version file: nothing to stay on)
                Check(VersionPolicy.PinnedTag(game) == null, "a folder with no version kept a pin");
            });

            // ---- the copy of the saves and settings ----

            Case("the copy: the saves and the settings, language.txt too, not the caches or the game", s =>
            {
                var game = s.Game();
                VersionPolicy.SetPinnedTag(game, "v2.3.0");
                var home = VersionPolicy.BackupHome(game);
                Check(Path.GetFullPath(home).StartsWith(Path.GetFullPath(BuilderHome.Base) + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                      "the copies are not in the builder's own folder: " + home);
                var copy = VersionPolicy.Backup(game, "v2.3.0", "v2.2.0", new DateTime(2026, 11, 12, 7, 15, 30, DateTimeKind.Utc));
                Check(Path.GetDirectoryName(copy) == home && Path.GetFileName(copy) == "20261112-071530 v2.3.0 to v2.2.0", "the copy is " + copy);
                Same(Files(copy), "hookshot_env.txt", "hookshot_options.ini", "language.txt", "user/present.txt",
                     "user/save/0000000000000001/extra/deep.bin", "user/save/0000000000000001/slot1.bin");
                Check(File.ReadAllText(Path.Combine(copy, "user", "save", "0000000000000001", "extra", "deep.bin")) == "save two", "a save's bytes changed");
                Check(File.ReadAllText(Path.Combine(copy, "language.txt")) == "en-US\n", "language.txt changed");
                Check(!Directory.EnumerateDirectories(home).Any(d => d.EndsWith(".part", StringComparison.Ordinal)), "a half-made copy is left");
                // (the same second again: another folder, not the first one overwritten)
                var again = VersionPolicy.Backup(game, "v2.3.0", "v2.2.0", new DateTime(2026, 11, 12, 7, 15, 30, DateTimeKind.Utc));
                Check(again != copy && Directory.Exists(copy) && Directory.Exists(again), "a second copy replaced the first");
                // (a folder with nothing to copy: an empty copy, no error)
                var bare = Path.Combine(s.Root, "bare");
                Directory.CreateDirectory(bare);
                Check(!Files(VersionPolicy.Backup(bare, null, "v2.2.0")).Any(), "a folder with no saves had something copied");
            });

            Case("the copy: each game folder its own, and the newest five stay", s =>
            {
                var game = s.Game();
                var other = s.Game(Path.Combine("another place", "Hookshot"));
                var home = VersionPolicy.BackupHome(game);
                Check(home != VersionPolicy.BackupHome(other), "two game folders of one name share their copies");
                Check(home == VersionPolicy.BackupHome(game + Path.DirectorySeparatorChar), "a trailing separator is another folder");
                Check(Path.GetFileName(home).StartsWith("Hookshot-", StringComparison.Ordinal), "the copies' folder is not named after the game folder: " + home);
                Directory.CreateDirectory(Path.Combine(home, "20260101-000000 stopped.part"));   // (a copy that stopped half way)
                var kept = VersionPolicy.Backup(other, "v2.3.0", "v2.2.0", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                var copies = Enumerable.Range(1, VersionPolicy.Backups + 2)
                    .Select(day => VersionPolicy.Backup(game, "v2.3.0", "v2.2.0", new DateTime(2026, 11, day, 12, 0, 0, DateTimeKind.Utc))).ToList();
                Same(Directory.EnumerateDirectories(home).Select(d => Path.GetFileName(d)!).OrderBy(d => d, StringComparer.Ordinal),
                     copies.Skip(2).Select(d => Path.GetFileName(d)!).ToArray());
                Check(VersionPolicy.Backups == 5, "the number of copies kept changed: the launcher's question and the docs say five");
                Check(Directory.Exists(kept), "another game folder's copy went with this one's old ones");
            });

            Case("the copy: a link to another folder among the saves is not followed", s =>
            {
                var game = s.Game();
                var outside = Path.Combine(s.Root, "outside");
                Directory.CreateDirectory(outside);
                File.WriteAllText(Path.Combine(outside, "not a save.bin"), "another folder's file");
                try { Directory.CreateSymbolicLink(Path.Combine(game, "user", "save", "linked"), outside); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Console.WriteLine("     (no symbolic links here: " + e.Message + ")");
                    return;
                }
                Check(!Files(VersionPolicy.Backup(game, "v2.3.0", "v2.2.0")).Any(file => file.Contains("linked")), "the link's folder was copied");
            });

            // ---- the switch and the update, through the updater ----

            await Case("a switch asks for that release by name and hands the pin on", async s =>
            {
                var game = s.Game();
                s.OneRelease("v2.2.0");
                Check(await s.Folder(game, "v2.2.0", named: true, pin: true) == "shutdown", "the switch did not hand over");
                Check(s.Handler.Requests[0] == Api + "/tags/v2.2.0", "the release asked for was " + s.Handler.Requests[0]);
                Same(s.Started, "--update", game, "--tag", "v2.2.0", "--fetched", "--pin");
                Check(s.Window.Begun.Single() == (game, "v2.2.0", true), "the window was not told of the pin");
                Check(!File.Exists(Policy(game)), "the pin was written before the version is built");
            });

            await Case("a switch that asked for no pin hands none on", async s =>
            {
                var game = s.Game();
                s.OneRelease("v2.2.0");
                Check(await s.Folder(game, "v2.2.0", named: true) == "shutdown", "the switch did not hand over");
                Same(s.Started, "--update", game, "--tag", "v2.2.0", "--fetched");
                Check(s.Window.Begun.Single() == (game, "v2.2.0", false), "a pin nobody asked for");
            });

            await Case("a switch GitHub answers with another release stops", async s =>
            {
                var game = s.Game();
                s.OneRelease("v2.4.0");
                var outcome = await s.Folder(game, "v2.2.0", named: true, pin: true);
                Check(outcome.StartsWith("failed: ", StringComparison.Ordinal) && outcome.Contains("v2.4.0") && outcome.Contains("v2.2.0"), outcome);
                Check(s.Starts.Count == 0 && s.Window.Built.Count == 0, "another release was installed as the one picked");
                Check(s.Handler.Requests.Count == 1, "something was downloaded");
            });

            await Case("a switch to the version of these tools builds here, the pin with it", async s =>
            {
                var game = s.Game();
                Tools.Installed = "v2.2.0";
                Check(await s.Folder(game, "v2.2.0", named: true, pin: true) == "build", "these tools did not build");
                Check(s.Handler.Requests.Count == 0 && s.Starts.Count == 0, "GitHub was asked for tools that are here");
                Check(s.Window.Begun.Single() == (game, "v2.2.0", true), "the window was not told of the pin");
            });

            await Case("the update is the one it was: the latest release, and no pin", async s =>
            {
                var game = s.Game();
                Tools.Installed = "v2.2.0";
                s.OneRelease("v2.3.0");
                Check(await s.Folder(game, "v2.3.0") == "shutdown", "the update did not hand over");
                Check(s.Handler.Requests[0] == Api + "/latest", "the update asked for " + s.Handler.Requests[0]);
                Same(s.Started, "--update", game, "--tag", "v2.3.0", "--fetched");
                Check(s.Window.Begun.Single() == (game, "v2.3.0", false), "an update set a pin");
            });

            await Case("the game's own offer is the one it was: the latest release, whatever its tag says", async s =>
            {
                var game = s.Game();
                Tools.Installed = "v2.2.0";
                s.OneRelease("v2.3.1");   // (out since the game asked)
                Check(await s.CommandLine("--update", game, "--tag", "v2.3.0") == "shutdown", "the game's update did not hand over");
                Check(s.Handler.Requests[0] == Api + "/latest", "the game's offer asked for " + s.Handler.Requests[0]);
                Same(s.Started, "--update", game, "--tag", "v2.3.1", "--fetched");
                Check(!Updater.PinAsked && s.Window.Begun.Single() == (game, "v2.3.0", false), "the game's offer set a pin");
            });

            await Case("the builder a switch handed over to keeps the pin for its build, and only when it was handed one", async s =>
            {
                var game = s.Game();
                Tools.Installed = "v2.2.0";
                Check(await s.CommandLine("--update", game, "--tag", "v2.2.0", "--fetched", "--pin") == "build", "the handed-over build did not start");
                Check(Updater.PinAsked && s.Window.Begun.Single() == (game, "v2.2.0", true), "--pin did not reach the window");
                Check(s.Handler.Requests.Count == 0, "a handed-over build asked GitHub");
                var plain = new State();
                Tools.Installed = "v2.2.0";
                Check(await plain.CommandLine("--update", game, "--tag", "v2.2.0", "--fetched") == "build", "the handed-over build did not start");
                Check(!Updater.PinAsked && plain.Window.Begun.Single() == (game, "v2.2.0", false), "a pin without --pin");
            });

            Console.WriteLine($"Launcher version cases: {cases - failures} passed, {failures} failed.");
            return failures == 0 ? 0 : 1;
        }
    }
}

namespace HookshotBuilder
{
    sealed class Tools
    {
        public static string? Installed = "v2.3.0";
        public string? Version => Installed;
        public static Tools Find() => new();
    }

    sealed class MainWindow
    {
        public readonly List<(string Folder, string? Tag, bool Pin)> Begun = new();
        public readonly List<string> Built = new();
        public CancellationToken DownloadToken => CancellationToken.None;
        public void Note(string _) { }
        public void BeginUpdate(string folder, string? tag, bool pin = false) => Begun.Add((folder, tag, pin));
        public void UpdateBuild(Tools tools, string folder)
        {
            Built.Add(folder);
            VersionsFixture.State.Current.Outcome.TrySetResult("build");
        }
        public void DownloadProgress(double _) { }
        public void DownloadFailed(string reason) => VersionsFixture.State.Current.Outcome.TrySetResult("failed: " + reason);
    }
}

namespace System.Windows
{
    sealed class Application
    {
        public static Application Current => new();
        public void Shutdown() => VersionsFixture.State.Current.Outcome.TrySetResult("shutdown");
    }
}
