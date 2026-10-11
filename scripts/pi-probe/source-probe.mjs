// Executed with the tested runtime. Reports actual loader output, not a regex assumption.
import { pathToFileURL } from 'node:url';
import { join } from 'node:path';
const [packageRoot, cwd, agentDir] = process.argv.slice(2);
const { DefaultResourceLoader } = await import(pathToFileURL(join(packageRoot, 'dist/core/resource-loader.js')));
const { builtInExtensions } = await import(pathToFileURL(join(packageRoot, 'dist/extensions/index.js')));
const named = { name: 'probe-named', factory(pi) { pi.registerCommand('probe-source', { description: 'source probe', handler: async () => {} }); } };
const syntheticBuiltin = { name: 'probe-builtin', builtin: true, factory(pi) { pi.registerCommand('probe-builtin-source', { description: 'builtin source probe', handler: async () => {} }); } };
const brokenInline = { name: 'probe-broken-inline', factory() { throw new Error('probe broken inline'); } };
const brokenBuiltin = { name: 'probe-broken-builtin', builtin: true, factory() { throw new Error('probe broken builtin'); } };
const loader = new DefaultResourceLoader({ cwd, agentDir, extensionFactories: [...builtInExtensions, named, syntheticBuiltin, brokenInline, brokenBuiltin], noSkills: true, noPromptTemplates: true, noThemes: true, noContextFiles: true });
await loader.reload();
const result = loader.getExtensions();
console.log(JSON.stringify({ factories: builtInExtensions.map(({ name, builtin, hidden, replaceable }) => ({ name, builtin, hidden, replaceable })), extensions: result.extensions.map(e => ({ path: e.path, sourceInfo: e.sourceInfo, commands: [...e.commands.keys()] })), errors: result.errors }));
