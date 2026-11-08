//How the viewer draws things the user asked to look otherwise (2026-11-02): the graph's card squared where the first
//query tab stands on it, a tool that is on filled a shade deeper with no ring, the link picker as small as it was, and the
//+ / − beside a saved list as the field's own border with a green + and a red −.
const { test, expect } = require('@playwright/test');
const { gotoApp, loadSampleGraph, openAccountMenu, openImportPanel, cyScreenPoint, switchTo3d, SAMPLE_NODE_COUNT } = require('./helpers');

//Dragging the divider or a page edge as far as it goes never gives the page a scroll bar it did not have (2026-11-03,
//the user): the drag stops where the page last fit, nothing past the window's edge and the row above the cards one line.
test('dragging the divider or a page edge as far as it goes gives the page no scroll bar', async ({ page }) => {
    await page.setViewportSize({ width: 1400, height: 900 });
    await gotoApp(page);
    await loadSampleGraph(page);

    await page.getByTitle('New query tab').click();

    const scrolls = () => page.evaluate(() => {
        const el = document.scrollingElement;
        const strip = document.querySelector('.gdbv-tabstrip-row');
        let across = el.scrollWidth > el.clientWidth;

        if (strip && strip.lastElementChild.getBoundingClientRect().right > strip.getBoundingClientRect().right + 0.5)
            across = true;

        return { x: across, y: el.scrollHeight > el.clientHeight };
    });
    const sideWidth = () => page.locator('.split-side').first().evaluate(el => el.getBoundingClientRect().width);
    const drag = async (handle, toX) => {
        const box = await handle.boundingBox();

        await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
        await page.mouse.down();
        await page.mouse.move(toX, box.y + box.height / 2, { steps: 20 });
        await page.mouse.up();
    };

    expect(await scrolls()).toEqual({ x: false, y: false });

    const before = await sideWidth();

    await drag(page.locator('.split-gutter').first(), 0);

    expect(await scrolls()).toEqual({ x: false, y: false });
    expect(await sideWidth()).toBeGreaterThan(before + 100);

    await drag(page.getByTestId('split-edge-left'), 1400);

    expect(await scrolls()).toEqual({ x: false, y: false });
});

//A sidebar share stored too wide to fit, as one dragged before drags stopped short was, is taken back as the page opens
//and again when the window narrows, so the page never opens or ends up scrolling (2026-11-03, the user).
test('a sidebar stored too wide is taken back as the page opens and as the window narrows', async ({ page }) => {
    await page.addInitScript(() => localStorage.setItem('graphdbviewer:sidebar-w', '85'));
    await page.setViewportSize({ width: 1400, height: 900 });
    await gotoApp(page);
    await loadSampleGraph(page);

    const scrolls = () => page.evaluate(() => {
        const el = document.scrollingElement;
        const strip = document.querySelector('.gdbv-tabstrip-row');
        let across = el.scrollWidth > el.clientWidth;

        if (strip && strip.lastElementChild.getBoundingClientRect().right > strip.getBoundingClientRect().right + 0.5)
            across = true;

        return { x: across, y: el.scrollHeight > el.clientHeight };
    });

    await expect.poll(scrolls).toEqual({ x: false, y: false });

    await page.setViewportSize({ width: 1100, height: 900 });

    await expect.poll(scrolls).toEqual({ x: false, y: false });
});

//Query tabs enough to wrap onto more lines make the cards shorter, not the page longer (2026-11-03, the user): the page
//never scrolls, and the cards start right under the row and end 8 px above the window's bottom, or at it in full width.
test('query tabs on more than one line make the cards shorter rather than the page scroll', async ({ page }) => {
    await page.setViewportSize({ width: 1400, height: 900 });
    await gotoApp(page);
    await loadSampleGraph(page);

    for (let i = 0; i < 14; i++)
        await page.getByTitle('New query tab').click();

    const measure = () => page.evaluate(() => {
        const el = document.scrollingElement;
        const row = document.querySelector('[data-testid="viewer-setup-row"]').getBoundingClientRect();
        const cards = document.querySelector('.split-row').getBoundingClientRect();

        return { scrolls: el.scrollHeight > el.clientHeight, rowHeight: row.height, top: cards.top - row.bottom, below: window.innerHeight - cards.bottom };
    });

    const wrapped = await measure();

    expect(wrapped.rowHeight).toBeGreaterThan(50);
    expect(wrapped.scrolls).toBe(false);
    expect(Math.abs(wrapped.top)).toBeLessThan(1);
    expect(Math.abs(wrapped.below - 8)).toBeLessThan(1);

    await page.getByTitle(/^Full-width/).click();
    await expect(page.locator('.gdbv-page.gdbv-fullscreen')).toHaveCount(1);

    const full = await measure();

    expect(full.scrolls).toBe(false);
    expect(Math.abs(full.below)).toBeLessThan(1);
});

