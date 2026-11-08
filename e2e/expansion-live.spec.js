//Double-click node expansion — the one interaction that needs a live database (OnNodeExpand
//runs a Neighbors() query). Opt-in: set GREMLIN_E2E_HOST (and optionally GREMLIN_E2E_PORT) to
//a reachable, seeded Gremlin server (e.g. a throwaway one gremlin-load-sample.ps1 has seeded: it
//empties the graph first, so never the dev graph); skipped otherwise, as the C# live-server tests
//are without GREMLIN_HOST.
//Uses the embed URL parameters (EmbedSettings) to auto-connect and auto-run the seed query.
const { test, expect } = require('@playwright/test');
const { cyPositions, waitForStableLayout, cyScreenPoint } = require('./helpers');

const host = process.env.GREMLIN_E2E_HOST;
let port = process.env.GREMLIN_E2E_PORT;
if (!port)
    port = '8182';

test('double-clicking a node expands its neighbors from the database', async ({ page }) => {
    test.skip(!host, 'set GREMLIN_E2E_HOST to run against a live, seeded Gremlin server');

    //No sign-in first: this host has no accounts, where Treeality's copy of this spec signs in as the administrator.
    const query = encodeURIComponent('g.V().limit(1)');
    await page.goto(`/?host=${host}&port=${port}&ssl=false&query=${query}`);

    await expect(page.locator('#cyGraph canvas').first()).toBeVisible({ timeout: 60000 });
    await waitForStableLayout(page);

    const before = await cyPositions(page);
    expect(before).toHaveLength(1);

    //Two quick taps on the node (within the interop's 350ms double-tap window).
    const point = await cyScreenPoint(page, before[0].id);
    await page.mouse.dblclick(point.x, point.y);

    //The seeded sample vertex has neighbors, so the graph grows.
    await expect.poll(async () => {
        const positions = await cyPositions(page);

        return positions.length;
    }, { timeout: 20000 }).toBeGreaterThan(1);
});
