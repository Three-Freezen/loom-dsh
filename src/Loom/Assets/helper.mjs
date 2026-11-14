// Loom for DSH - Electron Node helper (compatibility evaluator bridge).
//
// Runs under the installed DSH Electron Node:
//   ELECTRON_RUN_AS_NODE=1 "<installDir>\DeepSeek Harness.exe" --expose-internals "<dataRoot>\helper\helper.mjs"
//
// stdin : one JSON object:
//   {
//     schemaVersion: 1,
//     mode: "evaluate" | "probe",
//     appRoot: "D:/DeepSeek/resources/app.asar",
//     profileDir: "C:/Users/<user>/.dsh/profiles/desktop",
//     runtimeVersion?: "0.2.2-rc.1",          // optional override; else official/fallback detection
//     exemptions?: { "pkg@1.2.3": ["0.2.2-rc.1"] },
//     plugins?: [{ name, version, dir, manifestJson }],
//     forceFallback?: true                    // test hook: skip the official module
//   }
// stdout: exactly ONE JSON object (no other stdout writes):
//   {
//     schemaVersion: 1, mode, engine: "official"|"fallback"|"unknown",
//     engineDetail, runtimeVersion, appVersion, baseBundles: [...],
//     results: [{ name, version, level, compatible, exempted, requirements,
//                 static: { result, patchFiles, clientInject, missingRefs, notes }, error }],
//     error: null
//   }
// Diagnostics go to stderr only.
//
// Rules mirror the official @deepseek-ai/dsh-app-boot evaluator (v0.2.2-rc.1):
//  - engines.dsh present -> only it decides; peerDependencies ignored.
//  - otherwise every @deepseek-ai/dsh and @deepseek-ai/dsh-* peer participates.
//  - peerDependenciesMeta never exempts (optional peers are still checked).
//  - other peers (@deepseek-ai/cordis, react, ...) are ignored.
//  - ranges use includePrerelease: true.
//  - engines.dsh workspace:* / peer workspace:^|~|* semantics as in the official code.
//  - semver.satisfies() swallows invalid ranges and returns false, so an invalid
//    range is an unmet requirement (incompatible), not an error; malformed
//    engines/peer *field types*, an invalid runtime version, and missing identity
//    on a mismatch throw and become "unknown". (Design doc 10.2 said invalid
//    ranges raise; the shipped 0.2.2-rc.1 evaluator actually returns false.)

import { existsSync, readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

const SCHEMA_VERSION = 1;
const IS_WIN32 = process.platform === 'win32';

/* ------------------------------------------------------------------ logging */

function diag(message) {
  try {
    process.stderr.write(`[loom-helper] ${message}\n`);
  } catch {
    /* diagnostics must never fail the helper */
  }
}

function emit(payload) {
  process.stdout.write(JSON.stringify(payload));
}

/* --------------------------------------------------------------- file utils */

function locateAppRoot(raw) {
  if (typeof raw !== 'string' || raw.trim() === '') return null;
  let value = raw.trim();
  // Accept both Windows separators and URL-ish paths.
  if (value.startsWith('file:')) {
    try {
      value = new URL(value).pathname.replace(/^\/([A-Za-z]:\/)/, '$1');
    } catch {
      /* fall through */
    }
  }
  value = value.replaceAll('\\', '/').replace(/\/+$/, '');
  return value;
}

function appPath(appRoot, ...parts) {
  return [appRoot, ...parts].join('/');
}

function readJsonFile(filename) {
  try {
    return JSON.parse(readFileSync(filename, 'utf8'));
  } catch {
    return null;
  }
}

function readTextFile(filename) {
  try {
    return readFileSync(filename, 'utf8');
  } catch {
    return null;
  }
}

/* ------------------------------------------------------------- vendored semver
 *
 * A self-contained subset that reproduces `semver.satisfies(version, range,
 * { includePrerelease: true })` closely enough for manifest engines/peer ranges.
 * The comparator expansion (caret/tilde/x-range/hyphen and the `-0` upper
 * bounds) was captured from semver 7.8.5 shipped inside DSH 0.2.2-rc.1.
 */

const SEMVER_RE = /^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+([0-9A-Za-z.-]+))?$/;

