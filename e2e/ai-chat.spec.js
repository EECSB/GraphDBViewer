//The Ask AI panel as a running conversation. The Anthropic API is intercepted, so the whole loop runs in
//a real browser without spending tokens: two turns accumulate into a transcript, the query the model
//proposes is lifted out of its prose, and Clear empties the lot.
//
//What is NOT here is the approval card, which only appears when a tool runner exists, and that needs a
//live database connection. NlQueryChatTests covers that path with the provider and the runner both faked.
const { test, expect } = require('@playwright/test');
const { gotoApp } = require('./helpers');

const CORS = {
    'Access-Control-Allow-Origin': '*',
    'Access-Control-Allow-Headers': '*',
    'Access-Control-Allow-Methods': '*'
};

const panel = '.gdbv-dialog:not(.d-none)';
const promptBox = panel + " textarea[placeholder^='e.g. find all products']";
const modelPicker = "select[title^='Which saved AI model']";

//Everything that is not this app or the faked provider is refused outright.
//
//The browser under test is seeded from dev-secrets.json in a Development build, so it may hold real
//models with working keys. Picking the wrong one in a test would quietly spend a real key against a real
//API. Failing the request is loud, local, and free.
async function blockRealProviders(page) {
    await page.route('**', route => {
        const url = route.request().url();

        if (url.startsWith('http://localhost') || url.startsWith('https://localhost') || url.startsWith('https://api.anthropic.com')) {
            route.fallback();
            return;
        }

        route.abort('blockedbyclient');
    });
}

//Each POST returns the next reply, so a test can script a conversation rather than one answer.
async function routeFakeProvider(page, replies) {
    let call = 0;

    await page.route('https://api.anthropic.com/**', async route => {
        if (route.request().method() === 'OPTIONS') {
            await route.fulfill({ status: 204, headers: CORS });
            return;
        }

        const reply = replies[Math.min(call, replies.length - 1)];
        call++;

        await route.fulfill({
            status: 200,
            headers: { ...CORS, 'Content-Type': 'application/json' },
            body: JSON.stringify({ content: [{ type: 'text', text: reply }] })
        });
    });
}

//A throwaway model, so Send enables. The key never leaves the browser: the call is intercepted.
//Added under Settings, which is where models are kept — the Ask AI panel only picks one.
async function addModel(page) {
    await page.getByRole('button', { name: /Settings/ }).click();
    await page.getByRole('button', { name: 'AI settings', exact: true }).click();
    await expect(page.locator(panel)).toBeVisible();

    await page.locator(panel).getByTitle('Add an AI model').click();
    await page.locator(panel).getByPlaceholder('e.g. Claude Opus').fill('Fake');
    await page.locator(panel).locator('input[type="password"]').fill('not-a-real-key');
    await page.locator(panel).getByRole('button', { name: 'Add', exact: true }).click();

    await page.locator(panel + ' .btn-close').click();
    await expect(page.locator(panel)).toHaveCount(0);
}

//Offline mode opens the query editor without a database, which is what the panel hangs off.
async function openChat(page) {
    await blockRealProviders(page);
    await gotoApp(page);
    await page.getByRole('button', { name: /Offline mode/ }).click();

    await addModel(page);

    await page.getByRole('button', { name: /Ask AI/ }).click();
    await expect(page.locator(panel)).toBeVisible();

    //Pick the fake explicitly. It is not necessarily the only one on offer: a Development build seeds
    //whatever models dev-secrets.json holds, and one of those may be real.
    await page.locator(panel + ' ' + modelPicker).selectOption('Fake');
    await expect(page.locator(panel + ' ' + modelPicker)).toHaveValue('Fake');
}

async function send(page, text) {
    await page.locator(promptBox).fill(text);
    await page.locator(panel).getByRole('button', { name: 'Send', exact: true }).click();
}

