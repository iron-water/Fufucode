// Isolated D1 lifecycle fixture; never load in a user session.
export default function (api) {
    api.registerCommand('d1.crash', {
        description: 'D1 test-only deterministic process exit',
        handler: async () => { process.exit(73); }
    });
}