function parseSemver(text) {
  if (typeof text !== 'string') return null;
  const match = SEMVER_RE.exec(text.trim());
  if (match === null) return null;
  const prerelease = match[4] === undefined ? null : match[4];
  if (prerelease !== null) {
    const parts = prerelease.split('.');
    if (parts.some((part) => part === '')) return null;
  }
  return {
    major: Number(match[1]),
    minor: Number(match[2]),
    patch: Number(match[3]),
    prerelease,
    build: match[5] === undefined ? null : match[5],
  };
}

function compareSemver(a, b) {
  if (a.major !== b.major) return a.major < b.major ? -1 : 1;
  if (a.minor !== b.minor) return a.minor < b.minor ? -1 : 1;
  if (a.patch !== b.patch) return a.patch < b.patch ? -1 : 1;
  const left = a.prerelease;
  const right = b.prerelease;
  if (left === null && right === null) return 0;
  if (left === null) return 1;
  if (right === null) return -1;
  const leftParts = left.split('.');
  const rightParts = right.split('.');
  for (let index = 0; index < Math.max(leftParts.length, rightParts.length); index += 1) {
    if (index >= leftParts.length) return -1;
    if (index >= rightParts.length) return 1;
    const leftNumber = /^\d+$/.test(leftParts[index]);
    const rightNumber = /^\d+$/.test(rightParts[index]);
    if (leftNumber && rightNumber) {
      const diff = Number(leftParts[index]) - Number(rightParts[index]);
      if (diff !== 0) return diff < 0 ? -1 : 1;
    } else if (leftNumber) {
      return -1;
    } else if (rightNumber) {
      return 1;
    } else if (leftParts[index] !== rightParts[index]) {
      return leftParts[index] < rightParts[index] ? -1 : 1;
    }
  }
  return 0;
}

function semver(major, minor, patch, prerelease = null) {
  return { major, minor, patch, prerelease, build: null };
}

function withPrerelease(version, prerelease) {
  return semver(version.major, version.minor, version.patch, prerelease);
}

const ZERO_PRERELEASE = ['0'];

/** A partial version as written in a range: missing pieces become -1. */
function parsePartial(text) {
  const match = /^(?:v)?(\d+|[xX*])(?:\.(\d+|[xX*]))?(?:\.(\d+|[xX*]))?(?:-([0-9A-Za-z.-]+))?(?:\+([0-9A-Za-z.-]+))?$/.exec(
    text.trim(),
  );
  if (match === null) return null;
  const part = (raw) => {
    if (raw === undefined || raw === 'x' || raw === 'X' || raw === '*') return null;
    return Number(raw);
  };
  const major = part(match[1]);
  const minor = part(match[2]);
  const patch = part(match[3]);
  if (major === null) return { wildcard: true };
  const prerelease = match[4] === undefined ? null : match[4];
  if (prerelease !== null && patch === null) return null; // partial prerelease ranges are invalid
  return { wildcard: false, major, minor, patch, prerelease };
}

function fillOnes(partial, prerelease) {
  return semver(partial.major, partial.minor ?? 0, partial.patch ?? 0, prerelease);
}

function nextMajor(partial) {
  return semver(partial.major + 1, 0, 0, ZERO_PRERELEASE);
}

function nextMinor(partial) {
  return semver(partial.major, (partial.minor ?? 0) + 1, 0, ZERO_PRERELEASE);
}

function nextPatch(partial) {
  return semver(partial.major, partial.minor ?? 0, (partial.patch ?? 0) + 1, ZERO_PRERELEASE);
}

