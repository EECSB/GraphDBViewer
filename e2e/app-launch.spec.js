//Smoke test: the Blazor WASM app boots in the browser and shows its initial chrome. This host's own: its name and its
//edition are not Treeality's, which its copy of this spec checks for instead.
const { test, expect } = require('@playwright/test');
const { gotoApp, loadSampleGraph } = require('./helpers');

test('the app boots and shows the top bar and connection controls', async ({ page }) => {
    await gotoApp(page);

    await expect(page.getByRole('heading', { name: 'Graph DB Viewer' })).toBeVisible();
    await expect(page.getByRole('img', { name: 'Graph DB Viewer logo' })).toBeVisible();
    await expect(page.getByText('Web edition')).toBeVisible();
    await expect(page.getByRole('button', { name: /Disconnected/ })).toBeVisible();
    await expect(page.getByRole('button', { name: /Import\/Export/ })).toBeVisible();
});

//The core draws its icons from the Bootstrap Icons font, which this host's page has to load itself: without the
//stylesheet every <i class="bi …"> is an empty box, as the zoom buttons and the connection's plug were after the
//2026-11-02 sync (the user's screenshot). The glyph is a ::before whose content the stylesheet gives.
test('the icons the core draws from Bootstrap Icons are drawn', async ({ page }) => {
    await gotoApp(page);

    const glyph = await page.locator('.bi').first().evaluate(icon => {
        const before = getComputedStyle(icon, '::before');

        return { content: before.content, font: before.fontFamily };
    });

    expect(glyph.content).not.toBe('none');
    expect(glyph.font).toContain('bootstrap-icons');
    expect(await page.evaluate(async () => {
        await document.fonts.ready;

        return document.fonts.check('16px bootstrap-icons');
    })).toBe(true);
});

