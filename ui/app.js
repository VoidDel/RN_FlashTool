// RN FlashTool 界面逻辑。
// 状态都在这里，凡是要碰系统的事（启动 openocd、下载、读写文件、文件对话框）都交给 Rust 命令。
'use strict';

const { invoke, Channel } = window.__TAURI__.core;

const $ = (id) => document.getElementById(id);

const CANCELLED = '已取消';

const state = {
  version: '',
  settings: {},
  interfaces: [],
  chips: [],
  chipId: null,
  userChipDir: '',
  installations: [],
  openocd: null,
  busy: false,
  // 地址与长度不在主界面上：烧录用芯片 YAML 里声明的地址，读取 / 验证按下去时才弹窗询问，这里只记住上次填过的值。
  lastAddress: '0x08000000',
  lastReadSize: '0x10000',
  appUpdate: null,
  blockReason: null,
};

// ---------------------------------------------------------------- 工具

const FORMAT_NAMES = { Elf: 'ELF', Bin: 'BIN', Ihex: 'Intel HEX', S19: 'Motorola S19' };

/** 根据扩展名推断格式，认不出来时返回 null；与 Rust 侧 FirmwareFormat::from_file_name 保持一致。 */
function formatFromFileName(path) {
  const match = /\.([^.\\/]+)$/.exec(path || '');
  const ext = match ? match[1].toLowerCase() : '';
  if (ext === 'bin') return 'Bin';
  if (['elf', 'axf', 'out'].includes(ext)) return 'Elf';
  if (['hex', 'ihex'].includes(ext)) return 'Ihex';
  if (['s19', 's28', 's37', 'srec', 'mot'].includes(ext)) return 'S19';
  return null;
}

/** 接受 0x 前缀的十六进制与普通十进制，与 OpenOCD 的写法一致。 */
function parseNumber(text) {
  const value = (text || '').trim();
  if (/^0x[0-9a-f]+$/i.test(value)) return parseInt(value.slice(2), 16);
  if (/^\d+$/.test(value)) return parseInt(value, 10);
  return null;
}

function fileName(path) {
  return (path || '').split(/[\\/]/).pop();
}

function samePath(a, b) {
  return (a || '').toLowerCase() === (b || '').toLowerCase();
}

function timestamp() {
  return new Date().toTimeString().slice(0, 8);
}

function errorText(error) {
  return typeof error === 'string' ? error : error?.message || String(error);
}

function formatSize(bytes) {
  return bytes > 0 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : '未知大小';
}

function formatDate(iso) {
  return iso ? iso.slice(0, 10) : '';
}

function debounce(fn, ms) {
  let timer;
  return (...args) => {
    clearTimeout(timer);
    timer = setTimeout(() => fn(...args), ms);
  };
}

function el(tag, props = {}, children = []) {
  const node = document.createElement(tag);
  Object.assign(node, props);
  for (const child of [].concat(children)) {
    if (child != null) node.append(child);
  }
  return node;
}

/** 只传改动过的字段，Rust 侧合并后落盘。 */
async function saveSettings(patch) {
  Object.assign(state.settings, patch);
  try {
    state.settings = await invoke('update_settings', { patch });
  } catch (error) {
    log.system(`保存设置失败: ${errorText(error)}`);
  }
}

function setStatus(text) {
  $('status').textContent = text;
  $('status').title = text;
}

// ---------------------------------------------------------------- 日志

/**
 * 日志面板。逐行追加到 <pre>，按帧批量刷新；超过上限时整段丢掉开头，
 * 避免长时间运行后占用过多内存，也不会像整串重设那样每来一行就复制一遍全部内容。
 */
const log = {
  MAX_LINES: 5000,
  lines: [],
  pending: [],
  scheduled: false,

  raw(text) {
    for (const line of String(text).replace(/\s+$/, '').split(/\r?\n/)) {
      this.lines.push(line);
      this.pending.push(line);
    }
    this.schedule();
  },

  system(message) {
    this.raw(`[${timestamp()}] ${message}`);
  },

  clear() {
    this.lines = [];
    this.pending = [];
    $('log').textContent = '';
    $('saveLog').disabled = true;
  },

  text() {
    return this.lines.join('\n') + '\n';
  },

  schedule() {
    if (this.scheduled) return;
    this.scheduled = true;
    requestAnimationFrame(() => this.flush());
  },

  flush() {
    this.scheduled = false;
    const view = $('log');
    const atBottom = view.scrollHeight - view.scrollTop - view.clientHeight < 24;

    if (this.lines.length > this.MAX_LINES * 1.2) {
      this.lines = this.lines.slice(-this.MAX_LINES);
      view.textContent = this.text();
    } else if (this.pending.length > 0) {
      view.append(document.createTextNode(this.pending.join('\n') + '\n'));
    }
    this.pending = [];

    if (atBottom) view.scrollTop = view.scrollHeight;
    $('saveLog').disabled = this.lines.length === 0;
  },
};

// ---------------------------------------------------------------- 对话框

/** 叠在最上层的小对话框；Esc 等同取消。 */
function openOverlay(dialog, onEscape) {
  const backdrop = el('div', { className: 'backdrop' }, dialog);
  const keydown = (event) => {
    if (event.key === 'Escape' && $('overlays').lastElementChild === backdrop) {
      event.preventDefault();
      onEscape();
    }
  };
  document.addEventListener('keydown', keydown, true);
  $('overlays').append(backdrop);
  return () => {
    document.removeEventListener('keydown', keydown, true);
    backdrop.remove();
  };
}