/** Expand one comparator token into concrete comparator objects. */
function expandComparator(token) {
  const match = /^(<=|>=|<|>|=|~|\^)?\s*(.+)$/.exec(token);
  if (match === null) throw new Error(`Invalid comparator: ${token}`);
  const operator = match[1] ?? '';
  const raw = match[2].trim();
  const partial = parsePartial(raw);
  if (partial === null) throw new Error(`Invalid comparator: ${token}`);
  if (partial.wildcard) return []; // * / x / X = any
  const major = partial.major;
  const minor = partial.minor;
  const patch = partial.patch;
  const prerelease = partial.prerelease;
  const full = minor !== null && patch !== null;

  switch (operator) {
    case '':
    case '=': {
      if (full) return [{ op: '=', version: fillOnes(partial, prerelease) }];
      // x-range: 1 / 1.2 / 1.2.x
      const lower = fillOnes(partial, prerelease ?? ZERO_PRERELEASE);
      const upper = minor === null ? nextMajor(partial) : nextMinor(partial);
      return [
        { op: '>=', version: lower },
        { op: '<', version: upper },
      ];
    }
    case '^': {
      const lower = fillOnes(partial, prerelease);
      let upper;
      if (major > 0) upper = semver(major + 1, 0, 0, ZERO_PRERELEASE);
      else if (minor === null) upper = semver(1, 0, 0, ZERO_PRERELEASE); // ^0 -> <1.0.0-0
      else if (minor > 0) upper = semver(0, minor + 1, 0, ZERO_PRERELEASE);
      else if (patch === null) upper = semver(0, 1, 0, ZERO_PRERELEASE); // ^0.0 -> <0.1.0-0
      else upper = semver(0, 0, patch + 1, ZERO_PRERELEASE);
      return [
        { op: '>=', version: lower },
        { op: '<', version: upper },
      ];
    }
    case '~': {
      const lower = fillOnes(partial, prerelease ?? ZERO_PRERELEASE);
      const upper = minor === null ? nextMajor(partial) : nextMinor(partial);
      return [
        { op: '>=', version: lower },
        { op: '<', version: upper },
      ];
    }
    case '>=': {
      return [{ op: '>=', version: fillOnes(partial, prerelease ?? (full ? null : ZERO_PRERELEASE)) }];
    }
    case '>': {
      if (!full) return [{ op: '>=', version: minor === null ? nextMajor(partial) : nextMinor(partial) }];
      return [{ op: '>', version: fillOnes(partial, prerelease) }];
    }
    case '<': {
      if (!full) return [{ op: '<', version: fillOnes(partial, ZERO_PRERELEASE) }];
      return [{ op: '<', version: fillOnes(partial, prerelease) }];
    }
    case '<=': {
      if (!full) return [{ op: '<', version: minor === null ? nextMajor(partial) : nextMinor(partial) }];
      return [{ op: '<=', version: fillOnes(partial, prerelease) }];
    }
    default:
      throw new Error(`Invalid comparator: ${token}`);
  }
}

/** Expand one alternative (space separated comparators, possibly a hyphen range). */
function expandAlternative(alternative) {
  const text = alternative.trim();
  if (text === '') return []; // empty alternative = any version
  const hyphen = /^(.+?)\s+-\s+(.+)$/.exec(text);
  if (hyphen !== null) {
    const from = parsePartial(hyphen[1].trim());
    const to = parsePartial(hyphen[2].trim());
    if (from === null || to === null || from.wildcard === true || to.wildcard === true) {
      throw new Error(`Invalid comparator: ${text}`);
    }
    const lower = { op: '>=', version: fillOnes(from, from.prerelease ?? ZERO_PRERELEASE) };
    const upper = to.prerelease !== null && to.minor !== null && to.patch !== null
      ? { op: '<=', version: fillOnes(to, to.prerelease) }
      : {
          op: '<',
          version: to.minor === null ? nextMajor(to) : nextPatch(to),
        };
    return [lower, upper];
  }
  const comparators = [];
  for (const token of text.split(/\s+/)) {
    comparators.push(...expandComparator(token));
  }
  return comparators;
}

function parseRange(range) {
  return range
    .trim()
    .split('||')
    .map((alternative) => expandAlternative(alternative));
}

function compareTo(version, comparator) {
  const diff = compareSemver(version, comparator.version);
  switch (comparator.op) {
    case '=':
      return diff === 0;
    case '>':
      return diff > 0;
    case '>=':
      return diff >= 0;
    case '<':
      return diff < 0;
    case '<=':
      return diff <= 0;
    default:
      return false;
  }
}

/**
 * semver.satisfies(version, range, { includePrerelease: true }).
 * Like semver's own `satisfies`, an invalid range yields false rather than
 * throwing (the official app-boot evaluator relies on that); only an invalid
 * runtime version is a hard error.
 */
function satisfiesRange(versionText, rangeText) {
  const version = parseSemver(versionText);
  if (version === null) throw new Error(`Invalid version: ${versionText}`);
  let alternatives;
  try {
    alternatives = parseRange(rangeText);
  } catch {
    return false; // semver.satisfies swallows invalid-range errors and returns false
  }
  for (const comparators of alternatives) {
    if (comparators.every((comparator) => compareTo(version, comparator))) return true;
  }
  return false;
}

/* ------------------------------------------------------------ official engine */