test('the conversation keeps every turn, and Clear empties it', async ({ page }) => {
    await routeFakeProvider(page, [
        'Which metal did you mean?',
        "Here you go:\n```gremlin\ng.V().hasLabel('Product')\n```"
    ]);

    await openChat(page);

    await send(page, 'find metal products');
    await expect(page.locator(panel)).toContainText('Which metal did you mean?', { timeout: 15000 });

    await send(page, 'steel');
    await expect(page.locator(panel)).toContainText('Use this query', { timeout: 15000 });

    //A transcript, not a slot the newest answer overwrites: all four turns are still on screen.
    const transcript = await page.locator(panel + ' .gdbv-chat').innerText();
    expect(transcript).toContain('find metal products');
    expect(transcript).toContain('Which metal did you mean?');
    expect(transcript).toContain('steel');

    //The fence is the signal, so the query is shown on its own and the backticks are not shown at all.
    expect(transcript).toContain("g.V().hasLabel('Product')");
    expect(transcript).not.toContain('```');

    //Clearing asks first, and asking with nothing to lose would be noise, so it is only offered
    //once there is a conversation to throw away.
    const clear = page.locator(panel + " button[title^='Clear the conversation']");

    //It sits at the end of the composer row, past the model it applies to: the control that throws the
    //whole conversation away should be the one you travel furthest to reach.
    const picker = await page.locator(panel + ' ' + modelPicker).boundingBox();
    const bin = await clear.boundingBox();

    expect(bin.x).toBeGreaterThan(picker.x + picker.width);
    expect(Math.abs((bin.y + bin.height / 2) - (picker.y + picker.height / 2))).toBeLessThan(6);

    page.once('dialog', d => d.accept());
    await clear.click();

    const cleared = await page.locator(panel + ' .gdbv-chat').innerText();
    expect(cleared).not.toContain('find metal products');
    expect(cleared).not.toContain('Which metal did you mean?');
});

//The mode dropdown is how far the model may go on its own. It starts at the rung that asks, and it can
//be moved off it — including with no database attached, where it takes effect once there is one. A
//setting that cannot be changed is indistinguishable from a broken one.
test('how far the model may go is a setting, and it starts at ask', async ({ page }) => {
    await openChat(page);

    const mode = page.locator(panel + ' select').filter({ hasText: 'Queries:' });

    await expect(mode).toHaveValue('Ask');
    await expect(mode).toBeEnabled();

    //Every rung is offered, and the one that writes says so in its name.
    expect(await mode.locator('option').allInnerTexts()).toEqual([
        'Queries: ask each time',
        'Queries: auto-run reads',
        'Queries: auto-run reads & writes'
    ]);

    await mode.selectOption('AutoReadWrite');
    await expect(mode).toHaveValue('AutoReadWrite');

    await mode.selectOption('AutoRead');
    await expect(mode).toHaveValue('AutoRead');
});

test('the query the model proposed lands in the editor', async ({ page }) => {
    await routeFakeProvider(page, ["Try this:\n```gremlin\ng.V().hasLabel('Product').limit(5)\n```"]);

    await openChat(page);
    await send(page, 'products please');
    await expect(page.locator(panel)).toContainText('Use this query', { timeout: 15000 });

    await page.locator(panel).getByRole('button', { name: /Use this query/ }).click();

    //The panel stays open — the point of a conversation is that it carries on after a query is taken.
    await expect(page.locator(panel)).toBeVisible();
    await expect(page.locator('.monaco-editor').first()).toContainText("g.V().hasLabel('Product').limit(5)", { timeout: 15000 });
});

//Enter is the send key, so the newline it would otherwise type has to be refused in the page itself —
//which is why this is here and not only in the component tests. The box grows to fit what is in it.
test('Enter sends, Shift+Enter starts a new line, and the box grows to fit', async ({ page }) => {
    await routeFakeProvider(page, ['Got it.']);
    await openChat(page);

    const box = page.locator(promptBox);
    const oneLine = (await box.boundingBox()).height;

    await box.click();
    await page.keyboard.type('one');
    await page.keyboard.press('Shift+Enter');
    await page.keyboard.type('two');

    //Shift+Enter broke the line rather than sending, and the box is taller for it.
    expect(await box.inputValue()).toBe('one\ntwo');
    await expect(page.locator(panel + ' .gdbv-chat')).not.toContainText('Got it.');
    expect((await box.boundingBox()).height).toBeGreaterThan(oneLine);

    await page.keyboard.press('Enter');
    await expect(page.locator(panel)).toContainText('Got it.', { timeout: 15000 });

    //Sent, not typed: no stray newline was left behind, and the box is back to one line.
    expect(await box.inputValue()).toBe('');
    expect((await box.boundingBox()).height).toBeLessThanOrEqual(oneLine + 1);
});

//A pasted block grows it too, up to a ceiling past which it scrolls rather than eating the transcript.
test('a long paste grows the box but not without limit', async ({ page }) => {
    await routeFakeProvider(page, ['ok']);
    await openChat(page);

    const box = page.locator(promptBox);
    const oneLine = (await box.boundingBox()).height;

    await box.fill(Array.from({ length: 40 }, (_, i) => 'line ' + i).join('\n'));

    const grown = (await box.boundingBox()).height;

    expect(grown).toBeGreaterThan(oneLine);
    expect(grown).toBeLessThanOrEqual(220);
});