//With no page tabs in its bar, this host draws one strip of tabs over the graph's column (2026-11-03, the user): the
//connection first of all, at the card's left edge, then the query tabs, a gap of at least 2rem that no number of them and
//no narrowing takes away, then Import/Export, fixed, with no x, ending at the graph's card's right edge, which is square
//there. A tab behind stands on the card's
//line, which runs on under it, with a shade of its own and no accent; the one in front, of all of them, has the accent.
//Bringing the connection or Import/Export to the front shows its content over the graph's card in place of the canvas,
//and a query tab brings the canvas back. Share is one of the bar's icons, after full width and before the Settings gear.
test('the connection and Import/Export are tabs in the query tabs\' strip, their content in the graph\'s card, and Share and Settings are icons', async ({ page }) => {
    await page.setViewportSize({ width: 1400, height: 900 });
    await gotoApp(page);
    await loadSampleGraph(page);
    await page.emulateMedia({ reducedMotion: 'reduce' });

    const band = page.getByTestId('shell-band');
    const row = page.getByTestId('viewer-setup-row');
    const tabs = row.getByTestId('viewer-tabs');
    const connection = row.getByTestId('viewer-connection-tab');
    const query = page.getByTestId('workspace-tabs');
    const main = page.locator('.split-main > .card').first();

    await expect(connection.getByTestId('viewer-connection')).toBeVisible();
    await expect(tabs.getByTestId('viewer-import-export')).toBeVisible();
    await expect(band.getByTestId('viewer-import-export')).toHaveCount(0);
    await expect(tabs.getByTitle('Close tab')).toHaveCount(0);
    await expect(connection.getByTitle('Close tab')).toHaveCount(0);

    //Where the strip begins and ends, how wide its gap is, and whether the card's line shows under a tab behind.
    const measure = () => page.evaluate(() => {
        const box = el => el.getBoundingClientRect();
        const strip = box(document.querySelector('[data-testid="viewer-setup-row"] [data-testid="viewer-tabs"]'));
        //The connection's list and the query tabs' lay their tabs straight into the row's flow (2026-11-03, several
        //connections), so their own boxes have no size: the tabs' are measured.
        const first = box(document.querySelector('[data-testid="viewer-setup-row"] [data-testid="viewer-connection"]'));
        const tabs = [...document.querySelectorAll('[data-testid="workspace-tabs"] .nav-link')].map(box);
        const queries = { left: tabs[0].left, right: tabs[tabs.length - 1].right };
        const card = box(document.querySelector('.split-main > .card'));
        const link = document.querySelector('[data-testid="viewer-setup-row"] [data-testid="viewer-tabs"] [data-testid="viewer-import-export"]');
        const tab = box(link);
        const style = getComputedStyle(link);
        const front = box(document.querySelector('[data-testid="workspace-tabs"] .nav-link.active'));
        const over = tab.bottom - card.top;

        return {
            left: first.left - card.left,
            before: queries.left - first.right,
            right: strip.right - card.right,
            gap: strip.left - queries.right,
            standing: tab.bottom - front.bottom,
            lineShows: over <= 0 || (over <= parseFloat(style.borderBottomWidth) && style.borderBottomColor === 'rgba(0, 0, 0, 0)' && style.backgroundClip !== 'border-box'),
            scrolls: document.scrollingElement.scrollWidth > window.innerWidth
        };
    });

    const wide = await measure();

    expect(Math.abs(wide.left)).toBeLessThan(1);
    expect(wide.before).toBeGreaterThanOrEqual(0);
    expect(wide.before).toBeLessThan(8);
    expect(Math.abs(wide.right)).toBeLessThan(1);
    expect(wide.gap).toBeGreaterThanOrEqual(32);
    expect(Math.abs(wide.standing)).toBeLessThan(0.5);
    expect(wide.lineShows).toBe(true);
    expect(await main.evaluate(c => getComputedStyle(c).borderTopRightRadius)).toBe('0px');

    //Behind: a shade of its own and no accent, the query tab in front with it.
    const look = locator => locator.evaluate(tab => ({ bg: getComputedStyle(tab).backgroundColor, ring: getComputedStyle(tab).boxShadow }));
    const front = await look(query.locator('.nav-link.active'));
    const card = await main.evaluate(c => getComputedStyle(c).backgroundColor);

    for (const id of ['viewer-connection', 'viewer-import-export']) {
        const behind = await look(row.getByTestId(id));

        expect(behind.ring).toBe('none');
        expect(behind.bg).not.toBe('rgba(0, 0, 0, 0)');
        expect(behind.bg).not.toBe(card);
    }

    expect(front.ring).toContain('inset');

    //Twenty query tabs, and a narrower window: the gap stays, the strip still ends at the card's edge, nothing scrolls.
    for (let i = 0; i < 20; i++)
        await page.getByTitle('New query tab').click();

    await page.setViewportSize({ width: 1000, height: 900 });

    const crowded = await measure();

    expect(crowded.gap).toBeGreaterThanOrEqual(32);
    expect(Math.abs(crowded.left)).toBeLessThan(1);
    expect(Math.abs(crowded.right)).toBeLessThan(1);
    expect(crowded.scrolls).toBe(false);

    await page.setViewportSize({ width: 1400, height: 900 });

    //Import/Export to the front: its content fills the graph's card, it has the accent, and no query tab is in front.
    await tabs.getByTestId('viewer-import-export').click();

    const panel = main.locator('> .gdbv-setup-in-card');

    await expect(panel.getByPlaceholder(/GraphSON array/)).toBeVisible();
    await expect(tabs.getByTestId('viewer-import-export')).toHaveAttribute('aria-selected', 'true');
    await expect(query.locator('.nav-link.active')).toHaveCount(0);
    await expect(page.locator('.modal [data-testid="import-export-dialog"]')).toHaveCount(0);

    //The whole of the card inside its line, which stays drawn around it.
    const over = await panel.boundingBox();
    const under = await main.evaluate(c => ({ width: c.clientWidth, height: c.clientHeight }));

    expect(Math.abs(over.width - under.width)).toBeLessThan(1);
    expect(Math.abs(over.height - under.height)).toBeLessThan(1);
    expect((await look(tabs.getByTestId('viewer-import-export'))).ring).toContain('inset');
    expect((await look(tabs.getByTestId('viewer-import-export'))).bg).toBe(front.bg);

    //The connection in its place, with no title bar's ×, a query tab its only way back; then that query tab brings the
    //canvas back.
    await connection.getByTestId('viewer-connection').click();
    await expect(main.locator('> .gdbv-setup-in-card[data-testid="connection-dialog"]')).toBeVisible();
    await expect(page.getByTestId('import-export-dialog')).toHaveCount(0);
    await expect(tabs.getByTestId('viewer-import-export')).toHaveAttribute('aria-selected', 'false');
    await expect(connection.getByTestId('viewer-connection')).toHaveAttribute('aria-selected', 'true');
    await expect(main.locator('> .gdbv-setup-in-card .btn-close')).toHaveCount(0);

    await query.locator('.nav-link:not(.gdbv-ws-add)').first().click();
    await expect(page.locator('.gdbv-setup-in-card')).toHaveCount(0);
    await expect(query.locator('.nav-link.active')).toHaveCount(1);
    await expect(row.locator('.gdbv-viewer-tabs .nav-link.active')).toHaveCount(0);

    //The icons, in order: full width, Share, Settings, each the same 32px ghost with no words.
    const full = await band.getByTitle(/^Full-width/).boundingBox();
    const share = await band.getByTestId('viewer-share').boundingBox();
    const settings = await band.getByTestId('shell-settings').boundingBox();

    expect(full.x).toBeLessThan(share.x);
    expect(share.x).toBeLessThan(settings.x);

    for (const box of [share, settings]) {
        expect(Math.round(box.width)).toBe(Math.round(full.width));
        expect(Math.round(box.height)).toBe(Math.round(full.height));
    }

    await expect(band.getByTestId('shell-settings')).toHaveText('');
    await expect(band.getByTestId('viewer-share')).toHaveText('');
});