//Two connections share the row (the multiple-connections spec, 2026-11-03): each one's tab, its query tabs and its +,
//then the add-connection button and Import/Export, the query panel's own tabs after them. The row wraps tab by tab, and the
//page never scrolls, at a wide window or a narrow one. Treeality's row is the standalone viewer's since 2026-11-06.
test('two connections and their tabs share the row and the page never scrolls', async ({ page }) => {
    await page.setViewportSize({ width: 1400, height: 900 });
    await gotoApp(page);
    await loadSampleGraph(page);

    const add = page.getByTestId('viewer-add-connection');


    for (let i = 0; i < 3; i++)
        await page.getByTitle('New query tab').click();

    await add.click();
    await expect(page.getByTestId('viewer-connection')).toHaveCount(2);
    await page.getByTitle('New query tab').nth(1).click();

    const look = () => page.evaluate(() => {
        const el = document.scrollingElement;
        const strip = document.querySelector('.gdbv-tabstrip-row');
        const links = [...strip.querySelectorAll('.nav-link')].filter(l => !l.closest('.gdbv-tabstrip-side'));
        const order = links.map(l => l.dataset.testid || (l.classList.contains('gdbv-ws-add') && '+') || 'query');
        let across = el.scrollWidth > el.clientWidth;

        if (strip.lastElementChild.getBoundingClientRect().right > strip.getBoundingClientRect().right + 0.5)
            across = true;

        //Tab by tab: each connection's tab on the line of its first query tab, never left alone on one. Their feet, since
        //the query tabs are the lower.
        const together = links.every((l, i) => l.dataset.testid !== 'viewer-connection' || Math.abs(l.getBoundingClientRect().bottom - links[i + 1].getBoundingClientRect().bottom) < 1);

        return { order, together, x: across, y: el.scrollHeight > el.clientHeight };
    });

    const wide = await look();

    expect(wide.order).toEqual(['viewer-connection', 'query', 'query', 'query', 'query', '+', 'viewer-connection', 'query', 'query', '+', 'viewer-add-connection', 'viewer-import-export']);
    expect(wide).toMatchObject({ together: true, x: false, y: false });

    await page.setViewportSize({ width: 1100, height: 900 });

    await expect.poll(async () => {
        const narrow = await look();

        return { together: narrow.together, x: narrow.x, y: narrow.y };
    }).toEqual({ together: true, x: false, y: false });
});

//A connection's × (the multiple-connections spec, task 5): in its tab after the name, which gives way to it when the host
//is long, and gone with the connection once the closing is agreed; the last connection has none.
test('a connection keeps its × beside a long host, and goes with it once agreed', async ({ page }) => {
    await page.setViewportSize({ width: 1400, height: 900 });
    await gotoApp(page);
    await loadSampleGraph(page);

    const add = page.getByTestId('viewer-add-connection');


    await expect(page.getByTestId('viewer-close-connection')).toHaveCount(0);
    await add.click();
    await expect(page.getByTestId('viewer-close-connection')).toHaveCount(2);
    await page.locator('[data-testid="connection-dialog"] label:has-text("Hostname") + input').fill('a-graph-database-with-a-rather-long-name.east.example.com');

    const tab = page.getByTestId('viewer-connection').nth(1);

    await expect(tab).toContainText('a-graph-database');

    const look = await tab.evaluate(link => {
        const box = link.getBoundingClientRect();
        const close = link.querySelector('[data-testid="viewer-close-connection"]').getBoundingClientRect();
        const label = link.querySelector('.gdbv-conn-label');

        return { inside: close.width > 0 && close.left >= box.left && close.right <= box.right + 0.5, cut: label.scrollWidth > label.clientWidth };
    });

    expect(look).toEqual({ inside: true, cut: true });

    //Declined on the one behind, nothing changes: the × is not also a press on its tab, which would bring it in front.
    page.once('dialog', dialog => dialog.dismiss());
    await page.getByTestId('viewer-connection').nth(0).getByTestId('viewer-close-connection').click();

    await expect(page.getByTestId('viewer-connection')).toHaveCount(2);
    await expect(tab).toHaveClass(/active/);
    await expect(page.getByTestId('viewer-connection').nth(0)).not.toHaveClass(/active/);

    page.once('dialog', dialog => dialog.accept());
    await tab.getByTestId('viewer-close-connection').click();

    await expect(page.getByTestId('viewer-connection')).toHaveCount(1);
    await expect(page.getByTestId('viewer-close-connection')).toHaveCount(0);
});