const ICONS = { info: 'i', question: '?', warning: '!', error: '×' };

/** 消息框。confirm 为真时有“确定 / 取消”，返回是否确定。 */
function showMessage({ kind = 'info', title, message, confirm = false }) {
  return new Promise((resolve) => {
    const ok = el('button', { className: 'accent', textContent: '确定' });
    const cancel = confirm ? el('button', { textContent: '取消' }) : null;

    const dialog = el('div', { className: 'dialog' }, [
      el('div', { className: 'dialog-body' }, [
        el('div', { className: 'message' }, [
          el('div', { className: `icon ${kind}`, textContent: ICONS[kind] }),
          el('div', {}, [el('div', { className: 'title', textContent: title }), el('div', { className: 'text', textContent: message })]),
        ]),
      ]),
      el('div', { className: 'dialog-footer' }, el('div', { className: 'row end' }, [ok, cancel])),
    ]);

    const close = openOverlay(dialog, () => finish(false));
    function finish(result) {
      close();
      resolve(result);
    }

    ok.addEventListener('click', () => finish(true));
    cancel?.addEventListener('click', () => finish(false));
    ok.focus();
  });
}

const info = (title, message) => showMessage({ kind: 'info', title, message });
const warn = (title, message) => showMessage({ kind: 'warning', title, message });
const fail = (title, message) => showMessage({ kind: 'error', title, message });
const confirmDialog = (title, message) => showMessage({ kind: 'question', title, message, confirm: true });

/**
 * “读取固件 / 验证固件”点下去之后弹出的地址输入框。readSize 为 null 时只问起始地址。
 * 用户取消时返回 null。
 */
function askMemoryRange({ title, message, address, readSize }) {
  return new Promise((resolve) => {
    const addressInput = el('input', { type: 'text', value: address, spellcheck: false });
    const sizeInput = el('input', { type: 'text', value: readSize ?? '', spellcheck: false });
    const error = el('div', { className: 'error-text', hidden: true });
    const ok = el('button', { className: 'accent', textContent: '确定' });
    const cancel = el('button', { textContent: '取消' });
    const askSize = readSize != null;

    const form = el('div', { className: 'form', style: 'grid-template-columns: 62px minmax(0, 1fr)' }, [
      el('label', { className: 'label', textContent: '起始地址' }),
      addressInput,
      askSize ? el('label', { className: 'label', textContent: '读取大小' }) : null,
      askSize ? sizeInput : null,
    ]);

    const dialog = el('div', { className: 'dialog' }, [
      el('div', { className: 'dialog-body stack' }, [
        el('div', { className: 'title', textContent: title }),
        message ? el('div', { className: 'hint', textContent: message }) : null,
        form,
        error,
      ]),
      el('div', { className: 'dialog-footer' }, el('div', { className: 'row end' }, [ok, cancel])),
    ]);

    const close = openOverlay(dialog, () => finish(null));
    function finish(result) {
      close();
      resolve(result);
    }

    function submit() {
      const addressText = addressInput.value.trim();
      if (parseNumber(addressText) == null) {
        error.textContent = '起始地址无法识别，请填十六进制（如 0x08000000）或十进制数字。';
        error.hidden = false;
        return;
      }
      const sizeText = sizeInput.value.trim();
      if (askSize && !(parseNumber(sizeText) > 0)) {
        error.textContent = '读取大小无法识别，请填大于 0 的十六进制（如 0x10000）或十进制数字。';
        error.hidden = false;
        return;
      }
      finish({ address: addressText, readSize: sizeText });
    }

    ok.addEventListener('click', submit);
    cancel.addEventListener('click', () => finish(null));
    dialog.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') submit();
    });

    // 打开就能直接改地址，省一次点击。
    addressInput.focus();
    addressInput.select();
  });
}

/** 把进度回报接到某个进度条与状态文字上。 */
function progressChannel(barId, statusId) {
  const channel = new Channel();
  channel.onmessage = ({ stage, percent }) => {
    $(statusId).textContent = stage;
    const bar = $(barId);
    bar.classList.toggle('indeterminate', percent == null);
    bar.firstElementChild.style.width = percent == null ? '' : `${percent}%`;
  };
  return channel;
}

// ---------------------------------------------------------------- 主窗口：固件与目标

function currentChip() {
  return state.chips.find((chip) => chip.id === state.chipId) ?? null;
}

function effectiveFormat() {
  return formatFromFileName($('firmwarePath').value) ?? $('formatSelect').value;
}

function renderFirmware() {
  const path = $('firmwarePath').value.trim();
  const detected = formatFromFileName(path);
  const showPicker = path !== '' && detected == null;

  $('formatLabel').hidden = !showPicker;
  $('formatSelect').hidden = !showPicker;
  $('formatSummary').hidden = showPicker;
  $('firmwarePath').title = path;

  const esp = currentChip()?.flashMethod === 'esp';
  if (!path) {
    $('formatSummary').textContent = '支持 .elf / .axf / .out、.hex、.s19 / .srec / .mot 与 .bin，格式按扩展名自动识别';
  } else if (detected === 'Bin' || esp) {
    $('formatSummary').textContent = `格式: ${FORMAT_NAMES[detected] ?? '未知'}　烧录用芯片定义里的地址，验证时会询问地址`;
  } else if (detected) {
    $('formatSummary').textContent = `格式: ${FORMAT_NAMES[detected]}　地址由文件自带`;
  }
}

