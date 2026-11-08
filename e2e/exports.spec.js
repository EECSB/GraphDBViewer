//Export downloads from the Import/Export panel: the graph as JSON / addV-addE Gremlin text, image
//(PNG), the current 2D view as SVG, an auto-laid-out SVG (rendered off-screen by Cytoscape — the
//Graphviz-free replacement, so it works from any view), Graphviz DOT text, and OBJ / glTF / GLB 3D models.
//Asserts the browser download fires with the right filename and plausible file content.
const fs = require('fs');
const { test, expect } = require('@playwright/test');
const { gotoApp, openImportPanel, closeImportPanel, loadSampleGraph, switchTo3d, SAMPLE_NODE_COUNT } = require('./helpers');

test.beforeEach(async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);
    //Visualizing collapses the Import/Export panel; reopen it for the export controls.
    await openImportPanel(page);
});

//Clicks an export button and returns the resulting download's file content.
async function downloadContent(page, trigger, expectedName) {
    const downloadPromise = page.waitForEvent('download');
    await trigger();
    const download = await downloadPromise;

    expect(download.suggestedFilename()).toBe(expectedName);

    return fs.readFileSync(await download.path());
}

test('exports the 2D view as a PNG image', async ({ page }) => {
    const imageGroup = page.getByRole('group', { name: 'Image export' });
    await imageGroup.getByRole('combobox').selectOption('png');

    const content = await downloadContent(page, async () => {
        await imageGroup.getByRole('button', { name: 'Image' }).click();
    }, 'graph.png');

    //PNG magic number.
    expect(content.subarray(0, 4)).toEqual(Buffer.from([0x89, 0x50, 0x4e, 0x47]));
});

test('exports the 2D view as SVG', async ({ page }) => {
    const imageGroup = page.getByRole('group', { name: 'Image export' });
    await imageGroup.getByRole('combobox').selectOption('svg');

    const content = await downloadContent(page, async () => {
        await imageGroup.getByRole('button', { name: 'Image' }).click();
    }, 'graph.svg');

    expect(content.toString()).toContain('<svg');
});

test('exports the graph as Graphviz DOT text', async ({ page }) => {
    const imageGroup = page.getByRole('group', { name: 'Image export' });
    await imageGroup.getByRole('combobox').selectOption('dot');

    const content = await downloadContent(page, async () => {
        await imageGroup.getByRole('button', { name: 'Image' }).click();
    }, 'graph.dot');

    const text = content.toString();
    expect(text).toContain('digraph');
    expect(text).toContain('Alice');
});

test('exports an auto-laid-out SVG rendered by Cytoscape (no Graphviz)', async ({ page }) => {
    const imageGroup = page.getByRole('group', { name: 'Image export' });
    await imageGroup.getByRole('combobox').selectOption('svglayout');

    const content = await downloadContent(page, async () => {
        await imageGroup.getByRole('button', { name: 'Image' }).click();
    }, 'graph.svg');

    expect(content.toString()).toContain('<svg');
});

//The auto-layout SVG re-lays the graph off-screen, so unlike the plain "SVG" (current 2D view) it works
//even when the 2D canvas isn't the active view — the capability the old Graphviz render provided.
test('the auto-laid-out SVG export works from the 3D view', async ({ page }) => {
    await closeImportPanel(page);
    await switchTo3d(page, SAMPLE_NODE_COUNT);
    await openImportPanel(page);

    const imageGroup = page.getByRole('group', { name: 'Image export' });
    await imageGroup.getByRole('combobox').selectOption('svglayout');

    const content = await downloadContent(page, async () => {
        await imageGroup.getByRole('button', { name: 'Image' }).click();
    }, 'graph.svg');

    expect(content.toString()).toContain('<svg');
});

test('downloads the graph as JSON text', async ({ page }) => {
    const graphGroup = page.getByRole('group', { name: 'Graph export' });
    await graphGroup.getByRole('combobox').selectOption('json');

    const content = await downloadContent(page, async () => {
        await graphGroup.getByRole('button', { name: 'Text' }).click();
    }, 'graph-json.txt');

    const text = content.toString();
    //Valid JSON that carries the imported vertices.
    expect(() => JSON.parse(text)).not.toThrow();
    expect(text).toContain('Alice');
});

test('downloads the addV/addE Gremlin queries as text', async ({ page }) => {
    const graphGroup = page.getByRole('group', { name: 'Graph export' });
    await graphGroup.getByRole('combobox').selectOption('gremlin');

    const content = await downloadContent(page, async () => {
        await graphGroup.getByRole('button', { name: 'Text' }).click();
    }, 'graph-gremlin.txt');

    const text = content.toString();
    expect(text).toContain('addV');
    expect(text).toContain('addE');
});

test('exports the 3D view as OBJ, glTF and GLB models', async ({ page }) => {
    await closeImportPanel(page);
    await switchTo3d(page, SAMPLE_NODE_COUNT);
    await openImportPanel(page);

    const modelGroup = page.getByRole('group', { name: '3D export' });
    await modelGroup.getByRole('combobox').selectOption('obj');

    const obj = await downloadContent(page, async () => {
        await modelGroup.getByRole('button', { name: '3D' }).click();
    }, 'graph.obj');

    expect(obj.toString()).toMatch(/^v /m);

    await modelGroup.getByRole('combobox').selectOption('gltf');

    const gltf = await downloadContent(page, async () => {
        await modelGroup.getByRole('button', { name: '3D' }).click();
    }, 'graph.gltf');

    const parsed = JSON.parse(gltf.toString());
    expect(parsed.asset.version).toBe('2.0');

    //And as GLB, glTF's binary container: its magic, its version, its own length, and a JSON chunk that says glTF 2.0.
    await modelGroup.getByRole('combobox').selectOption('glb');

    const glb = await downloadContent(page, async () => {
        await modelGroup.getByRole('button', { name: '3D' }).click();
    }, 'graph.glb');

    expect(glb.subarray(0, 4).toString('latin1')).toBe('glTF');
    expect(glb.readUInt32LE(4)).toBe(2);
    expect(glb.readUInt32LE(8)).toBe(glb.length);

    const json = JSON.parse(glb.subarray(20, 20 + glb.readUInt32LE(12)).toString('utf8'));

    expect(json.asset.version).toBe('2.0');
    expect(json.buffers[0].uri).toBeUndefined();
});
