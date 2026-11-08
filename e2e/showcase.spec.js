//The viewer's feature showcase, a page of its own beside the app, which the core carries since 2026-11-06 (it was the
//web host's). Its slideshow is wider than the page's column and has arrows beside its dots, and any picture on it opens
//large in a lightbox, which goes through them all (2026-11-06, the user).
const { test, expect } = require('@playwright/test');

test.use({ viewport: { width: 1600, height: 1000 } });

//The address the README and the EECS blog link to sends the visitor on to the showcase where the core keeps it.
test("the old showcase address forwards to the core's showcase", async ({ page }) => {
    await page.goto('/showcase/index.html');

    await expect(page).toHaveURL(/\/_content\/GraphDBViewer\.Core\/showcase\/index\.html$/);
    await expect(page.locator('#heroTag')).toHaveText('2D graph view');
});


test('the slideshow is the larger part of the hero, and its arrows go through the pictures', async ({ page }) => {
    await page.goto('/_content/GraphDBViewer.Core/showcase/index.html');

    const show = page.locator('#heroshow');
    const tag = page.locator('#heroTag');

    await expect(show).toBeVisible();

    //Most of the hero's width, and wider than the page's own column of 1140.
    const widths = await page.evaluate(() => ({
        show: document.getElementById('heroshow').getBoundingClientRect().width,
        hero: document.querySelector('.hero').getBoundingClientRect().width
    }));

    expect(widths.show).toBeGreaterThan(widths.hero * 0.55);
    expect(widths.hero).toBeGreaterThan(1140);

    await expect(tag).toHaveText('2D graph view');

    await page.locator('#heroNext').click();
    await expect(tag).toHaveText('3D graph view');

    await page.locator('#heroPrev').click();
    await page.locator('#heroPrev').click();
    await expect(tag).toHaveText('AI');
    await expect(page.locator('#heroDots .dot-btn.on')).toHaveCount(1);
    await expect(page.locator('#heroDots .dot-btn').last()).toHaveClass(/on/);
});

test('a picture opens large in a lightbox, which goes through them all and closes', async ({ page }) => {
    await page.goto('/_content/GraphDBViewer.Core/showcase/index.html');

    const lightbox = page.locator('#lightbox');
    const big = page.locator('#lbImg');

    await expect(lightbox).toBeHidden();

    //The slideshow opens the picture on screen.
    await page.locator('#heroNext').click();
    await page.locator('#heroshow .slides').click({ position: { x: 300, y: 200 } });

    await expect(lightbox).toBeVisible();
    await expect(big).toHaveAttribute('src', /app-3d\.png$/);
    await expect(page.locator('#lbCaption')).toHaveText(/3D view/);

    //Larger than it was on the page.
    const sizes = await page.evaluate(() => ({
        big: document.getElementById('lbImg').getBoundingClientRect().width,
        small: document.querySelectorAll('#heroshow .slide')[1].getBoundingClientRect().width
    }));

    expect(sizes.big).toBeGreaterThan(sizes.small);

    //Detailed view's slide follows the 3D one since the refresh of 2026-11-07, and the map's comes after it.
    await page.keyboard.press('ArrowRight');
    await expect(big).toHaveAttribute('src', /app-detailed\.png$/);

    await page.keyboard.press('ArrowRight');
    await expect(big).toHaveAttribute('src', /app-map\.jpg$/);

    await page.locator('#lbPrev').click();
    await expect(big).toHaveAttribute('src', /app-detailed\.png$/);

    await page.locator('#lbPrev').click();
    await expect(big).toHaveAttribute('src', /app-3d\.png$/);

    await page.keyboard.press('Escape');
    await expect(lightbox).toBeHidden();

    //A picture further down opens too, and a click beside it closes it.
    const shot = page.locator('#viewimg');

    await shot.scrollIntoViewIfNeeded();
    await shot.click();
    await expect(lightbox).toBeVisible();
    await expect(big).toHaveAttribute('src', /graph-2d\.png$/);

    await page.mouse.click(10, 500);
    await expect(lightbox).toBeHidden();
});

//The page's words as somebody sees them, in lower case: text that is not displayed is not in innerText, and innerText
//spells the eyebrow in the capitals its style gives it. And how many links to the public demo are on screen.
async function whatThePageShows(page) {
    return page.evaluate(() => ({
        text: document.body.innerText.toLowerCase(),
        demoLinks: [...document.querySelectorAll('a[href*="eecs.blog"]')].filter(each => each.checkVisibility()).length
    }));
}

//The standalone app's own words: its live demo, that it needs no backend or server, that nothing leaves the machine, and
//how to clone and run it.
const standaloneWords = [
    'try it live',
    'no backend needed',
    'never leave your machine',
    'servers to run',
    'try it in your browser',
    'git clone https://github.com/eecsb/graphdbviewer.git',
    'dotnet run'
];

//The words that stand in for them inside another app.
const pluginWords = [
    'part of this app',
    'through the app\'s own server',
    'query languages',
    'open it in this app'
];

//Shown inside another app as one of its plugins, the address says so (shown=plugin, which ViewerPlugin's registration adds,
//2026-11-07): the standalone app's own words are not on the page, words that fit stand in for them, and the features all
//stay.
test('shown as a plugin, the page leaves out the standalone app\'s own words', async ({ page }) => {
    await page.goto('/_content/GraphDBViewer.Core/showcase/index.html?shown=plugin');

    await expect(page.locator('#heroshow')).toBeVisible();

    const shown = await whatThePageShows(page);

    expect(shown.demoLinks).toBe(0);

    for (const words of standaloneWords)
        expect(shown.text).not.toContain(words);

    for (const words of pluginWords)
        expect(shown.text).toContain(words);

    expect(shown.text).toContain('explore your graph database');
    expect(shown.text).toContain('six engines, browser-direct');
    expect(shown.text).toContain('turn detailed view on for a label');
    await expect(page.locator('.dbtable tbody tr')).toHaveCount(8);
});

//Opened as the standalone edition opens it, with nothing in its address, the page is the standalone app's as it was.
test('opened plainly, the page is the standalone app\'s, its demo and how to run it', async ({ page }) => {
    await page.goto('/_content/GraphDBViewer.Core/showcase/index.html');

    await expect(page.locator('#heroshow')).toBeVisible();

    const shown = await whatThePageShows(page);

    expect(shown.demoLinks).toBe(3);

    for (const words of standaloneWords)
        expect(shown.text).toContain(words);

    for (const words of pluginWords)
        expect(shown.text).not.toContain(words);
});