function renderChips() {
  const select = $('chipSelect');
  select.replaceChildren();

  // 按系列分组，与 YAML 里的 series 字段对应。
  const groups = new Map();
  for (const chip of state.chips) {
    const series = chip.series || '其它';
    if (!groups.has(series)) groups.set(series, el('optgroup', { label: series }));
    groups.get(series).append(el('option', { value: chip.id, textContent: chip.displayName, title: chip.description }));
  }
  select.append(...groups.values());
  select.value = state.chipId ?? '';
  renderTargetSummary();
}

function renderTargetSummary() {
  const chip = currentChip();
  const summary = $('targetSummary');
  summary.classList.remove('warning');

  if (!chip) {
    summary.textContent = '没有可用的芯片定义，请在 chips/ 目录添加对应的 YAML';
  } else if (chip.flashMethod === 'esp' && state.openocd && !state.openocd.supportsEsp) {
    summary.textContent = '当前 OpenOCD 不能烧录 ESP32，请在版本管理里安装 Espressif openocd-esp32';
    summary.classList.add('warning');
  } else {
    summary.textContent = `配置文件: ${chip.targetConfig}　来源: ${fileName(chip.sourceFile)}${chip.isUserDefined ? '（自定义）' : ''}`;
  }
  summary.title = summary.textContent;
  renderFirmware();
}

/** 换芯片时把 YAML 里声明的地址与容量作为下次弹窗的默认值。 */
function applyChipDefaults(chip) {
  if (chip?.flashAddress?.trim()) state.lastAddress = chip.flashAddress.trim();
  if (chip?.flashSize?.trim()) state.lastReadSize = chip.flashSize.trim();
}

function selectChip(id, { persist = true } = {}) {
  state.chipId = id;
  $('chipSelect').value = id ?? '';
  const chip = currentChip();
  applyChipDefaults(chip);
  renderTargetSummary();
  if (persist && chip) saveSettings({ TargetChip: chip.id, Address: state.lastAddress, ReadSize: state.lastReadSize });
}

function applyChipList(list, { announce = false } = {}) {
  state.chips = list.chips;
  state.userChipDir = list.userDir;
  for (const error of list.errors) log.system(`芯片配置警告: ${error}`);
  if (announce) log.system(`已重新加载芯片配置，共 ${list.chips.length} 项（目录: ${list.builtinDir}）`);
  renderChips();
  selectChip(list.selected, { persist: announce });
}

// ---------------------------------------------------------------- 主窗口：OpenOCD

function renderOpenOcd() {
  const button = $('openocdStatus');
  const openocd = state.openocd;
  button.textContent = openocd ? `OpenOCD ${openocd.version ?? '未知版本'}` : '未找到 OpenOCD，点击安装';
  button.title = openocd ? openocd.executablePath : '未找到 OpenOCD，点击这里下载一个版本';
  renderTargetSummary();
}

async function refreshOpenOcd() {
  state.installations = await invoke('discover_openocd');
  const preferred = state.settings.SelectedOpenOcdPath;
  state.openocd =
    state.installations.find((item) => samePath(item.executablePath, preferred)) ?? state.installations[0] ?? null;

  if (state.openocd) {
    log.system(`使用 OpenOCD ${state.openocd.version ?? '未知版本'}: ${state.openocd.executablePath}`);
    if (!samePath(preferred, state.openocd.executablePath)) {
      await saveSettings({ SelectedOpenOcdPath: state.openocd.executablePath });
    }
  }
  renderOpenOcd();
}

function setNotice(id, text) {
  $(id).textContent = text;
  $(id).title = text;
  $(id).hidden = !text;
}

/** 每天最多在后台检查一次 OpenOCD 更新，失败时静默处理，不打扰烧录流程。 */
async function checkOpenOcdUpdateIfDue() {
  try {
    const update = await invoke('check_openocd_update', { current: state.openocd?.version ?? null, force: false });
    if (update) {
      setNotice('openocdUpdateNotice', `OpenOCD ${update.version} 可用，点击升级`);
      log.system(`检测到新版本 OpenOCD ${update.version}（当前 ${state.openocd?.version ?? '未知'}）`);
    }
  } catch (error) {
    log.system(`检查 OpenOCD 更新失败（可忽略）: ${errorText(error)}`);
  }
}

/** 每天最多在后台检查一次程序更新，失败时静默处理。 */
async function checkAppUpdateIfDue() {
  try {
    const release = await invoke('check_app_update', { force: false });
    if (release) {
      state.appUpdate = release;
      setNotice('appUpdateNotice', `程序 ${release.version} 可用，点击查看`);
      log.system(`检测到程序新版本 ${release.version}（当前 ${state.version}）`);
    }
  } catch (error) {
    log.system(`检查程序更新失败（可忽略）: ${errorText(error)}`);
  }
}

// ---------------------------------------------------------------- 主窗口：执行操作

function setBusy(busy) {
  state.busy = busy;
  for (const button of document.querySelectorAll('[data-busy-lock]')) button.disabled = busy;
  $('cancelButton').disabled = !busy;
  $('busyBar').hidden = !busy;
}