async function importModule(url) {
  try {
    return await import(url);
  } catch (error) {
    return { __error: String((error && error.message) || error) };
  }
}

function toFileUrl(path) {
  try {
    return pathToFileURL(path).href;
  } catch {
    return 'file:///' + path.replaceAll('\\', '/');
  }
}

async function loadOfficial(appRoot) {
  const candidates = [
    appPath(appRoot, 'dsh', 'node_modules', '@deepseek-ai', 'dsh-app-boot', 'lib', 'index.js'),
    appPath(appRoot, 'node_modules', '@deepseek-ai', 'dsh-app-boot', 'lib', 'index.js'),
  ];
  const errors = [];
  for (const candidate of candidates) {
    if (!existsSync(candidate)) {
      errors.push(`${candidate} not found`);
      continue;
    }
    const module = await importModule(toFileUrl(candidate));
    if (module.__error !== undefined) {
      errors.push(`${candidate}: ${module.__error}`);
      continue;
    }
    if (typeof module.getDshRuntimeVersion !== 'function' || typeof module.evaluatePluginCompatibility !== 'function') {
      errors.push(`${candidate}: required exports missing`);
      continue;
    }
    return { module, path: candidate, error: null };
  }
  return { module: null, path: null, error: errors.join('; ') || 'official module not found' };
}

async function loadOfficialBundles(appRoot) {
  const candidate = appPath(appRoot, 'dsh', 'node_modules', '@deepseek-ai', 'dsh-official-bundles', 'lib', 'index.js');
  if (!existsSync(candidate)) return null;
  const module = await importModule(toFileUrl(candidate));
  return module.__error === undefined ? module : null;
}

function loadJsYaml(appRoot) {
  try {
    const requireFromApp = createRequire(appPath(appRoot, 'dsh', 'package.json'));
    return requireFromApp('js-yaml');
  } catch (error) {
    diag(`js-yaml unavailable: ${String((error && error.message) || error)}`);
    return null;
  }
}

function readRuntimeVersionFromFiles(appRoot) {
  // Official getDshRuntimeVersion() reads the app-boot package.json version.
  const appBoot = readJsonFile(
    appPath(appRoot, 'dsh', 'node_modules', '@deepseek-ai', 'dsh-app-boot', 'package.json'),
  );
  if (appBoot !== null && typeof appBoot.version === 'string' && parseSemver(appBoot.version) !== null) {
    return appBoot.version;
  }
  const runtime = readJsonFile(appPath(appRoot, 'dsh', 'package.json'));
  if (runtime !== null && typeof runtime.version === 'string' && parseSemver(runtime.version) !== null) {
    return runtime.version;
  }
  return null;
}

function readAppVersionFromFiles(appRoot) {
  const desktop = readJsonFile(appPath(appRoot, 'package.json'));
  if (desktop !== null && typeof desktop.version === 'string' && desktop.version.trim() !== '') {
    return desktop.version.trim();
  }
  return null;
}

/* ------------------------------------------------------- fallback evaluation */

function objectOf(value, field) {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    throw new Error(`${field} must be an object`);
  }
  return value;
}

function hasOwn(object, key) {
  return Object.prototype.hasOwnProperty.call(object, key);
}

function identityField(manifest, field) {
  const value = hasOwn(manifest, field) ? manifest[field] : undefined;
  if (typeof value !== 'string' || value.trim() === '') {
    throw new Error(`Plugin manifest ${field} must be a non-empty string when dsh requirements are unmet`);
  }
  return value;
}

function unmetRequirementsFallback(fields, runtimeVersion) {
  const engines = hasOwn(fields, 'engines') ? objectOf(fields.engines, 'Plugin manifest engines') : {};
  if (hasOwn(engines, 'dsh')) {
    const range = engines.dsh;
    if (typeof range !== 'string') throw new Error('Plugin manifest engines.dsh must be a string');
    const satisfied = range.trim() !== '' && satisfiesRange(runtimeVersion, range);
    return satisfied ? {} : { 'engines.dsh': range };
  }
  if (!hasOwn(fields, 'peerDependencies')) return {};
  const dependencies = objectOf(fields.peerDependencies, 'Plugin manifest peerDependencies');
  const unmet = {};
  for (const [name, range] of Object.entries(dependencies)) {
    if (typeof range !== 'string') {
      throw new Error(`Plugin manifest peerDependencies[${JSON.stringify(name)}] must be a string`);
    }
    if (name !== '@deepseek-ai/dsh' && !name.startsWith('@deepseek-ai/dsh-')) continue;
    const effective = ['workspace:^', 'workspace:~', 'workspace:*'].includes(range) ? runtimeVersion : range;
    if (!(effective.trim() !== '' && satisfiesRange(runtimeVersion, effective))) unmet[name] = range;
  }
  return unmet;
}

