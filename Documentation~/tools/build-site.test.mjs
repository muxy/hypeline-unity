import test from "node:test";
import assert from "node:assert/strict";
import { access, mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { buildSite } from "./build-site.mjs";
import { syncExamples } from "./sync-examples.mjs";

const published = ["index.html", "agents.md", "llms.txt", "prompts/install-hypeline.md", "examples/HypelineCodeDemo.cs", "examples/HypelineActionDemo.cs"];

test("the site is the documentation as written, with no staging copy", async () => {
  const output = await mkdtemp(join(tmpdir(), "hypeline-docs-"));
  try {
    await buildSite(output);
    for (const name of published) {
      const original = await readFile(new URL("../" + name, import.meta.url), "utf8");
      assert.equal(await readFile(join(output, name), "utf8"), original, "published file differs from source: " + name);
    }
    // Staging is internal: no staging guide is published, and the tools stay out.
    await assert.rejects(access(join(output, "staging")), "a staging guide was published");
    await assert.rejects(access(join(output, "tools")), "the build tools were published");
    await syncExamples(pathToFileURL(output + "/"), true);
    await assert.rejects(buildSite(output), /must be empty/);
  } finally {
    await rm(output, { recursive: true, force: true });
  }
});

test("the documentation describes testing on the developer's own channel", async () => {
  for (const name of published) {
    const text = await readFile(new URL("../" + name, import.meta.url), "utf8");
    // The sandbox stage is retired and staging is internal.
    assert.doesNotMatch(text, /sandbox|staging/i, name + " mentions a retired or internal stage");
    assert.doesNotMatch(text, /hypeline-test-(?!viewer)/, name + " mentions a test channel");
  }
  const agent = await readFile(new URL("../agents.md", import.meta.url), "utf8");
  assert.match(agent, /https:\/\/dev\.muxy\.io\/api\/agent\/test\/my-game\/pin/);
  assert.match(agent, /games:test/);
  assert.match(agent, /"stage": "production"/);
  assert.match(agent, /twitch_account_required/);
  const anchors = new Set([...agent.matchAll(/^#{1,6} (.+)$/gm)].map(m => m[1].toLowerCase().replace(/[^a-z0-9 -]/g, "").replace(/ /g, "-")));
  for (const match of agent.matchAll(/\]\(#([^)]*)\)/g)) assert.ok(anchors.has(match[1]), "broken agent-doc anchor: " + match[1]);
  const guide = await readFile(new URL("../index.html", import.meta.url), "utf8");
  for (const match of guide.matchAll(/href="agents\.md#([^"]*)"/g)) assert.ok(anchors.has(match[1]), "broken guide link to the agent doc: " + match[1]);
});
