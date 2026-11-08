//What the published viewer, v1.2.0 (f7c7504, 2026-09-13), left in a browser comes back in this build (TODO.md in Treeality,
//"Before the next viewer release", 2026-11-06). Its data is seeded as v1.2.0 wrote it: IndexedDB `graphdbviewer`, store `kv`,
//each value its JSON behind StorageCompression's marker, `r` and the text under 256 characters, `z` and the raw-deflated
//text in base64 from there (DeflateStream, which is deflate-raw); the one-time migration's marker `__gdbv_migrated` = "1",
//as v1.2.0 left it after carrying localStorage across; and a window's place in localStorage, as FloatingDialogInterop keeps
//it. GraphDBViewerWeb.Tests/ReleasedViewerStorageTests.cs reads every key's JSON through this build's types; this walks the
//ones that show on screen.
const { test, expect } = require('@playwright/test');
const { gotoApp, openAccountMenu } = require('./helpers');

const panel = '.gdbv-dialog:not(.d-none)';

//What v1.2.0 kept, by key, as the JSON it wrote (or the text, for the two it wrote with SetStringAsync). The tabs are long enough to be stored compressed, as a real one is.
const released = {
    'graphdbviewer:theme': 'light-tinted',//SetStringAsync: the text itself, not JSON
    'graphdbviewer:fullScreen': 'true',
    'graphdbviewer:expansionLimit': '75',
    'graphdbviewer:connections': JSON.stringify({
        'Local Neo4j': {
            Transport: 'bolt', Hostname: 'neo4j.e2e.test', AuthKey: '', Port: 7687, UseSSL: false, Database: 'neo4j',
            Collection: null, DatabaseType: 'Neo4j', Endpoint: null, Username: 'neo4j', ViaServer: false
        },
        'Team TinkerPop': {
            Transport: 'WebSocket', Hostname: 'gremlin.e2e.test', AuthKey: '', Port: 8182, UseSSL: false, Database: null,
            Collection: null, DatabaseType: 'ApacheTinkerPop', Endpoint: null, Username: null, ViaServer: true
        }
    }),
    'graphdbviewer:llmConnections': JSON.stringify({
        'Released Claude': {
            Name: 'Released Claude', ProviderType: 'Anthropic', BaseUrl: null, ApiKey: 'not-a-real-key',
            Model: 'claude-opus-4-8', MaxTokens: 4096, Temperature: null, Effort: 'high'
        }
    }),
    'graphdbviewer:savedQueries': JSON.stringify({ 'Released friends': "g.V().out('knows')" }),
    'graphdbviewer:queryHistory': JSON.stringify(['g.V().limit(17)', 'g.E().limit(3)']),
    'graphdbviewer:lastQuery': "g.V().hasLabel('released')",//SetStringAsync too
    'graphdbviewer:tabs': JSON.stringify([
        {
            Id: 'released-1', Name: 'Released people', QueryText: "g.V().hasLabel('person')", EditorLanguage: 'gremlin',
            GeneratedQueryText: '', QueryEditorTab: 0, QueryResults: '', VisualizationMode: 2, SearchTerm: '',
            HiddenLabels: [], LastResultJson: null, GraphResultJson: null, ResultsCleared: false, LoadDbLimit: 100,
            AutoReloadAfterCommit: false, ReflectDbState: true, Cy2dLayout: 'concentric', G3dLayout: 'force',
            MapStyle: 'satellite', MapDetail: 'standard', Saved2dPositionsJson: null, Saved3dPositionsJson: null,
            Show3dVertexLabels: false, Show3dEdgeLabels: false, Show3dProps: null, G3dExportFormat: 'gltf',
            ImageExportFormat: 'png', TableExportFormat: 'csv',
            LabelStyles: { person: { Color: '#ff0000', Size: 40, Shape: 'diamond', Shape3d: 'cube', DisplayProperty: 'name', Icon: null, Model: null } },
            EdgeColorMode: 0, EdgeColors: {}
        },
        {
            Id: 'released-2', Name: 'Released cypher', QueryText: 'MATCH (n) RETURN n LIMIT 4', EditorLanguage: 'cypher',
            GeneratedQueryText: '', QueryEditorTab: 0, QueryResults: '', VisualizationMode: 2, SearchTerm: '',
            HiddenLabels: [], LastResultJson: null, GraphResultJson: null, ResultsCleared: false, LoadDbLimit: 100,
            AutoReloadAfterCommit: false, ReflectDbState: true, Cy2dLayout: 'cose', G3dLayout: 'force',
            MapStyle: 'satellite', MapDetail: 'standard', Saved2dPositionsJson: null, Saved3dPositionsJson: null,
            Show3dVertexLabels: false, Show3dEdgeLabels: false, Show3dProps: null, G3dExportFormat: 'gltf',
            ImageExportFormat: 'png', TableExportFormat: 'csv', LabelStyles: {}, EdgeColorMode: 0, EdgeColors: {}
        }
    ]),
    'graphdbviewer:tabText': JSON.stringify([{ Id: 'released-1', Query: "g.V().hasLabel('person').limit(23)", Generated: '' }])
};

