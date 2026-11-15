import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { buildSite } from "./build-site.mjs";
import { syncExamples } from "./sync-examples.mjs";

test("staging guide, agent reference, prompt and examples stay in staging", async () => {
  const output = await mkdtemp(join(tmpdir(), "hypeline-docs-"));
  try {
    await buildSite(output);
    for (const name of ["index.html", "agents.md", "llms.txt", "prompts/install-hypeline.md", "examples/HypelineCodeDemo.cs", "examples/HypelineActionDemo.cs"]) {
      const original = await readFile(new URL("../" + name, import.meta.url), "utf8");
      assert.equal(await readFile(join(output, name), "utf8"), original, "production file changed: " + name);
      const staged = await readFile(join(output, "staging", name), "utf8");
      assert.doesNotMatch(staged, /https:\/\/dev\.muxy\.io|https:\/\/sandbox\.api\.muxy\.io|RunInProduction|RunInSandbox|"stage":\s*"(?:production|sandbox)"|#v0\.2\.0/, name);
      assert.doesNotMatch(staged, /https:\/\/muxy\.github\.io\/hypeline-unity\/(?!staging\/)/, name + " links to production docs");
      assert.doesNotMatch(staged, /@@API_/, name + " has unresolved placeholders");
    }
    const agent = await readFile(join(output, "staging/agents.md"), "utf8");
    assert.match(agent, /https:\/\/dev\.staging\.muxy\.io\/api\/agent\/sandbox\/my-game\/pin/);
    assert.match(agent, /sandbox:test/);
    assert.match(agent, /"stage": "staging"/);
    const anchors = new Set([...agent.matchAll(/^#{1,6} (.+)$/gm)].map(m => m[1].toLowerCase().replace(/[^a-z0-9 -]/g, "").replace(/ /g, "-")));
    for (const match of agent.matchAll(/\]\(#([^)]*)\)/g)) assert.ok(anchors.has(match[1]), "broken agent-doc anchor: " + match[1]);
    assert.doesNotMatch(agent, /Set the stage back|stage back to production/);
    const guide = await readFile(join(output, "staging/index.html"), "utf8");
    assert.match(guide, /hero-meta"><span>Unity 6<\/span>/);
    assert.doesNotMatch(guide, /Unity 2020\.3|href="https:\/\/github.com\/jrslyce\/foxfire-hypeline-demo"/);
    assert.match(guide, /Connect your Unity game to the staging backend/);
    assert.match(guide, /id="authenticate"[^]*Connect to staging/);
    assert.match(guide, /id="first-action"[^]*Send a staging test-viewer action/);
    assert.match(guide, /#4-test-in-staging/);
    // Wording that only fits the live Twitch flow.
    assert.doesNotMatch(guide, /Connect your Twitch channel|Test the complete round trip|extension is active|through the extension|viewer extension|extension reflects|extension update|Linked Games|Twitch channel if|Hypeline staging/);
    assert.match(guide, /<span>05<\/span> Connect to staging/);
    assert.match(guide, /Send healthpack through the staging test-viewer endpoint/);
    const prompt = await readFile(join(output, "staging/prompts/install-hypeline.md"), "utf8");
    assert.doesNotMatch(prompt, /\*\*Connect Twitch|manager back to the production stage|Linked\s+Games/);
    await syncExamples(pathToFileURL(join(output, "staging") + "/"), true);
    await assert.rejects(buildSite(output), /must be empty/);
  } finally {
    await rm(output, { recursive: true, force: true });
  }
});
