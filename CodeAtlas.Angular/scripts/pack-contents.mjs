import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdtempSync, readdirSync, readFileSync, rmSync, statSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const packageRoot = fileURLToPath(new URL("..", import.meta.url));

function run(command, args) {
  const result = spawnSync(command, args, {
    cwd: packageRoot,
    encoding: "utf8",
    shell: process.platform === "win32",
    env: { ...process.env, SOURCE_DATE_EPOCH: "0" },
  });
  if (result.status !== 0) {
    throw new Error(
      `${command} ${args.join(" ")} failed (${result.status ?? "signal"})\n${result.stdout}\n${result.stderr}`,
    );
  }
}

function walkFiles(directory) {
  const files = [];
  for (const entry of readdirSync(directory)) {
    const full = path.join(directory, entry);
    if (statSync(full).isDirectory()) {
      files.push(...walkFiles(full));
    } else {
      files.push(full);
    }
  }
  return files;
}

const destination = mkdtempSync(path.join(tmpdir(), "codeatlas-ng-pack-"));
const extracted = mkdtempSync(path.join(tmpdir(), "codeatlas-ng-extract-"));
try {
  const npmCommand = process.platform === "win32" ? "npm.cmd" : "npm";
  run(npmCommand, ["pack", "--pack-destination", destination, "--json"]);
  const tarball = readdirSync(destination).find((name) => name.endsWith(".tgz"));
  if (tarball === undefined) {
    throw new Error("npm pack did not write a tarball");
  }
  run("tar", ["-xzf", path.join(destination, tarball), "-C", extracted]);
  const root = path.join(extracted, "package");
  const lines = walkFiles(root)
    .map((full) => {
      const relative = path.relative(root, full).split(path.sep).join("/");
      const hash = createHash("sha256").update(readFileSync(full)).digest("hex");
      return `${hash}  ${relative}`;
    })
    .sort((left, right) => {
      const leftPath = left.slice(left.indexOf("  ") + 2);
      const rightPath = right.slice(right.indexOf("  ") + 2);
      if (leftPath < rightPath) return -1;
      if (leftPath > rightPath) return 1;
      return 0;
    });
  process.stdout.write(`${lines.join("\n")}\n`);
} finally {
  rmSync(destination, { recursive: true, force: true });
  rmSync(extracted, { recursive: true, force: true });
}