const OPERATION_NAMES = { flash: '烧录', verify: '验证', read: '读取', reset: '复位' };

/** 执行一次操作；日志与结论都从 Channel 推回来，结论一定排在最后一行日志之后。 */
function runOperation(request) {
  return new Promise((resolve, reject) => {
    const channel = new Channel();
    channel.onmessage = (event) => {
      if (event.kind === 'line') log.raw(event.text);
      else if (event.kind === 'done') resolve(event.result);
    };
    invoke('run_operation', { request, onEvent: channel }).catch(reject);
  });
}

async function execute(operation) {
  if (state.busy) return;

  const openocd = state.openocd;
  if (!openocd) {
    await warn('未找到 OpenOCD', '请先点击状态栏上的 OpenOCD 版本，选择或下载一个版本。');
    return;
  }

  const chip = currentChip();
  if (!chip) {
    await warn('无效的目标芯片', '没有可用的芯片定义，请在 chips/ 目录添加对应的 YAML。');
    return;
  }

  const firmwarePath = $('firmwarePath').value.trim();
  if ((operation === 'flash' || operation === 'verify') && !firmwarePath) {
    await warn('缺少固件', '请先选择固件文件。');
    return;
  }

  const format = effectiveFormat();
  const esp = chip.flashMethod === 'esp';
  // ESP 烧录写的是 SPI Flash 偏移，BIN 不带地址，所以 ESP 一律当作需要地址的情形。
  const needsAddress = esp || format === 'Bin';

  // 烧录固定用芯片声明的地址；读取每次都问，验证只在不带地址的格式下问。
  let address = chip.flashAddress?.trim() || '0x08000000';
  let readSize = state.lastReadSize;
  let outputPath = null;

  if (operation === 'flash') {
    const target = needsAddress ? `${chip.displayName} 的 ${address}` : chip.displayName;
    const extra = [
      $('fullErase').checked ? '烧录前会执行全片擦除。' : null,
      $('verifyAfterFlash').checked ? '烧录后会逐字节校验。' : null,
    ].filter(Boolean);
    const confirmed = await confirmDialog(
      '确认烧录',
      `确定要把 ${fileName(firmwarePath)} 烧录到 ${target} 吗？` + (extra.length ? `\n\n${extra.join('\n')}` : ''),
    );
    if (!confirmed) return;
  } else if (operation === 'verify' && needsAddress) {
    // ELF 的段地址和长度都在文件里，verify_image 自己会读，不必打扰用户；BIN 不问就不知道该跟哪一段比。
    const input = await askMemoryRange({
      title: '验证固件',
      message: `将 ${fileName(firmwarePath)} 与该地址开始的内容逐字节比对。`,
      address: state.lastAddress,
      readSize: null,
    });
    if (!input) return;
    address = state.lastAddress = input.address;
    saveSettings({ Address: address });
  } else if (operation === 'read') {
    const input = await askMemoryRange({
      title: '读取固件',
      message: `从 ${chip.displayName} 读回一段内容并存成 .bin 文件。`,
      address: state.lastAddress,
      readSize: state.lastReadSize,
    });
    if (!input) return;
    address = state.lastAddress = input.address;
    readSize = state.lastReadSize = input.readSize;
    saveSettings({ Address: address, ReadSize: readSize });

    outputPath = await invoke('pick_save_file', { kind: 'dump', suggestedName: `${chip.id}-dump.bin` });
    if (!outputPath) return;
  }

  const interfaceId = $('interfaceSelect').value;
  const interfaceName = state.interfaces.find((item) => item.id === interfaceId)?.displayName ?? interfaceId;
  const name = OPERATION_NAMES[operation];

  setBusy(true);
  setStatus(`正在${name}…`);
  log.system(`开始${name}（${chip.displayName} / ${interfaceName}）`);

  try {
    const result = await runOperation({
      operation,
      openOcdPath: openocd.executablePath,
      chipId: chip.id,
      interfaceId,
      resetMode: $('resetSelect').value,
      firmwarePath,
      format,
      address: address.trim(),
      readSize: readSize.trim(),
      fullErase: $('fullErase').checked,
      verifyAfterFlash: $('verifyAfterFlash').checked,
      runAfterFlash: $('runAfterFlash').checked,
      timeoutSeconds: Number($('timeoutInput').value) || 60,
      outputPath,
    });

    log.system(result.success ? `✅ ${result.summary}` : `❌ ${result.summary}`);
    if (result.detail) log.raw(result.detail);
    setStatus(result.summary);

    if (result.success) {
      const extra = operation === 'read' && outputPath ? `\n\n已保存到:\n${outputPath}` : '';
      await info('操作完成', result.summary + extra);
    } else {
      await fail('操作失败', `${result.summary}\n\n${result.detail}`.trim());
    }
  } catch (error) {
    log.system(`发生异常: ${errorText(error)}`);
    setStatus('就绪');
    await fail('发生异常', errorText(error));
  } finally {
    setBusy(false);
  }
}

async function cancelOperation() {
  log.system('正在取消当前操作…');
  setStatus('正在取消…');
  await invoke('cancel_operation');
}