//The query tabs' + has less room on its sides than a tab with a name; the add-connection button too, and it is shaded as
//Import/Export is beside it, hovered or not, and reaches the row's line as it does, with no gap below (2026-11-03, the user).
test('the + is narrow, and the add-connection button narrow, shaded and as tall as Import/Export is', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);
    await page.mouse.move(0, 600);

    const plus = await page.locator('[data-testid="workspace-tabs"] .gdbv-ws-add').first().evaluate(link => getComputedStyle(link).paddingLeft);

    expect(plus).toBe('9.6px');

    const add = page.getByTestId('viewer-add-connection');


    const look = el => el.evaluate(link => ({ bg: getComputedStyle(link).backgroundColor, pad: getComputedStyle(link).paddingLeft, bottom: link.getBoundingClientRect().bottom }));
    const button = await look(add);
    const importExport = await look(page.getByTestId('viewer-import-export'));

    expect(button.bg).not.toBe('rgba(0, 0, 0, 0)');
    expect(button.bg).toBe(importExport.bg);
    expect(button.pad).toBe('8px');
    expect(Math.abs(button.bottom - importExport.bottom)).toBeLessThan(0.5);
});

//A connection's query tabs and + are lower than its tab, on the same line, so they read as its (2026-11-03, the user).
test('a connection\'s query tabs are lower than its tab and stand on its line', async ({ page }) => {
    await page.setViewportSize({ width: 1400, height: 900 });
    await gotoApp(page);
    await loadSampleGraph(page);

    const add = page.getByTestId('viewer-add-connection');


    await page.getByTitle('New query tab').click();

    const boxes = await page.evaluate(() => {
        const box = el => el.getBoundingClientRect();
        const groups = document.querySelector('[data-testid="viewer-connections"]');
        const connection = box(groups.querySelector('[data-testid="viewer-connection"]'));

        const links = [...groups.querySelectorAll('[data-testid="workspace-tabs"] .nav-link')];
        const first = box(links[0]).height;

        //Each as tall as the others too, the one in front and those behind.
        return links.map(link => ({ shorter: box(link).height < connection.height - 4, level: Math.abs(box(link).bottom - connection.bottom) < 1, even: Math.abs(box(link).height - first) < 0.5 }));
    });

    expect(boxes.length).toBe(3);
    expect(boxes).toEqual(boxes.map(() => ({ shorter: true, level: true, even: true })));
});

//The settings rows have their icons in one column (2026-11-03, the user). Appearance's arrow stands as far from its edge
//and from its icon as Theme's from its edge and its label, with less room at the edge than a row's own.
test('the settings rows\' icons stand in one column', async ({ page }) => {
    await gotoApp(page);
    await openAccountMenu(page);
    await page.getByRole('button', { name: 'Appearance' }).hover();
    await expect(page.getByTestId('shell-appearance')).toBeVisible();

    const look = await page.evaluate(() => {
        const icons = [...document.querySelectorAll('.gdbv-menu-row-icon')].map(icon => icon.getBoundingClientRect());
        const arrows = [...document.querySelectorAll('.gdbv-menu-row > .gdbv-menu-arrow')];
        const spacing = arrow => {
            const box = arrow.getBoundingClientRect();

            return { edge: Math.round(box.left - arrow.parentElement.getBoundingClientRect().left), next: Math.round(arrow.nextElementSibling.getBoundingClientRect().left - box.right) };
        };

        return { count: icons.length, lefts: new Set(icons.map(icon => Math.round(icon.left))).size, arrows: arrows.map(spacing) };
    });

    expect(look.count).toBeGreaterThanOrEqual(3);
    expect(look.lefts).toBe(1);
    expect(look.arrows.length).toBe(2);
    expect(look.arrows[0]).toEqual(look.arrows[1]);
    expect(look.arrows[0].edge).toBe(8);
    expect(look.arrows[0].next).toBe(8);
});

