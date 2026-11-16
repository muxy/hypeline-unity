import { cp, mkdir, readdir } from "node:fs/promises";
import { resolve, join, relative } from "node:path";
import { fileURLToPath } from "node:url";

const source = fileURLToPath(new URL("../", import.meta.url));

// Copies the documentation to an empty directory for publishing. The site is
// static, so the published files are the source files, without the tools.
export async function buildSite(destination) {
  destination = resolve(destination);
  if (destination === source || !relative(source, destination).startsWith("..")) throw new Error("Output must be outside Documentation~");
  // Refuse to erase an arbitrary directory passed by mistake.
  await mkdir(destination, { recursive: true });
  if ((await readdir(destination)).length) throw new Error("Output directory must be empty");
  await cp(source, destination, { recursive: true, filter: path => path !== join(source, "tools") });
  return destination;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const output = process.argv[2];
  if (!output) throw new Error("Usage: node build-site.mjs OUTPUT_DIRECTORY");
  await buildSite(output);
  console.log("Built docs");
}
