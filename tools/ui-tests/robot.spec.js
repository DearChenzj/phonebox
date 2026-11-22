const { test, expect } = require('@playwright/test');

test('Android opens the robot and accepts Chinese commands from its own microphone session', async ({ page }) => {
  await openRobot(page);
  const face = page.locator('.robot-face');
  await expect(face).toBeInViewport();
  expect(await page.evaluate(async () => {
    const { resolvePage } = await import('/panel/main.js');
    return [resolvePage(1), resolvePage(2)];
  })).toEqual([0, -1]);
  await face.click();
  await expect(face).toHaveAttribute('data-expression', 'listening');
  expect(await page.evaluate(() => window.robotCalls)).toEqual([['listen', 'zh-CN', false]]);
  await page.evaluate(() => window.onAndroidSpeech('final', '睡觉', 2));
  await expect(face).toHaveAttribute('data-expression', 'listening');
  await page.evaluate(() => window.onAndroidSpeech('final', '笑一个。', 1));
  await expect(face).toHaveAttribute('data-expression', 'happy');
  await face.click();
  await page.evaluate(() => window.onAndroidSpeech('final', '打开仪表盘', 1));
  await expect(face).not.toBeInViewport();
  await page.evaluate(() => window.onAndroidBack());
  await expect(face).toBeInViewport();
});

test('PhoneBox notifications change the face and request a level-specific voice alert', async ({ page }) => {
  await openRobot(page);
  const face = page.locator('.robot-face');
  await notify(page, 'warn');
  await expect(face).toHaveAttribute('data-expression', 'warn');
  await expect(page.locator('.toast')).toContainText('A notification');
  expect(await page.evaluate(() => window.robotCalls)).toEqual([['notify', 'warn']]);

  await face.click();
  await notify(page, 'error');
  await expect(face).toHaveAttribute('data-expression', 'listening');
  await page.evaluate(() => window.onAndroidSpeech('error', '7', 1));
  await notify(page, 'ok');
  await expect(face).toHaveAttribute('data-expression', 'happy');
  expect(await page.evaluate(() => window.robotCalls)).toEqual([
    ['notify', 'warn'], ['listen', 'zh-CN', false], ['notify', 'ok'],
  ]);
});

test('leaving the portrait robot cancels recognition and a non-waking night notification is silent', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 780 });
  await openRobot(page);
  const face = page.locator('.robot-face');
  await expect(face).toBeInViewport();
  await face.click();
  await page.getByRole('button', { name: '仪表盘' }).click();
  await expect(face).not.toBeInViewport();
  await page.evaluate(async () => {
    const { setNight, handleNotify } = await import('/panel/main.js');
    setNight(true);
    handleNotify({ level: 'error', vibrate: false });
  });
  await expect(face).toHaveAttribute('data-expression', 'idle');
  expect(await page.evaluate(() => window.robotCalls)).toEqual([['listen', 'zh-CN', false], ['cancel', 1]]);
});

async function openRobot(page) {
  await page.addInitScript(() => {
    window.robotCalls = [];
    window.Android = {
      canListen: () => true,
      listen: (_key, lang, offline) => { window.robotCalls.push(['listen', lang, offline]); return 1; },
      stopListening: () => {},
      cancelListening: (_key, id) => window.robotCalls.push(['cancel', id]),
      robotNotify: (_key, level) => window.robotCalls.push(['notify', level]),
    };
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false },
      pages: [{ id: 'test', title: 'Test', enabled: true, widgets: [] }] }));
  });
  await page.goto('/panel/');
}

async function notify(page, level) {
  await page.evaluate(async (level) => {
    const { handleEvent } = await import('/panel/main.js');
    handleEvent({ kind: 'notify', data: { level, title: 'A notification', vibrate: false } });
  }, level);
}