//The AI settings window where v1.2.0 left it.
const releasedDialog = { key: 'graphdbviewer:dialog:ai-settings-2', geometry: { left: 260, top: 110, width: 700, height: 560 } };

//Writes the store as v1.2.0's IndexedDbInterop and StorageCompression did, from a page of the app's origin that does not
//start the app, so nothing of this build has opened the database first.
async function seedReleasedStorage(page) {
    await page.goto('/manifest.webmanifest');

    const stored = await page.evaluate(async ({ values, dialog }) => {
        async function encode(text) {
            if (text.length < 256)
                return 'r' + text;

            const deflated = new Blob([text]).stream().pipeThrough(new CompressionStream('deflate-raw'));
            const bytes = new Uint8Array(await new Response(deflated).arrayBuffer());
            let binary = '';

            for (const b of bytes)
                binary += String.fromCharCode(b);

            return 'z' + btoa(binary);
        }

        const db = await new Promise((resolve, reject) => {
            const req = indexedDB.open('graphdbviewer', 1);
            req.onupgradeneeded = () => req.result.createObjectStore('kv');
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => reject(req.error);
        });

        const entries = [['__gdbv_migrated', '1']];

        for (const [key, json] of Object.entries(values))
            entries.push([key, await encode(json)]);

        await new Promise((resolve, reject) => {
            const tx = db.transaction('kv', 'readwrite');

            for (const [key, value] of entries)
                tx.objectStore('kv').put(value, key);

            tx.oncomplete = resolve;
            tx.onerror = () => reject(tx.error);
        });
        db.close();

        localStorage.setItem(dialog.key, JSON.stringify(dialog.geometry));

        return entries.map(([key, value]) => key + ':' + value[0]);
    }, { values: released, dialog: releasedDialog });

    //Both of the encodings v1.2.0 wrote are among them.
    expect(stored).toContain('graphdbviewer:tabs:z');
    expect(stored).toContain('graphdbviewer:theme:r');
}

test('what v1.2.0 kept in a browser comes back in this build', async ({ page }) => {
    await seedReleasedStorage(page);
    await gotoApp(page);

    //The theme chosen there, Light 2, and the full width.
    await expect(page.locator('html')).toHaveAttribute('data-bs-theme', 'light');
    await expect(page.locator('html')).toHaveAttribute('data-gdbv-tint', 'on');
    await expect(page.locator('.gdbv-page.gdbv-fullscreen')).toHaveCount(1);

    //Its first saved connection fills the form, as v1.2.0 opened on it.
    await expect(page.getByRole('button', { name: 'Disconnected: neo4j.e2e.test:7687' })).toBeVisible();

    //Its query tabs, as the first connection's, the text typed since the last save on top. Nothing is connected, so the
    //working area waits for a connection or offline mode, as it did in v1.2.0.
    await page.getByRole('button', { name: 'Offline mode', exact: true }).click();

    const tabRow = page.locator('[data-tour="query-tabs"]:visible');

    await expect(page.getByRole('button', { name: /^Released people/ })).toBeVisible();
    await expect(page.getByRole('button', { name: /^Released cypher/ })).toBeVisible();
    await expect.poll(async () => {
        return await page.evaluate(() => {
            return Array.from(document.querySelectorAll('.monaco-editor .view-lines')).map(e => e.innerText.replace(/\s/g, '')).join('|');
        });
    }, { timeout: 20000 }).toContain("hasLabel('person').limit(23)");

    //Its saved queries and its history, in the query panel's own tabs.
    await tabRow.getByRole('button', { name: 'Query', exact: true }).click();
    await expect(page.locator('select[title="Saved queries"] option', { hasText: 'Released friends' })).toHaveCount(1);
    await tabRow.getByRole('button', { name: 'History', exact: true }).click();
    await expect(page.getByText('g.V().limit(17)').first()).toBeVisible();

    //Its saved connections, in the connection card's picker.
    if (await page.getByTestId('connection-picker-row').count() === 0)
        await page.getByTestId('viewer-connection').first().click();

    await page.getByTestId('connection-picker-chevron').click();

    const connections = page.getByTestId('connection-picker-menu');

    await expect(connections).toContainText('Local Neo4j');
    await expect(connections).toContainText('Team TinkerPop');
    await page.keyboard.press('Escape');

    //Its AI model, in AI settings, which opens where v1.2.0 left that window.
    await openAccountMenu(page);
    await page.getByRole('button', { name: 'AI settings', exact: true }).click();
    await expect(page.locator(panel)).toBeVisible();

    const box = await page.locator(panel).boundingBox();

    expect(Math.round(box.width)).toBe(releasedDialog.geometry.width);
    expect(Math.abs(box.x - releasedDialog.geometry.left)).toBeLessThan(4);

    await page.locator(panel).getByTestId('llm-picker-chevron').click();
    await expect(page.locator(panel).getByTestId('llm-picker-menu')).toContainText('Released Claude');
});
