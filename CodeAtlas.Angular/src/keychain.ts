import { writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { CommandError } from "./errors.js";

/** OS keychain. Tests inject a fake; production uses the platform tool. */
export interface Keychain {
  readonly name: string;
  isAvailable(): boolean;
  read(provider: string): string | null;
  write(provider: string, secret: string): void;
}

export type ProcessResult = {
  exitCode: number;
  stdout: string;
  stderr: string;
};

/** Runs keychain command-line tools. Secrets are passed on standard input, never as arguments. */
export interface ProcessRunner {
  exists(command: string): boolean;
  run(command: string, args: readonly string[], stdin: string | null): ProcessResult;
}

function storeError(message: string): CommandError {
  return new CommandError("ai_configuration_invalid", message, 1);
}

/** Absolute powershell.exe from %SystemRoot%. A bare name would be resolved from PATH. */
export function windowsPowershellExecutable(env: NodeJS.ProcessEnv): string | null {
  const root = env.SystemRoot ?? env.SYSTEMROOT;
  if (typeof root !== "string" || root.length === 0) return null;
  const executable = path.join(root, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
  return path.isAbsolute(executable) ? executable : null;
}

const windowsCredentialScript = `
param(
  [Parameter(Mandatory = $true)][ValidateSet("read", "write")][string]$Operation,
  [Parameter(Mandatory = $true)][string]$Target,
  [Parameter(Mandatory = $true)][string]$UserName
)
$ErrorActionPreference = "Stop"
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class CodeAtlasWinCred {
    const uint GenericType = 1;
    const uint PersistLocalMachine = 2;
    const int NotFound = 1168;
    const int MaximumBlobBytes = 2560;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NativeCredential {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll")]
    public static extern void CredFree(IntPtr buffer);

    public static int Read(string target) {
        IntPtr handle;
        if (!CredRead(target, GenericType, 0, out handle)) {
            int error = Marshal.GetLastWin32Error();
            return error == NotFound ? NotFound : Fail(error);
        }
        try {
            NativeCredential credential = (NativeCredential)Marshal.PtrToStructure(handle, typeof(NativeCredential));
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero) return NotFound;
            byte[] bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            Console.Out.Write(Encoding.UTF8.GetString(bytes));
            return 0;
        } finally {
            CredFree(handle);
        }
    }

    public static int Write(string target, string user, string secret) {
        byte[] bytes = Encoding.UTF8.GetBytes(secret);
        if (bytes.Length > MaximumBlobBytes) return Fail(1);
        IntPtr blob = Marshal.AllocCoTaskMem(System.Math.Max(1, bytes.Length));
        try {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            NativeCredential credential = new NativeCredential();
            credential.Type = GenericType;
            credential.TargetName = target;
            credential.Comment = "CodeAtlas AI provider key";
            credential.UserName = user;
            credential.CredentialBlobSize = (uint)bytes.Length;
            credential.CredentialBlob = blob;
            credential.Persist = PersistLocalMachine;
            if (!CredWrite(ref credential, 0)) return Fail(Marshal.GetLastWin32Error());
            return 0;
        } finally {
            if (bytes.Length > 0) Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    static int Fail(int error) {
        Console.Error.Write("Windows Credential Manager failed (" + error + ").");
        return error == 0 ? 1 : error;
    }
}
"@
if ($Operation -eq "read") {
  exit [CodeAtlasWinCred]::Read($Target)
}
$secret = [Console]::In.ReadToEnd()
exit [CodeAtlasWinCred]::Write($Target, $UserName, $secret)
`;

let windowsScriptPath: string | null = null;

function windowsCredentialScriptPath(): string {
  if (windowsScriptPath !== null) return windowsScriptPath;
  const file = path.join(tmpdir(), "codeatlas-ng-win-credential.ps1");
  writeFileSync(file, windowsCredentialScript.trimStart(), "utf8");
  windowsScriptPath = file;
  return file;
}

function assertPlainProvider(provider: string): void {
  if (!/^[a-z0-9]+$/.test(provider)) {
    throw storeError("The provider name cannot be stored in the keychain.");
  }
}

function trimTrailingNewlines(value: string): string {
  return value.replace(/[\r\n]+$/, "");
}

class MacKeychain implements Keychain {
  readonly name = "macOS Keychain";
  private readonly tool = "/usr/bin/security";
  private readonly service = "CodeAtlas";

  constructor(private readonly runner: ProcessRunner) {}

  isAvailable(): boolean {
    return this.runner.exists(this.tool);
  }

  read(provider: string): string | null {
    const result = this.runner.run(
      this.tool,
      ["find-generic-password", "-s", this.service, "-a", provider, "-w"],
      null,
    );
    if (result.exitCode === 44) return null;
    if (result.exitCode !== 0) {
      throw storeError(`macOS Keychain read failed (exit ${result.exitCode}).`);
    }
    const value = trimTrailingNewlines(result.stdout);
    return value.length === 0 ? null : value;
  }

  write(provider: string, secret: string): void {
    assertPlainProvider(provider);
    if (cannotQuoteForMacKeychain(secret)) {
      throw storeError(
        "The key contains characters the macOS Keychain tool cannot accept on standard input.",
      );
    }
    const command = `add-generic-password -U -s "${this.service}" -a "${provider}" -l "CodeAtlas AI key (${provider})" -w "${secret}"\n`;
    const result = this.runner.run(this.tool, ["-i"], command);
    if (result.exitCode !== 0) {
      throw storeError(`macOS Keychain write failed (exit ${result.exitCode}).`);
    }
  }
}

function cannotQuoteForMacKeychain(secret: string): boolean {
  for (const char of secret) {
    if (char === '"' || char === "\\") return true;
    if (/\s/u.test(char)) return true;
    const code = char.codePointAt(0) ?? 0;
    if (code < 32 || code === 127) return true;
  }
  return false;
}

class LinuxKeychain implements Keychain {
  readonly name = "Linux Secret Service";
  private readonly tool = "secret-tool";
  private readonly service = "codeatlas";

  constructor(
    private readonly runner: ProcessRunner,
    private readonly env: NodeJS.ProcessEnv,
  ) {}

  isAvailable(): boolean {
    const bus = this.env.DBUS_SESSION_BUS_ADDRESS;
    return bus !== undefined && bus.trim().length > 0 && this.runner.exists(this.tool);
  }

  read(provider: string): string | null {
    const result = this.runner.run(
      this.tool,
      ["lookup", "service", this.service, "provider", provider],
      null,
    );
    if (result.exitCode !== 0) return null;
    const value = trimTrailingNewlines(result.stdout);
    return value.length === 0 ? null : value;
  }

  write(provider: string, secret: string): void {
    assertPlainProvider(provider);
    const result = this.runner.run(
      this.tool,
      [
        "store",
        `--label=CodeAtlas AI key (${provider})`,
        "service",
        this.service,
        "provider",
        provider,
      ],
      secret,
    );
    if (result.exitCode !== 0) {
      throw storeError(`Linux Secret Service write failed (exit ${result.exitCode}).`);
    }
  }
}

class WindowsKeychain implements Keychain {
  readonly name = "Windows Credential Manager";
  private readonly tool: string | null;

  constructor(
    private readonly runner: ProcessRunner,
    env: NodeJS.ProcessEnv,
  ) {
    this.tool = windowsPowershellExecutable(env);
  }

  isAvailable(): boolean {
    return this.tool !== null && this.runner.exists(this.tool);
  }

  read(provider: string): string | null {
    const tool = this.tool;
    if (tool === null || !this.runner.exists(tool)) return null;
    const result = this.runner.run(tool, this.args("read", provider), null);
    if (result.exitCode === 1168) return null;
    if (result.exitCode !== 0) {
      throw storeError(`Windows Credential Manager read failed (exit ${result.exitCode}).`);
    }
    return result.stdout.length === 0 ? null : result.stdout;
  }

  write(provider: string, secret: string): void {
    assertPlainProvider(provider);
    const tool = this.tool;
    if (tool === null || !this.runner.exists(tool)) {
      throw storeError("Windows Credential Manager is not available.");
    }
    const bytes = Buffer.byteLength(secret, "utf8");
    if (bytes > 2560) {
      throw storeError("The key is too long for Windows Credential Manager.");
    }
    const result = this.runner.run(tool, this.args("write", provider), secret);
    if (result.exitCode !== 0) {
      throw storeError(`Windows Credential Manager write failed (exit ${result.exitCode}).`);
    }
  }

  private args(operation: "read" | "write", provider: string): string[] {
    return [
      "-NoProfile",
      "-NonInteractive",
      "-ExecutionPolicy",
      "Bypass",
      "-File",
      windowsCredentialScriptPath(),
      operation,
      `CodeAtlas:ai:${provider}`,
      "codeatlas",
    ];
  }
}

class UnavailableKeychain implements Keychain {
  readonly name = "OS keychain";

  isAvailable(): boolean {
    return false;
  }

  read(): string | null {
    return null;
  }

  write(): void {
    throw storeError("No OS keychain is available.");
  }
}

export function createKeychain(deps: {
  platform: NodeJS.Platform;
  runner: ProcessRunner;
  env: NodeJS.ProcessEnv;
}): Keychain {
  if (deps.platform === "darwin") return new MacKeychain(deps.runner);
  if (deps.platform === "linux") return new LinuxKeychain(deps.runner, deps.env);
  if (deps.platform === "win32") return new WindowsKeychain(deps.runner, deps.env);
  return new UnavailableKeychain();
}