function evaluateFallback(manifest, exemptions, runtimeVersion) {
  if (parseSemver(runtimeVersion) === null) {
    throw new Error(`Invalid dsh runtime version: ${JSON.stringify(runtimeVersion)}; expected a semantic version`);
  }
  const fields = objectOf(manifest, 'Plugin manifest');
  const requirements = unmetRequirementsFallback(fields, runtimeVersion);
  if (Object.keys(requirements).length === 0) return undefined;
  const name = identityField(fields, 'name');
  const version = identityField(fields, 'version');
  const key = `${name}@${version}`;
  const list = hasOwn(exemptions, key) ? exemptions[key] : undefined;
  return {
    name,
    version,
    runtimeVersion,
    requirements,
    exempted: Array.isArray(list) && list.includes(runtimeVersion),
  };
}

/* ------------------------------------------------------------- static checks */

const CLIENT_ANCHORS_CACHE = new Map();

function anchorsFor(appRoot, profileDir, pluginDir) {
  const key = `${appRoot}|${profileDir}|${pluginDir}`;
  if (CLIENT_ANCHORS_CACHE.has(key)) return CLIENT_ANCHORS_CACHE.get(key);
  const anchors = [];
  const add = (base) => {
    if (typeof base !== 'string' || base === '') return;
    anchors.push(join(base, 'package.json'));
  };
  add(appPath(appRoot, 'dsh'));
  add(appRoot);
  if (typeof profileDir === 'string' && profileDir !== '') add(profileDir);
  if (typeof pluginDir === 'string' && pluginDir !== '') add(pluginDir);
  CLIENT_ANCHORS_CACHE.set(key, anchors);
  return anchors;
}

function resolvePackageFrom(anchors, packageName) {
  for (const anchor of anchors) {
    try {
      const requireFrom = createRequire(anchor);
      requireFrom.resolve(`${packageName}/package.json`);
      return true;
    } catch {
      /* try the next anchor */
    }
  }
  return false;
}

/**
 * Collect the module names an `insert` list mounts. Handles the shapes seen in
 * shipped bundle patches: `insert: [{ id, name }]`, `insert: ["pkg"]`, and
 * nested insert lists. Non-insert patch rows (id-targeted config overrides) are
 * deliberately not treated as insert refs.
 */
function collectInsertNames(node, out) {
  if (node === null || node === undefined) return;
  if (typeof node === 'string') {
    out.push(node.trim());
    return;
  }
  if (Array.isArray(node)) {
    for (const item of node) collectInsertNames(item, out);
    return;
  }
  if (typeof node === 'object') {
    if (hasOwn(node, 'insert')) {
      if (typeof node.insert === 'string' || Array.isArray(node.insert)) {
        collectInsertNames(node.insert, out);
      } else {
        out.push('\u0000invalid-insert');
      }
      return;
    }
    if (typeof node.name === 'string' && node.name.trim() !== '') {
      out.push(node.name.trim());
    }
  }
}

function isIgnoredInsertName(name) {
  if (typeof name !== 'string' || name === '') return true;
  if (name.startsWith('cordis:')) return true;
  if (name.startsWith('./') || name.startsWith('../') || name.startsWith('.\\') || name.startsWith('..\\')) return true;
  if (/^[A-Za-z]:[\\/]/.test(name)) return true; // absolute Windows path
  if (name.startsWith('/')) return true; // absolute POSIX path
  return false;
}

