//Shared helpers for the Playwright e2e specs: boot the app, load an offline graph through the
//Import panel (DOT import — no database needed, as DESIGN.md's e2e item suggests), and read
//canvas state through the interop globals (getCytoscapeElementInfo / graph3DInterop).
const { expect } = require('@playwright/test');

//A small DOT fixture with two vertex labels (person via type=, company) so per-label styling
//can be asserted against a subset of nodes: 5 vertices (3 person + 2 company), 5 edges.
const SAMPLE_DOT = `digraph {
    Alice [type=person]
    Bob [type=person]
    Carol [type=person]
    Acme [type=company]
    Globex [type=company]
    Alice -> Bob [label=knows]
    Bob -> Carol [label=knows]
    Alice -> Acme [label=worksAt]
    Bob -> Acme [label=worksAt]
    Carol -> Globex [label=worksAt]
}`;

const SAMPLE_NODE_COUNT = 5;
const SAMPLE_PERSON_COUNT = 3;

//A 1x1 PNG as a data URL — an image the browser can "load" with no network, for icon styling.
const TINY_PNG =
    'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==';

//Navigates to the app and waits for the Blazor WASM runtime to boot (the top bar renders). This host has no accounts,
//so there is no one to sign up as; a role named by a spec shared with Treeality is ignored.
async function gotoApp(page) {
    await page.goto('/');
    await expect(page.getByRole('button', { name: /Import\/Export/ })).toBeVisible({ timeout: 60000 });
    await declineTour(page);
}

//The app asks as it opens whether a tour is wanted (2026-11-03): answered no, never to be asked again, so a reload in
//the same spec opens straight onto the app.
async function declineTour(page) {
    const prompt = page.getByTestId('tour-prompt');

    await expect(prompt).toBeVisible({ timeout: 15000 });
    await prompt.getByTestId('tour-never').check();
    await prompt.getByTestId('tour-no').click();
    await expect(prompt).toHaveCount(0);
}

//Opens the Settings dropdown, which holds here what Treeality's account menu does: this host has no account to be.
//Named as Treeality's helper is, so the specs the two repos share call one name.
async function openAccountMenu(page) {
    await page.getByRole('button', { name: /^Settings/ }).click();
    await expect(page.getByRole('button', { name: 'AI settings', exact: true })).toBeVisible();
}

//Opens the Import/Export dialog from its button in the workspace's row (the top bar's until 2026-10-29).
//The first click right after WASM boot can race the app's startup re-renders and get swallowed, so click
//until the dialog actually opens.
async function openImportPanel(page) {
    const textarea = page.getByPlaceholder(/GraphSON array/);

    for (let i = 0; i < 10; i++) {
        await page.getByRole('button', { name: /Import\/Export/ }).click();

        try {
            await expect(textarea).toBeVisible({ timeout: 1500 });
            return;
        }
        catch { }
    }

    throw new Error('the Import/Export panel did not open');
}

//Closes the Import/Export dialog by its cross. A modal since 2026-10-29, it covers the view's own buttons while it is open.
async function closeImportPanel(page) {
    //In the graph's card the panel has no × and a query tab brings the canvas back (2026-11-03); before there is a
    //workspace its × is still the way back.
    const queryTab = page.locator('[data-testid="workspace-tabs"] .nav-link:not(.gdbv-ws-add)').first();

    if (await queryTab.count() > 0)
        await queryTab.click();
    else
        await page.getByTestId('import-export-dialog').getByRole('button', { name: 'Close' }).first().click();

    await expect(page.getByTestId('import-export-dialog')).toHaveCount(0);
}

//Loads a graph without any database: opens the Import/Export panel, pastes DOT and clicks
//Visualize. The app renders it in the default 2D view; waits until the layout settles.
async function loadSampleGraph(page, dot) {
    await openImportPanel(page);
    await page.getByPlaceholder(/GraphSON array/).fill(dot || SAMPLE_DOT);
    await page.getByRole('button', { name: 'Visualize', exact: true }).click();
    await expect(page.locator('#cyGraph canvas').first()).toBeVisible();
    await waitForStableLayout(page);
}

//Returns the 2D node positions [{id, x, y}] via the CytoscapeInterop global.
async function cyPositions(page) {
    const json = await page.evaluate(() => getCytoscapePositions());

    return JSON.parse(json);
}

