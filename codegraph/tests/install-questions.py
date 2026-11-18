# /// script
# requires-python = ">=3.9"
# dependencies = [
#   "pyte>=0.8",
#   "pywinpty>=2.0; sys_platform == 'win32'",
#   "ptyprocess>=0.7; sys_platform != 'win32'",
# ]
# ///
"""Runs install.ps1 / install.sh in a pseudo-terminal, answers their questions, and checks what they did.

    uv run tests/install-questions.py [--work DIR] [--version vX.Y.Z] [--shells powershell,pwsh,bash]

Without uv: pip install pyte pywinpty (Windows) or pyte ptyprocess (elsewhere), then python tests/install-questions.py.

The scripts read their answers from a terminal, so each interactive case runs in a pseudo-terminal (ConPTY on
Windows, a pty elsewhere): the harness waits for a question on the rendered screen (pyte), types the keys, and
reads the screen back. Every case runs against a local copy of one release (downloaded once into WORK/mirror,
served to the scripts through CODEGRAPH_RELEASE_URL), so a run downloads the archive once, not once per case.

Nothing outside WORK is changed: each install goes into WORK, the VS Code extension goes into an extensions folder
of its own through a `code` wrapper put first on PATH for the child processes only, no case adds to PATH, and on
macOS and Linux HOME is a folder in WORK. On Windows the user PATH is read before and after and must not change.

It writes WORK/results.md and WORK/results.json, and one screen per interactive case in WORK/screens. The exit code
is 1 when a case fails.
"""
import argparse
import datetime
import hashlib
import json
import os
import pathlib
import platform
import queue
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request

import pyte

WINDOWS = sys.platform == "win32"
if WINDOWS:
    from winpty import PtyProcess  # pywinpty
else:
    from ptyprocess import PtyProcess

REPO_SLUG = "nerdcules/CodeGraph"
HERE = pathlib.Path(__file__).resolve().parent.parent  # the codegraph folder
EXT_ID = "nerdcules.codegraph"
KEYS = {"ENTER": "\r", "UP": "\x1b[A", "DOWN": "\x1b[B", "SPACE": " "}


# ---- the release, served from a folder -------------------------------------------------------------------
def rid():
    machine = platform.machine().lower()
    cpu = {"x86_64": "x64", "amd64": "x64", "aarch64": "arm64", "arm64": "arm64"}.get(machine)
    osname = {"win32": "win", "darwin": "osx"}.get(sys.platform, "linux")
    if not cpu:
        sys.exit(f"no release is built for this CPU ({machine})")
    return f"{osname}-{cpu}"


def asset_name():
    r = rid()
    return f"codegraph-{r}.zip" if r.startswith("win-") else f"codegraph-{r}.tar.gz"


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def build_mirror(work, version):
    """WORK/mirror/download/<version>/ with the checksums, this platform's archive and the extension, and a
    release without the extension (WORK/mirror/download/v0.0.0-novsix) for the case that needs one."""
    real = work / "mirror" / "download" / version
    real.mkdir(parents=True, exist_ok=True)
    base = f"https://github.com/{REPO_SLUG}/releases/download/{version}"
    sums_path = real / "SHA256SUMS"
    if not sums_path.exists():
        urllib.request.urlretrieve(f"{base}/SHA256SUMS", sums_path)
    sums = {line.split()[1]: line.split()[0] for line in sums_path.read_text().splitlines() if line.strip()}
    for name in (asset_name(), "codegraph-vscode.vsix"):
        if name not in sums:
            if name.endswith(".vsix"):
                continue
            sys.exit(f"{version} has no {name}")
        target = real / name
        if not target.exists() or sha256(target) != sums[name]:
            print(f"downloading {name} ({version}) ...", flush=True)
            urllib.request.urlretrieve(f"{base}/{name}", target)
            if sha256(target) != sums[name]:
                sys.exit(f"checksum mismatch for {name}")
    novsix = work / "mirror" / "download" / "v0.0.0-novsix"
    novsix.mkdir(parents=True, exist_ok=True)
    (novsix / "SHA256SUMS").write_text(
        "".join(f"{sums[n]}  {n}\n" for n in sums if not n.endswith(".vsix")))
    link = novsix / asset_name()
    if not link.exists():
        try:
            os.link(real / asset_name(), link)
        except OSError:
            shutil.copy2(real / asset_name(), link)
    return (work / "mirror").as_uri(), "codegraph-vscode.vsix" in sums


