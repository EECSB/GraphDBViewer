//The Ask AI panel as a running conversation. The Anthropic API is intercepted, so the whole loop runs in
//a real browser without spending tokens: two turns accumulate into a transcript, the query the model
//proposes is lifted out of its prose, and New chat empties the lot.
//
//What is NOT here is the approval card, which only appears when a tool runner exists, and that needs a
//live database connection. NlQueryChatTests covers that path with the provider and the runner both faked.
//
//Brought up to the window GDS Viewer's port drew (2026-11-02): the box, the model menu, Send and New chat are found by
//their data-testid, where the selectors before it named a placeholder, a model select and a Clear button that went with
//the window of 2026-11-01.
const fs = require('node:fs');
const { test, expect } = require('@playwright/test');
const { gotoApp, openAccountMenu } = require('./helpers');

const CORS = {
    'Access-Control-Allow-Origin': '*',
    'Access-Control-Allow-Headers': '*',
    'Access-Control-Allow-Methods': '*'
};

const panel = '.gdbv-dialog:not(.d-none)';
const ask = '.gdbv-ai-window:not(.d-none)';
const promptBox = ask + ' [data-testid="ai-prompt"]';

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
            body: JSON.stringify({ content: [{ type: 'text', text: reply }], stop_reason: 'end_turn', usage: { input_tokens: 100, output_tokens: 10 } })
        });
    });
}

//A throwaway model, so Send enables. The key reaches no provider: the call is intercepted.
//Added under Settings, which is where models are kept — the Ask AI panel only picks one.
async function addModel(page) {
    await openAccountMenu(page);
    await page.getByRole('button', { name: 'AI settings', exact: true }).click();
    await expect(page.locator(panel)).toBeVisible();

    await page.locator(panel).getByTestId('llm-picker-chevron').click();
    await page.locator(panel).getByTitle('Add an AI model').click();
    await page.locator(panel).getByPlaceholder('e.g. Claude Opus').fill('Fake');
    await page.locator(panel).locator('input[type="password"]').fill('not-a-real-key');
    await page.locator(panel).getByTestId('llm-form-save').click();

    await page.locator(panel + ' .btn-close').click();
    await expect(page.locator(panel)).toHaveCount(0);
}

//Offline mode opens the query editor without a database, which is what the panel hangs off.
async function openChat(page) {
    await blockRealProviders(page);
    await gotoApp(page);
    await page.getByRole('button', { name: /Offline mode/ }).click();

    await addModel(page);

    await page.getByRole('button', { name: /^✨ AI$/ }).click();
    await expect(page.locator(ask)).toBeVisible();

    //Pick the fake explicitly, in the model menu. It is not necessarily the only one on offer: a Development build
    //seeds whatever models dev-secrets.json holds, and one of those may be real.
    await page.locator(ask).getByTestId('ai-model-toggle').click();
    await page.locator(ask + ' [data-model="Fake"]').click();
    await expect(page.locator(ask).getByTestId('ai-model-chosen')).toHaveText('Fake');
}

//The chats down the window's left (2026-11-02). What a test asks is kept in its throwaway account, in the run's own
//database, which goes with the run.
const chatRows = ask + ' [data-testid="ai-chats"] [data-chat]';

async function send(page, text) {
    await page.locator(promptBox).fill(text);
    await page.locator(ask).getByTestId('ai-send').click();
}

test('the conversation keeps every turn, and New chat empties it', async ({ page }) => {
    await routeFakeProvider(page, [
        'Which metal did you mean?',
        "Here you go:\n```gremlin\ng.V().hasLabel('Product')\n```"
    ]);

    await openChat(page);

    //Starting afresh is offered only once there is a conversation to throw away.
    const newChat = page.locator(ask).getByTestId('ai-new-chat');

    await expect(newChat).toBeDisabled();

    await send(page, 'find metal products');
    await expect(page.locator(ask)).toContainText('Which metal did you mean?', { timeout: 15000 });

    await send(page, 'steel');
    await expect(page.locator(ask)).toContainText('Use this query', { timeout: 15000 });

    //A transcript, not a slot the newest answer overwrites: all four turns are still on screen.
    const transcript = await page.locator(ask).getByTestId('ai-transcript').innerText();
    expect(transcript).toContain('find metal products');
    expect(transcript).toContain('Which metal did you mean?');
    expect(transcript).toContain('steel');

    //The fence is the signal, so the query is shown on its own and the backticks are not shown at all.
    expect(transcript).toContain("g.V().hasLabel('Product')");
    expect(transcript).not.toContain('```');

    //It asks nothing, since the conversation is kept: it stays in the chats list, named for its first question.
    await newChat.click();

    await expect(page.locator(ask).getByTestId('ai-transcript')).not.toContainText('find metal products');
    await expect(page.locator(ask).getByTestId('ai-transcript')).not.toContainText('Which metal did you mean?');
    await expect(page.locator(chatRows).filter({ hasText: 'find metal products' })).toHaveCount(1);
});

