// Node on Windows aborts during startup when SystemRoot does not point at the
// real Windows directory. Tests still need a private SystemRoot so configure-ai
// resolves powershell.exe inside the fixture instead of Credential Manager.
// runCli starts the process with the real SystemRoot, then this preload puts
// the fixture path back before the CLI reads the environment.
const fake = process.env.CODEATLAS_FAKE_SYSTEM_ROOT;
if (typeof fake === "string" && fake.length > 0) {
  process.env.SystemRoot = fake;
  process.env.SYSTEMROOT = fake;
}
