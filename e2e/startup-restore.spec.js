//Reloading with a graph in the workspace must restore it onto the canvas without errors. Regression for
//a real bug: the working area (and so the #cyGraph / #graph3d container) rendered only when connected or
//in offline mode, but OnAfterRenderAsync redrew the restored graph whenever HasGraphData was true. On a
//reload while disconnected that drew into a container that was never rendered — "Cytoscape container not
//found" / "3D graph container not found" for both views, before any DB connection — and the orphaned 3D
//instance kept ticking against a detached renderer ("Cannot read properties of undefined (reading 'tick')").
const { test, expect } = require('@playwright/test');
const { gotoApp, loadSampleGraph, switchTo3d, SAMPLE_NODE_COUNT, collectConsoleErrors } = require('./helpers');

//The workspace is written to IndexedDB after the render settles; reloading before that would restore
//nothing and prove the wrong thing. Waits until the persisted tab snapshot actually exists.
async function waitForPersistedWorkspace(page) {
    await expect.poll(async () => {
        return await page.evaluate(async () => {
            if (!window.gdbvIdb || !window.gdbvIdb.get)
                return 0;

            const raw = await window.gdbvIdb.get('graphdbviewer:tabs');

            return raw ? String(raw).length : 0;
        });
    }, { timeout: 20000 }).toBeGreaterThan(0);
}

test('restores a 2D graph on reload — canvas renders, no container error', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);
    await waitForPersistedWorkspace(page);

    const errors = collectConsoleErrors(page);

    //Reload with no connection: the restored graph must draw onto its own container, not error.
    await page.reload();
    await expect(page.locator('#cyGraph canvas').first()).toBeVisible({ timeout: 60000 });

    //Disconnected — the restore path, not a live query, is what draws.
    await expect(page.getByRole('button', { name: /Disconnected/ })).toBeVisible();
    expect(errors, errors.join('\n')).toEqual([]);
});

test('restores a 3D graph on reload — canvas renders, no container or tick error', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);
    await switchTo3d(page, SAMPLE_NODE_COUNT);
    await waitForPersistedWorkspace(page);

    const errors = collectConsoleErrors(page);

    await page.reload();
    await expect(page.locator('#graph3d canvas').first()).toBeVisible({ timeout: 60000 });
    await page.waitForTimeout(1000);//let a frame or two of the animation loop run

    expect(errors, errors.join('\n')).toEqual([]);
});