//Someone who has chosen no theme starts on Light blue 1 here (2026-11-02, the user); Treeality starts dark.
test('a first visit starts on Light blue 1', async ({ page }) => {
    await gotoApp(page);

    await expect(page.locator('html')).toHaveAttribute('data-bs-theme', 'light');
    await expect(page.locator('html')).toHaveAttribute('data-gdbv-accent', 'blue');
    await expect(page.locator('html')).toHaveAttribute('data-gdbv-tint', 'off');
});

//The tour question is not bare (2026-11-03, the user): a small picture of the viewer under its words, no logo, and
//"Don't show this again" in the buttons' row rather than on a line of its own above them.
test('the tour question has its picture under its words, and its box beside the buttons', async ({ page }) => {
    await page.goto('/');

    const prompt = page.getByTestId('tour-prompt');

    await expect(prompt).toBeVisible({ timeout: 60000 });

    const look = await prompt.evaluate(dialog => {
        const box = el => dialog.querySelector(el).getBoundingClientRect();
        const never = box('[data-testid="tour-never"]');
        const no = box('[data-testid="tour-no"]');
        const middle = never.top + never.height / 2;
        const footer = box('.modal-footer');

        const art = box('[data-testid="tour-art"]');
        const words = box('.modal-body > p');

        return { art: art.height > 60, under: art.top >= words.bottom, logo: dialog.querySelectorAll('img').length, inRow: middle > no.top && middle < no.bottom, atLeft: never.left - footer.left < 24 };
    });

    expect(look).toEqual({ art: true, under: true, logo: 0, inRow: true, atLeft: true });
});

//"Show me" on the question as the app opens starts the walk through the viewer's parts (2026-11-03, the user: GDS
//Viewer's walkthrough, not just the showcase), at its first stop.
test('Show me on the tour question starts the walkthrough', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByTestId('tour-prompt')).toBeVisible({ timeout: 60000 });

    await page.getByTestId('tour-yes').click();

    await expect(page.getByTestId('tour-prompt')).toHaveCount(0);
    await expect(page.getByTestId('tour-title')).toHaveText('Welcome to Graph DB Viewer');
    await expect(page.getByTestId('tour-count')).toHaveText(/^1 of \d+$/);
    await expect(page.locator('iframe')).toHaveCount(0);
});