async function saveLog() {
  const now = new Date();
  const pad = (n) => String(n).padStart(2, '0');
  const stamp = `${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}-${pad(now.getHours())}${pad(now.getMinutes())}${pad(now.getSeconds())}`;

  const path = await invoke('pick_save_file', { kind: 'log', suggestedName: `rn-flashtool-${stamp}.log` });
  if (!path) return;
  try {
    await invoke('write_text_file', { path, contents: log.text() });
    setStatus(`日志已保存到 ${path}`);
  } catch (error) {
    await fail('保存日志失败', errorText(error));
  }
}

// ---------------------------------------------------------------- OpenOCD 版本管理

const SOURCE_NAMES = { bundled: '内置', managed: '已下载', systemPath: '系统 PATH', custom: '手动指定' };

const manager = {
  installations: [],
  releases: { xpack: null, espressif: null },
  provider: 'xpack',
  selectedInstall: null,
  selectedRelease: null,
  activePath: '',
  busy: false,

  async open() {
    this.activePath = state.openocd?.executablePath ?? '';
    this.selectedInstall = null;
    $('mirrorInput').value = state.settings.DownloadMirror ?? '';
    $('mgrAutoCheck').checked = !!state.settings.AutoCheckOpenOcdUpdate;
    $('managerDialog').hidden = false;
    this.render();

    // 先列出本机已有的，再尝试拉在线列表；拉不到在线列表（比如没联网）只写在状态栏，不弹窗。
    await this.refreshInstalled();
    if (!this.releases[this.provider]) await this.fetchReleases({ silent: true });
  },

  async close() {
    if (this.busy) return;
    $('managerDialog').hidden = true;
    await refreshOpenOcd();
  },

  status(text) {
    $('mgrStatus').textContent = text;
    $('mgrStatus').title = text;
  },

  setBusy(busy) {
    this.busy = busy;
    for (const button of document.querySelectorAll('[data-mgr-lock]')) button.disabled = busy;
    $('mgrClose').disabled = busy;
    $('mgrCancel').disabled = !busy;
    $('mgrProgress').hidden = !busy;
    $('mgrProgress').classList.add('indeterminate');
    $('mgrProgress').firstElementChild.style.width = '';
    if (!busy) this.render();
  },

  /** 统一处理忙碌状态与取消；出错时写到状态栏，非 silent 时再弹窗。 */
  async runBusy(message, work, { silent = false } = {}) {
    if (this.busy) return;
    this.setBusy(true);
    this.status(message);
    try {
      await work();
    } catch (error) {
      const text = errorText(error);
      if (text === CANCELLED) {
        this.status('已取消');
      } else {
        this.status(`操作失败：${text}`);
        if (!silent) await fail('OpenOCD 版本管理', text);
      }
    } finally {
      this.setBusy(false);
    }
  },

  refreshInstalled() {
    return this.runBusy('正在扫描本机 OpenOCD …', () => this.refreshInstalledCore());
  },

  async refreshInstalledCore() {
    this.installations = await invoke('discover_openocd');
    this.selectedInstall =
      this.installations.find((item) => samePath(item.executablePath, this.selectedInstall?.executablePath)) ??
      this.installations.find((item) => samePath(item.executablePath, this.activePath)) ??
      this.installations[0] ??
      null;
    this.status(
      this.installations.length > 0
        ? `找到 ${this.installations.length} 个 OpenOCD`
        : '没有找到任何 OpenOCD，可在下方下载一个版本',
    );
  },

  fetchReleases(options) {
    const provider = this.provider;
    return this.runBusy('正在获取在线版本列表 …', async () => {
      const releases = await invoke('fetch_openocd_releases', { provider });
      this.releases[provider] = releases;
      this.selectedRelease = releases.find((r) => !r.isPrerelease) ?? releases[0] ?? null;

      if (provider === 'xpack') {
        const current = this.installations.find((item) => samePath(item.executablePath, this.activePath))?.version;
        const newest = releases[0]?.version;
        this.status(
          newest && isUpgrade(newest, current)
            ? `有新版本可用：${newest}（当前 ${current ?? '未知'}）`
            : `在线版本 ${releases.length} 个，当前已是较新版本`,
        );
      } else {
        this.status(`在线版本 ${releases.length} 个。烧录 ESP32 需要用这里的版本`);
      }
    }, options);
  },

  switchProvider(provider) {
    if (this.busy || provider === this.provider) return;
    this.provider = provider;
    const releases = this.releases[provider];
    this.selectedRelease = releases ? (releases.find((r) => !r.isPrerelease) ?? releases[0] ?? null) : null;
    this.render();
    if (releases) this.status(`在线版本 ${releases.length} 个`);
    else this.fetchReleases();
  },

  install() {
    const release = this.selectedRelease;
    if (!release) return;

    return this.runBusy(`正在安装 ${release.version} …`, async () => {
      const installed = await invoke('install_openocd', {
        release,
        onProgress: progressChannel('mgrProgress', 'mgrStatus'),
      });
      await this.refreshInstalledCore();
      this.selectedInstall =
        this.installations.find((item) => samePath(item.executablePath, installed.executablePath)) ?? installed;
      await this.activate();
      this.status(`已安装并切换到 ${installed.version ?? release.version}`);
    });
  },

  async activate() {
    const installation = this.selectedInstall;
    if (!installation) return;

    this.activePath = installation.executablePath;
    await saveSettings({ SelectedOpenOcdPath: installation.executablePath });
    log.system(`已切换 OpenOCD: ${installation.version ?? '未知版本'} → ${installation.executablePath}`);
    setNotice('openocdUpdateNotice', '');
    this.status(`当前使用 ${installation.version ?? '未知版本'}`);
    this.render();
  },

  async uninstall() {
    const installation = this.selectedInstall;
    if (!installation?.installDirectory || this.busy) return;
    if (!(await confirmDialog('删除 OpenOCD', `确定要删除 ${installation.version ?? '该版本'} 吗？`))) return;

    await this.runBusy('正在删除 …', async () => {
      await invoke('uninstall_openocd', {
        installDirectory: installation.installDirectory,
        executablePath: installation.executablePath,
      });
      const wasActive = samePath(this.activePath, installation.executablePath);
      this.selectedInstall = null;
      await this.refreshInstalledCore();
      this.status('已删除');

      // 删掉的正好是当前使用的版本时，退回到列表里的第一个。
      if (wasActive) {
        this.activePath = '';
        if (this.selectedInstall) await this.activate();
      }
    });
  },

  async browse() {
    const path = await invoke('pick_file', { kind: 'openOcd', initial: null });
    if (!path) return;

    const installation = await invoke('custom_openocd', { path });
    if (!installation) {
      await warn('无效的路径', '指定的文件不存在。');
      return;
    }

    let existing = this.installations.find((item) => samePath(item.executablePath, installation.executablePath));
    if (!existing) {
      this.installations.push(installation);
      existing = installation;
    }
    this.selectedInstall = existing;
    await this.activate();
  },

  render() {
    const installedList = $('installedList');
    installedList.replaceChildren(
      ...(this.installations.length === 0
        ? [el('div', { className: 'empty', textContent: '没有找到任何 OpenOCD' })]
        : this.installations.map((item) => {
            const active = samePath(item.executablePath, this.activePath);
            const row = el('div', { className: 'item', title: item.executablePath }, [
              el('span', { className: 'ellipsis' }, [
                item.version ?? '未知版本',
                active ? el('span', { className: 'badge active', textContent: '当前' }) : null,
                item.supportsEsp ? el('span', { className: 'badge', textContent: 'ESP32', title: '可烧录 ESP32' }) : null,
              ]),
              el('span', { className: 'secondary', textContent: SOURCE_NAMES[item.source] ?? item.source }),
              el('span', { className: 'secondary ellipsis', textContent: item.executablePath }),
            ]);
            row.setAttribute('aria-selected', String(item === this.selectedInstall));
            row.addEventListener('click', () => this.select('selectedInstall', item));
            row.addEventListener('dblclick', () => !this.busy && this.activate());
            return row;
          })),
    );

    const releases = this.releases[this.provider];
    const releaseList = $('releaseList');
    releaseList.replaceChildren(
      ...(!releases || releases.length === 0
        ? [el('div', { className: 'empty', textContent: releases ? '没有适用于当前系统的版本' : '点击“获取版本列表”' })]
        : releases.map((release) => {
            const row = el('div', { className: 'item', title: release.assetName }, [
              el('span', { className: 'ellipsis', textContent: release.isPrerelease ? `${release.version}（预发布）` : release.version }),
              el('span', { textContent: formatDate(release.publishedAt) }),
              el('span', { textContent: formatSize(release.sizeBytes) }),
              el('span', { className: 'secondary ellipsis', textContent: release.assetName }),
            ]);
            row.setAttribute('aria-selected', String(release === this.selectedRelease));
            row.addEventListener('click', () => this.select('selectedRelease', release));
            row.addEventListener('dblclick', () => !this.busy && this.install());
            return row;
          })),
    );

    for (const button of $('providerSwitch').children) {
      button.setAttribute('aria-pressed', String(button.dataset.provider === this.provider));
    }

    $('mgrActivePath').textContent = `当前使用: ${this.activePath || '未选择'}`;
    $('mgrActivePath').title = this.activePath;

    if (!this.busy) {
      $('mgrActivate').disabled = !this.selectedInstall;
      $('mgrUninstall').disabled = !this.selectedInstall?.installDirectory;
      $('mgrInstall').disabled = !this.selectedRelease;
    }
  },

  select(field, item) {
    if (this.busy) return;
    this[field] = item;
    this.render();
  },
};

