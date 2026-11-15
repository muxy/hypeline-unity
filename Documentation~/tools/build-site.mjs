import { cp, mkdir, readFile, readdir, writeFile } from "node:fs/promises";
import { resolve, join, relative } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { syncExamples } from "./sync-examples.mjs";

const source = fileURLToPath(new URL("../", import.meta.url));
const siteURL = "https://muxy.github.io/hypeline-unity/";

function stagingText(text) {
  // Route names, scopes and error codes are the existing API contract. Only
  // the target environment and the human-facing instructions change.
  const identifiers = ["/api/agent/sandbox/", "sandbox:test", "sandbox_unavailable", "sandbox_error"];
  for (const [i, value] of identifiers.entries()) text = text.replaceAll(value, `@@API_${i}@@`);
  text = text.replaceAll(siteURL, siteURL + "staging/")
    .replaceAll("https://dev.muxy.io", "https://dev.staging.muxy.io")
    .replaceAll("dev.muxy.io/games", "dev.staging.muxy.io/games")
    .replaceAll("https://sandbox.api.muxy.io", "https://api.staging.muxy.io")
    .replaceAll("https://api.muxy.io", "https://api.staging.muxy.io")
    .replaceAll("wss://sandbox.gamelink.muxy.io", "wss://gamelink.staging.muxy.io")
    .replaceAll("wss://gamelink.muxy.io", "wss://gamelink.staging.muxy.io")
    .replaceAll("RunInProduction", "RunInStaging").replaceAll("RunInSandbox", "RunInStaging")
    .replaceAll("Stage.Production", "Stage.Staging").replaceAll("Stage.Sandbox", "Stage.Staging")
    .replaceAll('"stage": "production"', '"stage": "staging"')
    .replace(/\bsandbox\b/g, "staging").replace(/\bSandbox\b/g, "Staging")
    .replaceAll("the staging", "staging")
    .replaceAll("The staging", "Staging")
    .replaceAll("#4-test-in-the-staging", "#4-test-in-staging")
    .replaceAll("(the guide's steps 5 and\n6)", "(a separate live-release check)")
    .replaceAll("the live Twitch check in the guide's steps 5 and 6", "the separate live Twitch release check")
    .replaceAll("#v0.2.0", "#main")
    .replaceAll("This installs release v0.2.0. To track unreleased changes instead, replace <code>#main</code> with <code>#main</code>.", "This installs the current staging-capable SDK from main. Record the resolved commit for reproducible tests.")
    .replaceAll("The new ID works immediately in staging and production.", "The new ID works in staging only; production and its sandbox use a separate registry.")
    .replaceAll('`"production"` or `"staging"`.', '`"staging"` for this environment.')
    .replaceAll("Set the stage back to production and run the doctor again.", "Keep the stage set to staging and run the doctor again.")
    .replaceAll('Set the stage back with a setup job: `"stage": "staging"`. The doctor reports a\n  manager left on staging.', 'Keep the manager on `"stage": "staging"` for this test project. The doctor reports\n  the selected environment; do not switch to production during staging tests.')
    .replaceAll("Those are the guide's steps 5 and 6, and they are still required before release.", "Live Twitch testing is separate and remains required before release.");
  for (const [i, value] of identifiers.entries()) text = text.replaceAll(`@@API_${i}@@`, value);
  return text;
}

async function transformTree(directory) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) await transformTree(path);
    else if (/\.(html|md|txt|cs)$/.test(entry.name)) await writeFile(path, stagingText(await readFile(path, "utf8")));
  }
}