//Generate with AI stands level with Visualize's row when the two columns stand side by side, and is as solid as
//Visualize (2026-11-03, the user).
test('Generate with AI is level with Visualize and as solid', async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1000 });
    await gotoApp(page);
    await openImportPanel(page);
    await page.getByRole('button', { name: 'Example', exact: true }).click();
    await page.mouse.move(0, 900);

    const look = await page.evaluate(() => {
        const generate = document.querySelector('[data-testid="kg-generate"]');
        const visualize = [...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Visualize');
        const style = el => ({ bg: getComputedStyle(el).backgroundColor, color: getComputedStyle(el).color });

        const box = generate.getBoundingClientRect();

        return { level: Math.abs(box.bottom - visualize.getBoundingClientRect().bottom) < 1, fits: box.width < 250, generate: style(generate), visualize: style(visualize) };
    });

    expect(look).toMatchObject({ level: true, fits: true });
    expect(look.generate).toEqual(look.visualize);
});

//About opens over the connection's content in the graph's card, not behind it (2026-11-03, the user).
test('About opens over the connection in the card', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);
    await page.getByTestId('viewer-connection').first().click();
    await expect(page.locator('.gdbv-setup-in-card[data-testid="connection-dialog"]')).toBeVisible();
    await openAccountMenu(page);
    await page.getByRole('button', { name: 'About', exact: true }).click();

    const title = page.locator('.modal-title', { hasText: /^About$/ });

    await expect(title).toBeVisible();

    const onTop = await title.evaluate(el => {
        const box = el.closest('.modal-content').getBoundingClientRect();

        return el.closest('.modal-content').contains(document.elementFromPoint(box.left + 20, box.top + box.height / 2));
    });

    expect(onTop).toBe(true);
});

//In the card, Export's row is as far below its rule as Import's columns are below theirs (2026-11-03, the user).
test('Export stands as far below its rule as Import does, in the card', async ({ page }) => {
    await gotoApp(page);
    await page.getByTestId('viewer-offline').click();
    await openImportPanel(page);
    await expect(page.locator('.gdbv-setup-in-card [data-testid="import-export-body"] hr')).toHaveCount(2);

    const gaps = await page.evaluate(() => {
        const [importRule, exportRule] = document.querySelectorAll('[data-testid="import-export-body"] hr');
        const paste = importRule.nextElementSibling.querySelector('label');
        const row = document.querySelector('[data-testid="export-row"]');

        return [paste, row].map((el, i) => el.getBoundingClientRect().top - [importRule, exportRule][i].getBoundingClientRect().bottom);
    });

    expect(Math.abs(gaps[0] - gaps[1])).toBeLessThan(0.5);
});

test('the graph card is square under the first query tab', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);

    const corner = await page.locator('.split-main > .card').first().evaluate(card => getComputedStyle(card).borderTopLeftRadius);

    expect(corner).toBe('0px');
});

