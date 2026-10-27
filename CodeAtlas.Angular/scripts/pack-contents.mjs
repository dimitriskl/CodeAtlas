import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import {
  mkdirSync,
  mkdtempSync,
  readdirSync,
  readFileSync,
  rmSync,
  statSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { gunzipSync } from "node:zlib";

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

function headerField(header, start, end) {
  const raw = header.subarray(start, end).toString("utf8");
  const terminator = raw.indexOf("\0");
  return (terminator === -1 ? raw : raw.slice(0, terminator)).trim();
}

/** Read a ustar gzip archive. Git's tar treats `C:` as a remote host, so Windows cannot use it. */
function extractTarball(tarballPath, destination) {
  const archive = gunzipSync(readFileSync(tarballPath));
  let offset = 0;
  while (offset + 512 <= archive.length) {
    const header = archive.subarray(offset, offset + 512);
    if (header.every((byte) => byte === 0)) break;
    const name = headerField(header, 0, 100);
    const prefix = headerField(header, 345, 500);
    const size = Number.parseInt(headerField(header, 124, 136), 8);
    const type = header[156];
    const entryName = prefix.length > 0 ? `${prefix}/${name}` : name;
    offset += 512;
    const data = archive.subarray(offset, offset + (Number.isNaN(size) ? 0 : size));
    offset += Math.ceil((Number.isNaN(size) ? 0 : size) / 512) * 512;
    if (type !== 0 && type !== 48) continue;
    const parts = entryName.split("/");
    if (parts.includes("..")) throw new Error(`unsafe tarball path ${entryName}`);
    const out = path.join(destination, ...parts);
    mkdirSync(path.dirname(out), { recursive: true });
    writeFileSync(out, data);
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
  extractTarball(path.join(destination, tarball), extracted);
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