# ---- the machine -----------------------------------------------------------------------------------------
def run(argv, **kw):
    return subprocess.run(argv, capture_output=True, text=True, encoding="utf-8", errors="replace", **kw)


def make_code_wrapper(work):
    real = shutil.which("code")
    if not real:
        return None
    fakebin = work / "fakebin"
    fakebin.mkdir(parents=True, exist_ok=True)
    ext, data = work / "vsext", work / "vsdata"
    if WINDOWS:
        wrapper = fakebin / "code.cmd"
        wrapper.write_text(f'@echo off\r\n"{real}" --extensions-dir "{ext}" --user-data-dir "{data}" %*\r\n')
    else:
        wrapper = fakebin / "code"
        wrapper.write_text(f'#!/usr/bin/env bash\nexec "{real}" --extensions-dir "{ext}" --user-data-dir "{data}" "$@"\n')
        wrapper.chmod(0o755)
    return wrapper


def code(env, *args):
    """Runs `code` as the scripts find it: the wrapper, first on the children's PATH."""
    return run((["cmd", "/c", "code"] if WINDOWS else [shutil.which("code", path=env["PATH"]) or "code"]) + list(args), env=env)


def extension_version(env):
    """The version of the extension in the wrapper's folder, or None."""
    for line in code(env, "--list-extensions", "--show-versions").stdout.splitlines():
        if line.lower().startswith(EXT_ID + "@"):
            return line.split("@", 1)[1].strip()
    return None


def uninstall_extension(env):
    if extension_version(env):
        code(env, "--uninstall-extension", EXT_ID)


def user_path():
    if not WINDOWS:
        return None
    import winreg
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as key:
        try:
            return winreg.QueryValueEx(key, "Path")[0]
        except FileNotFoundError:
            return ""


def make_template_repo(work, env):
    """A small repository to set up: TypeScript when node is on PATH (fast), else C# when dotnet is."""
    repo = work / "template"
    if (repo / ".git").exists():
        return repo, (repo / "tsconfig.json").exists() and "typescript" or "csharp"
    repo.mkdir(parents=True, exist_ok=True)
    if shutil.which("node"):
        kind = "typescript"
        (repo / "src").mkdir(exist_ok=True)
        (repo / "tsconfig.json").write_text('{ "compilerOptions": { "target": "es2022", "module": "nodenext", "strict": true }, "include": ["src"] }\n')
        (repo / "src" / "math.ts").write_text("export function add(a: number, b: number) { return a + b; }\nexport const three = add(1, 2);\n")
        (repo / "package.json").write_text('{ "name": "tiny", "version": "1.0.0" }\n')
    elif shutil.which("dotnet"):
        kind = "csharp"
        for args in (["new", "console", "-n", "Tiny", "-o", str(repo), "--force"], ["new", "sln", "-n", "Tiny", "-o", str(repo), "--force"]):
            r = run(["dotnet", *args], env=env)
            if r.returncode != 0:
                sys.exit(f"dotnet {' '.join(args)} failed:\n{r.stdout}{r.stderr}")
        sln = next(repo.glob("Tiny.sln*"))
        run(["dotnet", "sln", str(sln), "add", str(repo / "Tiny.csproj")], env=env)
    else:
        sys.exit("node or dotnet is needed on PATH for the repository the cases set up")
    for args in (["init", "-q"], ["add", "-A"], ["-c", "user.name=t", "-c", "user.email=t@t", "commit", "-qm", "init"]):
        run(["git", *args], cwd=repo, env=env)
    return repo, kind


def fresh_repo(template, target, env):
    if target.exists():
        shutil.rmtree(target)
    run(["git", "clone", "-q", str(template), str(target)], env=env)
    return target


