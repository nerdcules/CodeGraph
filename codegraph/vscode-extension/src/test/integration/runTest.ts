// Starts VS Code with the extension on a copy of the Hello fixture and runs suite.ts inside it.
//   CODEGRAPH_TEST_INSTALL_DIR  the folder with codegraph-index (required: a build output or a release)
//   CODEGRAPH_TEST_VSCODE       a VS Code executable to use instead of downloading the stable build (optional)
//   CODEGRAPH_TEST_MCP          the codegraph-mcp the MCP step starts, when the install folder has none (a build output)
import { execFileSync } from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { runTests } from '@vscode/test-electron';

async function main(): Promise<void> {
  const installDir = process.env.CODEGRAPH_TEST_INSTALL_DIR;
  if (!installDir || !fs.existsSync(installDir)) {
    throw new Error('set CODEGRAPH_TEST_INSTALL_DIR to the folder holding codegraph-index');
  }
  const extensionDevelopmentPath = path.resolve(__dirname, '../../..');
  const fixture = path.join(extensionDevelopmentPath, 'test-fixtures', 'hello');
  // A copy outside any git repository, so the graph goes beside the solution and never into this repository's own.
  const workspace = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-vscode-'));
  fs.cpSync(fixture, workspace, { recursive: true });
  // The indexer reads a restored project: without obj/project.assets.json nothing resolves (its own note says so).
  execFileSync('dotnet', ['restore', 'Hello.slnx'], { cwd: workspace, stdio: 'inherit' });
  fs.mkdirSync(path.join(workspace, '.vscode'));
  fs.writeFileSync(
    path.join(workspace, '.vscode', 'settings.json'),
    JSON.stringify(
      {
        'codegraph.installDir': path.resolve(installDir),
        'codegraph.daemon.autoStart': false,
        'codegraph.checkForUpdates': false,
        'codegraph.daemon.idleExitMinutes': 10,
      },
      null,
      2,
    ),
  );
  const userData = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-vscode-user-'));
  try {
    await runTests({
      extensionDevelopmentPath,
      extensionTestsPath: path.resolve(__dirname, 'suite'),
      vscodeExecutablePath: process.env.CODEGRAPH_TEST_VSCODE || undefined,
      launchArgs: [workspace, '--disable-extensions', '--skip-welcome', '--skip-release-notes', '--disable-workspace-trust', `--user-data-dir=${userData}`],
      extensionTestsEnv: { CODEGRAPH_TEST_WORKSPACE: workspace },
    });
  } finally {
    fs.rmSync(workspace, { recursive: true, force: true });
    fs.rmSync(userData, { recursive: true, force: true });
  }
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
