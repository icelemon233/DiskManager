const fs = require('node:fs/promises');
const path = require('node:path');

async function runWindowQa(win, client, directory, nativeTheme, trayMenu, authorizationRoot) {
  const output = path.join(directory, 'captures');
  await fs.mkdir(output, { recursive: true });
  const errors = [];
  let authorization = null;
  async function finishJob(id, state = 'complete') {
    const deadline = Date.now() + 180000;
    while (Date.now() < deadline) {
      const job = (await client.call('jobs.list')).find((job) => job.id === id);
      if (job && !['running', 'queued'].includes(job.state)) {
        if (job.state !== state)
          throw new Error(job.error || `Expected ${state}, got ${job.state}`);
        return client.call('jobs.result', { id });
      }
      await new Promise((resolve) => setTimeout(resolve, 50));
    }
    throw new Error('QA job timed out');
  }
  if (authorizationRoot) {
    const startup = (await client.call('jobs.list')).find((job) => job.kind === 'authorization');
    if (!startup) throw new Error('Startup did not request authorization');
    await finishJob(startup.id);
    const first = await client.call('scan.start', { roots: [authorizationRoot], fast: true });
    const firstScan = (await finishJob(first.jobId)).result;
    const cancelled = await client.call('scan.start', { roots: [authorizationRoot], fast: true });
    await new Promise((resolve) => setTimeout(resolve, 50));
    await client.call('jobs.cancel', { id: cancelled.jobId });
    await finishJob(cancelled.jobId, 'cancelled');
    const resumed = await client.call('scan.start', { roots: [authorizationRoot], fast: true });
    const resumedScan = (await finishJob(resumed.jobId)).result;
    if (
      firstScan.method !== 'MFT' ||
      resumedScan.method !== 'MFT' ||
      firstScan.timing.permissionMs > 250 ||
      resumedScan.timing.permissionMs > 250
    )
      throw new Error('Repeated scans did not reuse startup authorization');
    const authorizationJobs = (await client.call('jobs.list')).filter(
      (job) => job.kind === 'authorization',
    );
    if (authorizationJobs.length !== 1) throw new Error('Startup authorization ran more than once');
    authorization = {
      firstScanMs: firstScan.durationMs,
      resumedScanMs: resumedScan.durationMs,
      cancelledScan: true,
      authorizationJobs: authorizationJobs.length,
    };
  }
  win.webContents.on('console-message', (details) => {
    if (details.level === 'error') errors.push(details.message);
  });
  async function wait(expression) {
    const deadline = Date.now() + 30000;
    while (Date.now() < deadline) {
      if (await win.webContents.executeJavaScript(expression)) return;
      await new Promise((resolve) => setTimeout(resolve, 100));
    }
    throw new Error(`QA condition timed out: ${expression}`);
  }
  async function click(expression) {
    await win.webContents.executeJavaScript(`(${expression}).click()`);
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  const metrics = [];
  const tooltipChecks = [];
  const copyChecks = [];
  const driveChecks = [];
  const progressChecks = [];
  async function checkTaskProgress() {
    const originalCall = client.call;
    const previousJobs = await client.call('jobs.list');
    let task = {
      id: 'qa-progress',
      kind: 'duplicates',
      state: 'running',
      phase: path.join(directory, '较长的任务目录'.repeat(12), '当前校验文件.txt'),
      completed: 26848,
      total: 50953,
      issues: 175,
      error: null,
      result: null,
      started: Date.now(),
    };
    const cancellations = [];
    function publish(changes = {}) {
      task = { ...task, ...changes };
      win.webContents.send('disk:event', { name: 'jobs', data: [task] });
    }
    async function inspect(name, expectedPercent, expectedBars = 2) {
      await wait(
        `document.querySelectorAll('.task-progress [role=progressbar]').length === ${expectedBars}`,
      );
      await wait(
        `Array.from(document.querySelectorAll('.task-progress [role=progressbar]')).every(bar => bar.getAttribute('aria-valuenow') === ${JSON.stringify(expectedPercent == null ? null : String(expectedPercent))})`,
      );
      if (expectedPercent != null)
        await wait(
          `Array.from(document.querySelectorAll('.task-progress [role=progressbar]')).every(bar => Math.abs(bar.querySelector('.progress-fill').getBoundingClientRect().width / bar.getBoundingClientRect().width * 100 - ${expectedPercent}) <= 1)`,
        );
      const values = await win.webContents
        .executeJavaScript(`Array.from(document.querySelectorAll('.task-progress [role=progressbar]')).map(bar => {
        const rect = bar.getBoundingClientRect(); const fill = bar.querySelector('.progress-fill');
        const width = fill.getBoundingClientRect().width; const region = bar.closest('.job-strip') || bar.closest('.task-row');
        return { height:rect.height, width:rect.width, fill:width, now:bar.getAttribute('aria-valuenow'), text:bar.getAttribute('aria-valuetext'), animation:getComputedStyle(fill).animationName, overflow:region.scrollWidth > region.clientWidth };
      })`);
      if (
        values.some(
          (value) =>
            value.height < 10 ||
            value.width < 60 ||
            value.overflow ||
            (expectedPercent != null &&
              Math.abs((value.fill / value.width) * 100 - expectedPercent) > 1),
        )
      )
        throw new Error('Task progress is too thin, clipped or inconsistent with its value');
      await capture(name);
      progressChecks.push({ name, values, passed: true });
    }
    try {
      client.call = async function (method, params) {
        if (method === 'jobs.cancel' && params.id === task.id) {
          cancellations.push(params.id);
          publish({ state: 'cancelled', phase: '已取消' });
          return null;
        }
        return originalCall.call(this, method, params);
      };
      await click(
        "Array.from(document.querySelectorAll('.sidebar button')).find(button => button.textContent === '后台任务')",
      );
      win.setSize(1440, 940);
      nativeTheme.themeSource = 'light';
      win.webContents.send('disk:event', {
        name: 'preferences',
        data: { theme: 'light', nativeTheme: 'light', closeBehavior: 'quit', qa: true },
      });
      publish();
      await inspect('progress-known-light', 52.6);
      win.setSize(920, 680);
      nativeTheme.themeSource = 'dark';
      win.webContents.send('disk:event', {
        name: 'preferences',
        data: { theme: 'dark', nativeTheme: 'dark', closeBehavior: 'quit', qa: true },
      });
      publish({ completed: 9000000, total: 10000000, issues: 123456 });
      await inspect('progress-known-dark-narrow', 90);
      publish({ completed: 0, total: 100, issues: 0 });
      await inspect('progress-zero', 0);
      publish({ completed: 105, total: 100 });
      await inspect('progress-clamped', 100);
      publish({ completed: 12345, total: null });
      await inspect('progress-unknown', null);
      if (
        await win.webContents.executeJavaScript(
          "document.querySelectorAll('.progress-value').length !== 0",
        )
      )
        throw new Error('Unknown progress displays a percentage');
      win.webContents.debugger.attach('1.3');
      await win.webContents.debugger.sendCommand('Emulation.setEmulatedMedia', {
        features: [{ name: 'prefers-reduced-motion', value: 'reduce' }],
      });
      await wait(
        "Array.from(document.querySelectorAll('.progress-fill')).every(fill => getComputedStyle(fill).animationName === 'none')",
      );
      await inspect('progress-reduced-motion', null);
      await win.webContents.debugger.sendCommand('Emulation.setEmulatedMedia', { features: [] });
      win.webContents.debugger.detach();
      publish({ completed: 0, total: 0 });
      await inspect('progress-zero-total', null);
      publish({ id: 'qa-progress-queued', state: 'queued', total: null, phase: '等待当前任务' });
      await inspect('progress-queued', null);
      if (
        await win.webContents.executeJavaScript(
          "Array.from(document.querySelectorAll('.progress-fill')).some(fill => getComputedStyle(fill).animationName !== 'none')",
        )
      )
        throw new Error('Queued progress is animated as active work');
      publish({ state: 'running', completed: 40, total: 100 });
      await wait(
        "document.querySelector('.job-strip [role=progressbar]')?.getAttribute('aria-valuenow') === '40'",
      );
      await click('document.querySelector(\'.job-strip button[aria-label="取消当前任务"]\')');
      await wait("!document.querySelector('.job-strip')");
      if (cancellations.length !== 1) throw new Error('Progress cancel action did not route once');
      await inspect('progress-cancelled', 40, 1);
      publish({ id: 'qa-progress-failed', state: 'failed', error: '测试文件读取失败' });
      await wait("!!document.querySelector('.error-banner')");
      await click("document.querySelector('.error-banner button')");
      await inspect('progress-failed', 40, 1);
      publish({
        id: 'qa-progress-complete',
        state: 'complete',
        completed: 100,
        total: 100,
        error: null,
      });
      await inspect('progress-complete', 100, 1);
    } finally {
      if (win.webContents.debugger.isAttached()) {
        await win.webContents.debugger.sendCommand('Emulation.setEmulatedMedia', { features: [] });
        win.webContents.debugger.detach();
      }
      client.call = originalCall;
      win.webContents.send('disk:event', { name: 'jobs', data: previousJobs });
      win.webContents.send('disk:event', { name: 'notice', data: null });
    }
  }
  async function checkDriveSelection() {
    const originalCall = client.call;
    const overview = await originalCall.call(client, 'overview');
    const requests = [];
    const firstRoot = path.parse(overview.current.roots[0]).root;
    const secondRoot = firstRoot === 'Z:\\' ? 'Y:\\' : 'Z:\\';
    const offlineRoot = firstRoot === 'X:\\' ? 'W:\\' : 'X:\\';
    const first = {
      root: firstRoot,
      label: '测试磁盘',
      ready: true,
      total: 1024 ** 4,
      free: 512 * 1024 ** 3,
      format: 'NTFS',
    };
    const second = {
      ...first,
      root: secondRoot,
      label: '外接盘 · 长卷标显示测试 · 旅行照片与项目备份资料',
    };
    let response = {
      ...overview,
      drives: [first, second, { ...first, root: offlineRoot, ready: false }],
    };
    const selector = "document.querySelector('.drive-selector button')";
    const scanButton = "document.querySelector('.toolbar > button.primary')";
    async function resizeForInput(width, height) {
      win.setSize(width, height);
      await wait(`Math.abs(innerWidth - ${width}) <= 2 && Math.abs(innerHeight - ${height}) <= 2`);
      await win.webContents.executeJavaScript(
        'new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))',
      );
    }
    async function openDrives() {
      await win.webContents.executeJavaScript(`${selector}.focus()`);
      win.webContents.sendInputEvent({ type: 'keyDown', keyCode: 'Space' });
      win.webContents.sendInputEvent({ type: 'keyUp', keyCode: 'Space' });
      await wait("!!document.querySelector('[role=listbox]')");
    }
    async function closeDrives() {
      win.webContents.sendInputEvent({ type: 'keyDown', keyCode: 'Escape' });
      win.webContents.sendInputEvent({ type: 'keyUp', keyCode: 'Escape' });
      await wait("!document.querySelector('[role=listbox]')");
    }
    try {
      client.call = async function (method, params) {
        if (method === 'overview') return response;
        if (method === 'scan.start') {
          requests.push(params);
          return { jobId: 'qa-scan-request' };
        }
        return originalCall.call(this, method, params);
      };
      await click(
        "Array.from(document.querySelectorAll('.sidebar button')).find(button => button.textContent === '空间分析')",
      );
      await resizeForInput(1440, 940);
      await openDrives();
      await wait("document.querySelectorAll('[role=option]').length === 3");
      await capture('drive-menu-dark');
      await closeDrives();
      await openDrives();
      await wait(
        "document.activeElement === document.querySelector('[role=option][data-state=checked]')",
      );
      win.webContents.sendInputEvent({ type: 'keyDown', keyCode: 'Down' });
      win.webContents.sendInputEvent({ type: 'keyUp', keyCode: 'Down' });
      await wait(
        `document.activeElement?.textContent.includes(${JSON.stringify(secondRoot.slice(0, 2))})`,
      );
      win.webContents.sendInputEvent({ type: 'keyDown', keyCode: 'Enter' });
      win.webContents.sendInputEvent({ type: 'keyUp', keyCode: 'Enter' });
      await wait(`${selector}.textContent.includes(${JSON.stringify(secondRoot.slice(0, 2))})`);
      await click(scanButton);
      if (
        requests.length !== 1 ||
        requests[0].roots.length !== 1 ||
        requests[0].roots[0] !== secondRoot ||
        requests[0].fast !== true
      )
        throw new Error('Drive scan did not submit exactly the selected volume');
      const breadcrumb = await win.webContents.executeJavaScript(
        "document.querySelector('.breadcrumb-row .text-button').title",
      );
      if (breadcrumb !== overview.current.roots.join('、'))
        throw new Error('Selecting another disk relabelled existing results');
      response = { ...response, drives: [first] };
      await openDrives();
      await wait(`${selector}.textContent.includes('已断开')`);
      await closeDrives();
      if (!(await win.webContents.executeJavaScript(`${scanButton}.disabled`)))
        throw new Error('Disconnected drive can still be scanned');
      await capture('drive-disconnected');
      response = {
        ...response,
        drives: [first, second, { ...first, root: offlineRoot, ready: false }],
      };
      nativeTheme.themeSource = 'light';
      win.webContents.send('disk:event', {
        name: 'preferences',
        data: { theme: 'light', nativeTheme: 'light', closeBehavior: 'quit', qa: true },
      });
      await resizeForInput(920, 680);
      await openDrives();
      await wait("document.querySelectorAll('[role=option]').length === 3");
      if (
        !(await win.webContents.executeJavaScript(
          "Array.from(document.querySelectorAll('[role=option]')).some(option => option.textContent.includes('未就绪') && option.getAttribute('aria-disabled') === 'true')",
        ))
      )
        throw new Error('Unavailable volume is not disabled');
      const layout = await win.webContents.executeJavaScript(`(() => {
        const menu = document.querySelector('.select-menu').getBoundingClientRect();
        const toolbar = document.querySelector('.toolbar');
        return { menuRight: menu.right, menuBottom: menu.bottom, width: innerWidth, height: innerHeight, toolbarWidth: toolbar.clientWidth, toolbarScroll: toolbar.scrollWidth };
      })()`);
      if (
        layout.menuRight > layout.width ||
        layout.menuBottom > layout.height ||
        layout.toolbarScroll > layout.toolbarWidth
      )
        throw new Error('Drive selector overflows the narrow window');
      await capture('drive-menu-light-narrow');
      await closeDrives();
      response = { ...response, current: null };
      await openDrives();
      await closeDrives();
      await wait("document.querySelectorAll('.drive-list .drive').length === 3");
      if (
        await win.webContents.executeJavaScript(
          "Array.from(document.querySelectorAll('.drive-list .drive')).some(button => !button.querySelector('.ellipsis').textContent.trim().startsWith(button.getAttribute('aria-label').replace('扫描 ', '')))",
        )
      )
        throw new Error('Drive letters must remain visible before truncated volume labels');
      if (
        await win.webContents.executeJavaScript(
          "Array.from(document.querySelectorAll('.drive-list .drive')).some(button => button.scrollWidth > button.clientWidth)",
        )
      )
        throw new Error('Long volume label overflows its scan card');
      await capture('drive-empty');
      await click(
        `Array.from(document.querySelectorAll('.drive-list .drive')).find(button => button.getAttribute('aria-label') === ${JSON.stringify('扫描 ' + firstRoot.slice(0, 2))})`,
      );
      if (
        requests.length !== 2 ||
        requests[1].roots.length !== 1 ||
        requests[1].roots[0] !== firstRoot
      )
        throw new Error('Drive card did not scan its own volume');
      response = { ...response, drives: [] };
      await openDrives();
      await closeDrives();
      await wait("document.querySelector('.analysis-empty h2')?.textContent === '未发现可用磁盘'");
      if (!(await win.webContents.executeJavaScript(`${scanButton}.disabled`)))
        throw new Error('Scan is enabled without a disk');
      await capture('drive-unavailable');
      driveChecks.push({
        requests,
        scanDispatch: 'substitute; no volume scan or elevation',
        keyboard: true,
        disconnect: true,
        empty: true,
        narrow: layout,
        passed: true,
      });
    } finally {
      client.call = originalCall;
      if (await win.webContents.executeJavaScript("!!document.querySelector('[role=listbox]')"))
        await closeDrives();
      await openDrives();
      await closeDrives();
    }
  }
  async function checkCopyFeedback(button, expectedPath, name, exerciseRetry = false) {
    const { clipboard } = require('electron');
    const originalWrite = clipboard.writeText;
    const originalRead = clipboard.readText;
    let contents = '';
    let acceptingWrites = true;
    const writeText = (value) => {
      if (acceptingWrites) contents = value;
    };
    const readText = () => contents;
    try {
      clipboard.writeText = writeText;
      clipboard.readText = readText;
      if (clipboard.writeText !== writeText || clipboard.readText !== readText)
        throw new Error('Could not isolate the clipboard for QA');
      await click(button);
      await wait("document.querySelector('.toast')?.textContent === '路径已复制'");
      if (contents !== expectedPath) throw new Error('Copy used a different path');
      if (
        await win.webContents.executeJavaScript(
          "getComputedStyle(document.querySelector('.toast')).pointerEvents !== 'none'",
        )
      )
        throw new Error('Copy feedback intercepts input');
      await capture(name);
      if (exerciseRetry) {
        await new Promise((resolve) => setTimeout(resolve, 900));
        await click(button);
        await new Promise((resolve) => setTimeout(resolve, 1000));
        if (
          !(await win.webContents.executeJavaScript(
            "document.querySelector('.toast')?.textContent === '路径已复制'",
          ))
        )
          throw new Error('Repeated copy did not restart the feedback timer');
      }
      await wait("!document.querySelector('.toast')");
      if (exerciseRetry) {
        await click(button);
        await wait("document.querySelector('.toast')?.textContent === '路径已复制'");
        acceptingWrites = false;
        contents = '';
        await click(button);
        await wait("document.querySelector('.error-banner')?.textContent.includes('复制失败')");
        if (await win.webContents.executeJavaScript("!!document.querySelector('.toast')"))
          throw new Error('Failed copy left a success notification');
        await click("document.querySelector('.error-banner button')");
        await wait("!document.querySelector('.error-banner')");
      }
      copyChecks.push({ name, clipboard: 'in-memory substitute', exerciseRetry, passed: true });
    } finally {
      clipboard.writeText = originalWrite;
      clipboard.readText = originalRead;
    }
  }
  async function hoverAt(horizontal, vertical) {
    const point = await win.webContents.executeJavaScript(`(() => {
      const canvas = document.querySelector('.treemap canvas');
      const bounds = canvas.getBoundingClientRect();
      const x = bounds.left + bounds.width * ${horizontal};
      const y = bounds.top + bounds.height * ${vertical};
      canvas.dispatchEvent(new MouseEvent('mousemove', {bubbles:true,clientX:x,clientY:y}));
      return {x,y};
    })()`);
    await wait(
      "getComputedStyle(document.querySelector('.map-tooltip') || document.body).visibility === 'visible' && !!document.querySelector('.map-tooltip')",
    );
    const bounds = await win.webContents.executeJavaScript(`(() => {
      const tip = document.querySelector('.map-tooltip'); const r = tip.getBoundingClientRect();
      return {left:r.left,top:r.top,right:r.right,bottom:r.bottom,width:r.width,height:r.height,viewportWidth:innerWidth,viewportHeight:innerHeight,pointerEvents:getComputedStyle(tip).pointerEvents};
    })()`);
    if (
      bounds.left < 0 ||
      bounds.top < 0 ||
      bounds.right > bounds.viewportWidth ||
      bounds.bottom > bounds.viewportHeight ||
      bounds.width > 361 ||
      bounds.pointerEvents !== 'none'
    )
      throw new Error('Tooltip exceeds the viewport or intercepts input');
    if (
      point.x >= bounds.left &&
      point.x <= bounds.right &&
      point.y >= bounds.top &&
      point.y <= bounds.bottom
    )
      throw new Error('Tooltip covers the inspected point');
    tooltipChecks.push({ point, ...bounds });
    return bounds;
  }
  async function checkTooltip(name) {
    const first = await hoverAt(0.2, 0.4);
    const second = await hoverAt(0.95, 0.95);
    if (first.left === second.left && first.top === second.top)
      throw new Error('Tooltip is fixed in one corner');
    await capture(name);
    await win.webContents.executeJavaScript(
      'window.dispatchEvent(new KeyboardEvent("keydown", {key:"Escape"}))',
    );
    await wait("!document.querySelector('.map-tooltip')");
    await hoverAt(0.65, 0.55);
    await win.webContents.executeJavaScript('window.dispatchEvent(new Event("blur"))');
    await wait("!document.querySelector('.map-tooltip')");
    await hoverAt(0.55, 0.65);
    await win.webContents.executeJavaScript(
      "document.querySelector('.treemap canvas').dispatchEvent(new MouseEvent('mouseout', {bubbles:true,relatedTarget:document.body}))",
    );
    await wait("!document.querySelector('.map-tooltip')");
    await win.webContents.executeJavaScript(`(() => {
      const canvas = document.querySelector('.treemap canvas'); const r = canvas.getBoundingClientRect();
      canvas.dispatchEvent(new MouseEvent('mousemove', {bubbles:true,clientX:r.left+20,clientY:r.top+20}));
      canvas.dispatchEvent(new MouseEvent('mouseout', {bubbles:true,relatedTarget:document.body}));
    })()`);
    await new Promise((resolve) => setTimeout(resolve, 450));
    if (await win.webContents.executeJavaScript("!!document.querySelector('.map-tooltip')"))
      throw new Error('Pending hover reopened after leaving the map');
  }
  async function capture(name) {
    await new Promise((resolve) => setTimeout(resolve, 300));
    const image = await win.webContents.capturePage();
    await fs.writeFile(path.join(output, name + '.png'), image.toPNG());
    const value = await win.webContents.executeJavaScript(`(() => {
      const box = selector => { const element = document.querySelector(selector); if (!element) return null; const r = element.getBoundingClientRect(); return {x:r.x,y:r.y,width:r.width,height:r.height,scrollWidth:element.scrollWidth,clientWidth:element.clientWidth}; };
      return {titlebar:box('.titlebar'),sidebar:box('.sidebar'),main:box('.main-content'),page:box('.page-slot:not([hidden]) .page-body'),table:box('.tree-table-scroll'),map:box('.treemap'),error:document.querySelector('.error-banner')?.innerText,rows:document.querySelectorAll('.tree-grid-row[data-position]').length};
    })()`);
    metrics.push({ name, bounds: win.getBounds(), ...value });
  }
  async function auditPages() {
    const overview = await client.call('overview');
    const fixture = overview.current?.roots[0];
    if (
      !fixture ||
      !/[\\/]\.runtime[\\/]integration-[a-f0-9-]+[\\/]中文 fixture$/i.test(fixture) ||
      path.dirname(fixture) !== path.dirname(directory)
    )
      throw new Error('Page audit requires an isolated integration fixture');
    async function navigate(label) {
      await click(
        `Array.from(document.querySelectorAll('.sidebar button')).find(button => button.textContent === ${JSON.stringify(label)})`,
      );
    }
    async function closeDialog() {
      await click('document.querySelector(\'dialog button[aria-label="关闭对话框"]\')');
    }
    win.setSize(1440, 940);
    await navigate('重复文件');
    await capture('audit-duplicates-empty');
    const copy = path.join(fixture, `qa-copy-${Date.now()}.bin`);
    await fs.copyFile(path.join(fixture, 'Projects', 'sample-01.bin'), copy);
    await client.call('preferences.save', {
      ...overview.preferences,
      preferredDirectories: [path.join(fixture, 'Projects')],
    });
    const scan = await client.call('scan.start', { roots: [fixture], fast: false });
    await finishJob(scan.jobId);
    const verification = await client.call('duplicates.start', { downloadOnline: false });
    await finishJob(verification.jobId);
    await wait("document.querySelectorAll('.duplicate-file').length >= 2");
    if (
      await win.webContents.executeJavaScript(
        "document.querySelector('.duplicate-groups').innerText.includes('内容相同')",
      )
    )
      throw new Error('Duplicate groups repeat their meaning on every file');
    await capture('audit-duplicates-results');
    await checkCopyFeedback(
      'document.querySelector(\'.duplicate-file button[aria-label="复制路径"]\')',
      await win.webContents.executeJavaScript(
        "document.querySelector('.duplicate-file .path').title",
      ),
      'copy-duplicate-path',
    );
    const preview = await client.call('operations.plan', { action: 'recycle', paths: [copy] });
    await finishJob(preview.jobId);
    await wait("!!document.querySelector('dialog[open] .plan-list')");
    await capture('audit-operation-preview');
    await closeDialog();
    const saves = (await client.call('jobs.list')).filter(
      (job) => job.kind === 'snapshot-save' && ['queued', 'running'].includes(job.state),
    );
    for (const save of saves) await finishJob(save.id);
    await navigate('历史对比');
    await wait("document.querySelectorAll('.history-row input[type=checkbox]').length >= 2");
    await capture('audit-history');
    await win.webContents.executeJavaScript(
      "Array.from(document.querySelectorAll('.history-row input[type=checkbox]')).slice(0,2).forEach(input=>input.click())",
    );
    await click(
      "Array.from(document.querySelectorAll('.page-slot:not([hidden]) button')).find(button=>button.textContent==='比较所选')",
    );
    await wait("!!document.querySelector('.diff-section')");
    await capture('audit-history-diff');
    await click("document.querySelector('.history-row .record-name button')");
    await capture('audit-history-rename');
    await closeDialog();
    await navigate('清理');
    await wait(
      "!Array.from(document.querySelectorAll('.page-slot:not([hidden]) [role=status]')).some(item=>item.textContent.includes('正在查找'))",
    );
    await capture('audit-cache');
    await click(
      "Array.from(document.querySelectorAll('.page-slot:not([hidden]) .tabs button')).find(button=>button.textContent==='整理规则')",
    );
    await capture('audit-rules');
    await click(
      "Array.from(document.querySelectorAll('.page-slot:not([hidden]) button')).find(button=>button.textContent==='新建规则')",
    );
    await capture('audit-rule-editor');
    await closeDialog();
    await navigate('操作记录');
    await wait("!!document.querySelector('.operation-record')");
    await click("document.querySelector('.operation-record summary')");
    await capture('audit-operations');
    await navigate('后台任务');
    if (
      await win.webContents.executeJavaScript(
        "Array.from(document.querySelectorAll('.task-row')).some(row=>row.querySelector('.status-label.complete') && row.querySelector('p'))",
      )
    )
      throw new Error('Completed tasks repeat their terminal status');
    await capture('audit-tasks');
    await navigate('空间分析');
    await click('document.querySelector(\'button[aria-label="矩形树图布局"]\')');
    await click(
      "Array.from(document.querySelectorAll('.breadcrumb-row button')).find(button=>button.textContent==='类型')",
    );
    await capture('audit-types');
    await closeDialog();
    await click('document.querySelector(\'button[title="导出扫描结果"]\')');
    if (
      !(await win.webContents.executeJavaScript(
        "document.querySelector('dialog').innerText.includes('树图 PNG')",
      ))
    )
      throw new Error('Treemap export is missing from the export menu');
    await capture('audit-export');
    await closeDialog();
    win.setSize(920, 680);
    await capture('audit-map-narrow');
    await navigate('清理');
    await capture('audit-cleanup-narrow');
    await navigate('后台任务');
    await capture('audit-tasks-narrow');
  }
  await wait(
    "!!document.querySelector('.tree-table-scroll') && !!document.querySelector('.tree-grid-row[data-position]')",
  );
  nativeTheme.themeSource = 'dark';
  win.webContents.send('disk:event', {
    name: 'preferences',
    data: { theme: 'dark', nativeTheme: 'dark', closeBehavior: 'quit', qa: true },
  });
  await click('document.querySelector(\'button[aria-label="目录与文件表布局"]\')');
  await win.webContents.executeJavaScript(`(() => {
    const row = document.querySelector('.tree-grid-row[data-position="0"]');
    row.focus();
    row.dispatchEvent(new KeyboardEvent('keydown', {key:'ArrowDown', bubbles:true}));
  })()`);
  await wait(
    'document.querySelector(\'.tree-grid-row[data-position="1"]\')?.getAttribute("aria-selected") === "true"',
  );
  const originalWidth = await win.webContents.executeJavaScript(
    'document.querySelector(\'.tree-grid-row[role="row"]\').style.gridTemplateColumns',
  );
  await win.webContents.executeJavaScript(
    'document.querySelector(\'.column-resizer\').dispatchEvent(new KeyboardEvent("keydown", {key:"ArrowRight", bubbles:true}))',
  );
  await wait(
    `document.querySelector('.tree-grid-row[role="row"]').style.gridTemplateColumns !== ${JSON.stringify(originalWidth)}`,
  );
  await win.webContents.executeJavaScript(
    'document.querySelector(\'.column-resizer\').dispatchEvent(new KeyboardEvent("keydown", {key:"ArrowLeft", bubbles:true}))',
  );
  await capture('table-dark');
  await checkCopyFeedback(
    "Array.from(document.querySelectorAll('.selection-bar button')).find(button => button.textContent === '复制路径')",
    await win.webContents.executeJavaScript(
      'document.querySelector(\'.tree-grid-row[aria-selected="true"] .ellipsis\').title',
    ),
    'copy-analysis-path',
    true,
  );
  await checkTooltip('tooltip-table-dark');
  await click('document.querySelector(\'.tree-grid-row[data-position="1"]\')');
  await wait("document.querySelector('.treemap canvas').width > 100");
  await capture('tree-selected');
  await click('document.querySelector(\'button[aria-label="矩形树图布局"]\')');
  await click("document.querySelector('.breadcrumb-row .text-button')");
  await capture('map-dark');
  nativeTheme.themeSource = 'light';
  win.webContents.send('disk:event', {
    name: 'preferences',
    data: { theme: 'light', nativeTheme: 'light', closeBehavior: 'quit', qa: true },
  });
  await capture('map-light');
  await checkTooltip('tooltip-map-light');
  await hoverAt(0.45, 0.45);
  await click('document.querySelector(\'button[aria-label="目录与文件表布局"]\')');
  await wait("!document.querySelector('.map-tooltip')");
  await capture('table-light');
  await hoverAt(0.5, 0.5);
  win.setSize(920, 680);
  await wait("!document.querySelector('.map-tooltip')");
  await capture('table-narrow');
  await hoverAt(0.95, 0.95);
  await capture('tooltip-narrow');
  await win.webContents.executeJavaScript('window.dispatchEvent(new Event("scroll"))');
  await wait("!document.querySelector('.map-tooltip')");
  await click(
    "Array.from(document.querySelectorAll('.tree-table-toolbar .tabs button')).find(button=>button.textContent==='文件排行')",
  );
  await capture('files-narrow');
  await click('document.querySelector(\'button[aria-label="选择显示列"]\')');
  await capture('columns-dialog');
  await click('document.querySelector(\'dialog button[aria-label="关闭对话框"]\')');
  await click(
    "Array.from(document.querySelectorAll('.sidebar button')).find(button => button.textContent === '设置')",
  );
  win.setSize(1440, 940);
  await capture('settings-wide-light');
  await win.webContents.executeJavaScript(
    "document.querySelector('.settings-page').scrollTop = document.querySelector('.settings-page').scrollHeight",
  );
  await capture('settings-bottom-light');
  win.setSize(920, 680);
  await capture('settings-narrow-light');
  nativeTheme.themeSource = 'dark';
  win.webContents.send('disk:event', {
    name: 'preferences',
    data: { theme: 'dark', nativeTheme: 'dark', closeBehavior: 'quit', qa: true },
  });
  await capture('settings-narrow-dark');
  for (const value of metrics.filter((value) => value.name.startsWith('settings-'))) {
    if (
      !value.page ||
      Math.abs(value.page.x + value.page.width - value.main.x - value.main.width) > 1 ||
      value.page.scrollWidth > value.page.clientWidth
    )
      throw new Error(
        'Settings scroll region does not fill the workspace or has horizontal overflow',
      );
  }
  if (process.argv.includes('--qa-audit')) {
    await auditPages();
    await checkDriveSelection();
    await checkTaskProgress();
  }
  const trayBounds = {
    x: win.getBounds().x + 100,
    y: win.getBounds().y + 100,
    width: 24,
    height: 24,
  };
  if (!(await trayMenu.popup(trayBounds)) || !trayMenu.window.isVisible())
    throw new Error('Tray menu failed to open');
  await fs.writeFile(
    path.join(output, 'tray-light.png'),
    (await trayMenu.window.webContents.capturePage()).toPNG(),
  );
  trayMenu.hide(false);
  const render = trayMenu.render;
  trayMenu.render = async () => {};
  const stalledMenuShown = await trayMenu.popup(trayBounds).catch(() => false);
  trayMenu.render = render;
  if (stalledMenuShown || trayMenu.pending)
    throw new Error('Tray timeout did not release the pending menu');
  if (!(await trayMenu.popup(trayBounds))) throw new Error('Tray menu did not recover');
  trayMenu.hide(false);
  const native = await client.call('meta');
  if (authorizationRoot) {
    await client.call('scan.start', { roots: [authorizationRoot], fast: true });
    await new Promise((resolve) => setTimeout(resolve, 50));
    const started = Date.now();
    await client.close();
    authorization.closeWhileScanningMs = Date.now() - started;
  }
  await fs.writeFile(
    path.join(output, 'metrics.json'),
    JSON.stringify(
      {
        metrics,
        tooltipChecks,
        copyChecks,
        driveChecks,
        progressChecks,
        errors,
        native,
        authorization,
      },
      null,
      2,
    ),
  );
  if (metrics.some((value) => value.error) || errors.length)
    throw new Error('QA found renderer errors');
  return output;
}
module.exports = { runWindowQa };