function staticPrecheck(manifest, context) {
  const { appRoot, profileDir, pluginDir, yaml } = context;
  const result = {
    result: 'notApplicable',
    patchFiles: [],
    clientInject: [],
    missingRefs: [],
    notes: [],
  };
  const dsh = manifest && typeof manifest === 'object' && !Array.isArray(manifest) ? manifest.dsh : undefined;
  const bundle = dsh && typeof dsh === 'object' ? dsh.bundle : undefined;
  const client = dsh && typeof dsh === 'object' ? dsh.client : undefined;
  const hasBundle = bundle !== undefined && bundle !== null;
  const inject = client && typeof client === 'object' && Array.isArray(client.inject) ? client.inject : undefined;
  if (!hasBundle && inject === undefined) return result;

  result.result = 'ok';
  const markUnverified = (note, ref) => {
    result.result = 'unverified';
    result.notes.push(note);
    if (ref !== undefined) result.missingRefs.push(ref);
  };
  const markMissing = (note, ref) => {
    result.result = 'missing';
    result.notes.push(note);
    if (ref !== undefined) result.missingRefs.push(ref);
  };
  const anchors = anchorsFor(appRoot, profileDir, pluginDir);

  // dsh.bundle.platforms
  if (hasBundle && typeof bundle === 'object') {
    if (bundle.platforms !== undefined) {
      const platforms = bundle.platforms;
      const valid = Array.isArray(platforms)
        && platforms.length > 0
        && platforms.every((entry) => typeof entry === 'string' && entry !== '');
      if (!valid) {
        markMissing('dsh.bundle.platforms 必须是平台名非空列表', 'platform:*');
      } else if (IS_WIN32 && !platforms.includes('win32')) {
        markMissing(`dsh.bundle.platforms 不含 win32（声明：${platforms.join(', ')}）`, 'platform:win32');
      }
    }

    // dsh.bundle.patch
    if (bundle.patch !== undefined) {
      const declared = typeof bundle.patch === 'string' ? [bundle.patch] : bundle.patch;
      const valid = Array.isArray(declared) && declared.every((entry) => typeof entry === 'string' && entry !== '');
      if (!valid) {
        markMissing('dsh.bundle.patch 必须是文件路径或文件路径列表', 'patch:*');
      } else if (typeof pluginDir !== 'string' || pluginDir === '') {
        markUnverified('插件目录未知，无法检查 dsh.bundle.patch', 'patch:unknown-dir');
      } else {
        if (yaml === null) {
          markUnverified('js-yaml 不可用，无法解析 dsh.bundle.patch', 'patch:no-yaml');
        }
        for (const declaredFile of declared) {
          result.patchFiles.push(declaredFile);
          const patchPath = join(pluginDir, declaredFile);
          const text = readTextFile(patchPath);
          if (text === null) {
            markMissing(`patch 文件不存在：${declaredFile}`, `patch:${declaredFile}`);
            continue;
          }
          if (yaml === null) continue;
          let parsed;
          try {
            parsed = yaml.load(text);
          } catch (error) {
            markMissing(`patch YAML 解析失败（${declaredFile}）：${String((error && error.message) || error)}`, `patch:${declaredFile}`);
            continue;
          }
          if (!Array.isArray(parsed)) {
            markMissing(`patch 顶层不是列表（${declaredFile}）`, `patch:${declaredFile}`);
            continue;
          }
          const names = [];
          collectInsertNames(parsed, names);
          if (names.includes('\u0000invalid-insert')) {
            markMissing(`patch 的 insert 不是列表或字符串（${declaredFile}）`, `patch:${declaredFile}`);
          }
          for (const name of names) {
            if (name === '\u0000invalid-insert') continue;
            if (isIgnoredInsertName(name)) continue;
            if (!resolvePackageFrom(anchors, name)) {
              markMissing(`patch 插入的包无法解析：${name}`, `insert:${name}`);
            }
          }
        }
      }
    }
  }

  // dsh.client.inject
  if (inject !== undefined) {
    if (typeof pluginDir !== 'string' || pluginDir === '') {
      markUnverified('插件目录未知，无法检查 dsh.client.inject', 'client:unknown-dir');
    } else if (!inject.every((entry) => typeof entry === 'string' && entry !== '')) {
      markUnverified('dsh.client.inject 必须是非空包名列表', 'client:*');
    } else {
      result.clientInject.push(...inject);
      for (const name of inject) {
        // Client packages load from the DSH installation runtime, not from the profile.
        const runtimeAnchors = anchorsFor(appRoot, '', '');
        if (!resolvePackageFrom(runtimeAnchors, name)) {
          markUnverified(`dsh.client.inject 的包在运行时 node_modules 不可解析：${name}`, `client:${name}`);
        }
      }
    }
  }

  return result;
}

/* -------------------------------------------------------------- evaluation */

const LEVEL = {
  compatible: 'compatible',
  incompatible: 'incompatible',
  unverified: 'unverified',
  unknown: 'unknown',
};