/** 与 Rust 侧 version::is_upgrade 一致：开发快照（+dev）只比较 主.次.补丁。 */
function isUpgrade(candidate, current) {
  const parts = (text) => {
    const match = /(\d+)\.(\d+)(?:\.(\d+))?(?:[-.](\d+))?/.exec(text || '');
    return match ? match.slice(1).map((v) => Number(v ?? 0)) : [-1, -1, -1, -1];
  };
  const [a, b] = [parts(candidate), parts(current)];
  const length = /dev/i.test(current || '') ? 3 : 4;
  for (let i = 0; i < length; i++) {
    if (a[i] !== b[i]) return a[i] > b[i];
  }
  return false;
}

// ---------------------------------------------------------------- 程序更新

const updater = {
  busy: false,

  async open() {
    $('updAutoCheck').checked = !!state.settings.AutoCheckAppUpdate;
    $('updateDialog').hidden = false;
    this.status(state.appUpdate ? `发现新版本 ${state.appUpdate.version}` : '尚未检查');
    this.render();

    // 还没查过就自动查一次。
    if (!state.appUpdate) await this.check();
  },

  close() {
    if (this.busy) return;
    $('updateDialog').hidden = true;
    // 已经看过更新内容了，提示条跟着消掉。
    if (state.appUpdate && !state.blockReason) setNotice('appUpdateNotice', '');
  },

  status(text) {
    $('updStatus').textContent = text;
    $('updStatus').title = text;
  },

  setBusy(busy) {
    this.busy = busy;
    for (const button of document.querySelectorAll('[data-upd-lock]')) button.disabled = busy;
    $('updCancel').disabled = !busy;
    $('updProgress').hidden = !busy;
    $('updProgress').classList.add('indeterminate');
    $('updProgress').firstElementChild.style.width = '';
    if (!busy) this.render();
  },

  async runBusy(message, work) {
    if (this.busy) return;
    this.setBusy(true);
    this.status(message);
    try {
      await work();
    } catch (error) {
      const text = errorText(error);
      this.status(text === CANCELLED ? '已取消' : `更新失败：${text}`);
      if (text !== CANCELLED) await fail('程序更新', text);
    } finally {
      this.setBusy(false);
    }
  },

  check() {
    return this.runBusy('正在检查更新 …', async () => {
      const release = await invoke('check_app_update', { force: true });
      state.appUpdate = release;
      this.status(release ? `发现新版本 ${release.version}` : `已是最新版本（${state.version}）`);
      if (release) setNotice('appUpdateNotice', `程序 ${release.version} 可用，点击查看`);
    });
  },

  async install() {
    const release = state.appUpdate;
    if (!release || this.busy) return;

    const confirmed = await confirmDialog(
      '更新程序',
      `将从 ${state.version} 更新到 ${release.version}。\n\n下载完成后程序会自动关闭并重启，请先结束正在进行的烧录操作。`,
    );
    if (!confirmed) return;

    // 成功时程序会直接退出，不会回到这里。
    await this.runBusy(`正在准备 ${release.version} …`, () =>
      invoke('install_app_update', { release, onProgress: progressChannel('updProgress', 'updStatus') }),
    );
  },

  render() {
    const release = state.appUpdate;
    $('updCurrent').textContent = state.version;
    $('updLatest').textContent = release?.version ?? '—';
    $('updAsset').textContent = release ? `${formatSize(release.sizeBytes)}　${formatDate(release.publishedAt)}` : '';
    $('updNotes').textContent = release?.notes?.trim() || '（该版本没有提供发行说明）';
    for (const node of document.querySelectorAll('.upd-has-release')) node.hidden = !release;

    // 开发版运行或程序目录不可写时，只提供手动下载。
    $('updBlockReason').hidden = !state.blockReason;
    $('updBlockReason').textContent = state.blockReason ?? '';
    $('updInstall').hidden = !!state.blockReason;
    $('updOpenPage').hidden = !state.blockReason;
    if (!this.busy) $('updInstall').disabled = !release;
  },
};