//A chat is kept from its first answer on (2026-11-02, GDS Viewer's): after a reload it is still listed, opens with a line
//saying so and carries on, saves as text from its row, and is deleted from its row, which asks there.
test('a chat is kept through a reload, saved as text and deleted from its row', async ({ page }) => {
    await routeFakeProvider(page, ['Four products.', 'Still four.']);
    await openChat(page);

    await send(page, 'how many products are kept?');
    await expect(page.locator(ask)).toContainText('Four products.', { timeout: 15000 });

    const kept = page.locator(chatRows).filter({ hasText: 'how many products are kept?' });

    await expect(kept).toHaveCount(1);

    const id = await kept.getAttribute('data-chat');
    const row = page.locator(`${ask} [data-chat="${id}"]`);

    //The workspace comes back with its editor open, so Ask AI is there to press.
    await page.reload();
    await page.getByRole('button', { name: /^✨ AI$/ }).click();
    await expect(page.locator(ask)).toBeVisible();
    await page.locator(ask).getByTestId('ai-model-toggle').click();
    await page.locator(ask + ' [data-model="Fake"]').click();

    await expect(page.locator(ask).getByTestId('ai-transcript')).not.toContainText('Four products.');

    await row.click();

    await expect(page.locator(ask).getByTestId('ai-reopened')).toBeVisible();
    await expect(page.locator(ask).getByTestId('ai-transcript')).toContainText('Four products.');

    await send(page, 'and now?');
    await expect(page.locator(ask)).toContainText('Still four.', { timeout: 15000 });

    const download = page.waitForEvent('download');

    await row.hover();
    await row.getByTestId('ai-chat-export').click();

    const text = fs.readFileSync(await (await download).path(), 'utf8').replace(/\r\n/g, '\n');

    expect(text).toContain('YOU\nhow many products are kept?');
    expect(text).toContain('ASSISTANT\nStill four.');

    await row.getByTestId('ai-chat-delete').click();
    await expect(page.locator(ask).getByTestId('ai-chat-delete-yes')).toBeVisible();
    await page.locator(ask).getByTestId('ai-chat-delete-yes').click();

    await expect(row).toHaveCount(0);
    await expect(page.locator(ask).getByTestId('ai-transcript')).not.toContainText('Still four.');
});

//The rung is how far the model may go on its own. It starts at the one that asks, and it can be moved off
//it, including with no database attached, where it takes effect once there is one. A setting that cannot
//be changed is indistinguishable from a broken one.
test('how far the model may go is a setting, and it starts at ask', async ({ page }) => {
    await openChat(page);

    const mode = page.locator(ask).getByTestId('ai-mode');

    await expect(mode).toHaveValue('Ask');
    await expect(mode).toBeEnabled();

    //Every rung is offered, and the one that writes says so in its name.
    expect(await mode.locator('option').allInnerTexts()).toEqual([
        'Ask before every query',
        'Ask before changes',
        'Change without asking'
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
    await expect(page.locator(ask)).toContainText('Use this query', { timeout: 15000 });

    await page.locator(ask).getByRole('button', { name: /Use this query/ }).click();

    //The panel stays open — the point of a conversation is that it carries on after a query is taken.
    await expect(page.locator(ask)).toBeVisible();
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
    await expect(page.locator(ask).getByTestId('ai-transcript')).not.toContainText('Got it.');
    expect((await box.boundingBox()).height).toBeGreaterThan(oneLine);

    await page.keyboard.press('Enter');
    await expect(page.locator(ask)).toContainText('Got it.', { timeout: 15000 });

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