function requirementsToList(requirements) {
  if (requirements === undefined || requirements === null || typeof requirements !== 'object') return [];
  return Object.entries(requirements).map(([name, range]) => `${name}: ${String(range)}`);
}

function errorMessage(error) {
  return String((error && error.message) || error);
}

function evaluatePlugins(request, official, runtimeVersion, yaml) {
  const plugins = Array.isArray(request.plugins) ? request.plugins : [];
  const exemptions = request.exemptions && typeof request.exemptions === 'object' && !Array.isArray(request.exemptions)
    ? request.exemptions
    : {};
  const context = {
    appRoot: request.appRoot,
    profileDir: typeof request.profileDir === 'string' ? request.profileDir : '',
    pluginDir: '',
    yaml,
  };
  const results = [];

  for (const plugin of plugins) {
    const entry = plugin && typeof plugin === 'object' ? plugin : {};
    const name = typeof entry.name === 'string' ? entry.name : '';
    const version = typeof entry.version === 'string' ? entry.version : '';
    const pluginDir = typeof entry.dir === 'string' ? entry.dir : '';
    const result = {
      name,
      version,
      level: LEVEL.unknown,
      compatible: false,
      exempted: false,
      requirements: [],
      static: { result: 'notApplicable', patchFiles: [], clientInject: [], missingRefs: [], notes: [] },
      error: null,
    };

    let manifest = null;
    try {
      if (typeof entry.manifestJson === 'string' && entry.manifestJson.trim() !== '') {
        manifest = JSON.parse(entry.manifestJson);
      } else if (pluginDir !== '') {
        manifest = readJsonFile(join(pluginDir, 'package.json'));
      }
    } catch (error) {
      result.error = `manifest JSON 解析失败：${errorMessage(error)}`;
      result.level = LEVEL.unknown;
      results.push(result);
      continue;
    }
    if (manifest === null || typeof manifest !== 'object' || Array.isArray(manifest)) {
      result.error = '缺少可解析的插件 manifest';
      result.level = LEVEL.unknown;
      results.push(result);
      continue;
    }

    result.name = result.name || (typeof manifest.name === 'string' ? manifest.name : '');
    result.version = result.version || (typeof manifest.version === 'string' ? manifest.version : '');

    let issue;
    let evaluationError = null;
    try {
      issue = official !== null
        ? official.evaluatePluginCompatibility(manifest, exemptions, runtimeVersion)
        : evaluateFallback(manifest, exemptions, runtimeVersion);
    } catch (error) {
      evaluationError = errorMessage(error);
    }

    context.pluginDir = pluginDir;
    try {
      result.static = staticPrecheck(manifest, context);
    } catch (error) {
      result.static = {
        result: 'unverified',
        patchFiles: [],
        clientInject: [],
        missingRefs: [],
        notes: [`静态预检异常：${errorMessage(error)}`],
      };
    }

    if (evaluationError !== null) {
      result.error = evaluationError;
      result.level = LEVEL.unknown;
      results.push(result);
      continue;
    }

    if (issue !== undefined) {
      result.requirements = requirementsToList(issue.requirements);
      result.exempted = issue.exempted === true;
      if (!result.exempted) {
        result.level = LEVEL.incompatible;
        result.error = null;
        results.push(result);
        continue;
      }
    }

    if (result.static.result === 'missing') {
      result.level = LEVEL.incompatible;
    } else if (result.static.result === 'unverified') {
      result.level = LEVEL.unverified;
    } else {
      result.level = LEVEL.compatible;
      result.compatible = true;
    }
    results.push(result);
  }

  return results;
}

/* --------------------------------------------------------------- entry point */