// ---------------------------------------------------------------- 事件绑定

function bind() {
  const saveFirmwarePath = debounce(() => saveSettings({ FirmwarePath: $('firmwarePath').value.trim() }), 400);
  $('firmwarePath').addEventListener('input', () => {
    renderFirmware();
    saveFirmwarePath();
  });

  $('browseFirmware').addEventListener('click', async () => {
    const path = await invoke('pick_file', { kind: 'firmware', initial: $('firmwarePath').value.trim() || null });
    if (!path) return;
    $('firmwarePath').value = path;
    // 下拉框也顺手对齐，下次选到没有扩展名的文件时它就是个合理的起点。
    const detected = formatFromFileName(path);
    if (detected) $('formatSelect').value = detected;
    renderFirmware();
    saveSettings({ FirmwarePath: path, Format: $('formatSelect').value });
  });

  $('formatSelect').addEventListener('change', () => saveSettings({ Format: $('formatSelect').value }));
  $('fullErase').addEventListener('change', () => saveSettings({ FullErase: $('fullErase').checked }));
  $('verifyAfterFlash').addEventListener('change', () => saveSettings({ VerifyAfterFlash: $('verifyAfterFlash').checked }));
  $('runAfterFlash').addEventListener('change', () => saveSettings({ RunAfterFlash: $('runAfterFlash').checked }));
  $('interfaceSelect').addEventListener('change', () => saveSettings({ InterfaceId: $('interfaceSelect').value }));
  $('resetSelect').addEventListener('change', () => saveSettings({ ResetMode: $('resetSelect').value }));
  $('chipSelect').addEventListener('change', () => selectChip($('chipSelect').value));

  $('timeoutInput').addEventListener('change', () => {
    const value = Math.min(3600, Math.max(5, Math.round(Number($('timeoutInput').value) || 60)));
    $('timeoutInput').value = value;
    saveSettings({ TimeoutSeconds: value });
  });

  $('reloadChips').addEventListener('click', async () => {
    applyChipList(await invoke('reload_chips', { current: state.chipId ?? '' }), { announce: true });
  });
  $('openChipFolder').addEventListener('click', () => openLocation('userChips'));

  $('flashButton').addEventListener('click', () => execute('flash'));
  $('verifyButton').addEventListener('click', () => execute('verify'));
  $('readButton').addEventListener('click', () => execute('read'));
  $('resetButton').addEventListener('click', () => execute('reset'));
  $('cancelButton').addEventListener('click', cancelOperation);

  $('saveLog').addEventListener('click', saveLog);
  $('clearLog').addEventListener('click', () => log.clear());

  $('openocdStatus').addEventListener('click', () => manager.open());
  $('openocdUpdateNotice').addEventListener('click', () => manager.open());
  $('appVersion').addEventListener('click', () => updater.open());
  $('appUpdateNotice').addEventListener('click', () => updater.open());

  $('mgrRefresh').addEventListener('click', () => manager.refreshInstalled());
  $('mgrActivate').addEventListener('click', () => manager.activate());
  $('mgrUninstall').addEventListener('click', () => manager.uninstall());
  $('mgrBrowse').addEventListener('click', () => manager.browse());
  $('mgrOpenFolder').addEventListener('click', () => openLocation('openOcdInstallRoot'));
  $('mgrFetch').addEventListener('click', () => manager.fetchReleases());
  $('mgrInstall').addEventListener('click', () => manager.install());
  $('mgrCancel').addEventListener('click', () => invoke('cancel_task'));
  $('mgrClose').addEventListener('click', () => manager.close());
  for (const button of $('providerSwitch').children) {
    button.addEventListener('click', () => manager.switchProvider(button.dataset.provider));
  }
  const saveMirror = debounce(() => saveSettings({ DownloadMirror: $('mirrorInput').value.trim() }), 400);
  $('mirrorInput').addEventListener('input', saveMirror);
  $('mgrAutoCheck').addEventListener('change', () => saveSettings({ AutoCheckOpenOcdUpdate: $('mgrAutoCheck').checked }));

  $('updCheck').addEventListener('click', () => updater.check());
  $('updInstall').addEventListener('click', () => updater.install());
  $('updCancel').addEventListener('click', () => invoke('cancel_task'));
  $('updOpenPage').addEventListener('click', () => openLocation('releasesPage'));
  $('updClose').addEventListener('click', () => updater.close());
  $('updAutoCheck').addEventListener('change', () => saveSettings({ AutoCheckAppUpdate: $('updAutoCheck').checked }));

  // 版本管理 / 更新窗口里按 Esc 关闭（上面没有叠着别的消息框时）。
  document.addEventListener('keydown', (event) => {
    if (event.key !== 'Escape' || $('overlays').childElementCount > 0) return;
    if (!$('managerDialog').hidden) manager.close();
    else if (!$('updateDialog').hidden) updater.close();
  });

  // WebView 默认的右键菜单（刷新、检查元素）对这个工具没有意义，输入框和日志里保留。
  document.addEventListener('contextmenu', (event) => {
    if (!event.target.closest('input, .log, .message .text, .notes')) event.preventDefault();
  });
}