//Every query tab shows where it is (2026-11-03, the user): one behind has a shade of its own, a step darker under the
//pointer, and no accent; the one in front has the card's color and the accent along its top; the + keeps no shade.
test('a query tab behind has a shade of its own, darker hovered, and only the one in front the accent', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.getByTitle('New query tab').click();

    const tabs = page.locator('[data-testid="workspace-tabs"] .nav-link:not(.gdbv-ws-add)');

    await expect(tabs.nth(1)).toHaveClass(/active/);

    //The pointer away first: left where the + was, it can be over the first tab, whose shade would then be its hovered one.
    await page.mouse.move(0, 600);

    const look = el => el.evaluate(tab => ({ bg: getComputedStyle(tab).backgroundColor, ring: getComputedStyle(tab).boxShadow }));
    const card = await page.locator('.split-main > .card').first().evaluate(card => getComputedStyle(card).backgroundColor);
    const behind = await look(tabs.nth(0));
    const front = await look(tabs.nth(1));
    const add = await look(page.locator('[data-testid="workspace-tabs"] .gdbv-ws-add'));

    expect(behind.bg).not.toBe('rgba(0, 0, 0, 0)');
    expect(behind.bg).not.toBe(card);
    expect(behind.ring).toBe('none');
    expect(front.ring).toContain('inset');
    expect(add.bg).toBe('rgba(0, 0, 0, 0)');

    //It stands as low as the one in front, and what it lays over the card's line is a clear border its shade stops at.
    const standing = await tabs.nth(0).evaluate(tab => {
        const style = getComputedStyle(tab);
        const front = document.querySelector('[data-testid="workspace-tabs"] .nav-link.active').getBoundingClientRect();
        const card = document.querySelector('.split-main > .card').getBoundingClientRect();
        const box = tab.getBoundingClientRect();
        const over = box.bottom - card.top;

        return {
            level: box.bottom - front.bottom,
            lineClear: over <= 0 || (over <= parseFloat(style.borderBottomWidth) && style.borderBottomColor === 'rgba(0, 0, 0, 0)' && style.backgroundClip !== 'border-box')
        };
    });

    expect(Math.abs(standing.level)).toBeLessThan(0.5);
    expect(standing.lineClear).toBe(true);

    await tabs.nth(0).hover();

    const hovered = await look(tabs.nth(0));

    expect(hovered.bg).not.toBe(behind.bg);
    expect(hovered.bg).not.toBe('rgba(0, 0, 0, 0)');
});

test('a tool that is on is filled a shade deeper, with no ring', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);

    const grid = page.getByTitle(/^Grid mode/);
    const before = await grid.evaluate(button => getComputedStyle(button).backgroundColor);

    await grid.click();
    await expect(grid).toHaveClass(/active/);

    const on = await grid.evaluate(button => ({ bg: getComputedStyle(button).backgroundColor, ring: getComputedStyle(button).boxShadow }));

    expect(on.ring).toBe('none');
    expect(on.bg).not.toBe(before);
    expect(on.bg).not.toBe('rgba(0, 0, 0, 0)');
});

//Zoom out, zoom in and re-center as the user knew them (2026-11-02): the bold "−", "+" and "⛶" at 14px, drawn to their
//measure, each in its button's exact middle, in buttons as high as the search box. As typed characters they had sat on
//the font's baseline, 0.6 to 1.5px low. On the canvas since 2026-11-05, stacked in its bottom right corner, square.
test('the zoom and re-center glyphs are the 14px bold characters\' size, in the middle of their buttons', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);

    const search = await page.getByPlaceholder('Search').boundingBox();

    //The re-center frame 10.35 across: its 1.35px strokes then start a whole 9px apart, so the right and bottom ones fall
    //on the pixels as the left and top ones do. At 10.25 they fell 8.9 apart and drew thinner (2026-11-04, the user).
    for (const [title, size] of [['Zoom out', 6.7], ['Zoom in', 6.7], ['Re-center graph', 10.35]]) {
        const button = page.getByTitle(title, { exact: true });
        const box = await button.boundingBox();
        const glyph = await button.locator('.gdbv-glyph').boundingBox();

        expect(Math.abs(glyph.width - size)).toBeLessThan(0.03);

        if (title == 'Re-center graph') {
            const apart = await button.locator('.gdbv-glyph path').evaluate(p => [p.getBBox().width, p.getBBox().height]);

            expect(apart.map(a => Math.round(a * 1000) / 1000)).toEqual([9, 9]);
        }

        expect(Math.abs((glyph.y + glyph.height / 2) - (box.y + box.height / 2))).toBeLessThan(0.1);
        expect(Math.abs((glyph.x + glyph.width / 2) - (box.x + box.width / 2))).toBeLessThan(0.1);
        expect(Math.abs(box.height - search.height)).toBeLessThan(1.5);
        expect(Math.abs(box.width - box.height)).toBeLessThan(1);
    }

    //On the canvas, in its bottom right corner, one under another.
    const canvas = await page.locator('#cyGraph').boundingBox();
    const stack = await page.getByTestId('canvas-zoom').boundingBox();

    expect(Math.abs(canvas.x + canvas.width - 12 - (stack.x + stack.width))).toBeLessThan(2);
    expect(stack.y + stack.height).toBeLessThanOrEqual(canvas.y + canvas.height);
    expect(stack.height).toBeGreaterThan(stack.width * 2.5);
});