# ---- a pseudo-terminal -------------------------------------------------------------------------------------
class Terminal:
    """A child process in a pseudo-terminal, rendered by pyte."""

    def __init__(self, argv, cwd, env, rows, cols, log):
        self.screen = pyte.HistoryScreen(cols, rows, history=10000)
        self.stream = pyte.Stream(self.screen)
        self.chunks = queue.Queue()
        self.log = open(log, "w", encoding="utf-8", newline="")
        if WINDOWS:
            self.proc = PtyProcess.spawn(subprocess.list2cmdline(argv), cwd=str(cwd), env=env, dimensions=(rows, cols))
        else:
            self.proc = PtyProcess.spawn(argv, cwd=str(cwd), env=env, dimensions=(rows, cols))
        threading.Thread(target=self._reader, daemon=True).start()
        self.eof = False

    def _reader(self):
        while True:
            try:
                data = self.proc.read(65536)
            except EOFError:
                self.chunks.put(None)
                return
            except OSError:
                self.chunks.put(None)
                return
            if isinstance(data, bytes):
                data = data.decode("utf-8", errors="replace")
            self.chunks.put(data)

    def pump(self, wait=0.2):
        end = time.time() + wait
        while True:
            try:
                data = self.chunks.get(timeout=max(0.0, end - time.time()))
            except queue.Empty:
                return
            if data is None:
                self.eof = True
                return
            self.log.write(data)
            self.log.flush()
            self.stream.feed(data)

    def text(self):
        return "".join(self.screen.display)

    def wait_for(self, needle, timeout):
        end = time.time() + timeout
        while time.time() < end:
            if needle in self.text():
                return True
            if self.eof:
                return needle in self.text()
            self.pump(0.3)
        return False

    def send(self, keys):
        for key in keys:
            text = KEYS.get(key, key)
            self.proc.write(text if WINDOWS else text.encode("utf-8"))  # winpty takes str, ptyprocess bytes
            time.sleep(0.15)
            self.pump(0.05)

    def finish(self, timeout):
        end = time.time() + timeout
        while not self.eof and time.time() < end:
            self.pump(0.5)
        while self.proc.isalive() and time.time() < end:
            time.sleep(0.2)
        if self.proc.isalive():
            self.proc.terminate(force=True)
            return None
        return self.proc.exitstatus

    def dump(self):
        history = ["".join(ch.data for ch in (line[x] for x in range(self.screen.columns))).rstrip()
                   for line in self.screen.history.top]
        lines = history + [line.rstrip() for line in self.screen.display]
        while lines and not lines[-1]:
            lines.pop()
        return "\n".join(lines) + "\n"


# ---- the cases -----------------------------------------------------------------------------------------------
class Case:
    def __init__(self, shell, name, title):
        self.shell, self.name, self.title = shell, name, title
        self.checks, self.status, self.note = [], "pass", ""

    def check(self, ok, what):
        self.checks.append(("ok" if ok else "FAIL", what))
        if not ok:
            self.status = "fail"
        return ok

    def skip(self, why):
        self.status, self.note = "skip", why


class Shell:
    """How to run the install scripts in one shell: powershell (5.1), pwsh, or bash."""

    def __init__(self, name):
        self.name = name
        self.ps = name in ("powershell", "pwsh")
        if name == "powershell":  # Windows PowerShell 5.1, Windows only
            self.exe = shutil.which("powershell.exe") if WINDOWS else None
        else:
            self.exe = shutil.which(name)

    def available(self):
        return self.exe is not None

    def script(self, which):
        return str(HERE / (f"{which}.ps1" if self.ps else f"{which}.sh"))

    def argv(self, which, args):
        if self.ps:
            head = [self.exe, "-NoProfile"] + (["-ExecutionPolicy", "Bypass"] if WINDOWS else []) + ["-File", self.script(which)]
        else:
            head = [self.exe, self.script(which)]
        return head + args

    def opt(self, name, value=None):
        names = {"install-dir": "-InstallDir", "version": "-Version", "yes": "-Yes", "vscode": "-VSCode",
                 "force": "-Force", "repo": "-Repo"}
        flag = names[name] if self.ps else f"--{name}"
        return [flag] if value is None else [flag, str(value)]

    def version(self):
        if self.ps:
            r = run([self.exe, "-NoProfile", "-Command", "$PSVersionTable.PSVersion.ToString(); [Console]::OutputEncoding.CodePage"])
        else:
            r = run([self.exe, "-c", "echo $BASH_VERSION"])
        return " / ".join(r.stdout.split())