export async function buildSite(destination) {
  destination = resolve(destination);
  if (destination === source || !relative(source, destination).startsWith("..")) throw new Error("Output must be outside Documentation~");
  // Refuse to erase an arbitrary directory passed by mistake.
  await mkdir(destination, { recursive: true });
  if ((await readdir(destination)).length) throw new Error("Output directory must be empty");
  const copyOptions = { recursive: true, filter: path => path !== join(source, "tools") };
  await cp(source, destination, copyOptions);
  const staging = join(destination, "staging");
  await cp(source, staging, copyOptions);
  await transformTree(staging);

  const promptPath = join(staging, "prompts/install-hypeline.md");
  let prompt = await readFile(promptPath, "utf8");
  prompt = prompt.replace("# Install Muxy Hypeline in my Unity project", "# Test Muxy Hypeline on staging in my Unity 6 project")
    .replace(/- If the portal offers staging testing,[\s\S]*?(?=\n- Use available Unity)/, "- Use the staging portal and staging backend throughout. Set the manager's Stage\n  to Staging (or use a setup job with `\"stage\":\"staging\"`) before checking the\n  Game ID. Keep it on Staging afterwards. Use Unity 6 and the SDK from `#main`.\n  Verify delivery with the staging test-viewer endpoints; live Twitch checks\n  remain a separate release task.")
    .replace(/5\. \*\*Connect Twitch\.[\s\S]*?(?=\n7\. \*\*Adapt)/, `5. **Connect to staging.** Follow the staging agent reference to request a PIN\n   for this Game ID. Set the manager to Staging, enter Play mode, enter the PIN\n   in the game's Hypeline panel, and confirm the game publishes its actions.\n   Keep PINs and tokens out of source, screenshots, commits, and reports.\n\n6. **Verify a staging round trip.** Send the healthpack through the staging\n   portal's test-viewer endpoint. Confirm health changes exactly once, the\n   required in-game viewer-action alert appears, and the transaction lookup\n   reports accepted. Exercise the refund path too. Report this as a staging\n   test, not a live Twitch or production verification.\n`)
    .replace("Set its Game ID to", "Set its Stage to Staging, then set its Game ID to")
    .replace("Distinguish local previews, real Twitch delivery, visible in-game alert,\ntransaction acknowledgment, extension state, broadcast video, actual gameplay,", "Distinguish local previews, staging delivery, visible in-game alert,\ntransaction acknowledgment, published game state, actual gameplay,")
    .replace("until the required live alert", "until the required staging viewer-action alert");
  await writeFile(promptPath, prompt);

  const agentsPath = join(staging, "agents.md");
  let agents = await readFile(agentsPath, "utf8");
  agents = agents.replace(/\| `stage` \|.*\|/, '| `stage` | `"staging"` for this environment. |');
  agents = agents.replace("# Muxy Hypeline for coding agents", "# Muxy Hypeline for coding agents — staging\n\nUse Unity 6 and the SDK from `https://github.com/muxy/hypeline-unity.git#main`.\nSet the manager to **Staging** before checking its Game ID. Portal, Game ID\nlookups, PINs, and game connections all stay in staging. Production's sandbox\nis a separate environment. The API retains its `/sandbox/` route names and\n`sandbox:test` scope for compatibility; PIN responses here say `staging`.");
  await writeFile(agentsPath, agents);

  const indexPath = join(staging, "index.html");
  let html = await readFile(indexPath, "utf8");
  html = html.replace("<title>Unity SDK walkthrough", "<title>Staging · Unity SDK walkthrough")
    .replace("Unity 2020.3+", "Unity 6")
    .replace("using Unity 2020.3 or newer", "using Unity 6")
    .replace(/<a href="https:\/\/github.com\/jrslyce\/foxfire-hypeline-demo">[^]*?<\/a>(?:<br>)?/g, "")
    .replace("Choose the starter project for your Unity version", "Use the Unity 6 starter project")
    .replace(/<p class="lead">[^]*?<\/p>/, '<p class="lead">Connect your Unity game to the staging backend and receive your first test-viewer action. Send <strong>Spawn Healthpack</strong> through the staging portal, confirm the sample&#x27;s health increases from 50 to 60, and check that an in-game alert identifies the viewer and action. Confirm the updated value through the portal&#x27;s game-state endpoint. The alert is a required part of this walkthrough.</p>')
    .replace("A Twitch broadcaster account that can install and activate the Muxy Gateway extension on its channel.", "An account on the staging developer portal to register a Game ID and approve agent access.")
    .replace("A separate viewer account or browser session to test the audience experience.", "An agent token with the staging guide&#x27;s scopes to request a PIN and send test-viewer actions. A live Twitch channel is not needed for this flow.")
    .replace(/<body([^>]*)>/, '<body$1><aside style="padding:1rem;background:#563c00;color:#fff;text-align:center"><strong>STAGING</strong> — Use Unity 6, SDK #main, and manager Stage = Staging. Staging Game IDs and tokens stay in staging.</aside>')
    .replace(/<section id="authenticate">[\s\S]*?<\/section>/, '<section id="authenticate"><div class="section-label">STEP 05</div><h2>Connect to staging</h2><p>Set the manager\'s Stage to <strong>Staging</strong> before checking its Game ID. Follow <a href="agents.md#4-test-in-the-staging">the staging agent reference</a> to approve access and request a test-channel PIN from the staging portal. Enter Play mode, enter the PIN in the Hypeline panel, and confirm the game publishes its actions.</p><p>Keep PINs and tokens private. This flow uses the staging backend, without a live Twitch channel.</p></section>')
    .replace(/<section id="first-action">[\s\S]*?<\/section>/, '<section id="first-action"><div class="section-label">STEP 06</div><h2>Send a staging test-viewer action</h2><p>Use the staging portal\'s action endpoint from <a href="agents.md#4-test-in-the-staging">the agent reference</a>. Confirm the healthpack changes health exactly once, the required in-game alert appears, and the transaction lookup reports <code>accepted</code>. Exercise the refund path and verify <code>refunded</code> too.</p><p>Keep the manager on Staging. Live Twitch and production verification are separate release checks.</p></section>')
    .replace("Set <strong>Game ID</strong>", "Set <strong>Stage</strong> to <strong>Staging</strong>, then set <strong>Game ID</strong>");
  html = html.replaceAll("#4-test-in-the-staging", "#4-test-in-staging");
  await writeFile(indexPath, html);
  const llmsPath = join(staging, "llms.txt");
  await writeFile(llmsPath, "# STAGING documentation\nUse the staging portal, Unity 6, SDK #main, and manager Stage = Staging.\n\n" + await readFile(llmsPath, "utf8"));
  await syncExamples(pathToFileURL(staging + "/"));
  return destination;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const output = process.argv[2];
  if (!output) throw new Error("Usage: node build-site.mjs OUTPUT_DIRECTORY");
  await buildSite(output);
  console.log("Built production docs and /staging/ docs");
}