//Zoom out, zoom in and re-center are drawn in the buttons' own gray, as Data model and Algorithms are, not the darker
//text color they carried (2026-11-04, the user: "a bit darker than the rest").
test('the zoom and re-center glyphs are the gray of Data model\'s and Algorithms\' icons', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);

    const color = title => page.getByTitle(title, { exact: true }).first().evaluate(b => getComputedStyle(b).color);
    const dataModel = await page.getByRole('button', { name: 'Data model' }).first().evaluate(b => getComputedStyle(b).color);

    expect(await page.getByTestId('algorithms-open').evaluate(b => getComputedStyle(b).color)).toBe(dataModel);

    for (const title of ['Zoom out', 'Zoom in', 'Re-center graph'])
        expect(await color(title), title).toBe(dataModel);
});

//The link picker as it was before the 2026-10-28 style pass (the user, 2026-11-02): its Continuous link label in 0.6rem
//type on two lines, which keeps the picker one small control high. The pass had moved the size into a class that the
//small input group's own rule outranked, and the label grew to 14px and the picker from 49px to 65px.
test('the link picker is one small control high, its Continuous link label in small type', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);

    await page.getByTitle(/^Link nodes mode/).click();
    await expect(page.locator('#chkContinuousLink')).toBeVisible();

    const look = await page.evaluate(() => {
        const label = document.getElementById('chkContinuousLink').closest('.input-group').querySelector('.input-group-text');
        const picker = label.closest('.card');

        return { size: getComputedStyle(label).fontSize, height: picker.getBoundingClientRect().height };
    });

    expect(look.size).toBe('9.6px');
    expect(look.height).toBeLessThan(52);
});

test('a saved list\'s + and − have the field\'s border and background, a green + and a red −', async ({ page }) => {
    //No transitions: the theme the account keeps arrives after the first paint, and a button's background eases into
    //it while the field's does not, so a read mid-way caught one in each theme (1 run in 3, 2026-11-03).
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await gotoApp(page);
    //The saved queries' list in the query bar: the saved connections' picker has kept no + or - since 2026-11-04.
    await loadSampleGraph(page);
    await page.locator('[data-tour="query-tabs"]:visible').getByRole('button', { name: 'Query', exact: true }).click();
    await expect(page.getByTitle('Saved queries')).toBeVisible();

    const look = await page.evaluate(() => {
        const select = document.querySelector('select[title="Saved queries"]');
        const add = select.parentElement.querySelector('.gdbv-pick-add');
        const remove = select.parentElement.querySelector('.gdbv-pick-remove');
        const style = element => getComputedStyle(element);

        return {
            fieldBorder: style(select).borderTopColor,
            fieldBg: style(select).backgroundColor,
            addBorder: style(add).borderTopColor,
            addBg: style(add).backgroundColor,
            addColor: style(add).color,
            removeColor: style(remove).color
        };
    });

    expect(look.addBorder).toBe(look.fieldBorder);
    expect(look.addBg).toBe(look.fieldBg);

    //Green: the green channel leads; red: the red one does.
    const rgb = text => text.match(/\d+/g).map(Number);
    const [ar, ag, ab] = rgb(look.addColor);
    const [rr, rg, rb] = rgb(look.removeColor);

    expect(ag).toBeGreaterThan(ar);
    expect(ag).toBeGreaterThan(ab);
    expect(rr).toBeGreaterThan(rg);
    expect(rr).toBeGreaterThan(rb);
});