async function openLocation(target) {
  try {
    await invoke('open_location', { target });
  } catch (error) {
    await fail('打开失败', errorText(error));
  }
}

// ---------------------------------------------------------------- 启动

/** 读芯片目录、恢复上次的选择、找 OpenOCD、按需检查更新。 */
async function init() {
  bind();

  const boot = await invoke('bootstrap');
  state.version = boot.version;
  state.settings = boot.settings;
  state.interfaces = boot.interfaces;
  state.blockReason = boot.selfUpdateBlockReason;

  const s = boot.settings;
  $('appVersion').textContent = `v${boot.version}`;
  $('interfaceSelect').replaceChildren(
    ...boot.interfaces.map((item) => el('option', { value: item.id, textContent: item.displayName })),
  );
  $('interfaceSelect').value = boot.interfaces.some((item) => item.id === s.InterfaceId) ? s.InterfaceId : 'cmsis-dap';
  $('resetSelect').value = s.ResetMode === 'hardware' ? 'hardware' : 'software';
  $('firmwarePath').value = s.FirmwarePath ?? '';
  $('formatSelect').value = FORMAT_NAMES[s.Format] ? s.Format : 'Elf';
  $('fullErase').checked = !!s.FullErase;
  $('verifyAfterFlash').checked = !!s.VerifyAfterFlash;
  $('runAfterFlash').checked = !!s.RunAfterFlash;
  $('timeoutInput').value = s.TimeoutSeconds || 60;

  applyChipList(boot.chips);
  // 芯片默认值之后再恢复用户上次填的，后者优先。
  state.lastAddress = s.Address || state.lastAddress;
  state.lastReadSize = s.ReadSize || state.lastReadSize;

  await refreshOpenOcd();
  if (!state.openocd) {
    log.system('未找到 OpenOCD。点击状态栏上的 OpenOCD 可以直接下载一个版本。');
    await warn(
      '未找到 OpenOCD',
      '没有检测到可用的 OpenOCD。\n\n可以点击状态栏上的 OpenOCD 在线下载，或手动指定一个已有的 openocd。',
    );
  }

  await checkOpenOcdUpdateIfDue();
  await checkAppUpdateIfDue();
}

// 兜住逸出的异常：弹窗告知，而不是让界面卡在半初始化的状态。
window.addEventListener('error', (event) => fail('发生未处理的异常', errorText(event.error ?? event.message)));
window.addEventListener('unhandledrejection', (event) => fail('发生未处理的异常', errorText(event.reason)));

init().catch((error) => fail('启动失败', errorText(error)));
