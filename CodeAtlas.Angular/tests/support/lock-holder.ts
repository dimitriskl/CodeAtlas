import { spawn, type ChildProcess } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";

/**
 * Another process creates `.write.lock` the way `index` does:
 * exclusive create, decimal pid, newline, fsync, then close.
 * The process stays alive until `stop`.
 */
export function holdWriterLock(lockPath: string): Promise<{ pid: number; stop: () => void }> {
  const child: ChildProcess = spawn(
    process.execPath,
    [
      "-e",
      "const fs=require('node:fs');const fd=fs.openSync(process.argv[1],'wx');fs.writeSync(fd,String(process.pid)+'\\n');fs.fsyncSync(fd);fs.closeSync(fd);setInterval(()=>{},1000);",
      lockPath,
    ],
    { stdio: "ignore" },
  );
  return new Promise((resolve, reject) => {
    const handles: {
      timer?: ReturnType<typeof setInterval>;
      timeout?: ReturnType<typeof setTimeout>;
    } = {};
    let settled = false;
    const finish = (error?: Error, pid?: number) => {
      if (settled) return;
      settled = true;
      if (handles.timer !== undefined) clearInterval(handles.timer);
      if (handles.timeout !== undefined) clearTimeout(handles.timeout);
      if (error !== undefined || pid === undefined) {
        child.kill("SIGKILL");
        reject(error ?? new Error("lock holder did not publish .write.lock"));
        return;
      }
      resolve({
        pid,
        stop: () => {
          child.kill("SIGKILL");
        },
      });
    };
    handles.timeout = setTimeout(() => {
      finish(new Error("lock holder did not publish .write.lock"));
    }, 5000);
    handles.timer = setInterval(() => {
      if (child.exitCode !== null) {
        finish(new Error("lock holder exited early"));
        return;
      }
      if (!existsSync(lockPath)) return;
      const pid = Number(readFileSync(lockPath, "utf8").trim());
      if (pid !== child.pid) return;
      finish(undefined, pid);
    }, 20);
  });
}