//The sidebar's add and remove buttons beside an input are as tall as it, square, their bolder + or − in the middle
//(2026-11-03, the user: they stood taller than the inputs, the glyph thin and off center).
test('the sidebar\'s row buttons are as tall as their inputs, the bolder glyph in the middle', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);

    const point = await cyScreenPoint(page, 'Alice');

    await page.mouse.click(point.x, point.y);
    await expect(page.getByTestId('add-property')).toBeVisible();

    const rows = await page.evaluate(() => [...document.querySelectorAll('.gdbv-row-btn')].filter(b => b.offsetParent).map(b => {
        const box = b.getBoundingClientRect();
        const input = b.parentElement.querySelector('input, select').getBoundingClientRect();
        const glyph = b.querySelector('i.bi-plus-lg, i.bi-dash-lg').getBoundingClientRect();

        return {
            tall: Math.abs(box.height - input.height) < 0.5,
            square: Math.abs(box.width - box.height) < 0.5,
            middle: Math.abs(glyph.left + glyph.width / 2 - box.left - box.width / 2) < 0.5 && Math.abs(glyph.top + glyph.height / 2 - box.top - box.height / 2) < 0.5
        };
    }));

    expect(rows.length).toBeGreaterThanOrEqual(2);
    expect(rows).toEqual(rows.map(() => ({ tall: true, square: true, middle: true })));
});

//The VR / AR viewer's two ways in look alike, each with its icon, it closes on a ×, and the QR card stands in from the
//corner (2026-11-03, the user).
test('the VR / AR viewer has two like buttons with icons, a ×, and its card in from the corner', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);
    await switchTo3d(page, SAMPLE_NODE_COUNT);

    //VR / AR stands in the canvas's bottom right corner, the zoom buttons over it with room between (2026-11-05, the user).
    const canvas = await page.locator('#graph3d').boundingBox();
    const open = await page.getByTestId('xr-open').boundingBox();
    const zoom = await page.getByTestId('canvas-zoom').boundingBox();

    expect(Math.abs(canvas.y + canvas.height - (open.y + open.height) - 12)).toBeLessThan(2);
    expect(Math.abs(canvas.x + canvas.width - (open.x + open.width) - 12)).toBeLessThan(2);
    expect(open.y - (zoom.y + zoom.height)).toBeGreaterThanOrEqual(12);

    await page.getByRole('button', { name: 'VR / AR' }).click();
    await expect(page.getByTestId('xr-close')).toBeVisible();

    const look = await page.evaluate(() => {
        const vr = document.querySelector('[data-testid="xr-enter-vr"]');
        const ar = document.querySelector('[data-testid="xr-enter-ar"]');
        const close = document.querySelector('[data-testid="xr-close"]');
        const card = document.querySelector('[data-testid="xr-qr-card"]');
        const style = el => ({ bg: getComputedStyle(el).backgroundColor, color: getComputedStyle(el).color });
        const inset = card && card.parentElement.getBoundingClientRect().right - card.getBoundingClientRect().right;

        return { alike: JSON.stringify(style(vr)) === JSON.stringify(style(ar)), icons: !!vr.querySelector('i.bi') && !!ar.querySelector('i.bi'), cross: close.classList.contains('btn-close') && close.textContent.trim() === '', inset };
    });

    expect(look).toMatchObject({ alike: true, icons: true, cross: true });

    if (look.inset !== null)
        expect(Math.round(look.inset)).toBe(32);
});

//The chain of Link nodes is drawn larger than the tools' 18px, since it fills less of its box, and the toolbar is no
//taller for it: as tall with the link button as with the button taken out (2026-11-04, the user).
test('the link tool\'s chain is drawn larger, and the toolbar is no taller for it', async ({ page }) => {
    await gotoApp(page);
    await loadSampleGraph(page);

    const sizes = await page.evaluate(() => {
        const link = document.querySelector('button[title^="Link nodes mode"]');
        const style = document.querySelector('button[title^="Style:"]');
        const group = link.closest('.gdbv-toolbar-group');
        const withLink = group.getBoundingClientRect().height;

        link.style.display = 'none';

        const withoutLink = group.getBoundingClientRect().height;

        link.style.display = '';

        return {
            chain: link.querySelector('svg').getBoundingClientRect().width,
            palette: style.querySelector('svg').getBoundingClientRect().width,
            withLink,
            withoutLink
        };
    });

    expect(sizes.chain).toBe(22);
    expect(sizes.palette).toBe(18);
    expect(sizes.withLink).toBe(sizes.withoutLink);
});