//Waits for the canvas to hold exactly these ids, and says WHY when it does not.
//
//A delete goes through Blazor - keyboardInterop or a tap handler calls into .NET, which stages the drop
//and redraws. Everything a delete spec does before that is pure Cytoscape and needs no circuit at all, so
//an app whose circuit has died reads its selection back perfectly and then swallows the delete in silence.
//That is indistinguishable from "the delete is broken" if all you assert is what is left on the canvas.
//
//Measured 2026-09-16 at six workers: a delete shows in 113-123 ms against a 15 s budget, so a poll that
//runs its full length is not a slow machine. It is an app that stopped listening. Say which.
async function expectCanvasHolds(page, ids) {
    try {
        await expect.poll(async () => (await cyPositions(page)).map(p => p.id).sort()).toEqual(ids);
    }
    catch (failure) {
        if (await page.locator('#blazor-error-ui').isVisible())
            throw new Error('The app hit an unhandled error, so the delete reached nothing. The canvas is not the problem.');

        throw failure;
    }
}

//The 2D layouts animate for ~500ms; waits until the layout has stopped and two consecutive position samples
//match, so rendered coordinates are safe to click. Matching samples alone are not enough: cose moves the nodes
//on animation frames, and a page busy with other workers can go 150 ms without one, so two samples matched while
//it still ran and the click landed on whichever node came to stand there (2026-11-02, Globex selected for Alice).
//CytoscapeInterop.js holds the running layout in currentLayout until it stops.
async function waitForStableLayout(page) {
    let previous = null;

    for (let i = 0; i < 50; i++) {
        const current = await page.evaluate(() => {
            if (typeof currentLayout !== 'undefined' && currentLayout !== null)
                return null;

            return getCytoscapePositions();
        });

        if (previous !== null && current === previous && current !== '[]')
            return;

        previous = current;
        await page.waitForTimeout(150);
    }

    throw new Error('2D layout did not settle');
}

//Screen coordinates (page space) of a rendered 2D element, for real mouse clicks on the canvas.
async function cyScreenPoint(page, id) {
    const info = await page.evaluate(elementId => getCytoscapeElementInfo(elementId), id);

    if (!info)
        throw new Error(`element not found in the 2D graph: ${id}`);

    const box = await page.locator('#cyGraph').boundingBox();

    return { x: box.x + info.renderedPosition.x, y: box.y + info.renderedPosition.y };
}

//Switches the visualization mode via the JSON / 2D / 3D / Table button group.
async function setViewMode(page, name) {
    await page.getByRole('group', { name: 'Visualization mode' }).getByRole('button', { name: name, exact: true }).click();
}

//Switches to the 3D view and waits for the force-graph instance to hold the expected node count.
async function switchTo3d(page, expectedNodes) {
    await setViewMode(page, '3D');
    await expect(page.locator('#graph3d canvas').first()).toBeVisible();
    await expect.poll(async () => {
        return await page.evaluate(() => {
            if (!window.graph3DInterop || !window.graph3DInterop.instance)
                return -1;

            return window.graph3DInterop.instance.graphData().nodes.length;
        });
    }).toBe(expectedNodes);
}

//Collects everything the page logs as an error, for a test that asserts it logged none.
//
//One 404 is expected and filtered: a Development build asks for the git-ignored dev-secrets.json, which
//is absent on any machine whose owner never wrote one, including CI. The app handles the absence; the
//browser still logs the failed request, and that is not the kind of error these tests are looking for.
function collectConsoleErrors(page) {
    const errors = [];

    page.on('console', m => {
        if (m.type() !== 'error')
            return;

        //Chrome says only "Failed to load resource: ... 404" here; which resource is in location(),
        //so the text alone cannot tell this one apart from any other failed request.
        const from = (m.location() && m.location().url) || '';

        if (!from.includes('dev-secrets.json') && !m.text().includes('dev-secrets.json'))
            errors.push(m.text());
    });

    page.on('pageerror', e => errors.push(String((e && e.message) || e)));

    return errors;
}

module.exports = {
    collectConsoleErrors,
    SAMPLE_DOT,
    SAMPLE_NODE_COUNT,
    SAMPLE_PERSON_COUNT,
    TINY_PNG,
    gotoApp,
    declineTour,
    openAccountMenu,
    openImportPanel,
    closeImportPanel,
    loadSampleGraph,
    cyPositions,
    expectCanvasHolds,
    waitForStableLayout,
    cyScreenPoint,
    setViewMode,
    switchTo3d
};
