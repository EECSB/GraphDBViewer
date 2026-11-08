//The MCP server's live link, end to end (2026-11-03, GDS Viewer's brought across): the real gdbv-mcp, started as Claude
//starts it and spoken to over its standard input and output as Claude speaks to it, linked to a real browser tab by the
//code the tab's AI settings show. What only a browser has - the page's own WebSocket to 127.0.0.1, the card and the bar's
//chip - is checked here; both ends' protocol is LiveLinkTests'.
const { test, expect } = require('@playwright/test');
const { execFileSync, spawn } = require('child_process');
const fs = require('fs');
const net = require('net');
const path = require('path');
const { gotoApp, openAccountMenu } = require('./helpers');

const PROJECT = path.join(__dirname, '..', 'GraphDBViewer.Mcp');
const SERVER = path.join(PROJECT, 'bin', 'Debug', 'net10.0', 'gdbv-mcp.dll');

//Only where gdbv-mcp is beside the spec: both trees, since sync-core.ps1 mirrors it with the core.
test.skip(!fs.existsSync(path.join(PROJECT, 'GraphDBViewer.Mcp.csproj')), 'gdbv-mcp is not in this tree');

//From the settings: the account's menu on a host with accounts, the Settings button on one without. Treeality draws a
//Settings button for a moment as it opens, until it knows the account, so this tries until the row is there.
async function openAiSettings(page) {
    const row = page.getByRole('button', { name: 'AI settings', exact: true });

    await expect(async () => {
        if (await page.getByTestId('shell-account').count() > 0)
            await openAccountMenu(page);
        else
            await page.getByTestId('shell-settings').click({ timeout: 2000 });

        await expect(row).toBeVisible({ timeout: 2000 });
    }).toPass();

    await row.click();
}

test.beforeAll(() => {
    execFileSync('dotnet', ['build', PROJECT, '-v', 'q', '-nologo'], { stdio: 'ignore' });
});

//A port nothing is listening on, so a gdbv-mcp somebody has running is never the one found.
function freePort() {
    return new Promise(resolve => {
        const probe = net.createServer();

        probe.listen(0, '127.0.0.1', () => {
            const port = probe.address().port;

            probe.close(() => resolve(port));
        });
    });
}

//gdbv-mcp as an MCP client runs it: JSON-RPC, a message a line, on its standard input and output.
class McpClient {
    constructor(port) {
        this.next = 1;
        this.waiting = new Map();
        this.buffer = '';
        this.child = spawn('dotnet', [SERVER, '--link-port', String(port)], { stdio: ['pipe', 'pipe', 'pipe'] });
        this.child.stdout.setEncoding('utf8');
        this.child.stdout.on('data', text => this.read(text));
    }

    read(text) {
        this.buffer += text;

        let end;

        while ((end = this.buffer.indexOf('\n')) >= 0) {
            const line = this.buffer.slice(0, end).trim();

            this.buffer = this.buffer.slice(end + 1);

            if (line.length === 0)
                continue;

            const message = JSON.parse(line);

            if (message.id != null && this.waiting.has(message.id)) {
                this.waiting.get(message.id)(message);
                this.waiting.delete(message.id);
            }
        }
    }

    send(message) {
        this.child.stdin.write(JSON.stringify(message) + '\n');
    }

    request(method, params) {
        const id = this.next++;

        return new Promise(resolve => {
            this.waiting.set(id, resolve);
            this.send({ jsonrpc: '2.0', id, method, params });
        });
    }

    async start() {
        await this.request('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'e2e', title: 'The e2e client', version: '1' } });
        this.send({ jsonrpc: '2.0', method: 'notifications/initialized' });
    }

    //A tool's answer: its text, and whether it is a refusal.
    async call(name, args) {
        const reply = await this.request('tools/call', { name, arguments: args || {} });

        return { text: reply.result.content.map(block => block.text || '').join(''), isError: reply.result.isError === true };
    }

    stop() {
        this.child.kill();
    }
}

test('Claude links to the tab by its code, its calls are answered there, and Stop is said on the next one', async ({ page }) => {
    const port = await freePort();
    const mcp = new McpClient(port);

    try {
        await mcp.start();

        const tools = await mcp.request('tools/list', {});

        expect(tools.result.tools.map(tool => tool.name)).toEqual(['link_tab', 'unlink_tab', 'describe_tab', 'run_read_query', 'run_write_query', 'look_at_graph']);

        await gotoApp(page);
        await openAiSettings(page);

        const card = page.getByTestId('live-link-card');

        await card.getByTestId('live-link-port').fill(String(port));
        await card.getByTestId('live-link-port').blur();
        await card.getByTestId('live-link-start').click();
        await expect(card.getByTestId('live-link-code')).toHaveText(/^\d{4} \d{4}$/);

        const code = await card.getByTestId('live-link-code').textContent();
        const linked = await mcp.call('link_tab', { code });

        expect(linked.isError).toBe(false);
        expect(linked.text).toContain('Linked to the Graph DB Viewer tab with no database (offline) open.');
        expect(linked.text).toContain('Not connected to a database');
        await expect(card.getByTestId('live-link-status')).toContainText('to The e2e client');
        await expect(page.getByTestId('live-link-chip')).toBeVisible();

        const query = await mcp.call('run_read_query', { query: 'g.V().count()' });

        expect(query.isError).toBe(true);
        expect(query.text).toContain('not connected to a database that runs queries');
        await expect(card.getByTestId('live-link-call').first()).toContainText('run read query');

        await card.getByTestId('live-link-stop').click();
        await expect(page.getByTestId('live-link-chip')).toHaveCount(0);

        const after = await mcp.call('describe_tab');

        expect(after.isError).toBe(true);
        expect(after.text).toContain('closed the link');
    }
    finally {
        mcp.stop();
    }
});
