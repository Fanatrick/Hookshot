"""The launcher's version list and "Stay on <version>" in the Windows builder (builder/HookshotBuilder: Updater.cs,
VersionPolicy.cs, and the launcher page of MainWindow.xaml.cs).

What a player relies on, and what each part of the check holds:

- The launcher opens as it always did: one question to GitHub, "Update available" and Update for the track's newest,
  and Update is the update it was (no new question, no copy, no pin). The release list is asked for only when the
  version list is opened, so GitHub refusing it cannot take Update away.
- Another version is a switch: asked first, the saves and settings copied, then the update's own way to that release
  by name. The pin is written when that version's build ends well, never before: a download or a build that fails
  leaves the folder and its pin as they were.
- The list offers releases from the first one with the list on (VersionPolicy.Since), a page of a hundred at a time.
- The copies are in the builder's own folder, a game folder's apart from another's, the newest five kept.

The first and second are read in MainWindow.xaml.cs (a window, not run here). The rest runs: the real Updater.cs,
VersionPolicy.cs and BuilderHome.cs compiled with tests/launcher_versions_test.cs, which authors only GitHub's
answers, the process a finished download starts, the command line and the window the updater reports to.

Built for net8.0, the builder's framework, as tests/test_updater_asset_digest.py is (its docstring has why a later SDK
alone builds for its own framework on a PC, and never on a GitHub runner).

python -I -B tests/test_launcher_versions.py
"""
import argparse
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
RUN_ALL_ARGS = ["--dotnet", "{dotnet}"]
PROJECT = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<OutputType>Exe</OutputType><TargetFramework>%s</TargetFramework><ImplicitUsings>enable</ImplicitUsings>
<Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors><NuGetAudit>false</NuGetAudit>
<UseSharedCompilation>false</UseSharedCompilation>
</PropertyGroup></Project>"""
# (the outside of Updater.cs: only these names are replaced, never its control flow)
ALIASES = """using HttpClient = VersionsFixture.FakeHttpClient;
using Process = VersionsFixture.FakeProcess;
using Environment = VersionsFixture.FakeEnvironment;
using PortableArchive = VersionsFixture.UnusedPortableArchive;
"""
UPDATE = "if (OlderOk(GameVersion(launchFolder))) Updater.UpdateFolder(this, launchFolder, updateTarget, fetched: false, named: Switching);"


def block(text, marker):
    """A method of the window, from its first line to its closing brace"""
    if marker not in text:
        raise SystemExit("the launcher page: MainWindow.xaml.cs no longer has \"%s\" (renamed? this check reads it by that line)" % marker)
    start = text.index(marker)
    end = text.index("{", start) + 1
    depth = 1
    while depth:
        depth += (text[end] == "{") - (text[end] == "}")
        end += 1
    return text[start:end]


def in_order(text, parts):
    """Every part is there, each after the one before"""
    at = [text.find(part) for part in parts]
    return -1 not in at and at == sorted(at)


def read_the_window(source):
    """The launcher page's code, read: each line is something the reviews of #2201 asked for, or a rule of the feature"""
    main = (source / "builder/HookshotBuilder/MainWindow.xaml.cs").read_text(encoding="utf-8")
    xaml = (source / "builder/HookshotBuilder/MainWindow.xaml").read_text(encoding="utf-8")
    policy = (source / "builder/HookshotBuilder/VersionPolicy.cs").read_text(encoding="utf-8")
    wrong = []

    def check(ok, what):
        if not ok:
            wrong.append(what)

    # the launcher's start: one question to GitHub, as before, and not the release list
    show = block(main, "async void ShowLauncher(string folder)")
    check(show.count("await ") == 1 and "await Updater.LatestTag(Track)" in show,
          "the launcher's start asks GitHub more than its one question")
    check("ReleaseTags" not in show, "the launcher's start asks for the release list")
    check(main.count("Updater.ReleaseTags(") == 1 and
          "await Updater.ReleaseTags(VersionPolicy.Since)" in block(main, "async void LaunchVersionsOpened("),
          "the release list is not asked for by the list's opening alone")
    check('DropDownOpened="LaunchVersionsOpened"' in xaml, "opening the list does not ask for the releases")
    check("LaunchVersionsNote.Text" in block(main, "async void LaunchVersionsOpened(").split("catch (Exception error)")[1],
          "a release list GitHub refused is not said beside the list")

    # Update is the update it was: the same line, behind the track's newest, with no question, copy or pin of the switch's
    click = block(main, "void LaunchUpdateClick(object sender, RoutedEventArgs e)")
    check(main.count(UPDATE) == 1 and UPDATE in click, "the launcher's Update is not the line it was")
    check(in_order(click, ["case VersionPolicy.Step.Update:", UPDATE, "case VersionPolicy.Step.Switch:", "LaunchSwitch("]),
          "the update is not what the track's newest does")
    check("Backup" not in click and "MessageBox" not in click and "pin" not in click.replace("LaunchPickedTag", ""),
          "the update asks, copies or pins as a switch does")
    refresh = block(main, "void RefreshLaunch()")
    for text in ('$"Update available: {updateTarget}"', '"Could not check for updates (GitHub did not answer)"',
                 '$"{updateTarget} is out: build this folder again with the Hookshot Builder to get it"',
                 '"Up to date with this builder (GitHub did not answer)" : "Up to date"'):
        check(text in refresh, "the launcher's status lost: " + text)
    check('step == VersionPolicy.Step.Switch ? "Switch version" : "Update"' in refresh, "the button is not Update for the update")

    # a switch: asked, the saves copied (a copy that fails stops it), then that release by name. No pin before the build
    switch = block(main, "void LaunchSwitch(string game, string tag)")
    check(in_order(switch, ["MessageBox.Show(this, text,", "!= MessageBoxResult.Yes) return;", "VersionPolicy.Backup(launchFolder, game, tag)",
                            "catch (Exception error)", "return;\n        }\n        Updater.UpdateFolder(",
                            "Updater.UpdateFolder(this, launchFolder, tag, fetched: false, named: true, pin: pin);"]),
          "a switch is not: asked, copied, then updated by name")
    check("VersionPolicy.BackupHome(launchFolder)" in switch and "{copies}" in switch, "the question does not say where the copies go")
    check("SetPinnedTag" not in switch and "Settle" not in switch, "a switch writes the pin before its version is built")

    # the pin's two writers: the tick for the version installed, and the end of a build that went well
    check(main.count("VersionPolicy.SetPinnedTag(") == 1 and
          "VersionPolicy.SetPinnedTag(" in block(main, "void LaunchPinClick(object sender, RoutedEventArgs e)"),
          "the pin is written somewhere else than the tick")
    ended = block(main, "void Ended(string? failure)")
    good = ended[ended.index("if (finished.Length > 0)"):ended.index('if (failure == "cancelled")')]
    check(main.count("VersionPolicy.Settle(") == 1 and "VersionPolicy.Settle(finished, ReadVersion(finished), pin)" in good,
          "the pin is not settled by a build that ended well, and by that alone")
    check(in_order(ended, ["var pin = pinWhenBuilt;", "pinWhenBuilt = false;", "if (finished.Length > 0)"]),
          "a build that stopped leaves its pin for the next one")
    check("pinWhenBuilt = pin;" in block(main, "public void BeginUpdate(string folder, string? tag, bool pin = false)"),
          "an update does not say whether it asked for the pin")
    check("pinWhenBuilt = false;" in block(main, "void Run(Tools tools, List<string> args,"), "a build from game files inherits an update's pin")

    # the tick follows the folder's file whenever the list's pick changes
    check("void LaunchVersionChanged(object sender, SelectionChangedEventArgs e) => RefreshLaunch();" in main and
          "if (pinFor == game) LaunchPin.IsChecked = pinned;" in refresh, "the tick is not set from the folder's file with the list's pick")

    # the page's controls are the app's own (a list and a tick in Windows' looks are unreadable on its dark page)
    check('<ComboBox x:Name="LaunchVersions" Style="{StaticResource Choice}"' in xaml, "the version list is not styled as the app's lists")
    check('<CheckBox x:Name="LaunchPin" Style="{StaticResource Tick}"' in xaml, "the tick is not styled as the app's ticks")
    for handler in re.findall(r'(?:SelectionChanged|DropDownOpened|Click)="(Launch\w+)"', xaml):
        check(re.search(r"\bvoid %s\(" % handler, main), "MainWindow.xaml names %s, which the window does not have" % handler)

    # the first release with the list is the one the docs name
    since = re.search(r'public const string Since = "(v\d+\.\d+\.\d+)";', policy)
    check(since, "VersionPolicy.Since is not a release's name")
    if since:
        for doc in ("docs/versioning.md", "CONTRIBUTING.md"):
            check("`%s`" % since.group(1) in (source / doc).read_text(encoding="utf-8"),
                  "%s does not name %s, the first release with the version list (VersionPolicy.Since)" % (doc, since.group(1)))
    # the pin's file is the one the game reads (tests/test_version_pin.py has the game's side)
    check('File.WriteAllText(policy, $"mode=pinned\\nversion={tag}\\n");' in policy and
          'Path.Combine(folder, "user", "hookshot_update_policy.txt")' in policy, "the pin's file is not the two lines the game reads")
    return wrong


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", type=Path, default=ROOT)
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    wrong = read_the_window(args.source)
    if wrong:
        raise SystemExit("the launcher page:\n  " + "\n  ".join(wrong))
    print("the launcher page: read, as the reviews asked", flush=True)
    dotnet = shutil.which(args.dotnet)
    if not dotnet:
        parser.error("a .NET SDK is required (the builder's is .NET 8)")
    builder = args.source / "builder/HookshotBuilder"
    with tempfile.TemporaryDirectory(prefix="launcher-versions-") as folder:
        root = Path(folder)
        (root / "Updater.cs").write_text(ALIASES + (builder / "Updater.cs").read_text(encoding="utf-8"), encoding="utf-8")
        for name in ("VersionPolicy.cs", "BuilderHome.cs"):   # (as they are)
            shutil.copyfile(builder / name, root / name)
        shutil.copyfile(ROOT / "tests/launcher_versions_test.cs", root / "Program.cs")
        (root / "NuGet.Config").write_text("<configuration><packageSources><clear/></packageSources></configuration>", encoding="utf-8")
        project = root / "Fixture.csproj"
        project.write_text(PROJECT % "net8.0", encoding="utf-8")
        restore = subprocess.run([dotnet, "restore", str(project), "--disable-build-servers"], capture_output=True, text=True)
        if restore.returncode and "NU1100" in restore.stdout + restore.stderr:
            if os.environ.get("GITHUB_ACTIONS") == "true":   # (as tests/run_all.py tells a runner from a PC)
                raise SystemExit("on a GitHub runner the builder's files must build for its net8.0, and the reference pack "
                                 "is missing: no fallback to a later framework here\n" + restore.stdout + restore.stderr)
            sdk = subprocess.run([dotnet, "--version"], capture_output=True, text=True, check=True).stdout.strip()
            framework = "net%s.0" % sdk.split(".")[0]
            print("this .NET SDK (%s) has no .NET 8 reference pack: built for %s, not the builder's net8.0" % (sdk, framework),
                  flush=True)
            project.write_text(PROJECT % framework, encoding="utf-8")
        elif restore.returncode:
            raise SystemExit(restore.stdout + restore.stderr)
        # (the builder's folder and the game folders: authored, beside the fixture. The real BuilderHome reads the first)
        home, games = root / "builder home", root / "games é"
        home.mkdir()
        games.mkdir()
        env = dict(os.environ, HOOKSHOT_SETUP_BASE=str(home))
        env.pop("HOOKSHOT_BUILDER_PORTABLE", None)
        subprocess.run([dotnet, "run", "--project", str(project), "--configuration", "Release", "--disable-build-servers",
                        "--", str(games)], check=True, env=env)


if __name__ == "__main__":
    main()
