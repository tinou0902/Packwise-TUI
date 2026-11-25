// Creates the GitHub release of a tag on the public mirror (the GitLab push mirror copies tags, not releases), with
// the release archives as assets. Idempotent: an existing release is kept and only missing assets are uploaded.
// Usage: GITHUB_TOKEN=… node scripts/github-release.mjs <tag> <archive>...
import { readFileSync } from 'node:fs';
import { basename } from 'node:path';

const REPO = 'tinou0902/packwise-tui';
const [tag, ...files] = process.argv.slice(2);
const token = process.env.GITHUB_TOKEN;
if (!tag || files.length === 0 || !token) {
  console.error('Usage: GITHUB_TOKEN=… node scripts/github-release.mjs <tag> <archive>...');
  process.exit(2);
}

const headers = { authorization: `Bearer ${token}`, accept: 'application/vnd.github+json', 'x-github-api-version': '2022-11-28' };

async function api(url, init = {}) {
  const res = await fetch(url, { ...init, headers: { ...headers, ...init.headers } });
  if (!res.ok && res.status !== 404) throw new Error(`${init.method ?? 'GET'} ${url}: ${res.status} ${await res.text()}`);
  return res.status === 404 ? undefined : res.json();
}

const body = `Packwise TUI ${tag}: self-contained single-file binaries (no .NET runtime needed to start).
The .NET SDK is still required to manage NuGet projects, Node.js and npm for npm projects.`;

let release = await api(`https://api.github.com/repos/${REPO}/releases/tags/${tag}`);
if (release) console.log(`Release ${tag} already exists`);
else {
  release = await api(`https://api.github.com/repos/${REPO}/releases`, {
    method: 'POST',
    body: JSON.stringify({ tag_name: tag, name: `Packwise TUI ${tag}`, body, make_latest: 'legacy' }),
  });
  console.log(`Created release ${tag}`);
}

for (const file of files) {
  const name = basename(file);
  if (release.assets.some(a => a.name === name)) {
    console.log(`Asset ${name} already uploaded`);
    continue;
  }
  await api(`https://uploads.github.com/repos/${REPO}/releases/${release.id}/assets?name=${encodeURIComponent(name)}`, {
    method: 'POST',
    headers: { 'content-type': 'application/octet-stream' },
    body: readFileSync(file),
  });
  console.log(`Uploaded ${name}`);
}
console.log(release.html_url);