def start_server(install_dir):
    exe = install_dir / ("codegraph-mcp.exe" if WINDOWS else "codegraph-mcp")
    if not exe.exists():
        return None
    proc = subprocess.Popen([str(exe)], stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(2)
    return proc


def stop(proc):
    if proc and proc.poll() is None:
        proc.kill()
        proc.wait(10)


def run_shell(sh, work, version, env, template, have_code, have_vsix, timeout):
    base = work / sh.name
    if base.exists():
        shutil.rmtree(base, ignore_errors=True)
    base.mkdir(parents=True)
    screens = work / "screens"
    screens.mkdir(exist_ok=True)
    cases = []

    def noninteractive(name, title, args, cwd):
        case = Case(sh.name, name, title)
        r = run(sh.argv("install", args), cwd=cwd, env=env, stdin=subprocess.DEVNULL, timeout=timeout)
        out = r.stdout + r.stderr
        (screens / f"{sh.name}-{name}.txt").write_text(out, encoding="utf-8")
        cases.append(case)
        return case, r.returncode, out

    def interactive(name, title, args, cwd, steps, rows=50, cols=160):
        """steps: (text to wait for, keys to send) pairs. Returns the case, the exit code and the terminal."""
        case = Case(sh.name, name, title)
        cases.append(case)
        term = Terminal(sh.argv("install", args), cwd, env, rows, cols, screens / f"{sh.name}-{name}.log")
        snapshots = {}
        for needle, keys in steps:
            if not term.wait_for(needle, timeout):
                case.check(False, f"the question with {needle!r} appeared")
                (screens / f"{sh.name}-{name}.txt").write_text(term.dump(), encoding="utf-8")
                term.finish(5)
                return case, None, term, snapshots
            time.sleep(0.2)
            term.pump(0.1)
            snapshots[needle] = term.text()
            term.send(keys)
        code = term.finish(timeout)
        (screens / f"{sh.name}-{name}.txt").write_text(term.dump(), encoding="utf-8")
        return case, code, term, snapshots

    # N1: no terminal, a repository given: no question, exit 0, the repository set up.
    d1, r1 = base / "n1" / "bin", fresh_repo(template, base / "n1-repo", env)
    case, code, out = noninteractive("N1", "no terminal, -Yes and a repository",
                                     sh.opt("install-dir", d1) + sh.opt("version", version) + sh.opt("yes") + [str(r1)], r1)
    case.check(code == 0, f"exit code 0 (was {code})")
    case.check("? " not in "\n".join(l[:2] for l in out.splitlines()), "no question was printed")
    case.check((r1 / ".mcp.json").exists(), ".mcp.json written in the repository")
    case.check((d1 / ("codegraph-index.exe" if WINDOWS else "codegraph-index")).exists(), "codegraph-index installed")

    # N2: -VSCode with no terminal.
    case = Case(sh.name, "N2", "no terminal, -VSCode -Yes")
    if not have_code:
        cases.append(case); case.skip("code is not on PATH")
    elif not have_vsix:
        cases.append(case); case.skip(f"release {version} has no codegraph-vscode.vsix")
    else:
        uninstall_extension(env)
        case, code, out = noninteractive("N2", "no terminal, -VSCode -Yes",
                                         sh.opt("install-dir", d1) + sh.opt("version", version) + sh.opt("vscode") + sh.opt("yes"), base)
        case.check(code == 0, f"exit code 0 (was {code})")
        installed = extension_version(env)
        case.check(installed is not None, f"the extension is installed ({installed})")
        case.check("codegraph.installDir" in out, "the summary says to set codegraph.installDir (not the default folder)")

    # N3: -VSCode with a release that has no extension stops before anything is installed.
    d3 = base / "n3" / "bin"
    case, code, out = noninteractive("N3", "-VSCode with a release without the extension",
                                     sh.opt("install-dir", d3) + sh.opt("version", "v0.0.0-novsix") + sh.opt("vscode") + sh.opt("yes"), base)
    case.check(code != 0, f"non-zero exit code (was {code})")
    case.check("has no VS Code extension" in out, "says the release has no VS Code extension")
    case.check(not d3.exists(), "the install folder was not created")

    # N4: a running server, no terminal: refused without -Force, stopped with it.
    case = Case(sh.name, "N4", "a running codegraph-mcp, no terminal")
    cases.append(case)
    server = start_server(d1)
    if server is None:
        case.check(False, f"{d1} has codegraph-mcp to start (N1 did not install it)")
    try:
        if server is None:
            raise RuntimeError("skipped")
        r = run(sh.argv("install", sh.opt("install-dir", d1) + sh.opt("version", version) + sh.opt("yes")), cwd=base, env=env,
                stdin=subprocess.DEVNULL, timeout=timeout)
        case.check(r.returncode != 0 and "is running" in r.stdout + r.stderr, f"refused without -Force (exit {r.returncode})")
        case.check(server.poll() is None, "the server still runs after the refusal")
        r = run(sh.argv("install", sh.opt("install-dir", d1) + sh.opt("version", version) + sh.opt("yes") + sh.opt("force")),
                cwd=base, env=env, stdin=subprocess.DEVNULL, timeout=timeout)
        case.check(r.returncode == 0, f"exit code 0 with -Force (was {r.returncode})")
        time.sleep(1)
        case.check(server.poll() is not None, "the server was stopped")
        (screens / f"{sh.name}-N4.txt").write_text(r.stdout + r.stderr, encoding="utf-8")
    except RuntimeError:
        pass
    finally:
        stop(server)

    # I1: every question, answered in a terminal.
    if have_code:
        uninstall_extension(env)
    d_i1, r_i1 = base / "i1" / "bin", fresh_repo(template, base / "i1-repo", env)
    ext_step = [("extension for VS Code", ["y"])] if have_code and have_vsix else []
    case, code, term, shots = interactive("I1", "every question, in a terminal", sh.opt("version", version), r_i1, [
        ("Install folder", [str(d_i1), "ENTER"]),
        ("Set up ", ["y"]),
        ("Another repository", ["ENTER"]),
        # all on, all off, the first row on, then up from the first row wraps to the last (Continue) and turns it on
        ("Assistants to set", ["a", "a", "SPACE", "UP", "SPACE", "ENTER"]),
        ("live daemon on its first call", ["n"]),
        ("to PATH", ["n"]),
        *ext_step,
        ("Go ahead", ["y"]),
    ])
    if code is not None or shots:
        folded = shots.get("live daemon on its first call", "")
        case.check("Assistants to set each repository up for Claude Code, Copilot CLI, Continue" in folded,
                   "the list folded into one line naming Claude Code and Continue")
        case.check("Kilo Code" not in folded, "no row of the list is left on the screen after it folded")
        if ext_step:
            case.check("Install the CodeGraph extension for VS Code?" in shots.get("extension for VS Code", ""),
                       "the extension question says Install (none installed)")
        case.check(code == 0, f"exit code 0 (was {code})")
        case.check((r_i1 / ".mcp.json").exists(), ".mcp.json written")
        case.check((r_i1 / ".continue" / "mcpServers" / "codegraph.json").exists(), ".continue/mcpServers/codegraph.json written")
        case.check(not (r_i1 / ".kilo").exists(), "nothing written for Kilo Code (not picked)")
        if ext_step:
            case.check(extension_version(env) is not None, "the extension is installed")

    # I2: a window too small for the list, and No to the plan.
    d_i2 = base / "i2" / "bin"
    ext_step = [("extension for VS Code", ["n"])] if have_code and have_vsix else []
    case, code, term, shots = interactive("I2", "a 14-row window, No to the plan", sh.opt("version", version), r_i1, [
        ("Install folder", [str(d_i2), "ENTER"]),
        ("Set up ", ["y"]),
        ("Another repository", ["ENTER"]),
        ("Assistants to set", ["a", "a", "SPACE", "DOWN", "SPACE", "ENTER"]),
        ("live daemon on its first call", ["n"]),
        ("to PATH", ["n"]),
        *ext_step,
        ("Go ahead", ["n"]),
    ], rows=14, cols=160)
    if code is not None or shots:
        folded = shots.get("live daemon on its first call", "")
        case.check("Assistants to set each repository up for Claude Code, Copilot CLI, VS Code chat" in folded,
                   "the list folded into one line naming Claude Code and VS Code chat")
        case.check("Kilo Code" not in folded and "Roo Code" not in folded, "no row of the list is left on the screen")
        if ext_step:
            case.check("installed:" in shots.get("extension for VS Code", ""), "the extension question names the installed version")
        case.check("Nothing was changed." in term.text(), "says Nothing was changed.")
        case.check(code == 0, f"exit code 0 (was {code})")
        case.check(not d_i2.exists(), "the install folder was not created")

    # I3: a running server, in a terminal: No keeps it running and stops; Yes stops it and installs.
    for answer, name in (("n", "I3a"), ("y", "I3b")):
        server = start_server(d_i1)
        if server is None:
            c = Case(sh.name, name, f"a running codegraph-mcp, answer {answer} to Stop it?")
            c.check(False, f"{d_i1} has codegraph-mcp to start (I1 did not install it)")
            cases.append(c)
            continue
        ext_step = [("extension for VS Code", ["n"])] if have_code and have_vsix else []
        try:
            case, code, term, shots = interactive(name, f"a running codegraph-mcp, answer {answer} to Stop it?",
                                                  sh.opt("version", version), r_i1, [
                ("Install folder", [str(d_i1), "ENTER"]),
                ("Set up ", ["n"]),
                ("Repository to set up", ["ENTER"]),
                ("to PATH", ["n"]),
                *ext_step,
                ("Go ahead", ["y"]),
                ("Stop it?", [answer]),
            ])
            time.sleep(1)
            if answer == "n":
                case.check(code not in (0, None), f"non-zero exit code (was {code})")
                case.check(server.poll() is None, "the server still runs")
            else:
                case.check(code == 0, f"exit code 0 (was {code})")
                case.check(server.poll() is not None, "the server was stopped")
        finally:
            stop(server)

    # U1: uninstall, no terminal.
    case = Case(sh.name, "U1", "uninstall -Yes")
    cases.append(case)
    r = run(sh.argv("uninstall", sh.opt("install-dir", d_i1) + sh.opt("repo", r_i1) + sh.opt("yes")), cwd=base, env=env,
            stdin=subprocess.DEVNULL, timeout=timeout)
    (screens / f"{sh.name}-U1.txt").write_text(r.stdout + r.stderr, encoding="utf-8")
    case.check(r.returncode == 0, f"exit code 0 (was {r.returncode})")
    case.check(not d_i1.exists(), "the install folder was deleted")
    mcp = r_i1 / ".mcp.json"
    case.check(not mcp.exists() or "codegraph" not in json.loads(mcp.read_text() or "{}").get("mcpServers", {}),
               "the codegraph entry is gone from .mcp.json")
    if have_code:
        case.check(extension_version(env) is None, "the extension was uninstalled")
    return cases


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--work", default=str(pathlib.Path(tempfile.gettempdir()) / "cg-install-questions"))
    parser.add_argument("--version", default="v1.3.0-beta.1")
    parser.add_argument("--shells", default="powershell,pwsh" if WINDOWS else "bash,pwsh")
    parser.add_argument("--timeout", type=int, default=600, help="seconds for each script run")
    args = parser.parse_args()

    work = pathlib.Path(args.work).resolve()
    work.mkdir(parents=True, exist_ok=True)
    started = datetime.datetime.now().astimezone()
    path_before = user_path()

    mirror, have_vsix = build_mirror(work, args.version)
    env = dict(os.environ)
    env["CODEGRAPH_RELEASE_URL"] = mirror
    env.pop("CODEGRAPH_NONINTERACTIVE", None)
    env.pop("NO_COLOR", None)
    if not WINDOWS:
        env["TERM"] = "xterm-256color"
        home = work / "home"
        home.mkdir(exist_ok=True)
        (home / ".bashrc").touch()
        env["HOME"] = str(home)
        env.setdefault("LANG", "en_US.UTF-8")
    wrapper = make_code_wrapper(work)
    if wrapper:
        env["PATH"] = str(wrapper.parent) + os.pathsep + env["PATH"]
    template, kind = make_template_repo(work, env)

    info = {
        "started": started.isoformat(timespec="seconds"),
        "platform": platform.platform(),
        "python": platform.python_version(),
        "release": args.version,
        "repository kind": kind,
        "commit": run(["git", "-C", str(HERE), "log", "--oneline", "-1"]).stdout.strip(),
        "code": (code(env, "--version").stdout.splitlines() or ["?"])[0] if wrapper else "not on PATH",
    }
    cases = []
    for name in args.shells.split(","):
        sh = Shell(name.strip())
        if not sh.available():
            c = Case(sh.name, "-", "the shell"); c.skip(f"{sh.name} is not installed"); cases.append(c)
            continue
        info[f"shell {sh.name}"] = sh.version()
        print(f"== {sh.name}", flush=True)
        try:
            shell_cases = run_shell(sh, work, args.version, env, template, wrapper is not None, have_vsix, args.timeout)
        except Exception:  # one shell's crash is a failed case, not the end of the run
            import traceback
            c = Case(sh.name, "!", "the harness")
            c.check(False, "crashed: " + traceback.format_exc().replace("\n", " / "))
            shell_cases = [c]
        for c in shell_cases:
            print(f"  {c.status:4}  {c.name:4} {c.title}" + (f" ({c.note})" if c.note else ""), flush=True)
            for state, what in c.checks:
                if state != "ok":
                    print(f"          FAIL {what}", flush=True)
            cases.append(c)

    if WINDOWS:
        c = Case("-", "P1", "the user PATH did not change")
        c.check(user_path() == path_before, "the user PATH is byte-identical before and after")
        cases.append(c)
    if not WINDOWS:
        c = Case("-", "P1", "no rc file was written")
        c.check((work / "home" / ".bashrc").read_text() == "" and not (work / "home" / ".zshrc").exists(),
                "~/.bashrc in WORK/home is still empty, and there is no ~/.zshrc")
        cases.append(c)

    info["finished"] = datetime.datetime.now().astimezone().isoformat(timespec="seconds")
    failed = [c for c in cases if c.status == "fail"]
    lines = ["# install script questions: results", ""]
    lines += [f"- {k}: {v}" for k, v in info.items()]
    summary = (f"{sum(c.status == 'pass' for c in cases)} passed, {len(failed)} failed, "
               f"{sum(c.status == 'skip' for c in cases)} skipped, of {len(cases)} cases.")
    lines += ["", summary, "",
              "| Shell | Case | What | Result | Checks |", "|---|---|---|---|---|"]
    for c in cases:
        checks = "<br>".join(f"{s} {w}" for s, w in c.checks) or c.note
        lines.append(f"| {c.shell} | {c.name} | {c.title} | {c.status} | {checks} |")
    lines += ["", "Each case's screen or output is in screens/<shell>-<case>.txt; interactive cases also have a raw .log."]
    (work / "results.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    (work / "results.json").write_text(json.dumps({"info": info, "cases": [
        {"shell": c.shell, "case": c.name, "title": c.title, "status": c.status, "note": c.note,
         "checks": [{"result": s, "check": w} for s, w in c.checks]} for c in cases]}, indent=2), encoding="utf-8")
    print(f"\n{summary}\nresults: {work / 'results.md'}")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