async function main() {
  let request;
  try {
    const raw = readFileSync(0, 'utf8');
    request = JSON.parse(raw);
  } catch (error) {
    emit({
      schemaVersion: SCHEMA_VERSION,
      mode: 'unknown',
      engine: 'unknown',
      engineDetail: `helper input 解析失败：${errorMessage(error)}`,
      runtimeVersion: null,
      appVersion: null,
      baseBundles: [],
      results: [],
      error: `input 解析失败：${errorMessage(error)}`,
    });
    return;
  }

  const mode = request.mode === 'probe' ? 'probe' : 'evaluate';
  const appRoot = locateAppRoot(request.appRoot);
  if (appRoot === null) {
    emit({
      schemaVersion: SCHEMA_VERSION,
      mode,
      engine: 'unknown',
      engineDetail: 'appRoot 缺失或无效',
      runtimeVersion: null,
      appVersion: null,
      baseBundles: [],
      results: [],
      error: 'appRoot 缺失或无效',
    });
    return;
  }

  const forceFallback = request.forceFallback === true;
  const officialLoad = forceFallback ? { module: null, path: null, error: 'forceFallback requested' } : await loadOfficial(appRoot);
  const official = officialLoad.module;
  let engine = official !== null ? 'official' : 'fallback';
  let engineDetail = official !== null
    ? `official @deepseek-ai/dsh-app-boot (${officialLoad.path})`
    : `fallback (official import failed: ${officialLoad.error})`;

  // runtime version: explicit input > official getDshRuntimeVersion() > file read
  let runtimeVersion = null;
  const requestedRuntime = typeof request.runtimeVersion === 'string' ? request.runtimeVersion.trim() : '';
  if (requestedRuntime !== '' && parseSemver(requestedRuntime) !== null) {
    runtimeVersion = requestedRuntime;
  } else if (official !== null) {
    try {
      runtimeVersion = official.getDshRuntimeVersion();
    } catch (error) {
      diag(`getDshRuntimeVersion failed: ${errorMessage(error)}`);
      runtimeVersion = readRuntimeVersionFromFiles(appRoot);
    }
  } else {
    runtimeVersion = readRuntimeVersionFromFiles(appRoot);
  }

  const appVersion = readAppVersionFromFiles(appRoot);
  let baseBundles = [];
  const bundlesModule = request.forceFallback === true ? null : await loadOfficialBundles(appRoot);
  if (bundlesModule !== null) {
    const web = bundlesModule.PROFILE_TEMPLATES && bundlesModule.PROFILE_TEMPLATES.web;
    if (web && Array.isArray(web.bundles) && web.bundles.every((entry) => typeof entry === 'string')) {
      baseBundles = [...web.bundles];
    }
  }

  if (mode === 'probe') {
    if (official === null && runtimeVersion !== null) {
      engine = 'fallback';
      engineDetail = `fallback (official import failed: ${officialLoad.error})`;
    } else if (runtimeVersion === null) {
      engine = 'unknown';
      engineDetail = `unknown (无法确定运行时版本：${officialLoad.error ?? 'package.json 不可读'})`;
    }
    emit({
      schemaVersion: SCHEMA_VERSION,
      mode,
      engine,
      engineDetail,
      runtimeVersion,
      appVersion,
      baseBundles,
      results: [],
      error: null,
    });
    return;
  }

  if (runtimeVersion === null) {
    emit({
      schemaVersion: SCHEMA_VERSION,
      mode,
      engine: 'unknown',
      engineDetail: 'unknown (无法确定运行时版本)',
      runtimeVersion: null,
      appVersion,
      baseBundles,
      results: Array.isArray(request.plugins)
        ? request.plugins.map((plugin) => ({
            name: typeof plugin?.name === 'string' ? plugin.name : '',
            version: typeof plugin?.version === 'string' ? plugin.version : '',
            level: LEVEL.unknown,
            compatible: false,
            exempted: false,
            requirements: [],
            static: { result: 'notApplicable', patchFiles: [], clientInject: [], missingRefs: [], notes: [] },
            error: '无法确定 dsh 运行时版本',
          }))
        : [],
      error: null,
    });
    return;
  }

  const yaml = loadJsYaml(appRoot);
  const results = evaluatePlugins(request, official, runtimeVersion, yaml);

  emit({
    schemaVersion: SCHEMA_VERSION,
    mode,
    engine,
    engineDetail,
    runtimeVersion,
    appVersion,
    baseBundles,
    results,
    error: null,
  });
}

main().catch((error) => {
  // Never let an unexpected throw leave stdout empty: the caller maps this to "unknown".
  try {
    emit({
      schemaVersion: SCHEMA_VERSION,
      mode: 'unknown',
      engine: 'unknown',
      engineDetail: `helper 异常：${errorMessage(error)}`,
      runtimeVersion: null,
      appVersion: null,
      baseBundles: [],
      results: [],
      error: errorMessage(error),
    });
  } catch {
    process.stderr.write(`[loom-helper] fatal: ${errorMessage(error)}\n`);
  }
});
