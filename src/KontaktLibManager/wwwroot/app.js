/* ══════════ 全局错误上报：把浏览器侧异常写进后端日志（排查「界面无反应」） ══════════ */
window.addEventListener('error', (e) => {
    try {
        if (typeof bridge !== 'undefined') {
            bridge.call('clientLog', {
                kind: 'error',
                message: String(e.message || e.error || ''),
                source: `${e.filename || ''}:${e.lineno || 0}:${e.colno || 0}`,
                stack: (e.error && e.error.stack) ? String(e.error.stack).slice(0, 1200) : '',
            });
        }
    } catch { }
});
window.addEventListener('unhandledrejection', (e) => {
    try {
        const r = e.reason;
        if (typeof bridge !== 'undefined') {
            bridge.call('clientLog', {
                kind: 'reject',
                message: String((r && r.message) || r || ''),
                source: 'promise',
                stack: (r && r.stack) ? String(r.stack).slice(0, 1200) : '',
            });
        }
    } catch { }
});

/* ══════════ 与 C# 后端的 postMessage 桥 ══════════ */
const bridge = (() => {
    const pending = new Map();
    let seq = 0;
    const listeners = [];

    const supported = typeof window.chrome !== 'undefined'
        && window.chrome.webview
        && typeof window.chrome.webview.postMessage === 'function';

    if (supported) {
        window.chrome.webview.addEventListener('message', (ev) => {
            const data = ev.data;
            // 🔴 **兼容【批量投递】（2026-09-25 性能优化）** ——
            //   后端现在会把多个消息攒成一个 JSON **数组**一次投递，
            //   以减少 WPF UI 线程 / WebView2 跨进程通信次数（这是「打字轻微卡」的最后一层）。
            //   ⇒ 前端也要能处理数组：逐条走同一套分发逻辑。
            if (Array.isArray(data)) {
                for (const m of data) handleOneMessage(m);
                return;
            }
            handleOneMessage(data);
        });
    }

    /** 处理【单条】来自后端的消息（RPC 回包 或 事件推送）。 */
    function handleOneMessage(msg) {
        if (!msg || typeof msg !== 'object') return;

        if (typeof msg.id === 'number' && pending.has(msg.id)) {
            const { resolve, reject } = pending.get(msg.id);
            pending.delete(msg.id);
            if (msg.ok) resolve(msg.result);
            else reject(new Error(msg.error || '未知错误'));
            return;
        }
        if (msg.type) listeners.forEach((fn) => fn(msg));
    }

    function call(method, args = {}) {
        if (!supported) return Promise.reject(new Error('未运行在 WebView2 宿主中'));
        const id = ++seq;
        return new Promise((resolve, reject) => {
            pending.set(id, { resolve, reject });
            window.chrome.webview.postMessage(JSON.stringify({ id, method, args }));
            setTimeout(() => {
                if (pending.has(id)) {
                    pending.delete(id);
                    reject(new Error(`调用超时：${method}`));
                }
            }, 600000);
        });
    }

    function onMessage(fn) { listeners.push(fn); }
    return { call, onMessage, supported };
})();

/* ══════════ 工具 ══════════ */
const CATEGORY_COLORS = {
    '弦乐': '#4c8dff',
    '交响管弦': '#e0655f',
    '电影配乐': '#c96a8f',
    '铜管': '#e0a13c',
    '木管': '#3fb6a8',
    '打击乐': '#c85c9e',
    '钢琴/键盘': '#7c6cf0',
    '竖琴/槌击': '#4fc3d9',
    '鼓机/电子鼓': '#b05cd9',
    '综合音源': '#8892a6',
    '吉他/贝斯': '#5cc46a',
    '合成器': '#9b6cf0',
    '氛围/音效': '#6f8fa8',
    '人声/合唱': '#e0c04c',
    '世界民族': '#c9a227',
    '其他': '#6b7280',
};

const REG_LABEL = {
    'registered': ['已入库', 'st-ok'],
    'incomplete': ['记录不完整', 'st-incomplete'],
    'pending-manager': ['待库管理器保存', 'st-pending'],
    'partial': ['部分入库', 'st-partial'],
    'path-mismatch': ['路径失效', 'st-mismatch'],
    'missing': ['未入库', 'st-missing'],
    'non-standard': ['非标准库', 'st-nonstd'],
    'unknown': ['未知', 'st-unknown'],
};

const el = (id) => document.getElementById(id);

function formatBytes(bytes) {
    if (!bytes) return '0 B';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let i = 0, v = bytes;
    while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
    return `${v.toFixed(v >= 100 || i < 2 ? 0 : 2)} ${units[i]}`;
}

function formatNumber(n) { return (n ?? 0).toLocaleString('zh-CN'); }

function escapeHtml(s) {
    return String(s ?? '').replace(/[&<>"']/g, (c) => (
        { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
    ));
}

function categoryColor(cat) { return CATEGORY_COLORS[cat] || '#6b7280'; }

let toastTimer = null;
function toast(message, kind = '') {
    const t = el('toast');
    t.textContent = message;
    t.className = 'toast' + (kind ? ' ' + kind : '');
    t.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { t.hidden = true; }, kind === 'err' ? 9000 : 5000);
}

/** 通用模态框：resolve(true/false)。showCancel=false 时仅作信息展示。 */
function showModal(title, bodyHtml, opts = {}) {
    const { okText = '确定', cancelText = '取消', showCancel = true } = opts;
    return new Promise((resolve) => {
        el('modalTitle').textContent = title;
        el('modalBody').innerHTML = bodyHtml;
        el('modalOk').textContent = okText;
        el('modalCancel').textContent = cancelText;
        el('modalCancel').hidden = !showCancel;
        el('modal').hidden = false;

        const cleanup = () => {
            el('modalOk').removeEventListener('click', onOk);
            el('modalCancel').removeEventListener('click', onCancel);
        };
        const done = (v) => { el('modal').hidden = true; cleanup(); resolve(v); };
        const onOk = () => done(true);
        const onCancel = () => done(false);

        el('modalOk').addEventListener('click', onOk);
        el('modalCancel').addEventListener('click', onCancel);
    });
}

function confirmModal(title, bodyHtml, opts = {}) {
    // **必须透传 opts** —— 之前只收 2 个参数，调用方传的按钮文案（如「确认删除」）全被忽略（实测发现）
    return showModal(title, bodyHtml, Object.assign({ okText: '确定', cancelText: '取消' }, opts));
}

/* ══════════ 状态 ══════════ */
const state = {
    favSet: new Set(),
    tagAll: [],
    filterTag: '',
    libTags: {},
    dashboard: null,
    libraries: [],
    roots: [],
    config: null,
    registration: null,
    compat: null,
    candidates: [],
    ignored: [],
    scanning: false,
    filterText: '',
    filterCat: '',
    filterReg: '',
    sortBy: 'name',
    sortDir: 1,
    tab: 'home',
    // 乐器中心
    instTreeLoaded: false,
    instItems: [],
    instTotal: 0,
    instPage: 0,
    instPageSize: 12,
    instSelected: null,
    instLibraryId: 0,
};

/* ══════════ 标签页切换（固定框架，各页自己管滚动）══════════ */
    const TAB_TITLES = { home: '概览', libraries: '音色库列表', instruments: '乐器中心', maintain: '维护', health: '健康检查', chat: '问问AI', settings: '设置', map: '音色地图' };

function switchTab(tab) {
    if (tab === 'settings') tab = 'settings';
    state.tab = tab;

    for (const name of ['home', 'libraries', 'instruments', 'chat', 'maintain', 'health', 'settings', 'map']) {
        const page = el('tab-' + name);
        if (page) page.hidden = name !== tab;
    }
    document.querySelectorAll('.tab').forEach((b) =>
        b.classList.toggle('active', b.dataset.tab === tab));

    el('btnBack').hidden = tab !== 'settings';
    el('btnSettings').hidden = tab === 'settings';
    el('pageTitle').textContent = TAB_TITLES[tab] || 'Kontakt 音色库管理器';

    if (tab === 'health') { loadHealth(); loadJunk(); }
    if (tab === 'chat') { if (chatState.manuals === null) loadChatWorkspace(); loadChatSessions(); }

    if (tab === 'instruments') {
        if (!state.instTreeLoaded) loadInstrumentTree();
        else setTimeout(() => { if (computeInstPageSize() !== state.instPageSize) loadInstruments(); }, 30);
    }
    if (tab === 'maintain') loadMaintain();
    if (tab === 'map') mapOnEnter();
    if (tab === 'settings') loadSettings();
    if (window.__uiLang) { try { translateDom(window.__uiLang); } catch { } }   // 切页后重新翻译
}



function renderDashboard(d) {
    loadSuggestions(false);   // 主动建议（体检）
    const hasData = d && d.hasData;
    el('emptyHome').hidden = hasData;
    el('homeContent').hidden = !hasData;
    if (!hasData) {
        el('compatBanner').hidden = true;
        return;
    }

    el('statLibs').textContent = formatNumber(d.libraryCount);
    el('statRoots').textContent = `${d.rootCount || 0} 个路径`;
    el('statSize').textContent = formatBytes(d.totalBytes);
    el('statFiles').textContent = formatNumber(d.totalFiles);
    el('statInst').textContent = formatNumber(d.instrumentCount);
    el('statInstHint').textContent = `${formatNumber(d.instrumentNamedFromHeader)} 个读取自文件头`;
    el('statJunk').textContent = formatNumber(d.junkCount);
    el('statJunkHint').textContent = formatBytes(d.junkBytes);

    const meta = [];
    if (d.lastScannedAt) meta.push(`上次扫描 ${d.lastScannedAt}`);
    if (d.lastScanSeconds) meta.push(`耗时 ${d.lastScanSeconds}s`);
    if (d.librariesWithoutNicnt) meta.push(`${d.librariesWithoutNicnt} 个库无 nicnt`);
    if (d.legacyInstrumentCount) meta.push(`${d.legacyInstrumentCount} 个旧格式 NKI`);
    el('scanMeta').textContent = meta.join(' · ');

    renderCategoryBars(d.categories);
    renderCategoryFilter(d.categories);
    renderTopLibraries();

    // 兼容性横幅
    const c = d.compat || {};
    const banner = el('compatBanner');
    if (!c.kontaktVersion) {
        banner.hidden = false;
        banner.className = 'banner';
        banner.innerHTML = `尚未设置 Kontakt 主程序路径，无法做版本兼容检查。` +
            `<div class="banner-actions"><button class="btn small" id="bannerGoSettings">前往设置</button></div>`;
        el('bannerGoSettings')?.addEventListener('click', () => switchTab('settings'));
    } else if (c.tooOld > 0) {
        banner.hidden = false;
        banner.className = 'banner bad';
        banner.innerHTML = `⚠ 检测到 <b>${c.tooOld}</b> 个音色库要求的 Kontakt 版本高于当前设定版本 <b>${escapeHtml(c.kontaktVersion)}</b>，` +
            `这些库在当前版本下<b>无法加载</b>。请改用更高版本 Kontakt，或查看设置中的兼容列表。` +
            `<div class="banner-actions"><button class="btn small" id="bannerGoCompat">查看详情</button></div>`;
        el('bannerGoCompat')?.addEventListener('click', async () => {
            switchTab('settings');
            await loadCompat();
            el('compatBox')?.scrollIntoView({ behavior: 'smooth', block: 'center' });
        });
    } else {
        banner.hidden = false;
        banner.className = 'banner ok';
        banner.innerHTML = `✓ 当前 Kontakt 版本 <b>${escapeHtml(c.kontaktVersion)}</b> 可加载全部已索引音色库。`;
    }
}

/** 入库统计（迁移到概览页的指标条） */
function renderHomeStats(rep) {
    if (!rep) return;
    const set = (id, value, cls) => {
        const node = el(id);
        if (!node) return;
        node.textContent = formatNumber(value);
        const metric = node.closest('.metric');
        if (metric) {
            metric.classList.remove('ok', 'warn', 'bad', 'dim');
            if (cls) metric.classList.add(cls);
        }
    };

    // 报告是扁平结构：{ total, registered, incomplete, missing, pathMismatch, nonStandard, partial }
    const n = (v) => Number(v ?? 0);
    set('statLibs', n(rep.total), '');
    set('statRegistered', n(rep.registered), n(rep.registered) > 0 ? 'ok' : 'dim');
    set('statIncomplete', n(rep.incomplete), n(rep.incomplete) > 0 ? 'warn' : 'dim');
    set('statMissing', n(rep.missing), n(rep.missing) > 0 ? 'bad' : 'dim');
    set('statMismatch', n(rep.pathMismatch) + n(rep.partial), n(rep.pathMismatch) > 0 ? 'warn' : 'dim');
    set('statNonstd', n(rep.nonStandard), 'dim');

    // 卡片可点击 → 跳到列表并预设筛选
    const goto = (reg) => () => {
        state.filterReg = reg;
        state.filterText = '';
        state.filterCat = '';
        el('filterReg').value = reg;
        el('filterText').value = '';
        switchTab('libraries');
        renderTable();
    };
    el('statRegistered')?.closest('.metric')?.addEventListener('click', goto('registered'));
    el('statIncomplete')?.closest('.metric')?.addEventListener('click', goto('incomplete'));
    el('statMissing')?.closest('.metric')?.addEventListener('click', goto('missing'));
    el('statMismatch')?.closest('.metric')?.addEventListener('click', goto('path-mismatch'));
    el('statNonstd')?.closest('.metric')?.addEventListener('click', goto('non-standard'));
}

/** 占用 Top 10（点击跳到列表并定位） */
function renderTopLibraries() {
    const box = el('topLibs');
    if (!box) return;
    const top = [...state.libraries].sort((a, b) => b.sizeBytes - a.sizeBytes).slice(0, 16);
    if (!top.length) { box.innerHTML = '<div class="hint-row">暂无数据</div>'; return; }

    box.innerHTML = top.map((l, i) => `
        <div class="top-row" data-lib="${l.id}">
            <span class="rank">${i + 1}</span>
            <span class="tname" title="${escapeHtml(l.name)}">${escapeHtml(l.name)}</span>
            <span class="tsize">${formatBytes(l.sizeBytes)}</span>
        </div>`).join('');

    box.querySelectorAll('[data-lib]').forEach((row) => row.addEventListener('click', () => {
        const lib = state.libraries.find((l) => l.id === Number(row.dataset.lib));
        if (!lib) return;
        state.filterText = lib.name;
        state.filterCat = '';
        state.filterReg = '';
        el('filterText').value = lib.name;
        el('filterCat').value = '';
        el('filterReg').value = '';
        switchTab('libraries');
        renderTable();
    }));
}

function renderCategoryBars(categories) {
    // 全部类别都成行显示（已按体积降序）；0 值类别显示「0 库」并弱化配色
    const max = Math.max(1, ...categories.map((c) => c.sizeBytes));
    el('catBars').innerHTML = categories.map((c) => `
        <div class="bar-row">
            <span class="bar-name" title="${escapeHtml(c.category)}">${escapeHtml(c.category)}</span>
            <div class="bar-track">
                <div class="bar-fill" style="width:${((c.sizeBytes / max) * 100).toFixed(2)}%;background:${categoryColor(c.category)}"></div>
            </div>
            <span class="bar-val">${c.sizeBytes === 0 ? '0 库' : `${c.libraryCount} 库 · ${formatBytes(c.sizeBytes)}`}</span>
        </div>`).join('');
}

function renderCategoryFilter(categories) {
    const sel = el('filterCat');
    const cur = sel.value;
    sel.innerHTML = '<option value="">全部分类</option>' +
        categories.map((c) => `<option value="${escapeHtml(c.category)}">${escapeHtml(c.category)}</option>`).join('');
    sel.value = cur;
}

function visibleLibraries() {
    const text = state.filterText.trim().toLowerCase();
    let rows = state.libraries.filter((l) => {
        if (state.filterCat && l.category !== state.filterCat) return false;
        if (state.filterReg && l.regStatus !== state.filterReg) return false;
        // 自定义标签筛选
        if (state.filterTag && !tagsOfLib(l.id).some((t) => t === state.filterTag)) return false;
        if (!text) return true;
        return l.name.toLowerCase().includes(text) || (l.category || '').toLowerCase().includes(text);
    });

    const verKey = (v) => {
        const p = String(v || '').split('.').map((x) => parseInt(x, 10) || 0);
        return p[0] * 1e6 + (p[1] || 0) * 1e4 + (p[2] || 0) * 100 + (p[3] || 0);
    };
    const cmp = {
        name: (a, b) => a.name.localeCompare(b.name, 'zh-CN'),
        category: (a, b) => (a.category || '').localeCompare(b.category || '', 'zh-CN'),
        size: (a, b) => a.sizeBytes - b.sizeBytes,
        nki: (a, b) => a.nkiCount - b.nkiCount,
        junk: (a, b) => a.junkCount - b.junkCount,
        manuals: (a, b) => (a.manualCount || 0) - (b.manualCount || 0),
        required: (a, b) => verKey(a.requiredKontakt) - verKey(b.requiredKontakt),
    }[state.sortBy] || ((a, b) => 0);

    rows.sort((a, b) => cmp(a, b) * state.sortDir);
    return rows;
}

/** 标签编辑器：编辑某个实体（默认音色库）的标签 */
async function editTags(entityType, entityId, entityName) {
    let cur = [];
    try {
        const r = await bridge.call('tagsOf', { entityType, entityId });
        cur = r.tags || [];
    } catch (e) { toast('读取标签失败：' + e.message, 'err'); return; }

    let all = [];
    try { const t = await bridge.call('tags', {}); all = (t.tags || []).map((x) => x.name); } catch { }

    // 用现有 modal 承载（showModal 的按钮处理器是它自己绑的，能正常关闭）
    const p = showModal('编辑标签', `
        <div class="hint-row" style="padding:0 0 8px">
            给「<b>${escapeHtml(entityName || '')}</b>」加标签。多个标签用<b>逗号</b>分隔，留空即清空。<br>
            <span style="color:#8b93a3">标签用于跨库筛选与整理；厂商分类与本标签互不干扰。</span>
        </div>
        <input class="tag-input" id="tagEditInput" type="text" value="${escapeHtml(cur.join(', '))}"
               placeholder="例如：常用, 弦乐, 待整理" />
        <div class="hint-row" style="padding:10px 0 0">已有标签（点击追加）：</div>
        <div class="tag-suggest" id="tagSuggest">
            ${all.length ? all.map((t) => `<span class="tag-chip" data-tag="${escapeHtml(t)}">${escapeHtml(t)}</span>`).join('') : '<span style="color:#8b93a3">还没有任何标签</span>'}
        </div>`, { okText: '保存', cancelText: '取消' });

    // 点击建议标签 → 追加到输入框
    const sug = el('tagSuggest');
    if (sug) sug.querySelectorAll('[data-tag]').forEach((c) => c.addEventListener('click', () => {
        const inp = el('tagEditInput');
        const parts = inp.value.split(',').map((x) => x.trim()).filter(Boolean);
        const t = c.dataset.tag;
        if (!parts.some((x) => x.toLowerCase() === t.toLowerCase())) parts.push(t);
        inp.value = parts.join(', ');
    }));

    const ok = await p;
    if (!ok) return;

    const names = (el('tagEditInput').value || '').split(',').map((x) => x.trim()).filter(Boolean);
    try {
        const r = await bridge.call('tagsSet', { entityType, entityId, tags: names });
        if (!r.ok) { toast(r.message || '保存失败', 'err'); return; }
        toast(names.length ? `已保存 ${names.length} 个标签` : '已清空标签', 'ok');
        await loadTags();
        renderTable();
    } catch (e) { toast('保存失败：' + e.message, 'err'); }
}

/** 加载标签（含各库的标签映射） */
async function loadTags() {
    try {
        const r = await bridge.call('tags', {});
        state.tagAll = r.tags || [];
        state.libTags = r.libraryTags || {};
        // 更新标签筛选下拉
        const sel = el('libTagFilter');
        if (sel) {
            const cur = sel.value;
            sel.innerHTML = '<option value="">全部标签</option>' +
                state.tagAll.map((t) => `<option value="${escapeHtml(t.name)}">${escapeHtml(t.name)}（${t.count}）</option>`).join('');
            sel.value = cur;
        }
    } catch { state.tagAll = []; state.libTags = {}; }
}

/** 取某库的标签（用于列表渲染） */
function tagsOfLib(libId) {
    return (state.libTags && state.libTags[String(libId)]) || (state.libTags && state.libTags[libId]) || [];
}

/** 标签管理区（设置页 ⑥）：列出全部标签，支持重命名与删除 */
/** 标签管理区（设置页 ⑥）：卡片网格，列出全部标签，支持重命名与删除 */
async function renderTagManager() {
    const box = el('tagManagerBox');
    if (!box) return;
    box.innerHTML = '<div class="hint-row">正在读取标签…</div>';
    let data;
    try { data = await bridge.call('tags', {}); }
    catch (e) { box.innerHTML = '<div class="hint-row">读取失败：' + escapeHtml(e.message) + '</div>'; return; }

    const tags = data.tags || [];
    if (!tags.length) {
        box.innerHTML = '<div class="hint-row">还没有任何标签。<br>到「音色库列表」里点某个库的「🏷」即可创建。</div>';
        return;
    }

    box.innerHTML = `
        <div class="tag-mgr-head">
            <span>共 <b>${tags.length}</b> 个标签</span>
            <span style="color:#8b93a3">重命名会同步更新所有已关联的音色库</span>
        </div>
        <div class="tag-mgr-list">
            ${tags.map((t) => `
                <div class="tag-mgr-row" data-tagrow="${escapeHtml(t.name)}">
                    <div class="tag-mgr-top">
                        <input class="tag-mgr-input" type="text" value="${escapeHtml(t.name)}" title="${escapeHtml(t.name)}" />
                        <span class="tag-mgr-cnt">${t.count} 个库</span>
                    </div>
                    <div class="tag-mgr-acts">
                        <button class="btn small" data-tagrename="${escapeHtml(t.name)}">重命名</button>
                        <button class="btn small danger" data-tagdel="${escapeHtml(t.name)}">删除</button>
                    </div>
                </div>`).join('')}
        </div>`;

    box.querySelectorAll('[data-tagrename]').forEach((b) => b.addEventListener('click', async () => {
        const oldName = b.dataset.tagrename;
        const row = box.querySelector(`[data-tagrow="${CSS.escape(oldName)}"]`);
        const inp = row ? row.querySelector('input') : null;
        const newName = inp ? inp.value.trim() : '';
        if (!newName) { toast('新名称不能为空', 'err'); return; }
        if (newName === oldName) { toast('名称没有变化', 'ok'); return; }
        try {
            const r = await bridge.call('tagRename', { from: oldName, to: newName });
            toast(r.message || (r.ok ? '已重命名' : '重命名失败'), r.ok ? 'ok' : 'err');
        } catch (e) { toast('重命名失败：' + e.message, 'err'); }
        await renderTagManager();
        await loadTags();
    }));

    box.querySelectorAll('[data-tagdel]').forEach((b) => b.addEventListener('click', async () => {
        const name = b.dataset.tagdel;
        const yes = await confirmModal('删除标签',
            `删除标签「<b>${escapeHtml(name)}</b>」？<br><br>` +
            `只会解除它与音色库的关联，<b>不会动任何音色文件</b>。`,
            { okText: '删除', cancelText: '取消' });
        if (!yes) return;
        try {
            const r = await bridge.call('tagDelete', { name });
            toast(r.message || (r.ok ? '已删除' : '删除失败'), r.ok ? 'ok' : 'err');
        } catch (e) { toast('删除失败：' + e.message, 'err'); }
        await renderTagManager();
        await loadTags();
        renderTable();
    }));
}


function renderTable() {
    const rows = visibleLibraries();
    const body = el('libBody');

    if (!rows.length) {
        body.innerHTML = `<tr><td colspan="8" class="empty-row">没有匹配的音色库</td></tr>`;
    } else {
        body.innerHTML = rows.map((l) => {
            const [label, cls] = REG_LABEL[l.regStatus] || REG_LABEL['unknown'];
            const ver = l.requiredKontakt || '—';
            const verCls = l.compatStatus === 'ok' ? 'ver-ok' : (l.compatStatus === 'too-old' ? 'ver-bad' : 'ver-unk');
            const verTip = l.compatStatus === 'too-old' ? '需要更高版本的 Kontakt' : (l.compatStatus === 'ok' ? '与当前 Kontakt 兼容' : '未知');
            const cover = l.coverFile
                ? `<img class="lib-cover" data-cover="${l.id}" src="https://kontakt-covers/${encodeURIComponent(l.coverFile)}" alt="" loading="lazy" title="${escapeHtml(l.name)}" />`
                : `<div class="lib-cover placeholder" data-cover="${l.id}" title="点击添加封面">+ 添加封面</div>`;
            const manuals = l.manualCount > 0
                ? `<button class="btn small" data-manuals="${l.id}" title="查看该库自带的说明书">📖 ${l.manualCount}</button>`
                : '–';
            return `<tr>
                <td>
                    <div class="lib-cell">
                        ${cover}
                        <div class="lib-meta">
                            <div class="lib-name" title="${escapeHtml(l.name)}">${escapeHtml(l.name)}</div>
                            <div class="lib-path" title="${escapeHtml(l.path)}">${escapeHtml(l.path)}</div>

                        </div>
                    </div>
                </td>
                <td class="lib-tags-cell">${tagsOfLib(l.id).map((t) => `<span class="tag-chip mini">${escapeHtml(t)}</span>`).join("")}<span class="tag-chip mini add" data-edittags="${l.id}" title="编辑标签">🏷</span></td>
                <td><span class="tag" style="border-color:${categoryColor(l.category)}55;color:${categoryColor(l.category)}">${escapeHtml(l.category)}</span></td>
                <td class="num">${formatBytes(l.sizeBytes)}</td>
                <td class="num">${formatNumber(l.nkiCount)}</td>
                <td><span class="${verCls}" title="${verTip}">${escapeHtml(ver)}</span></td>
                <td><span class="st ${cls}" title="${escapeHtml(l.regContentDir || '')}">${label}</span></td>
                <td class="num">${manuals}</td>
                <td class="num">${l.junkCount ? `<span class="junk-has">${l.junkCount}</span>` : '–'}</td>
                <td class="row-actions">
                    <button class="btn small" data-ai="${l.id}" title="基于说明书的知识库问答">🤖 问问AI</button>
                    <button class="btn small" data-move="${l.id}" title="把整个音色库移动到其他路径（自动修复入库信息）">移动</button>
                    <button class="btn small" data-open="${escapeHtml(l.path)}">打开</button>
                    <button class="btn small" data-test="${l.id}" title="调用 Kontakt 打开库内首个 NKI">测试</button>
                </td>
            </tr>`;
        }).join('');
    }

    const totalSize = rows.reduce((s, l) => s + l.sizeBytes, 0);
    const totalNki = rows.reduce((s, l) => s + l.nkiCount, 0);
    el('tableFoot').textContent =
        `显示 ${rows.length} / ${state.libraries.length} 个库 · 合计 ${formatBytes(totalSize)} · ${formatNumber(totalNki)} 个 NKI`;

    // 标签：编辑按钮
    body.querySelectorAll('[data-edittags]').forEach((b) => b.addEventListener('click', (ev) => {
        ev.stopPropagation();
        const lib = rows.find((x) => x.id === Number(b.dataset.edittags));
        editTags('library', Number(b.dataset.edittags), lib ? lib.name : '');
    }));
    body.querySelectorAll('[data-open]').forEach((b) =>
        b.addEventListener('click', () => doOpenPath(b.dataset.open)));
    body.querySelectorAll('[data-test]').forEach((b) =>
        b.addEventListener('click', () => doTestLibrary(Number(b.dataset.test))));
    body.querySelectorAll('[data-manuals]').forEach((b) =>
        b.addEventListener('click', () => showManuals(Number(b.dataset.manuals))));
    body.querySelectorAll('[data-ai]').forEach((b) =>
        b.addEventListener('click', () => openChatTab(Number(b.dataset.ai))));
    body.querySelectorAll('[data-cover]').forEach((b) =>
        b.addEventListener('click', () => openCoverEditor(Number(b.dataset.cover))));
    body.querySelectorAll('[data-move]').forEach((b) =>
        b.addEventListener('click', () => startMove(Number(b.dataset.move))));
}

/* ══════════ 移动音色库 ══════════ */
const moveState = { libraryId: 0, plan: null };

async function startMove(libraryId) {
    const lib = state.libraries.find((l) => l.id === libraryId);
    if (!lib) return;

    const pick = await bridge.call('pickFolder', { initial: '' });
    if (!pick.picked) return;

    let plan;
    try { plan = await bridge.call('movePlan', { libraryId, destRoot: pick.path }); }
    catch (e) { toast('生成移动计划失败：' + e.message, 'err'); return; }

    moveState.libraryId = libraryId;
    moveState.plan = plan;

    const errs = (plan.errors || []).map((e) => `<div class="move-err">⛔ ${escapeHtml(e)}</div>`).join('');
    const warns = (plan.warnings || []).map((w) => `<div class="move-warn">⚠ ${escapeHtml(w)}</div>`).join('');
    const space = plan.freeSpaceBytes
        ? `${formatBytes(plan.freeSpaceBytes)} 可用`
        : '未知';

    const body = `
        <div class="move-grid">
            <div class="move-row"><span class="k">音色库</span><span class="v">${escapeHtml(plan.libraryName)}</span></div>
            <div class="move-row"><span class="k">源路径</span><span class="v mono">${escapeHtml(plan.sourcePath)}</span></div>
            <div class="move-row"><span class="k">目标路径</span><span class="v mono">${escapeHtml(plan.destPath)}</span></div>
            <div class="move-row"><span class="k">规模</span><span class="v">${formatBytes(plan.sizeBytes)} · ${formatNumber(plan.fileCount)} 个文件 · ${formatNumber(plan.nkiCount)} 个 NKI</span></div>
            <div class="move-row"><span class="k">目标磁盘</span><span class="v">${space}</span></div>
            <div class="move-row"><span class="k">待改入库</span><span class="v">${(plan.productKeys || []).length} 个产品${plan.isAdmin ? '' : '（非管理员，将跳过）'}</span></div>
        </div>
        ${errs}${warns}
        ${plan.canExecute
            ? `<div class="move-note">流程：<b>拷贝 → 校验（文件数与字节完全一致）→ 改写入库路径 → 更新索引</b>。
               校验通过前<b>不会删除</b>源目录；删除源目录需要你在完成后单独确认。</div>`
            : ''}`;

    const ok = await showModal('移动音色库', body, {
        okText: plan.canExecute ? '开始移动' : '关闭',
        showCancel: plan.canExecute,
    });
    if (!ok || !plan.canExecute) return;

    el('progressWrap').hidden = false;
    el('progressFill').style.transform = 'scaleX(0)';
    el('progressText').textContent = '正在启动移动…';

    try {
        const res = await bridge.call('moveExecute', { libraryId, destRoot: pick.path });
        if (!res.started) {
            toast('无法开始移动：' + (res.reason || '未知原因'), 'err');
            el('progressWrap').hidden = true;
        }
    } catch (e) {
        toast('启动移动失败：' + e.message, 'err');
        el('progressWrap').hidden = true;
    }
}

async function showMoveResult(msg) {
    const details = (msg.details || []).map((d) => `<li>${escapeHtml(d)}</li>`).join('');
    const body = `
        <div class="move-note ${msg.ok ? 'ok' : 'err'}">${escapeHtml(msg.message)}</div>
        <div class="move-row"><span class="k">新路径</span><span class="v mono">${escapeHtml(msg.newPath || '')}</span></div>
        ${details ? `<ul class="move-details">${details}</ul>` : ''}
        ${msg.ok
            ? `<div class="move-warn">源目录仍在：<span class="mono">${escapeHtml(msg.sourcePath || '')}</span><br>
               确认一切正常后可以删除它（工具会再次校验目标完整性）。</div>`
            : ''}`;

    const ok = await showModal(msg.ok ? '移动完成' : '移动未完成', body, {
        okText: msg.ok ? '删除源目录' : '关闭',
        cancelText: '稍后手动处理',
        showCancel: msg.ok,
    });

    if (ok && msg.ok) {
        const confirm = await showModal('删除源目录',
            `将<b>永久删除</b>：<br><span class="mono">${escapeHtml(msg.sourcePath || '')}</span><br><br>
             工具会先再校验一次目标完整性，不一致则拒绝删除。`,
            { okText: '确认删除', cancelText: '取消' });
        if (!confirm) return;
        try {
            const res = await bridge.call('moveDeleteSource', {
                libraryId: moveState.libraryId,
                destRoot: msg.destPath,
                sourcePath: msg.sourcePath,
                destPath: msg.destPath,
            });
            toast(res.message, res.ok ? 'ok' : 'err');
        } catch (e) { toast('删除失败：' + e.message, 'err'); }
        await refresh();
    } else if (msg.ok) {
        await refresh();
    }
}

/* ══════════ ⑤ AI 助手 ══════════ */
/** 应用界面语言：目前把语言设置存下来，供后续文案替换使用 */
/* ══════════════════════════════════════════════════════════
   界面语言（i18n）
   ----------------------------------------------------------
   做法：**按文本节点查词典替换**，而不是给每个元素加 data-i18n。
   理由：本项目 HTML 有 240+ 条中文文案，逐条加属性既慢又容易漏；
   文本节点方案一次覆盖按钮/标题/标签/表头/下拉选项。

   可逆：首次替换前把原文存进节点自身（_i18nZh），切回中文时还原。
   安全：跳过 script/style/pre/code、聊天消息区、日志区，以及任何带
         data-i18n-skip 的元素 —— 避免动到动态内容与用户数据。
   ══════════════════════════════════════════════════════════ */
const I18N_EN = {
    // ── 顶部导航 ──
    '概览': 'Overview', '音色库列表': 'Libraries', '乐器中心': 'Instruments',
    '问问AI': 'Ask AI', '问问 AI': 'Ask AI', '健康检查': 'Health', '维护': 'Maintenance',
    '⚙ 设置': '⚙ Settings', '← 返回': '← Back',

    // ── 通用按钮 ──
    '保存': 'Save', '取消': 'Cancel', '确定': 'OK', '关闭': 'Close', '删除': 'Delete',
    '刷新': 'Refresh', '重置': 'Reset', '重命名': 'Rename', '说明': 'Notes',
    '导出 CSV': 'Export CSV', '导出 Markdown': 'Export Markdown', '导出库清单': 'Export Library List',
    '开始扫描': 'Start Scan', '刷新状态': 'Refresh Status', '重新检查': 'Recheck',
    '重新检测': 'Redetect', '测试连接': 'Test Connection', '测试启动': 'Test Launch',
    '清除当前': 'Clear Current', '创建快照': 'Create Snapshot', '删除会话': 'Delete Session',
    '清空对话': 'Clear Chat', '删除缓存': 'Delete Cache', '建立知识库': 'Build KB',
    '批量建立': 'Build All', '批量收尾': 'Batch Finalize', '检查库关系': 'Check Relations',
    '扫描重复大文件': 'Scan Large Duplicates', '一键清理杂质': 'Clean Junk',
    '补全封面': 'Fill Covers', '全部送回收站': 'All to Recycle Bin', '永久删除': 'Delete Permanently',
    '拒绝': 'Deny', '允许执行': 'Allow', '发送': 'Send', '+ 添加路径': '+ Add Path',
    '+ 手动添加…': '+ Add Manually…', '+ 跨库会话': '+ Cross-library Chat',
    '一键入库（未入库）': 'Register All (Pending)', '选择图片…': 'Choose Image…',
    '保存封面': 'Save Cover', '重置': 'Reset', '编辑路径': 'Edit Path', '移除': 'Remove',
    '打开': 'Open', '移动': 'Move', '测试': 'Test', '查看': 'View', '加入 Quick-Load': 'Add to Quick-Load',

    // ── 设置页各区块 ──
    '⓪ 语言 / Language': '⓪ Language', '① 音色库路径': '① Library Paths',
    '② Kontakt 主程序': '② Kontakt Executable', '③ 入库管理': '③ Registration',
    '④ 版本兼容检查': '④ Version Compatibility', '⑤ AI 助手（说明书问答）': '⑤ AI Assistant (Manual Q&A)',
    '⑥ 标签管理': '⑥ Tag Manager',
    '界面语言 / UI': 'UI Language', 'AI 思考语言 / Thinking': 'AI Thinking Language',
    'AI 回复语言 / Reply': 'AI Reply Language',
    '中文': 'Chinese', 'English': 'English',
    '自动（不干预）': 'Auto (no override)', '自动（跟随提问）': 'Auto (follow question)',
    '强制中文': 'Force Chinese', '强制英文': 'Force English',
    '界面语言': 'UI Language', 'AI 思考语言': 'AI Thinking Language', 'AI 回复语言': 'AI Reply Language',

    // ── AI 设置 ──
    'Base URL': 'Base URL', '模型': 'Model', '温度': 'Temperature',
    '最大输出 tokens': 'Max Output Tokens', '上下文长度': 'Context Length',
    '压缩阈值 %': 'Compress Threshold %', '思考强度': 'Thinking Effort',
    '该模型具备多模态（视觉）能力': 'Model supports multimodal (vision)',
    '允许 Agent 联网搜索（默认关闭）': 'Allow Agent web search (off by default)',
    '允许 Agent 操作外部程序界面（点击/输入，每次都会确认）': 'Allow Agent to control external app UI (click/type, confirmed each time)',
    '允许 Agent 执行命令 / 代码（只读白名单直通，风险操作逐条确认）': 'Allow Agent to run commands / code (read-only allowlist, risky ops confirmed)',

    // ── 概览页 ──
    '音色库总数': 'Total Libraries', '已入库': 'Registered', '记录不完整': 'Incomplete Records',
    '未入库': 'Not Registered', '路径失效/部分': 'Path Invalid / Partial', '非标准库': 'Non-standard',
    '总占用': 'Total Size', '文件数': 'Files', '乐器预设': 'Instrument Presets',
    '杂质文件': 'Junk Files', '分类占比': 'Category Breakdown', '占用 Top 16': 'Top 16 by Size',
    '点击查看该库': 'Click to view library',

    // ── 库列表 ──
    '音色库': 'Library', '标签': 'Tags', '分类': 'Category', '占用': 'Size', 'NKI': 'NKI',
    '版本要求': 'Required', '入库状态': 'Status', '说明书': 'Manuals', '杂质': 'Junk',
    '按名称': 'By Name', '按路径': 'By Path', '按库': 'By Library', '按大小': 'By Size',
    '按乐器数': 'By Instrument Count', '按版本要求': 'By Required Version',
    '按说明书数': 'By Manual Count', '按杂质数': 'By Junk Count', '按占用': 'By Size',
    '全部分类': 'All Categories', '全部入库状态': 'All Statuses', '全部标签': 'All Tags',
    '全部类型': 'All Types', '全部演奏法': 'All Articulations', '全部音色库': 'All Libraries',
    '★ 我的收藏': '★ My Favorites', '★ 我的收藏': '★ My Favorites',
    '非标准库': 'Non-standard', '待库管理器保存': 'Pending Library Manager',
    '缺 Service Center 记录': 'Missing Service Center record', '部分入库': 'Partial',
    '路径失效': 'Path Invalid', '默认': 'Default', '已启用': 'Enabled',

    // ── 乐器中心 ──
    '分类 / 音色库': 'Category / Library', '详情': 'Details', '名称': 'Name', '类型': 'Type',
    '演奏法': 'Articulation', '大小': 'Size', '引擎版本': 'Engine Version',
    '名称来源': 'Name Source', '相对路径': 'Relative Path', '试听': 'Preview',
    '随机抓几个采样': 'Random samples', '从左侧选择一个乐器查看详情': 'Select an instrument on the left',
    '输入关键词开始搜索': 'Type to search', '所属音色库': 'Library',
    '基础音色': 'Sustained', '循环乐句': 'Loops', '短音': 'Short', '多轨合奏': 'Multi',
    '效果/氛围': 'FX / Ambient', '长音': 'Long', '打击/单音': 'Percussion / One-shot',
    '连奏': 'Legato', '颤音/震音': 'Tremolo', '拨弦/弹拨': 'Pizzicato / Plucked',
    '键位切换': 'Key Switch', 'NKI 乐器': 'NKI Instrument', 'NKM 多轨': 'NKM Multi',

    // ── 问问AI ──
    '会话': 'Sessions', '说明书': 'Manuals', '知识库': 'Knowledge Base', '记忆': 'Memory',
    '跨库会话': 'Cross-library Chat', '未选择会话': 'No session selected',
    '问问 AI': 'Ask AI', '发送': 'Send', '思考过程': 'Thinking',
    '正在读取说明书…': 'Loading manuals…', '正在读取知识库…': 'Loading knowledge base…',
    '正在读取记忆…': 'Loading memory…', '正在读取会话…': 'Loading sessions…',
    '正在读取标签…': 'Loading tags…',

    // ── 健康检查 / 维护 ──
    '杂质清理': 'Junk Cleanup', '重复内容检测': 'Duplicate Detection',
    '重复大文件': 'Large Duplicates', '库关系': 'Library Relations',
    '入库快照与回滚': 'Registration Snapshots & Rollback', '自动备份': 'Auto Backup',
    '送回收站可撤销': 'Sent to Recycle Bin (undoable)', '尚未建立索引': 'No index yet',
    '点上方按钮开始检测': 'Click the button above to start',

    // ── 确认条 ──
    '需要你确认': 'Confirmation needed',
    '⚠ 需要你确认才能执行': '⚠ Confirmation required',
    '⚠ 需要你确认才能运行这段脚本': '⚠ Confirmation required to run this script',
    '⚠ 需要你确认才能操作界面': '⚠ Confirmation required to control the UI',
    '⚠ 需要你确认才能执行这条命令': '⚠ Confirmation required to run this command',
    '收起（等于拒绝）': 'Collapse (= deny)',

    // ── 标签管理 ──
    '个库': 'libraries', '重命名会同步更新所有已关联的音色库': 'Renaming updates all linked libraries',

    
// ── 整段翻译（data-i18n-html）──
    
pathsHint: 'Add multiple library root folders (across drives). Scanning walks every <b>enabled</b> path and refreshes the index.',
langHint: '<b>UI Language</b> switches the interface text. <b>AI Thinking Language</b> forces the model to reason in the chosen language. <b>AI Reply Language</b> forces the final answer to use it (proper nouns keep their original form). The three are independent.',
    
kontaktHint: 'Once set, the tool reads its version for compatibility checks and can launch Kontakt to test a library.<br>Pick the main <b>Kontakt N.exe</b> (e.g. <code>D:\\...\\Kontakt 8\\x64\\Kontakt 8.exe</code>), <b>not</b> the installer (like <code>KontaktPortable_v871.exe</code>).',
    
compatHint: 'Reads the Kontakt engine version recorded in each library&apos;s NKI headers and compares it with the current Kontakt, flagging libraries that cannot load.',
};

/** 需要跳过的容器（动态内容 / 用户数据 / 代码） */
const I18N_SKIP_SELECTOR = [
    'script', 'style', 'pre', 'code', 'textarea',
    // 说明：**不跳过列表区** —— 词典只替换精确匹配，
    // 库名/路径/乐器名等用户数据不会命中任何 key，天然安全；
    // 反而能顺带翻译列表里动态生成的按钮（打开/移动/测试…）。
    '.diff-list', '.ac-body', '.think-body', '.chat-msgs', '#logBox',
].join(',');

/** 把整棵 DOM 的文本节点按词典替换（可逆） */
function translateDom(lang) {
    const toEn = lang === 'en';
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
        acceptNode(n) {
            const t = n.nodeValue;
            if (!t || !t.trim()) return NodeFilter.FILTER_REJECT;
            const p = n.parentElement;
            if (!p) return NodeFilter.FILTER_REJECT;
            if (p.closest(I18N_SKIP_SELECTOR)) return NodeFilter.FILTER_REJECT;
            if (p.closest('[data-i18n-skip]')) return NodeFilter.FILTER_REJECT;
            return NodeFilter.FILTER_ACCEPT;
        },
    });
    const nodes = [];
    while (walker.nextNode()) nodes.push(walker.currentNode);

    for (const n of nodes) {
        const raw = n.nodeValue;
        const key = raw.trim();
        if (toEn) {
            const hit = I18N_EN[key];
            if (!hit) continue;
            if (n._i18nZh === undefined) n._i18nZh = raw;   // 记住原文
            n.nodeValue = raw.replace(key, hit);
        } else if (n._i18nZh !== undefined) {
            n.nodeValue = n._i18nZh;                        // 还原
            delete n._i18nZh;
        }
    }

    // 属性：placeholder / title
    document.querySelectorAll('[placeholder],[title]').forEach((elx) => {
        if (elx.closest(I18N_SKIP_SELECTOR)) return;
        for (const attr of ['placeholder', 'title']) {
            const v = elx.getAttribute(attr);
            if (!v) continue;
            if (toEn) {
                const hit = I18N_EN[v.trim()];
                if (hit) {
                    if (!elx.dataset['i18n' + attr]) elx.dataset['i18n' + attr] = v;
                    elx.setAttribute(attr, hit);
                }
            } else {
                const zh = elx.dataset['i18n' + attr];
                if (zh) { elx.setAttribute(attr, zh); delete elx.dataset['i18n' + attr]; }
            }
        }
    });

    // 整段翻译：给带 data-i18n-html 的元素整体替换 innerHTML
    // （用于「说明文字里夹着 <b>/<code>」的段落 —— 逐文本节点替换会中英混杂）
    document.querySelectorAll('[data-i18n-html]').forEach((elx) => {
        const key = elx.dataset.i18nHtml;
        if (toEn) {
            const hit = I18N_EN[key];
            if (!hit) return;
            if (elx.dataset.i18nZhHtml === undefined) elx.dataset.i18nZhHtml = elx.innerHTML;
            elx.innerHTML = hit;
        } else if (elx.dataset.i18nZhHtml !== undefined) {
            elx.innerHTML = elx.dataset.i18nZhHtml;
            delete elx.dataset.i18nZhHtml;
        }
    });
}
function applyUiLanguage(lang) {
    window.__uiLang = lang === 'en' ? 'en' : 'zh';
    document.documentElement.lang = window.__uiLang === 'en' ? 'en' : 'zh-CN';
    // 真正替换界面文案（可逆：切回中文会还原）
    try { translateDom(window.__uiLang); } catch (e) { console.warn('i18n 失败', e); }
}

/** 把语言设置回填到下拉 */
function fillLanguageSettings(s) {
    const u = el('setUiLang'), t = el('setThinkLang'), r = el('setReplyLang');
    if (u) u.value = s.uiLanguage || 'zh';
    if (t) t.value = s.thinkLanguage || 'auto';
    if (r) r.value = s.replyLanguage || 'auto';
    applyUiLanguage(s.uiLanguage || 'zh');
}

/** 语言下拉变更即保存（不必等「保存」按钮） */
function bindLanguageSelects() {
    const save = async () => {
        const body = {
            uiLanguage: el('setUiLang') ? el('setUiLang').value : 'zh',
            thinkLanguage: el('setThinkLang') ? el('setThinkLang').value : 'auto',
            replyLanguage: el('setReplyLang') ? el('setReplyLang').value : 'auto',
        };
        applyUiLanguage(body.uiLanguage);
        try {
            await bridge.call('setAiSettings', body);
            toast('语言设置已保存', 'ok');
        } catch (e) { toast('保存失败：' + e.message, 'err'); }
    };
    ['setUiLang', 'setThinkLang', 'setReplyLang'].forEach((id) => {
        const n = el(id);
        if (n) n.addEventListener('change', save);
    });
}

/**
 * 🔴 **Agent 权限档位（右上角，2026-09-26）**
 *   readonly  = 只能查（不给 shell / UI 控制）
 *   readwrite = 可执行【只读命令】（自动放行）+ 写操作逐条确认
 *   full      = 全部放行（含写操作与 UI 控制，不再逐条授权）
 * ⚠️ 这是【用户明确选择的档位】，不是 Agent 自己攒的授权 —— 对标 DSH 的权限预设。
 */
function applyPermissionTier(tier) {
    const sel = el('permSelect');
    const hint = el('permHint');
    if (!sel) return;
    if (tier !== 'readonly' && tier !== 'readwrite' && tier !== 'full') tier = 'readonly';
    sel.value = tier;
    sel.dataset.tier = tier;
    if (hint) {
        hint.textContent = tier === 'readonly' ? 'Agent 只能查询索引与知识库'
            : tier === 'readwrite' ? '可跑只读命令；改文件会逐条问'
            : '全部放行（含改文件），请确认信任当前任务';
    }
}

/** 绑定右上角档位下拉（只需一次）。 */
function wirePermissionTier() {
    const sel = el('permSelect');
    if (!sel || sel.dataset.wired) return;
    sel.dataset.wired = '1';
    sel.addEventListener('change', async () => {
        const tier = sel.value;
        try {
            const r = await bridge.call('aiSetPermissionTier', { tier });
            if (r && r.ok === false) { toast(r.message || '设置失败', 'err'); return; }
            applyPermissionTier(tier);
            toast(tier === 'readonly' ? '已切到「只读」' : tier === 'readwrite' ? '已切到「读写」' : '已切到「完全」——请谨慎', 'ok');
        } catch (e) {
            toast('设置权限失败：' + (e && e.message ? e.message : e), 'err');
        }
    });
}

async function loadAiSettings() {
    const s = await bridge.call('aiSettings');
    el('aiBaseUrl').value = s.baseUrl || '';
    el('aiModel').value = s.model || '';
    el('aiApiKey').value = '';
    el('aiApiKey').placeholder = s.hasKey ? `已保存（${s.keyMasked}），留空不修改` : '必填';
    el('aiTemperature').value = s.temperature;
    el('aiMaxTokens').value = s.maxTokens > 0 ? s.maxTokens : '';
    el('aiContextLength').value = s.contextLength || 262144;
    el('aiCompressPercent').value = s.compressPercent || 70;
    el('aiMultimodal').checked = s.multimodal !== false;
    el('aiAllowNetwork').checked = s.allowNetwork === true;
    el('aiAllowShell').checked = s.allowShell === true;
    el('aiAllowUiControl').checked = s.allowUiControl === true;
    const ct = el('chatThink'); if (ct) ct.value = s.thinkingEffort || 'auto';
    fillLanguageSettings(s);   // 界面语言 / AI 思考语言 / AI 回复语言
    el('aiTestResult').textContent = s.configured ? `已配置：${s.model}` : '尚未配置';
    return s;
}

async function saveAiSettings() {
    const res = await bridge.call('setAiSettings', {
        baseUrl: el('aiBaseUrl').value.trim(),
        model: el('aiModel').value.trim(),
        apiKey: el('aiApiKey').value.trim(),
        temperature: el('aiTemperature').value.trim(),
        maxTokens: el('aiMaxTokens').value.trim(),
        contextLength: el('aiContextLength').value.trim(),
        compressPercent: el('aiCompressPercent').value.trim(),
        multimodal: el('aiMultimodal').checked,
        allowNetwork: el('aiAllowNetwork').checked,
        allowShell: el('aiAllowShell').checked,
        allowUiControl: el('aiAllowUiControl').checked,
        thinkingEffort: el('chatThink') ? el('chatThink').value : 'auto',
    });
    toast(res.message, res.ok ? 'ok' : 'err');
    await loadAiSettings();
}

async function testAiConnection() {
    el('aiTestResult').textContent = '正在测试连接…';
    try {
        const res = await bridge.call('testAi');
        el('aiTestResult').textContent = (res.ok ? '✅ ' : '❌ ') + res.message;
        toast(res.ok ? 'AI 连接正常' : '连接失败：' + res.message, res.ok ? 'ok' : 'err');
    } catch (e) {
        el('aiTestResult').textContent = '❌ ' + e.message;
    }
}

const ai = { libraryId: 0, libraryName: '', messages: [], kb: null, busy: false, current: null, renderPending: false };

/** 流式渲染节流：每 ~80ms 重绘一次，避免逐字重绘卡顿 */
function scheduleAiRender() {
    if (ai.renderPending) return;
    ai.renderPending = true;
    setTimeout(() => { ai.renderPending = false; renderAiMessages(); }, 80);
}

async function openAiPanel(libraryId) {
    const lib = state.libraries.find((l) => l.id === libraryId);
    ai.libraryId = libraryId;
    ai.libraryName = lib ? lib.name : '';
    ai.messages = [];
    el('aiLibName').textContent = ai.libraryName;
    el('aiPanel').hidden = false;
    el('aiProgress').hidden = true;
    await refreshAiKb();
    renderAiMessages();
    el('aiInput').focus();
}

async function refreshAiKb() {
    try {
        const st = await bridge.call('kbStatus', { libraryId: ai.libraryId });
        ai.kb = st;
        const parts = [];
        if (!st.manualName) {
            parts.push('该库没有 PDF 说明书，无法建立知识库');
            el('btnKbBuild').disabled = true;
        } else if (st.building) {
            parts.push(`正在构建…`);
            el('btnKbBuild').disabled = true;
        } else if (!st.built) {
            parts.push(`说明书：${st.manualName}（${formatBytes(st.manualSize)}）· 尚未建立知识库`);
            el('btnKbBuild').disabled = false;
            el('btnKbBuild').textContent = '建立知识库';
        } else {
            parts.push(`${st.fresh ? '✓ 知识库就绪' : '⚠ 手册已变更，建议重建'}：${st.pages} 页 / ${st.chunks} 块` +
                (st.visionPages ? ` / 视觉解析 ${st.visionPages} 页` : '') +
                ` · ${formatBytes(st.kbSize)}`);
            el('btnKbBuild').disabled = false;
            el('btnKbBuild').textContent = st.fresh ? '重建知识库' : '重建（已变更）';
        }
        el('aiKbStatus').textContent = parts.join(' · ');
        return st;
    } catch (e) {
        el('aiKbStatus').textContent = '读取知识库状态失败：' + e.message;
        return null;
    }
}

function renderAiMessages() {
    const box = el('aiMessages');
    if (!ai.messages.length) {
        const ready = ai.kb && ai.kb.built;
        box.innerHTML = `<div class="ai-msg system">
            ${ai.kb?.manualName ? `说明书：<b>${escapeHtml(ai.kb.manualName)}</b><br>` : ''}
            ${ready
                ? '我是这个音色库的助手，可以直接问我怎么用它。<br>' +
                  '我能查手册（会标注页码、可调出原页例图），也能用我自己的理解补充解释、给使用建议。<br>' +
                  '例如：<b>怎么切换演奏法？</b> / <b>keyswitch 是什么？</b> / <b>这个库装好了吗？</b> / <b>适合做什么风格？</b>'
                : '还没有知识库。点右上角「建立知识库」，我会读取该库说明书（文字抽取 + 图片页视觉解读），之后就能对话了。'}
        </div>`;
        return;
    }

    box.innerHTML = ai.messages.map((m, idx) => {
        if (m.role === 'system') return `<div class="ai-msg system">${m.content}</div>`;
        if (m.role === 'error') return `<div class="ai-msg error">${escapeHtml(m.content)}</div>`;

        const pages = (m.pages && m.pages.length)
            ? `<div class="ai-pages">${m.pages.map((p) => `<span class="page-chip" data-page="${p}" data-msg="${idx}">第 ${p} 页</span>`).join('')}</div>`
            : '';
        const meta = m.meta ? `<div class="ai-meta">${escapeHtml(m.meta)}</div>` : '';
        const figs = (m.figures || []).map((f) => `
            <div class="ai-figure">
                <img src="${f.url}" alt="第 ${f.page} 页" loading="lazy" />
                <div class="cap">《${escapeHtml(ai.kb?.manualName || '')}》第 ${f.page} 页（例图）</div>
            </div>`).join('');

        // 工具调用徽章（Agent 的检索动作，对用户透明可见）
        const tools = (m.tools && m.tools.length)
            ? `<div class="ai-tools">${m.tools.map((t) => `
                <span class="ai-tool" title="${escapeHtml(t.args || '')}">
                    ${t.done ? '✓' : '⟳'} ${escapeHtml(t.label)}
                </span>`).join('')}</div>`
            : '';

        // 思考过程：可折叠，默认收起（推理模型的 reasoning）
        const think = (m.reasoning && m.reasoning.length)
            ? `<details class="ai-think"><summary>💭 思考过程（${m.reasoning.length} 字）</summary>
                 <div class="ai-think-body">${escapeHtml(m.reasoning)}</div></details>`
            : '';

        // 流式中用纯文本（快），结束后切 Markdown 渲染
        const body = m.role === 'assistant'
            ? (m.streaming
                ? `<div class="md-stream">${escapeHtml(m.content || '')}${m.content ? '' : '<span class="ai-typing">正在思考…</span>'}</div>`
                : `<div class="md">${renderMarkdown(m.content)}</div>`)
            : escapeHtml(m.content);

        return `<div class="ai-msg ${m.role}">${think}${body}${tools}${pages}${figs}${meta}</div>`;
    }).join('');

    box.querySelectorAll('[data-page]').forEach((chip) =>
        chip.addEventListener('click', () => loadPageImage(Number(chip.dataset.page), Number(chip.dataset.msg))));

    scrollToBottom(box, true);   // OK 只写不读，零强制重排（见 scrollToBottom 注释）
}

async function loadPageImage(page, msgIndex) {
    try {
        const res = await bridge.call('pageImage', { libraryId: ai.libraryId, page });
        if (!res.ok) { toast(res.message, 'err'); return; }
        const msg = ai.messages[msgIndex];
        if (!msg) return;
        msg.figures = msg.figures || [];
        if (msg.figures.some((f) => f.page === page)) return;   // 已展示
        msg.figures.push({ page, url: res.url + '?t=' + Date.now() });
        renderAiMessages();
    } catch (e) { toast('调取页面图失败：' + e.message, 'err'); }
}

async function askQuestion() {
    if (ai.busy) return;
    const q = el('aiInput').value.trim();
    if (!q) return;

    if (!ai.kb || !ai.kb.built) {
        toast('请先建立该库的知识库', 'err');
        return;
    }

    el('aiInput').value = '';

    // 状态行：新一轮 → 轮次 +1、步数清零
    aiStatsNewTurn();
    ai.messages.push({ role: 'user', content: q });

    // 立即插入流式消息（打字机效果 + 思考过程 + 工具徽章）
    const msg = { role: 'assistant', content: '', reasoning: '', tools: [], streaming: true, pages: [], meta: '' };
    ai.messages.push(msg);
    ai.current = msg;
    ai.busy = true;
    el('btnAsk').disabled = true;
    renderAiMessages();

    try {
        const history = ai.messages
            .filter((m) => (m.role === 'user' || m.role === 'assistant') && !m.streaming)
            .map((m) => ({ role: m.role, content: m.content }));

        const res = await bridge.call('ask', { libraryId: ai.libraryId, question: q, history });

        msg.streaming = false;
        if (!res.ok && res.error) {
            msg.role = 'error';
            msg.content = res.error;
        } else {
            if (!msg.content) msg.content = res.answer || '（没有返回内容）';
            msg.pages = res.pages || [];
            const metaParts = [];
            if (res.chunks) metaParts.push(`手册命中 ${res.chunks} 块`);
            if (res.noManualMatch) metaParts.push('手册未直接命中：含 Agent 通用知识');
            if (res.compressed) metaParts.push('已压缩历史');
            metaParts.push(`耗时 ${res.seconds}s`);
            msg.meta = metaParts.join(' · ');
        }
    } catch (e) {
        msg.streaming = false;
        msg.role = 'error';
        msg.content = '提问失败：' + e.message;
    } finally {
        ai.busy = false;
        ai.current = null;
        el('btnAsk').disabled = false;
        renderAiMessages();
    }
}

async function buildKb() {
    try {
        const res = await bridge.call('kbBuild', { libraryId: ai.libraryId });
        if (!res.ok) { toast(res.message, 'err'); return; }
        toast(res.message, 'ok');
        el('btnKbBuild').disabled = true;
        el('aiProgress').hidden = false;
        el('aiProgressFill').style.transform = 'scaleX(0)';
        el('aiProgressText').textContent = '正在启动…';
    } catch (e) { toast('启动构建失败：' + e.message, 'err'); }
}

/* ══════════ 轻量 Markdown 渲染（零依赖、离线可用） ══════════
   支持：标题 / 粗体 / 斜体 / 行内代码 / 代码块 / 有序无序列表 /
        表格 / 引用 / 分隔线 / 链接。
   安全：先做 HTML 转义，再应用标记，避免注入。 */
/* **安全的 markdown 渲染**：内部实现是 renderMarkdownInner；
   这里包一层 try/catch —— 渲染失败时降级为纯文本，**绝不抛异常、绝不丢内容**。
   （实测踩过：markdown 渲染抛异常被 console.warn 吞掉 → AI 气泡空白但数据库里有内容） */
function renderMarkdown(src) {
    try {
        return renderMarkdownInner(src);
    } catch (e) {
        try {
            bridge.call('clientLog', {
                kind: 'markdown',
                message: 'renderMarkdown 抛异常：' + (e && e.message ? e.message : e),
                source: 'renderMarkdown',
                stack: (e && e.stack) ? String(e.stack).slice(0, 1200) : '',
            });
        } catch { }
        // 降级：把纯文本按等宽块显示（至少内容不丢）
        return '<pre class="md-fallback">' + escapeHtml(String(src || '')) + '</pre>';
    }
}

function renderMarkdownInner(src) {
    if (!src) return '';
    let s = escapeHtml(src);

    // 1) 先抽出代码块（避免内部内容被后续规则破坏）
    const codes = [];
    s = s.replace(/```([a-zA-Z0-9+#-]*)\n?([\s\S]*?)```/g, (m, lang, body) => {
        codes.push(`<pre class="md-pre"><code>${body.replace(/\n$/, '')}</code></pre>`);
        return `\u0000C${codes.length - 1}\u0000`;
    });

    const inline = (t) => t
        .replace(/`([^`\n]+)`/g, '<code class="md-inline">$1</code>')
        .replace(/\*\*([^*\n]+)\*\*/g, '<strong>$1</strong>')
        .replace(/(^|[^*])\*([^*\n]+)\*(?!\*)/g, '$1<em>$2</em>')
        .replace(/\[([^\]\n]+)\]\((https?:\/\/[^)\s]+)\)/g,
            '<a href="$2" target="_blank" rel="noreferrer">$1</a>');

    const lines = s.split('\n');
    const out = [];
    let i = 0;
    let para = [];
    let list = null;   // { type:'ul'|'ol', items:[] }

    const flushPara = () => {
        if (para.length) { out.push(`<p>${inline(para.join('<br>'))}</p>`); para = []; }
    };
    const flushList = () => {
        if (list) { out.push(`<${list.type}>${list.items.join('')}</${list.type}>`); list = null; }
    };
    const flushAll = () => { flushPara(); flushList(); };

    while (i < lines.length) {
        const line = lines[i];
        const trimmed = line.trim();

        // 空行
        if (trimmed === '') { flushAll(); i++; continue; }

        // 代码块占位
        if (/^\u0000C\d+\u0000$/.test(trimmed)) { flushAll(); out.push(trimmed); i++; continue; }

        // 分隔线
        if (/^(-{3,}|\*{3,}|_{3,})$/.test(trimmed)) { flushAll(); out.push('<hr>'); i++; continue; }

        // 标题
        const h = trimmed.match(/^(#{1,6})\s+(.*)$/);
        if (h) { flushAll(); const lv = h[1].length; out.push(`<h${lv}>${inline(h[2])}</h${lv}>`); i++; continue; }

        // 表格（当前行以 | 开头且下一行是分隔行）
        if (trimmed.startsWith('|') && i + 1 < lines.length &&
            /^\|[\s:|-]+\|$/.test(lines[i + 1].trim())) {
            flushAll();
            const cells = (r) => r.trim().replace(/^\||\|$/g, '').split('|').map((c) => c.trim());
            const head = cells(trimmed);
            let j = i + 2;
            const rows = [];
            while (j < lines.length && lines[j].trim().startsWith('|')) { rows.push(cells(lines[j].trim())); j++; }
            out.push('<table class="md-table"><thead><tr>' +
                head.map((c) => `<th>${inline(c)}</th>`).join('') +
                '</tr></thead><tbody>' +
                rows.map((r) => '<tr>' + r.map((c) => `<td>${inline(c)}</td>`).join('') + '</tr>').join('') +
                '</tbody></table>');
            i = j;
            continue;
        }

        // 引用（注意：内容已做 HTML 转义，故 > 表现为 &gt;）
        if (/^(&gt;|>)\s?/.test(trimmed)) {
            flushAll();
            const buf = [];
            while (i < lines.length && /^(&gt;|>)\s?/.test(lines[i].trim())) {
                buf.push(lines[i].trim().replace(/^(&gt;|>)\s?/, '')); i++;
            }
            out.push(`<blockquote>${inline(buf.join('<br>'))}</blockquote>`);
            continue;
        }

        // 列表（支持缩进一层嵌套）
        const li = line.match(/^(\s*)([-*+]|\d+[.)])\s+(.*)$/);
        if (li) {
            flushPara();
            const type = /\d/.test(li[2]) ? 'ol' : 'ul';
            if (!list || list.type !== type) { flushList(); list = { type, items: [] }; }
            const nested = li[1].length >= 2;
            list.items.push(`<li${nested ? ' class="md-nested"' : ''}>${inline(li[3])}</li>`);
            i++;
            continue;
        }

        // 普通段落
        flushList();
        para.push(trimmed);
        i++;
    }
    flushAll();

    // 还原代码块
    return out.join('\n').replace(/\u0000C(\d+)\u0000/g, (m, n) => codes[Number(n)] || '');
}

/* ══════════ 说明书浏览 ══════════ */
async function showManuals(libraryId) {
    try {
        const res = await bridge.call('manuals', { libraryId });
        const lib = state.libraries.find((l) => l.id === libraryId);
        if (!res.items.length) { toast('该音色库未找到说明书', 'err'); return; }

        // 同名同大小合并（多子库常各带一份相同手册，如 Chris Hein Solo Strings 有 4 份）
        const groups = new Map();
        for (const m of res.items) {
            const key = `${m.name}|${m.sizeBytes}`;
            if (!groups.has(key)) groups.set(key, { ...m, count: 1, paths: [m.fullPath] });
            else { const g = groups.get(key); g.count++; g.paths.push(m.fullPath); }
        }
        const items = [...groups.values()];

        const rows = items.map((m) => `
            <div class="manual-item">
                <span class="manual-ext">${escapeHtml(m.ext.toUpperCase())}</span>
                <span class="manual-name" title="${escapeHtml(m.paths.join('\n'))}">${escapeHtml(m.name)}</span>
                ${m.count > 1 ? `<span class="tag" title="${escapeHtml(m.paths.join('\n'))}">×${m.count}</span>` : ''}
                <span class="manual-size">${formatBytes(m.sizeBytes)}</span>
                ${m.isPrimary ? '<span class="st st-ok">主要手册</span>' : ''}
                <button class="btn small" data-openfile="${escapeHtml(m.paths[0])}">打开</button>
            </div>`).join('');

        const dupNote = items.length < res.items.length
            ? `（同名合并后 ${items.length} 项，原始 ${res.items.length} 个文件）`
            : '';

        showModal(`📖 ${lib ? lib.name : ''} — 说明书`,
            `<div class="manual-list">${rows}</div>
             <div class="hint-row" style="padding:8px 0 0">
               共 ${res.total} 个文档 · ${res.primary} 个主要手册 ${dupNote}。点「打开」用系统默认程序阅读；
               悬停文件名可看全部路径。
             </div>`,
            { okText: '关闭', showCancel: false });

        el('modalBody').querySelectorAll('[data-openfile]').forEach((b) =>
            b.addEventListener('click', async () => {
                try { await bridge.call('openFile', { path: b.dataset.openfile }); }
                catch (e) { toast('打开失败：' + e.message, 'err'); }
            }));
    } catch (e) { toast('读取说明书失败：' + e.message, 'err'); }
}

/* ══════════ 设置：① 路径 ══════════ */
function renderRoots(roots) {
    state.roots = roots || [];
    const box = el('rootList');
    if (!state.roots.length) {
        box.innerHTML = `<div class="hint-row">还没有添加任何路径。点击右上角「+ 添加路径」选择音色库根目录。</div>`;
        return;
    }
    box.innerHTML = state.roots.map((r) => `
        <div class="root-item ${r.exists ? '' : 'missing'} ${r.enabled ? '' : 'disabled'}">
            <label class="switch" title="${r.enabled ? '点击停用' : '点击启用'}">
                <input type="checkbox" data-enable="${r.id}" ${r.enabled ? 'checked' : ''} />
                <span></span>
            </label>
            <span class="path">${escapeHtml(r.path)}</span>
            <span class="meta">${r.libraryCount} 库 · ${formatBytes(r.sizeBytes)}${r.lastScannedAt ? ' · ' + escapeHtml(r.lastScannedAt) : ''}</span>
            ${r.exists ? '' : '<span class="st st-mismatch">路径不存在</span>'}
            <button class="btn small" data-scanroot="${r.id}">打开</button>
            <button class="btn small" data-removeroot="${r.id}">移除</button>
        </div>`).join('');

    box.querySelectorAll('[data-enable]').forEach((cb) =>
        cb.addEventListener('change', async () => {
            const res = await bridge.call('setRootEnabled', { id: Number(cb.dataset.enable), enabled: cb.checked });
            renderRoots(res.roots);
        }));
    box.querySelectorAll('[data-removeroot]').forEach((b) =>
        b.addEventListener('click', async () => {
            const root = state.roots.find((x) => x.id === Number(b.dataset.removeroot));
            const yes = await confirmModal('移除路径',
                `确定移除 <b>${escapeHtml(root?.path || '')}</b>？<br>该路径下的索引数据也会一并删除（不会删除任何实际文件）。`);
            if (!yes) return;
            const res = await bridge.call('removeRoot', { id: Number(b.dataset.removeroot) });
            toast(res.message, 'ok');
            renderRoots(res.roots);
            await refresh();
        }));
    box.querySelectorAll('[data-scanroot]').forEach((b) =>
        b.addEventListener('click', () => {
            const root = state.roots.find((x) => x.id === Number(b.dataset.scanroot));
            if (root) doOpenPath(root.path);
        }));
}

async function doAddRoot() {
    try {
        const pick = await bridge.call('pickFolder', { initial: state.config?.defaultRoot || '' });
        if (!pick.picked) return;
        const res = await bridge.call('addRoot', { path: pick.path });
        toast(res.message, res.ok ? 'ok' : 'err');
        renderRoots(res.roots);
    } catch (e) { toast('添加失败：' + e.message, 'err'); }
}

/* ══════════ 设置：② Kontakt 本体 ══════════ */
function renderKontaktCurrent() {
    const exe = state.config?.kontaktExe || '';
    const ver = state.config?.kontaktVersion || '';
    el('kontaktCurrent').innerHTML = `
        <div class="kv"><span class="k">当前 Kontakt</span><span class="v">${ver ? escapeHtml(ver) : '未设置'}</span></div>
        <div class="kv"><span class="k">程序路径</span><span class="v">${exe ? escapeHtml(exe) : '—'}</span></div>
        <div class="kv"><span class="k">进程状态</span><span class="v">${state.config?.kontaktRunning ? '运行中' : '未运行'}</span></div>`;
}

let editingPath = null;

function renderKontaktCandidates(candidates, ignored) {
    state.candidates = candidates || [];
    state.ignored = ignored || [];
    const box = el('kontaktInstalls');

    if (!state.candidates.length) {
        box.innerHTML = `<div class="hint-row">列表为空。点「重新检测」自动查找，或点「+ 手动添加…」指定主程序路径。</div>`;
    } else {
        box.innerHTML = state.candidates.map((c) => {
            const editing = editingPath === c.path;
            const badges = [];
            badges.push(c.source === 'manual'
                ? '<span class="tag src-manual">手动</span>'
                : '<span class="tag src-auto">自动</span>');
            if (c.isCurrent) badges.push('<span class="st st-ok">当前使用</span>');
            if (!c.exists) badges.push('<span class="st st-mismatch">文件不存在</span>');

            if (editing) {
                return `<div class="detect-item editing">
                    <input class="path-input" id="editPathInput" value="${escapeHtml(c.path)}" spellcheck="false" />
                    <button class="btn small primary" data-saveedit="${escapeHtml(c.path)}">保存</button>
                    <button class="btn small" data-canceledit="1">取消</button>
                    <div class="note">可手动粘贴完整路径，或点「浏览…」选择文件</div>
                    <button class="btn small" data-browseedit="${escapeHtml(c.path)}">浏览…</button>
                </div>`;
            }

            return `<div class="detect-item">
                <span class="path">${escapeHtml(c.path)}</span>
                <span class="meta">${c.version ? 'v' + escapeHtml(c.version) : '版本未知'}${c.sizeMB ? ' · ' + c.sizeMB + ' MB' : ''}${c.note ? ' · ' + escapeHtml(c.note) : ''}</span>
                ${badges.join('')}
                ${c.isCurrent ? '' : `<button class="btn small primary" data-usecand="${escapeHtml(c.path)}">使用此程序</button>`}
                <button class="btn small" data-editcand="${escapeHtml(c.path)}">编辑</button>
                <button class="btn small" data-delcand="${escapeHtml(c.path)}">删除</button>
            </div>`;
        }).join('');
    }

    // 忽略清单（透明展示为什么没被收录）
    const igBox = el('ignoredBox');
    if (state.ignored.length) {
        igBox.hidden = false;
        el('ignoredSummary').textContent = `已自动忽略 ${state.ignored.length} 个安装包 / 非程序文件（点开查看）`;
        el('ignoredList').innerHTML = state.ignored.map((i) => `
            <div class="detect-item ignored">
                <span class="path">${escapeHtml(i.path)}</span>
                <span class="meta">${i.sizeMB ? i.sizeMB + ' MB · ' : ''}${escapeHtml(i.note.replace(/^IGNORE:\s*/, ''))}</span>
            </div>`).join('');
    } else {
        igBox.hidden = true;
    }

    // 事件
    box.querySelectorAll('[data-usecand]').forEach((b) =>
        b.addEventListener('click', async () => {
            const res = await bridge.call('setKontaktExe', { path: b.dataset.usecand });
            toast(res.message, res.ok ? 'ok' : 'err');
            await loadConfig();
            renderKontaktCurrent();
            await loadCandidates();
            await loadCompat();
            await refresh();
        }));
    box.querySelectorAll('[data-editcand]').forEach((b) =>
        b.addEventListener('click', () => { editingPath = b.dataset.editcand; renderKontaktCandidates(state.candidates, state.ignored); }));
    box.querySelectorAll('[data-canceledit]').forEach((b) =>
        b.addEventListener('click', () => { editingPath = null; renderKontaktCandidates(state.candidates, state.ignored); }));
    box.querySelectorAll('[data-delcand]').forEach((b) =>
        b.addEventListener('click', async () => {
            const p = b.dataset.delcand;
            const isAuto = state.candidates.find((c) => c.path === p)?.source === 'auto';
            const yes = await confirmModal('从列表删除',
                `确定把以下路径从列表中删除？<br><b>${escapeHtml(p)}</b><br><br>` +
                (isAuto ? '这是自动检测到的条目，删除后会记住不再显示（不会删除任何文件）。' : '不会删除磁盘上的任何文件。'));
            if (!yes) return;
            const res = await bridge.call('removeKontaktCandidate', { path: p });
            toast(res.message, res.ok ? 'ok' : 'err');
            applyCandidatesResult(res);
            await loadConfig();
            renderKontaktCurrent();
            await loadCompat();
            await refresh();
        }));
    box.querySelectorAll('[data-saveedit]').forEach((b) =>
        b.addEventListener('click', async () => {
            const newPath = el('editPathInput').value.trim();
            const res = await bridge.call('updateKontaktCandidate', { path: b.dataset.saveedit, newPath });
            toast(res.message, res.ok ? 'ok' : 'err');
            if (res.ok) {
                editingPath = null;
                applyCandidatesResult(res);
                await loadConfig();
                renderKontaktCurrent();
                await loadCompat();
                await refresh();
            }
        }));
    box.querySelectorAll('[data-browseedit]').forEach((b) =>
        b.addEventListener('click', async () => {
            const pick = await bridge.call('pickKontaktExe', {});
            if (pick.picked) el('editPathInput').value = pick.path;
        }));
}

function applyCandidatesResult(res) {
    if (res.candidates) {
        state.candidates = res.candidates;
        renderKontaktCandidates(state.candidates, state.ignored);
    }
    if (typeof res.currentExe === 'string') {
        state.config.kontaktExe = res.currentExe;
        state.config.kontaktVersion = res.currentVersion || '';
    }
}

async function loadCandidates() {
    const det = await bridge.call('detectKontakt');
    renderKontaktCandidates(det.candidates, det.ignored);
    state.config.kontaktExe = det.currentExe;
    state.config.kontaktVersion = det.currentVersion;
}

/* ══════════ 设置：③ 入库管理 ══════════ */
/** 把非标准库（无 .nicnt）加入 Kontakt 的 Quick-Load —— 这是这类库唯一正当的「入库」途径 */
/** 批量收尾：把非标准库批量加入 Quick-Load + 把待处理的库批量写入便携版列表 */
async function doBatchFinalize() {
    const rep = state.registration;
    if (!rep || !rep.items) { toast('请先「重新检查」', 'err'); return; }

    const nonStd = rep.items.filter((l) => l.regStatus === 'non-standard');
    const pending = rep.items.filter((l) => l.regStatus === 'pending-manager');

    if (!nonStd.length && !pending.length) { toast('没有需要批量处理的库', 'ok'); return; }

    const yes = await confirmModal('批量收尾',
        `将执行两项批量操作：<br><br>` +
        `<b>① 加入 Quick-Load</b>：${nonStd.length} 个非标准库（无 .nicnt）<br>` +
        `<span style="color:#8b93a3">在每个库目录建 Windows 快捷方式到 <code>UserData\\Kontakt 8\\QuickLoad\\Instr\\</code>，` +
        `<b>不复制任何音色文件</b>。这类库无法进入 Kontakt 的 Libraries 页（需 NI 签发许可），Quick-Load 是正当入口。</span><br><br>` +
        `<b>② 写入便携版库列表</b>：${pending.length} 个库<br>` +
        `<span style="color:#8b93a3">写入 <code>UserData\\Settings.cfg</code>，写前自动备份、写后校验。需 Kontakt 完全关闭。</span>`,
        { okText: '开始批量收尾', cancelText: '取消' });
    if (!yes) return;

    const info = el('regStats');
    const show = (t) => { if (info) info.innerHTML = `<div class="hint-row">${t}</div>`; };

    // ── ① Quick-Load ──
    let qlOk = 0, qlFail = 0;
    for (let i = 0; i < nonStd.length; i++) {
        show(`正在加入 Quick-Load … ${i + 1}/${nonStd.length}（${escapeHtml(nonStd[i].name)}）`);
        try {
            const r = await bridge.call('addToQuickLoad', { libraryId: nonStd[i].id });
            if (r.ok) qlOk++; else qlFail++;
        } catch { qlFail++; }
    }
    if (nonStd.length) show(`Quick-Load 完成：成功 ${qlOk} 个，失败 ${qlFail} 个`);

    // ── ② 便携版库列表 ──
    let wpMsg = '';
    if (pending.length) {
        show(`正在写入便携版库列表 …（${pending.length} 个）`);
        try {
            let res = await bridge.call('writePortableLibraries', { ids: pending.map((l) => l.id) });
            if (!res.ok && res.kontaktRunning) {
                const close = await confirmModal('Kontakt 正在运行',
                    `${escapeHtml(res.message)}<br><br>是否<b>立即关闭 Kontakt</b> 并继续？`);
                if (close) {
                    const c = await bridge.call('closeKontakt');
                    if (c.ok) {
                        await new Promise((r) => setTimeout(r, 1200));
                        res = await bridge.call('writePortableLibraries', { ids: pending.map((l) => l.id) });
                    }
                }
            }
            wpMsg = res.message || '';
        } catch (e) { wpMsg = '写入失败：' + e.message; }
    }

    toast(`批量收尾完成：Quick-Load 成功 ${qlOk} 个${qlFail ? '（失败 ' + qlFail + '）' : ''}；便携版写入 ${pending.length} 个`, 'ok');
    if (info) info.innerHTML = `<div class="hint-row">批量收尾结果：<br>Quick-Load：成功 <b>${qlOk}</b> 个${qlFail ? '，失败 ' + qlFail + ' 个' : ''}<br>便携版库列表：${escapeHtml(wpMsg)}<br><br>⚠ 请重新启动 Kontakt 生效。</div>`;

    renderRegistration(await bridge.call('registration'));
}

async function doAddQuickLoad(libraryId, libName) {
    const yes = await confirmModal('加入 Quick-Load',
        `把「<b>${escapeHtml(libName)}</b>」加入 Kontakt 的 <b>Quick-Load</b> 浏览器？<br><br>` +
        `做法：在该库目录下建一个 <b>Windows 快捷方式</b>放到<br>` +
        `<code>UserData\\Kontakt 8\\QuickLoad\\Instr\\</code><br><br>` +
        `<b>不会复制任何音色文件</b>（库动辄几十 GB），只是建个链接。<br>` +
        `重启 Kontakt 后可在 Quick-Load 里看到它，点开即可加载乐器。<br><br>` +
        `<span style="color:#8b93a3">说明：无 .nicnt 的库无法出现在 Kontakt 的「Libraries」标签页` +
        `（那需要 NI 签发的许可）；Quick-Load 是这类库的正当用法。</span>`,
        { okText: '加入 Quick-Load', cancelText: '取消' });
    if (!yes) return;

    let r;
    try { r = await bridge.call('addToQuickLoad', { libraryId }); }
    catch (e) { toast('失败：' + e.message, 'err'); return; }
    toast(r.message || (r.ok ? '已加入 Quick-Load' : '失败'), r.ok ? 'ok' : 'err');
    if (r.ok) renderRegistration(await bridge.call('registration'));
}

/**
 * **为「没有 .nicnt」的第三方库生成 .nicnt 并入库**（复刻 Nicnt Maker 机制）。
 * 全程用自绘弹窗，不用系统 prompt/confirm。
 */
async function doGenNicnt(libraryId, libName, libPath) {
    let st;
    try { st = await bridge.call('nicntGenStatus'); }
    catch (e) { toast('读取 SNPID 状态失败：' + e.message, 'err'); return; }

    const srcNote = st.officialListIsBuiltin
        ? `内置副本 <code>data\\Native_Access_SNPID_List.txt</code>`
        : `<span style="color:#e8b04b">⚠ 用的是 Native Access 导出目录（项目内置副本缺失）</span>`;

    const body =
        `库：<b>${escapeHtml(libName || '')}</b><br>` +
        `<span style="color:#8b93a3">${escapeHtml(libPath || '')}</span><br><br>` +
        `SNPID 查重来源：<br>` +
        `· Service Center 已注册：<b>${st.fromServiceCenter}</b> 个（共 ${st.serviceCenterXmlCount} 个记录，其中含 SNPID 的）<br>` +
        `· 官方 SNPID 表：<b>${st.fromOfficialList}</b> 个（${srcNote}）<br>` +
        `· 合计占用：<b>${st.totalUsed}</b> 个<br>` +
        `<span style="color:#8b93a3">SNPID 由本工具自动分配（<b>${st.suggested}</b>），无需你操心。</span><br><br>` +
        `<label style="display:block;margin:6px 0 4px;color:#c9d1de">厂商名（留空 = 3rd Party）</label>` +
        `<input id="gnCompany" class="dlg-input" value="8Dio">` +
        `<label style="display:block;margin:12px 0 4px;color:#c9d1de">库名（写进 .nicnt 的 Name 与 RegKey）</label>` +
        `<input id="gnName" class="dlg-input" value="${escapeHtml(libName || '')}">` +
        `<div style="margin-top:12px;color:#8b93a3;font-size:12px">` +
        `会在库目录生成 <code>${escapeHtml(libName || '')}.nicnt</code>（约 514 KB），并完成入库。` +
        `若同名文件已存在，会先备份为 <code>*.klm-backup-*</code>。</div>`;

    const yes = await confirmModal('生成 .nicnt 并入库', body, { okText: '生成并入库', cancelText: '取消' });
    if (!yes) return;

    const company = (document.getElementById('gnCompany')?.value || '').trim();
    const name = (document.getElementById('gnName')?.value || '').trim();
    if (!name) { toast('库名不能为空', 'err'); return; }

    toast('正在生成 .nicnt 并入库…', 'ok');
    let r;
    try {
        r = await bridge.call('nicntGenMake', {
            libraryId, libraryPath: libPath, name, company: company || '3rd Party', snpid: '',
        });
    } catch (e) { toast('生成失败：' + e.message, 'err'); return; }
    toast(r.message, r.ok ? 'ok' : 'err');
    if (r.ok) renderRegistration(await bridge.call('registration'));
}

function renderRegistration(rep) {
    loadQuickLoadTargets();   // 异步刷新，下次渲染即生效
    state.registration = rep;
    if (!rep) return;

    // 入库统计同时反映到「概览」页的指标条（设置页保留明细）
    renderHomeStats(rep);

    const statsBox = el('regStats');
    if (statsBox) statsBox.innerHTML = '';

    const box = el('regList');
    // 非标准库若已加入 Quick-Load，就当成「已处理」不再列在待办里（用户要求）
    const ql = (window.__qlTargets || new Set());
    const need = (rep.items || []).filter((l) => {
        if (l.regStatus === 'registered') return false;
        if (l.regStatus === 'non-standard' && l.path && ql.has(String(l.path).replace(/[\\\\/]+$/, ''))) return false;
        return true;
    });
    if (!need.length) {
        box.innerHTML = `<div class="hint-row">✓ 全部音色库均已正确入库（注册表 + Service Center 记录 + 便携版库列表齐全）。</div>`;
        return;
    }

    box.innerHTML = need.map((l) => {
        const [label, cls] = REG_LABEL[l.regStatus] || REG_LABEL['unknown'];
        const canReg = ['missing', 'path-mismatch', 'incomplete'].includes(l.regStatus);
        const scHint = (l.regStatus === 'incomplete' && l.scMissingRecords && l.scMissingRecords.length)
            ? `<br><span style="color:#c8b6f0">缺 Service Center 记录：${escapeHtml(l.scMissingRecords.join(', '))}.xml</span>`
            : '';
        const mgrHint = (l.regStatus === 'pending-manager')
            ? `<br><span style="color:#f0c07a">注册表已写入，但便携版 Kontakt 的库列表里没有它 → 需在库管理器中「扫描 → 保存」</span>`
            : '';
        return `<div class="reg-item">
            <span class="st ${cls}">${label}</span>
            <span class="path">${escapeHtml(l.name)}${l.regContentDir ? `<br><span style="color:#8b93a3">注册表指向：${escapeHtml(l.regContentDir)}</span>` : ''}${scHint}${mgrHint}</span>
            <span class="meta">${escapeHtml(l.productKey || '无 .nicnt')}</span>
            ${canReg ? `<button class="btn small" data-reg="${l.id}">${l.regStatus === 'incomplete' ? '补全记录' : '入库'}</button>` : ''}
            ${l.regStatus === 'path-mismatch' ? `<button class="btn small primary" data-repair="${l.id}" title="把注册表/便携版里失效的路径改回该库当前的真实路径">修复路径</button>` : ''}
            ${l.regStatus === 'pending-manager' ? `<button class="btn small primary" data-writeportable="${l.id}">自动写入便携版</button><button class="btn small" data-libmgr="1">库管理器</button>` : ''}
            ${l.regStatus === 'path-mismatch' && l.productKey ? `<button class="btn small" data-unreg="${escapeHtml(l.productKey)}">取消注册</button>` : ''}
            ${l.regStatus === 'non-standard' ? `<button class="btn small primary" data-gennicnt="${l.id}" title="该库没有 .nicnt，Kontakt 的 Libraries 页认不出它。点这里为它生成 .nicnt 并直接入库（SNPID 自动挑选，不与已注册/官方表冲突）">生成 .nicnt 并入库</button><button class="btn small" data-quickload="${l.id}" title="也可以走 Quick-Load（不改动库目录）">加入 Quick-Load</button>` : ''}
        </div>`;
    }).join('');

    box.querySelectorAll('[data-gennicnt]').forEach((b) =>
        b.addEventListener('click', () => {
            const l = (state.registration?.items || []).find((x) => x.id === Number(b.dataset.gennicnt));
            doGenNicnt(Number(b.dataset.gennicnt), l ? l.name : '', l ? l.path : '');
        }));
    box.querySelectorAll('[data-quickload]').forEach((b) =>
        b.addEventListener('click', () => {
            const l = (state.registration?.items || []).find((x) => x.id === Number(b.dataset.quickload));
            doAddQuickLoad(Number(b.dataset.quickload), l ? l.name : '');
        }));
    box.querySelectorAll('[data-writeportable]').forEach((b) =>
        b.addEventListener('click', () => doWritePortable([Number(b.dataset.writeportable)])));
    box.querySelectorAll('[data-repair]').forEach((b) =>
        b.addEventListener('click', async () => {
            const yes = await confirmModal('修复入库路径',
                `把注册表与便携版库列表里指向该库的路径，改成它<b>当前的真实路径</b>。<br><br>` +
                `适用于「库被移动过 / 换过硬盘，Kontakt 里提示库未安装」的场景，<b>不会移动或修改任何音色文件</b>。<br><br>` +
                `需要 Kontakt 完全关闭。`);
            if (!yes) return;
            try {
                const res = await bridge.call('repairPath', { libraryId: Number(b.dataset.repair) });
                let msg = res.message;
                if (res.details && res.details.length) msg += '\n' + res.details.slice(0, 6).join('\n');
                toast(msg, res.ok ? 'ok' : 'err');
                if (res.report) renderRegistration(res.report);
                await refresh();
            } catch (e) { toast('修复失败：' + e.message, 'err'); }
        }));

    box.querySelectorAll('[data-libmgr]').forEach((b) =>
        b.addEventListener('click', doOpenLibraryManager));

    // 便携版 Kontakt 说明（关键：它的库列表在自己的 Settings.cfg 里）
    const pbox = el('portableBox');
    if (rep.portableRoot) {
        pbox.hidden = false;
        const pending = rep.pendingManager || 0;
        pbox.className = 'banner ' + (pending > 0 ? 'warn' : 'ok');
        pbox.innerHTML =
            `检测到<b>便携版 Kontakt</b>：<code>${escapeHtml(rep.portableRoot)}</code><br>` +
            `它的库列表保存在 <code>UserData\\Settings.cfg</code>（当前 ${rep.portableLibraryCount} 个库，最后修改 ${escapeHtml(rep.portableSettingsCfgModified || '?')}）。` +
            `本工具会<b>自动写入</b>该文件（等价于库管理器的「扫描 → 保存」），写入前自动备份、写后校验、失败自动还原。<br>` +
            (pending > 0
                ? `⚠ 有 <b>${pending}</b> 个库还没进便携版自己的库列表 —— 点它们的「<b>自动写入便携版</b>」按钮，或在上面「一键入库」时一并处理。`
                : `✓ 便携版库列表已与注册表一致。`) +
            `<div class="banner-actions">` +
            `<button class="btn" id="btnOpenLibMgr">改用库管理器手动保存</button>` +
            `<button class="btn" id="btnRecheckPortable">重新检查</button></div>`;
        el('btnOpenLibMgr')?.addEventListener('click', doOpenLibraryManager);
        el('btnRecheckPortable')?.addEventListener('click', async () => {
            renderRegistration(await bridge.call('registration'));
            toast('已重新检查', 'ok');
        });
    } else {
        pbox.hidden = true;
    }

    box.querySelectorAll('[data-reg]').forEach((b) =>
        b.addEventListener('click', () => doRegister([Number(b.dataset.reg)])));
    box.querySelectorAll('[data-unreg]').forEach((b) =>
        b.addEventListener('click', async () => {
            const yes = await confirmModal('取消注册',
                `确定从 Kontakt 库浏览器中移除 <b>${escapeHtml(b.dataset.unreg)}</b>？<br>仅删除注册表项，不会删除任何音色文件。`);
            if (!yes) return;
            const res = await bridge.call('unregister', { regKey: b.dataset.unreg });
            toast(res.message, res.ok ? 'ok' : 'err');
            renderRegistration(res.report);
            await refresh();
        }));
}

async function doOpenLibraryManager() {
    try {
        const res = await bridge.call('openLibraryManager');
        toast(res.message, res.ok ? 'ok' : 'err');
    } catch (e) { toast('启动库管理器失败：' + e.message, 'err'); }
}

async function doRegister(ids, force = false) {
    try {
        const res = await bridge.call('registerLibraries', { ids, force });

        // Kontakt 正在运行：先征得同意再关闭它，然后重试
        if (!res.ok && res.kontaktRunning) {
            const yes = await confirmModal('Kontakt 正在运行',
                `${escapeHtml(res.message).replace(/\n/g, '<br>')}<br><br>` +
                `是否<b>立即关闭 Kontakt</b> 并继续入库？（未保存的工程请先在 Kontakt 里保存）`);
            if (!yes) { toast('已取消入库。建议手动关闭 Kontakt 后重试。', 'err'); return; }

            const closed = await bridge.call('closeKontakt');
            if (!closed.ok) { toast(closed.message, 'err'); return; }
            toast(closed.message, 'ok');
            await new Promise((r) => setTimeout(r, 1200));
            return doRegister(ids, true);
        }

        let msg = res.message;
        const fails = (res.details || []).filter((d) => !d.ok);
        if (fails.length) msg += '\n' + fails.slice(0, 5).map((f) => `· ${f.library}：${f.message}`).join('\n');
        if (res.portable) {
            msg += '\n便携版库列表：' + res.portable.message;
            if (res.portable.backupSettingsCfg) msg += `\n（已备份原配置：${res.portable.backupSettingsCfg}）`;
        }
        if (res.ok) msg += '\n\n⚠ 请重新启动 Kontakt，库才会出现在 Library 浏览器中。';
        toast(msg, res.ok ? 'ok' : 'err');
        renderRegistration(res.report);
        await refresh();

        // 功能 4：入库后自动把说明书转成知识库（后台进行，不阻塞界面）
        if (res.ok) {
            try {
                const ak = await bridge.call('autoKb', { ids });
                if (ak.started > 0) {
                    toast(`已在后台为 ${ak.started} 个库建立说明书知识库（可用于「问问AI」）`, 'ok');
                } else if (ak.skippedCount > 0) {
                    // **不再静默** —— 旧实现没启动任何库时什么都不显示，
                    // 用户只看到「知识库还是空的」却不知道为什么（实测反馈）。
                    const why = (ak.skipped || []).map((s) => `库 ${s.libraryId}：${s.reason}`).join('\n');
                    toast(`未自动建库（${ak.skippedCount} 个库）：\n${why}`, 'err');
                }
            } catch (e) { /* 自动建库失败不影响入库结果 */ }
        }
    } catch (e) { toast('入库失败：' + e.message, 'err'); }
}

/** 仅把库写进便携版 Kontakt 的库列表（不碰注册表） */
async function doWritePortable(ids) {
    try {
        let res = await bridge.call('writePortableLibraries', { ids });
        if (!res.ok && res.kontaktRunning) {
            const yes = await confirmModal('Kontakt 正在运行',
                `${escapeHtml(res.message)}<br><br>是否<b>立即关闭 Kontakt</b> 并继续？`);
            if (!yes) return;
            const closed = await bridge.call('closeKontakt');
            if (!closed.ok) { toast(closed.message, 'err'); return; }
            await new Promise((r) => setTimeout(r, 1200));
            res = await bridge.call('writePortableLibraries', { ids });
        }
        let msg = res.message;
        if (res.backupSettingsCfg) msg += `\n（已备份原配置：${res.backupSettingsCfg}）`;
        if (res.ok) msg += '\n\n⚠ 请重新启动 Kontakt 生效。';
        toast(msg, res.ok ? 'ok' : 'err');
        if (res.report) renderRegistration(res.report);
    } catch (e) { toast('写入便携版库列表失败：' + e.message, 'err'); }
}

/* ══════════ 设置：④ 版本兼容 ══════════ */
function renderCompat(c) {
    state.compat = c;
    const box = el('compatBox');
    if (!c) { box.innerHTML = ''; return; }

    const head = !c.kontaktVersion
        ? `<div class="banner">尚未设置 Kontakt 主程序，无法比较版本。请在上方「② Kontakt 主程序」中设置。</div>`
        : `<div class="banner ${c.tooOld > 0 ? 'danger' : 'ok'}">
             当前 Kontakt 版本：<b>${escapeHtml(c.kontaktVersion)}</b>
             ${c.kontaktPath ? `（${escapeHtml(c.kontaktPath)}）` : ''}<br>
             兼容 <b>${c.ok}</b> 个 · 需要更高版本 <b>${c.tooOld}</b> 个 · 无法判定 <b>${c.unknown}</b> 个
           </div>`;

    const list = (c.incompatible || []).length
        ? `<div class="reg-list">${c.incompatible.map((l) => `
            <div class="reg-item">
                <span class="st st-mismatch">需 ${escapeHtml(l.requiredKontakt)}</span>
                <span class="path">${escapeHtml(l.name)}</span>
                <span class="meta">当前 ${escapeHtml(c.kontaktVersion)} 无法加载</span>
            </div>`).join('')}</div>`
        : (c.kontaktVersion ? `<div class="hint-row">✓ 没有发现版本不兼容的音色库。</div>` : '');

    box.innerHTML = head + list;
}

/* ══════════ 扫描 ══════════ */
function setScanning(on) {
    state.scanning = on;
    el('btnScan').disabled = on;
    el('btnScan').textContent = on ? '扫描中…' : '开始扫描';
    if (!on) el('scanState').textContent = '';
}


/* ══════════════════════════════════════════════════════════
   问问AI：左侧工作区（说明书 / 知识库）+ 会话管理 + 对话
   入口语义：
     · 从单个库进入 → 默认「单库问答模式」，只装载该库知识库
     · 从标签页直接进入 → 默认「跨库问答模式」
     · 单库会话触发跨库 → 在该会话下新建一个跨库分支（主分支永远是单库）
   ══════════════════════════════════════════════════════════ */

const chatState = {
    sessionId: 0,
    branchId: 0,
    session: null,
    branches: [],
    streaming: false,
    live: null,            // 流式中的消息（纯数据；渲染时按 id 查元素，不缓存 DOM 指针）
    pendingLibraryId: 0,   // 从单库进入时暂存，用于「是否使用旧会话」询问
    manuals: null,
    kbs: null,
};

/** 打开问问AI页（可指定从某个库进入） */
async function openChatTab(libraryId) {
    switchTab('chat');
    chatState.pendingLibraryId = libraryId || 0;
    if (chatState.manuals === null) loadChatWorkspace();
    await loadChatSessions();

    if (libraryId) {
        // 该库已有会话 → 询问是否使用旧会话
        const res = await bridge.call('chatSessions', {});
        const all = (res.groups || []).flatMap((g) => g.sessions || []);
        const mine = all.filter((s) => s.libraryId === libraryId);
        if (mine.length > 0) {
            const use = await showModal(
                '使用旧会话？',
                '你之前已经针对该库创建过一个会话，是否使用旧会话？<br><span class="meta">选「否」将新建一个会话。</span>',
                { okText: '使用旧会话', cancelText: '新建会话' });
            if (use) { await openChatSession(mine[0].id); return; }
        }
        await createChatSession(libraryId, 'single');
    }
}

/* ── 左侧工作区 ── */
async function loadChatWorkspace() {
    loadChatManuals();
    loadChatKbs();
    loadChatMemories();
}

async function loadChatManuals() {
    const box = el('cstManuals');
    box.innerHTML = '<div class="hint-row">正在读取说明书…</div>';
    let data;
    try { data = await bridge.call('manualsAll', {}); }
    catch (e) { box.innerHTML = '<div class="hint-row">读取失败：' + escapeHtml(e.message) + '</div>'; return; }
    chatState.manuals = data;

    if (!data.total) { box.innerHTML = '<div class="hint-row">没有抓取到任何说明书</div>'; return; }
    box.innerHTML = data.groups.map((g) => `
        <div class="cst-cat">${escapeHtml(g.category)}</div>
        ${g.libraries.map((l) => `
            <details class="cst-lib">
                <summary>
                    <span title="${escapeHtml(l.libraryName)}">${escapeHtml(l.libraryName)}</span>
                    <span class="cnt">${l.count} 本${l.kbBuilt ? ' · 已建库' : ''}</span>
                </summary>
                ${l.items.map((m) => `
                    <div class="cst-item" data-lib="${m.libraryId}" data-path="${escapeHtml(m.fullPath)}">
                        <span class="nm" title="${escapeHtml(m.name)}">${escapeHtml(m.name)}</span>
                        <span class="sz">${formatBytes(m.sizeBytes)}</span>
                        <span class="kb ${m.kbBuilt ? 'yes' : 'no'}">${m.kbBuilt ? '已知识库化' : '未建库'}</span>
                    </div>`).join('')}
            </details>`).join('')}
    `).join('');

    box.querySelectorAll('.cst-item').forEach((it) => it.addEventListener('click', () => {
        openManualItem(Number(it.dataset.lib), it.dataset.path);
    }));
}

/** 点说明书：未建库则询问是否建库；已建库则直接开一个单库会话 */
/**
 * 点说明书 → **先弹窗预览内容**（用户要求）。
 *
 * 旧行为：点一下就直接问「要不要建库」——用户无法在建库前确认这到底是不是说明书
 *（实测踩过：`license.rtf` 也会被当成说明书）。现在改为：
 *   ① 弹窗显示**抽取出来的正文**（**建库用的就是这份文本，所见即所得**）；
 *   ② PDF 可翻页，并可切换「看原页图」（按需渲染该页）；
 *   ③ 顶部给出机械规则判定（像不像说明书）+ 文件大小；
 *   ④ 底部两个按钮：**关闭** / **建立知识库**。
 */
/* ══════════ 建库实时日志窗口 ══════════ */

/** 往建库日志窗口追加一行（自动滚动 + 上限 400 行）。 */
function kbLogLine(kind, msg) {
    const box = el('kbLog');
    if (!box) return;
    // 首次写入时清掉占位提示
    if (box.dataset.started !== '1') { box.innerHTML = ''; box.dataset.started = '1'; }

    const t = new Date().toTimeString().slice(0, 8);
    let cls = '', text = '';
    const lib = msg.libraryName || msg.libraryId || '';
    if (kind === 'progress') {
        const pct = msg.total > 0 ? ` ${msg.current}/${msg.total}` : '';
        text = `[${t}] ${lib} · ${msg.phase || ''}${pct}${msg.message ? ' — ' + msg.message : ''}`;
    } else if (kind === 'done') {
        const s = msg.summary || {};
        cls = 'ok';
        text = `[${t}] ✅ ${lib} 建库完成：${s.pages || '?'} 页 / ${s.chunks || '?'} 块 / ${formatNumber(s.chars || 0)} 字符`
             + (s.visionPages ? ` / 视觉解析 ${s.visionPages} 页` : '')
             + (s.manual ? `（${s.manual}）` : '');
    } else if (kind === 'error') {
        cls = 'err';
        text = `[${t}] ❌ ${lib} 建库失败：${msg.error || ''}`;
    } else {
        cls = 'head';
        text = `[${t}] ▶ ${msg.text || ''}`;
    }

    const d = document.createElement('div');
    d.className = 'kb-log-line' + (cls ? ' ' + cls : '');
    d.textContent = text;
    box.appendChild(d);
    while (box.children.length > 400) box.removeChild(box.firstChild);
    scrollToBottom(box, true);   // OK 只写不读，零强制重排（见 scrollToBottom 注释）
}

/** 手动触发建库时，先往日志里写一条起始行（让用户立刻看到"已开始"）。 */
function kbLogStart(text) { kbLogLine('head', { text }); }
async function openManualItem(libraryId, manualPath) {
    let found = null;
    for (const g of (chatState.manuals?.groups || [])) for (const l of g.libraries)
        for (const m of l.items) if (m.libraryId === libraryId && m.fullPath === manualPath) found = m;
    const name = found ? found.name : manualPath.split(/[\\/]/).pop();
    let page = 1, pages = 1, showImage = false;
    const mask = document.createElement('div');
    mask.className = 'modal-mask';
    mask.innerHTML = `
        <div class="modal manual-preview">
            <h3 style="display:flex;align-items:center;gap:10px">
                <span style="flex:1 1 auto;overflow:hidden;text-overflow:ellipsis">📖 ${escapeHtml(name)}</span>
                <button class="btn small" data-act="close">✕</button>
            </h3>
            <div class="mp-meta" data-meta>正在读取…</div>
            <div class="mp-toolbar">
                <button class="btn small" data-act="prev">‹ 上一页</button>
                <span class="mp-page" data-page></span>
                <button class="btn small" data-act="next">下一页 ›</button>
                <button class="btn small" data-act="image">看原页图</button>
            </div>
            <div class="mp-body" data-body><div class="hint-row">正在读取…</div></div>
            <div class="filters" style="justify-content:flex-end;padding-top:12px">
                <button class="btn" data-act="close">关闭</button>
                <button class="btn primary" data-act="build">建立知识库</button>
            </div>
        </div>`;
    document.body.appendChild(mask);
    const q = (sel) => mask.querySelector(sel);
    const close = () => mask.remove();
    async function load() {
        q('[data-meta]').textContent = '正在读取…';
        q('[data-body]').innerHTML = '<div class="hint-row">正在读取…</div>';
        try {
            const r = await bridge.call('manualPreview', { libraryId, path: manualPath, page, image: showImage });
            if (!r.ok) { q('[data-body]').innerHTML = `<div class="hint-row">${escapeHtml(r.message || '预览失败')}</div>`; return; }
            pages = r.pages || 1;
            const kb = found && found.kbBuilt ? '　·　**已建知识库**' : '';
            q('[data-meta]').innerHTML =
                `${escapeHtml(r.ext.toUpperCase())}　${formatBytes(r.sizeBytes)}　${r.kind === 'pdf' ? r.pages + ' 页' : '纯文本'}`
                + `　·　本页 ${formatNumber(r.chars)} 字符${kb}<br><span style="color:#8b93a3">${escapeHtml(r.isManualHint || '')}</span>`;
            q('[data-page]').textContent = r.kind === 'pdf' ? `第 ${page} / ${pages} 页` : '全文';
            q('[data-act="prev"]').disabled = r.kind !== 'pdf' || page <= 1;
            q('[data-act="next"]').disabled = r.kind !== 'pdf' || page >= pages;
            q('[data-act="image"]').disabled = r.kind !== 'pdf';
                // **渲染质量提示** —— Docnet 是精简版 PDFium、无系统字体回退：
                // 内嵌字体缺失时 PDFium 用灰方块占位（用户看到的「灰色色块」），
                // 白底白字或特殊颜色空间则渲染成一片空白。这里如实告知并引导看抽取文本。
                const warn = r.imageBlank
                    ? '<div class="mp-warn">⚠ 这一页的<b>原图渲染没有有效内容</b>：PDF 内嵌字体缺失时会被渲染成灰块，'
                      + '白底白字或特殊颜色空间则渲染成空白。<b>这不是你的文件坏了</b> —— 请点「看抽取文字」查看正文；'
                      + '建库用的也是抽取文本，不受影响。</div>'
                    : '';
            if (showImage && r.imageUrl) {
                q('[data-body]').innerHTML = warn + `<img class="mp-img" src="${escapeHtml(r.imageUrl)}" alt="第 ${page} 页" />`;
            } else if (r.text && r.text.trim()) {
                q('[data-body]').innerHTML = `<pre class="mp-text">${escapeHtml(r.text)}</pre>`;
            } else {
                q('[data-body]').innerHTML =
                    `<div class="hint-row">这一页**没有可抽取的文字**（多半是扫描/纯图片页）。` +
                    `点「看原页图」看渲染结果；建库时这类页面会交给视觉模型解析。</div>`;
            }
        } catch (e) {
            q('[data-body]').innerHTML = `<div class="hint-row">预览失败：${escapeHtml(e.message || e)}</div>`;
        }
    }
    mask.addEventListener('click', async (ev) => {
        if (ev.target === mask) { close(); return; }
        const b = ev.target.closest('[data-act]');
        if (!b) return;
        const act = b.dataset.act;
        if (act === 'close') { close(); return; }
        if (act === 'prev') { page = Math.max(1, page - 1); await load(); return; }
        if (act === 'next') { page = Math.min(pages, page + 1); await load(); return; }
        if (act === 'image') { showImage = !showImage; b.textContent = showImage ? '看抽取文字' : '看原页图'; await load(); return; }
        if (act === 'build') {
            close();
            const lib = (chatState.manuals?.groups || []).flatMap((g) => g.libraries).find((x) => x.id === libraryId);
            await doBuildKbFor(libraryId, found ? found.name : name, lib ? lib.libraryName : '');
        }
    });
    await load();
}
/** 为「某库的某本说明书」建知识库（预览弹窗底部按钮调用）。 */
async function doBuildKbFor(libraryId, manualName, libraryName) {
    kbLogStart('手动建库：' + manualName);
    try {
        const r = await bridge.call('kbBuildManual', { libraryId, path: manualName });
        if (r && r.ok === false) { toast(r.message || '建库失败', 'err'); return; }
        toast(`已开始为「${manualName}」建立知识库（后台进行，可在知识库面板看进度）`, 'ok');
    } catch (e) {
        toast('建库失败：' + (e && e.message ? e.message : e), 'err');
    }
}

async function loadChatMemories() {
    const box = el('cstMem');
    if (!box) return;
    box.innerHTML = '<div class="hint-row">正在读取记忆…</div>';
    let d;
    try { d = await bridge.call('memList', {}); }
    catch (e) { box.innerHTML = '<div class="hint-row">读取失败：' + escapeHtml(e.message) + '</div>'; return; }
    if (!d.total) {
        box.innerHTML = '<div class="hint-row">还没有长期记忆。<br>AI 会在对话中把值得记住的事写进来（如你的版本偏好、某库的结论），你也可以在对话里直接说「记住…」。</div>';
        return;
    }
    const tag = { global: '通用', library: '库', session: '临时' };
    box.innerHTML = d.items.map((m) => `
        <div class="mem-item" data-mem="${m.id}">
            <div class="mem-head">
                <span class="mem-tag">${escapeHtml(tag[m.scope] || m.scope)}</span>
                ${m.libraryName ? '<span class="mem-lib">' + escapeHtml(m.libraryName) + '</span>' : ''}
                <span class="mem-imp">★${m.importance}</span>
                <span class="mem-del" data-del="${m.id}" title="删除这条记忆">✕</span>
            </div>
            ${m.key ? '<div class="mem-key">' + escapeHtml(m.key) + '</div>' : ''}
            <div class="mem-body">${escapeHtml(m.content)}</div>
        </div>`).join('');

    box.querySelectorAll('[data-del]').forEach((x) => x.addEventListener('click', async (ev) => {
        ev.stopPropagation();
        await bridge.call('memDelete', { id: Number(x.dataset.del) });
        toast('已删除该条记忆', 'ok');
        loadChatMemories();
    }));
}
/** 批量建立知识库（顺序执行，可取消） */
async function startKbBatch() {
    const info = el('kbBatchInfo');
    const btnAll = el('btnKbBuildAll');
    const btnCancel = el('btnKbBatchCancel');
    try {
        const r = await bridge.call('kbBuildAll', { visionLimit: 12 });
        if (!r.ok) { toast(r.message || '无法开始', 'err'); return; }
        if (!r.total) { toast(r.message || '无需建立', 'ok'); return; }
        btnAll.disabled = true;
        btnCancel.hidden = false;
        info.hidden = false;
        info.textContent = '准备中… 共 ' + r.total + ' 个库';
        toast('开始批量建立 ' + r.total + ' 个知识库', 'ok');
    } catch (e) { toast('失败：' + e.message, 'err'); }
}

/** 处理批量建库进度事件 */
function onKbBatch(msg) {
    const info = el('kbBatchInfo');
    const btnAll = el('btnKbBuildAll');
    const btnCancel = el('btnKbBatchCancel');
    if (!info) return;
    if (msg.state === 'start') {
        info.hidden = false;
        info.textContent = '开始：共 ' + msg.total + ' 个库' + (msg.useVision ? '（每本最多解析 ' + msg.visionLimit + ' 页图片）' : '（仅文字抽取）');
        return;
    }
    if (msg.state === 'item') {
        info.textContent = '[' + msg.done + '/' + msg.total + '] 正在建：' + (msg.libraryName || '') + ' — ' + (msg.manualName || '');
        return;
    }
    if (msg.state === 'itemDone') {
        info.textContent = '[' + msg.done + '/' + msg.total + '] 完成：' + msg.pages + ' 页 / ' + msg.chunks + ' 块';
        return;
    }
    if (msg.state === 'itemFail') {
        info.textContent = '[' + msg.done + '/' + msg.total + '] 失败：' + (msg.error || '');
        return;
    }
    if (msg.state === 'cancelled') {
        info.textContent = '已取消：完成 ' + msg.ok + ' 个，失败 ' + msg.fail + ' 个（共 ' + msg.total + '）';
        btnAll.disabled = false; btnCancel.hidden = true;
        loadChatKbs();
        return;
    }
    if (msg.state === 'done') {
        let t = '全部完成：成功 ' + msg.ok + ' 个' + (msg.fail ? '，失败 ' + msg.fail + ' 个' : '') + '，耗时 ' + msg.seconds + 's';
        if (msg.failures && msg.failures.length) t += '（' + msg.failures.map((f) => f.name + ':' + f.error).join('；').slice(0, 120) + '）';
        info.textContent = t;
        btnAll.disabled = false; btnCancel.hidden = true;
        toast('批量建库完成：成功 ' + msg.ok + ' 个', 'ok');
        loadChatKbs();
    }
}

async function loadQuickLoadTargets() {
    try {
        const r = await bridge.call('quickLoadTargets', {});
        window.__qlTargets = new Set((r.paths || []).map((p) => String(p).replace(/[\\\\/]+$/, '')));
    } catch { window.__qlTargets = new Set(); }
}

async function loadChatKbs() {
    // **只更新 `#kbList`，绝不重写整个 `#cstKb`** ——
    // 旧写法 `box.innerHTML = ...` 会把 `#cstKb` 的【全部子元素】替换掉，
    // 连「批量建立」按钮栏和下方刚加的**建库日志窗口**一起删掉，
    // 表现就是「知识库面板下方什么都没有」（用户实测反馈）。
    const box = el('kbList');
    if (!box) return;
    box.innerHTML = '<div class="hint-row">正在读取知识库…</div>';
    let data;
    try { data = await bridge.call('kbAll', {}); }
    catch (e) { box.innerHTML = '<div class="hint-row">读取失败：' + escapeHtml(e.message) + '</div>'; return; }
    chatState.kbs = data;

    if (!data.total) {
        box.innerHTML = '<div class="hint-row">还没有任何知识库。<br>到「说明书」里选一本建立即可，建好后可反复复用。</div>';
        return;
    }
    box.innerHTML = data.items.map((k) => `
        <div class="cst-item" data-kb="${k.libraryId}">
            <span class="nm" title="${escapeHtml(k.libraryName)}">
                ${escapeHtml(k.libraryName)}
                <br><span class="sz">${escapeHtml(k.manualName || '')} · ${k.pageCount} 页 · ${formatNumber(k.totalChars)} 字${k.visionPages ? ' · 视觉 ' + k.visionPages + ' 页' : ''}</span>
            </span>
            <span class="kb yes">查看</span>
        </div>`).join('');

    box.querySelectorAll('[data-kb]').forEach((it) => it.addEventListener('click', () => viewKb(Number(it.dataset.kb))));
}

async function viewKb(libraryId) {
    let page = 0;
    const render = async () => {
        const d = await bridge.call('kbView', { libraryId, page, size: 40 });
        if (!d.ok) return '<div class="hint-row">' + escapeHtml(d.message) + '</div>';
        return `
            <div style="font-size:12.5px;line-height:1.85">
                <b>${escapeHtml(d.libraryName)}</b><br>
                <span class="meta">${escapeHtml(d.manualName || '')} · ${d.pageCount} 页 / ${formatNumber(d.totalChars)} 字 · 建于 ${escapeHtml(d.builtAt || '')}</span>
            </div>
            ${d.overview ? '<div style="margin-top:10px;padding:10px 12px;background:#12161c;border-radius:8px;font-size:12.5px;line-height:1.85;white-space:pre-wrap;max-height:220px;overflow:auto">' + escapeHtml(d.overview) + '</div>' : ''}
            <div style="margin-top:12px;font-size:12px;color:#7d8698">第 ${d.page + 1} / ${Math.max(1, d.totalPages)} 页</div>
            <div style="margin-top:8px;max-height:46vh;overflow:auto;font-size:12px;line-height:1.8">
                ${d.items.map((p) => '<div style="padding:8px 0;border-bottom:1px solid #1e232c"><span class="meta">第 ' + p.n + ' 页 · ' + p.source + '</span><div style="white-space:pre-wrap;margin-top:4px">' + escapeHtml(p.preview) + '</div></div>').join('')}
            </div>
            <div class="filters" style="margin-top:12px;justify-content:flex-end">
                <button class="btn small" id="kbPrev" ${d.page <= 0 ? 'disabled' : ''}>上一页</button>
                <button class="btn small" id="kbNext" ${d.page >= d.totalPages - 1 ? 'disabled' : ''}>下一页</button>
            </div>`;
    };

    const body = await render();
    showModal('知识库内容', body, { okText: '关闭', showCancel: false });
    const bind = () => {
        const p = el('kbPrev'), n = el('kbNext');
        if (p) p.addEventListener('click', async () => { page--; const h = await render(); el('modalBody').innerHTML = h; bind(); });
        if (n) n.addEventListener('click', async () => { page++; const h = await render(); document.querySelector('.modal-body').innerHTML = h; bind(); });
    };
    bind();
}

/* ── 会话 ── */
async function loadChatSessions() {
    const box = el('chatSessions');
    let data;
    try { data = await bridge.call('chatSessions', {}); }
    catch (e) { box.innerHTML = '<div class="hint-row">读取失败：' + escapeHtml(e.message) + '</div>'; return; }

    if (!data.total) { box.innerHTML = '<div class="hint-row">还没有会话</div>'; return; }
    box.innerHTML = data.groups.map((g, gi) => `
        <details class="sess-cat" ${gi === 0 ? 'open' : ''}>
            <summary>${escapeHtml(g.category)} <span class="cnt">${g.count}</span></summary>
            ${g.sessions.map((s) => `
                <div class="sess-item ${s.id === chatState.sessionId ? 'active' : ''}" data-sess="${s.id}">
                    <span class="t" title="${escapeHtml(s.title)}">${escapeHtml(s.title)}</span>
                    <span class="b">${s.messageCount} 条${s.branchCount > 1 ? ' · ' + s.branchCount + ' 分支' : ''}</span>
                </div>`).join('')}
        </details>`).join('');

    box.querySelectorAll('[data-sess]').forEach((it) =>
        it.addEventListener('click', () => openChatSession(Number(it.dataset.sess))));
}

async function createChatSession(libraryId, mode) {
    const r = await bridge.call('chatSessionCreate', { libraryId: libraryId || 0, mode });
    if (!r.ok) { toast(r.message || '创建会话失败', 'err'); return; }
    applyChatSession(r);
    await loadChatSessions();
}

async function openChatSession(sessionId) {
    const r = await bridge.call('chatSessionOpen', { sessionId });
    if (!r.ok) { toast(r.message || '打开会话失败', 'err'); return; }
    applyChatSession(r);
    await loadChatSessions();
}

function applyChatSession(r) {
    chatState.sessionId = r.session.id;
    chatState.session = r.session;
    chatState.branches = r.branches || [];
    chatState.branchId = r.activeBranchId;
    renderChatHeader();
    renderChatMessages(r.messages || []);
    syncAiStats();   // 会话级统计（从库里数，重启不归零）
}

function renderChatHeader() {
    const s = chatState.session;
    if (!s) return;
    el('chatTitle').textContent = s.title + (s.libraryName ? '　·　' + s.libraryName : '');
    el('btnChatRename').hidden = false;
    el('btnChatDelete').hidden = false;

    const br = chatState.branches.find((b) => b.id === chatState.branchId);
    const tag = el('chatModeTag');
    if (br) {
        tag.hidden = false;
        tag.className = 'chat-mode-tag ' + (br.mode === 'cross' ? 'cross' : 'single');
        tag.textContent = br.mode === 'cross' ? '跨库问答模式' : '单库问答模式';
    } else tag.hidden = true;

    el('chatBranches').innerHTML = chatState.branches.map((b) => `
        <span class="chat-branch ${b.id === chatState.branchId ? 'active' : ''}" data-br="${b.id}">
            ${escapeHtml(b.title || (b.mode === 'cross' ? '跨库' : '单库'))}<span class="m">${b.messageCount}</span>
        </span>`).join('') +
        `<span class="chat-branch" data-br-new="1">+ 跨库分支</span>`;

    el('chatBranches').querySelectorAll('[data-br]').forEach((x) =>
        x.addEventListener('click', () => switchChatBranch(Number(x.dataset.br))));
    el('chatBranches').querySelector('[data-br-new]')?.addEventListener('click', () => createCrossBranch(true));
}

async function switchChatBranch(branchId) {
    const r = await bridge.call('chatBranchSwitch', { sessionId: chatState.sessionId, branchId });
    if (r.ok) applyChatSession(r);
}

/** 在当前会话下新建跨库分支并切过去 */
async function createCrossBranch(announce) {
    if (!chatState.sessionId) return 0;
    const n = chatState.branches.filter((b) => b.mode === 'cross').length + 1;
    const r = await bridge.call('chatBranchCreate', {
        sessionId: chatState.sessionId,
        parentId: chatState.branches.find((b) => !b.parentId)?.id || 0,
        mode: 'cross',
        title: '跨库问答 #' + n,
    });
    if (!r.ok) { toast(r.message || '创建分支失败', 'err'); return 0; }
    await openChatSession(chatState.sessionId);
    if (announce) toast('已切换到跨库问答模式', 'ok');
    return r.branch.id;
}

/* ── 对话渲染 ── */
function renderChatMessages(msgs) {
    const box = el('chatMsgs');
    // OK 挂上「是否在底部」监听（只需一次）—— 让流式渲染不必读布局属性，
    //    从而避免每帧强制同步重排（见 scrollToBottom 的注释）。
    attachScrollWatcher(box);
    if (!msgs.length) {
        box.innerHTML = `<div class="chat-empty">
            <div class="chat-empty-title">开始提问</div>
            <div class="chat-empty-body">
                当前会话：<b>${escapeHtml(chatState.session?.title || '')}</b><br>
                单库模式只依据该库说明书回答；跨库模式可检索全部已建库的知识库。
            </div></div>`;
        return;
    }
    // 🔴 **只渲染最近 N 条（2026-09-24 修性能）** ——
    //   旧写法 `box.innerHTML = msgs.map(...).join('')` 会把【全部历史】拼成一个巨型 HTML 串，
    //   实测用户有 81 条消息的会话 ⇒ 每次打开都要解析一大坨 HTML ⇒ **卡**（用户实测反馈：
    //   「当对话变长时，界面始终加载并显示很长的上文，这会导致界面变卡」）。
    //   ⇒ 默认只渲染最近 30 条；更早的用「加载更早的 N 条」按钮按需追加（不一次全上）。
    const RECENT = 30;
    const skip = Math.max(0, msgs.length - RECENT);
    const shown = msgs.slice(skip);
    const earlierBtn = skip > 0
        ? `<div class="chat-earlier"><button class="btn small" id="btnChatEarlier">加载更早的 ${skip} 条</button></div>`
        : '';
    box.innerHTML = earlierBtn + shown.map((m) => renderMsg(m.role, m.content, m.reasoning)).join('');
    // 🔴 **重建后补回试听卡（2026-09-26）** —— 它是客户端临时状态，不补就丢了。
    //   ⚠️ **要补回【所有批次】** —— 一轮里 Agent 可能调 audition 多次
    //      （实测「找 5 个音色并试听」= 5 组）；只补最后一批会让 5 组塌成 1 组。
    try {
        const batches = Array.isArray(chatState._auditionBatches) ? chatState._auditionBatches : [];
        for (const b of batches) {
            if (Array.isArray(b) && b.length) renderAudition(b, true);   // true = 恢复模式，不再累积
        }
        // 兼容：若没有累积数组但有旧的单批，也补上
        if (batches.length === 0 && Array.isArray(chatState._lastAudition) && chatState._lastAudition.length) {
            renderAudition(chatState._lastAudition, true);
        }
    } catch (e) { }
    chatState._earlierSkip = skip;
    chatState._earlierMsgs = skip > 0 ? msgs.slice(0, skip) : [];
    if (el('btnChatEarlier')) el('btnChatEarlier').addEventListener('click', () => {
        const btn = el('btnChatEarlier');
        const wrap = btn ? btn.parentElement : null;
        const older = chatState._earlierMsgs || [];
        if (older.length === 0) return;
        const html = older.map((m) => renderMsg(m.role, m.content, m.reasoning)).join('');
        if (wrap) { wrap.remove(); }
        box.insertAdjacentHTML('afterbegin', html);
        chatState._earlierMsgs = [];
        scrollToBottom(box, true);   // OK 只写不读，零强制重排（见 scrollToBottom 注释）
    });
    scrollToBottom(box, true);   // OK 只写不读，零强制重排（见 scrollToBottom 注释）
}

/* ══════════ 流式渲染性能（rAF 合并 + 增量追加）══════════
   背景：旧实现**每个 chatDelta 都整段重渲染 Markdown**、**每个 chatReasoning 都
   重写整个 textContent** —— 都是 O(n²)，再叠加高频事件就足以让界面卡顿。
   做法：① 正文用 requestAnimationFrame 合并，每帧最多渲染一次；
        ② 思考文本**只追加新片段**（appendChild 文本节点，O(1)），不再重写全文。 */
/* ══════════════════════════════════════════════════════════
   流式消息：**状态驱动渲染**（重构）
   ----------------------------------------------------------
   旧实现把流式状态存在 DOM 指针里（chatState._curBubble / _curThink），
   一旦元素被重绘/替换/短暂为 null，后续所有事件就静默丢失 ——
   表现为「看不到思考、看不到回答、切会话才看到结果」。

   新实现照搬成熟 Agent 的通用做法：
     · 流式消息是一份**纯数据**（chatState.live）；
     · 事件只改数据，不改 DOM；
     · 渲染函数**每次按 id 查元素**（#liveMsg），不缓存任何指针；
     · 用 requestAnimationFrame 合并渲染，每帧最多一次。
   这样无论 DOM 怎么变，渲染总能找到正确的落点。
   ══════════════════════════════════════════════════════════ */

const LIVE_ID = 'liveMsg';

/** 流式消息的 HTML（含 id，渲染时按 id 定位） */
/* ══════════════════════════════════════════════════════════
   内嵌试听播放器
   ----------------------------------------------------------
   Agent 调用 `audition` 工具后，后端会把可播放的采样清单推过来
   （type = chatAudition），这里把它渲染成**对话内的播放器列表**，
   用户直接点播即可 —— 不用切到别的软件。
   ══════════════════════════════════════════════════════════ */

function renderAudition(items, fromRestore) {
    const box = el('chatMsgs');
    // 🔴 诊断日志（2026-09-26）：确认事件到达情况
    try { bridge.call('clientLog', { kind: 'audition', message: 'renderAudition 被调用 items=' + (Array.isArray(items) ? items.length : 'notArray') + ' box=' + !!box + ' restore=' + !!fromRestore, source: 'renderAudition', stack: '' }); } catch (e) { }
    if (!box || !Array.isArray(items) || items.length === 0) return;
    // 🔴 记住这一批试听项（2026-09-26）
    //   这个卡是【客户端临时渲染】的（服务端消息里没有），
    //   任何一次 renderChatMessages() 重建都会抹掉它。
    //   ⚠️ **必须累积所有批次** —— 一轮里 Agent 可能调 audition 多次
    //   （实测「找 5 个音色并试听」会按库各调一次 ⇒ 5 组播放器）；
    //   旧实现只存最后一批 ⇒ 重建后 5 组变 1 组、且是相关性最低的那组（用户实测发现）。
    //   ⚠️ **恢复调用（fromRestore）不能再累积** —— 否则每重建一次就翻倍。
    if (!fromRestore) {
        if (!Array.isArray(chatState._auditionBatches)) chatState._auditionBatches = [];
        chatState._auditionBatches.push(items.slice());
        chatState._lastAudition = items.slice();   // 兼容旧引用
    }

    const wrap = document.createElement('div');
    wrap.className = 'audition-card';
    // **一行两个 + 只显示文件名（去后缀）** ——
    // 用户反馈：每个音频占一行太占地方；后缀（.ncw/.wav/.mp3）对辨认采样没有帮助。
    const stripExt = (s) => String(s || '').replace(/\.[A-Za-z0-9]{1,5}$/, '');
    wrap.innerHTML = '<div class="au-head">🎧 试听采样（点击播放）</div>' +
        '<div class="au-grid">' +
        items.map((it, i) => `
            <div class="au-item">
                <button class="au-play" data-idx="${i}" title="播放/暂停">▶</button>
                <div class="au-info">
                    <div class="au-name" title="${escapeHtml(it.name || '')}">${escapeHtml(stripExt(it.name))}</div>
                    <div class="au-meta">${it.sizeMB != null ? it.sizeMB + ' MB' : ''}</div>
                </div>
                <audio preload="none" data-idx="${i}" src="${escapeHtml(it.url || '')}"></audio>
            </div>`).join('') +
        '</div>';
    box.appendChild(wrap);
    scrollToBottom(box, true);   // OK 只写不读，零强制重排（见 scrollToBottom 注释）

    // 事件委托：同一时刻只播一个
    const audios = [...wrap.querySelectorAll('audio')];
    const btns = [...wrap.querySelectorAll('.au-play')];
    const stopAll = (except) => {
        audios.forEach((a) => { if (a !== except) { a.pause(); a.currentTime = 0; } });
        btns.forEach((b) => { b.textContent = '▶'; b.classList.remove('playing'); });
    };
    wrap.addEventListener('click', (ev) => {
        const btn = ev.target.closest('.au-play');
        if (!btn) return;
        const idx = Number(btn.dataset.idx);
        const audio = audios[idx];
        if (!audio) return;
        if (audio.paused) {
            stopAll(audio);
            audio.play().then(() => {
                btn.textContent = '⏸';
                btn.classList.add('playing');
            }).catch((e) => {
                console.warn('播放失败', e);
                btn.textContent = '⚠';
            });
        } else {
            audio.pause();
            btn.textContent = '▶';
            btn.classList.remove('playing');
        }
    });
    audios.forEach((a, i) => {
        a.addEventListener('ended', () => {
            btns[i].textContent = '▶';
            btns[i].classList.remove('playing');
        });
    });
}
function renderLiveMsg() {
    return `<div class="msg assistant" id="${LIVE_ID}">
        <div class="who">AI</div>
        <details class="think" hidden><summary>思考过程</summary><div class="think-body"></div></details>
        <div class="tools" hidden></div>
        <div class="bubble"></div>
    </div>`;
}

/* ══════════ 步骤切块（对齐 DSH / OpenClaw 的渲染方式）══════════ */

/**
 * **把当前 live 块「冻结」，再开一个新的 live 块**。
 *
 * **为什么需要**（用户的关键观察）：
 *   原来【整轮对话只用一个 live 块】—— 思考、工具、正文全堆在同一个 DOM 节点里。
 *   这同时造成 4 个问题：
 *     ① **思考框无限增长** ⇒ 每帧重绘都要碰一个巨大节点 ⇒ **越跑越卡、打字都卡**
 *     ② **正文气泡只在最后才填**（中间步骤只产思考）⇒ **正文长时间空白**
 *     ③ **看不出进度**（工具/思考混在一起）
 *     ④ **插话只能追加到最后**，阅读上不连贯
 *   ⇒ 参照 DSH/OpenClaw：**每步一个独立块**（思考一块、工具一块、正文一块）。
 *
 * **切块时机**：后端已经推 `chatStep` 事件（每轮工具循环的边界）⇒ 收到就切。
 */
function sealLiveBlock() {
    const node = document.getElementById(LIVE_ID);
    const live = chatState.live;
    if (!node || !live) return;

    // ① 若这一块【什么都没产出】（既无思考也无正文也无工具），直接删掉（避免空块）
    const hasAny = (live.reasoning && live.reasoning.trim())
                || (live.content && live.content.trim())
                || (live.tools && live.tools.length);
    if (!hasAny) { node.remove(); return; }

    // ② 冻结：去掉 live id（后续 paintLive 不再碰它），正文做一次完整 markdown
    node.removeAttribute('id');
    const bubble = node.querySelector('.bubble');
    if (bubble && live.content) {
        bubble.innerHTML = renderMarkdown(live.content);
    }
    // 思考过程折叠起来（历史块默认收起，让版面清爽）
    const think = node.querySelector('.think');
    if (think && live.reasoning) think.open = false;
    // 标记为历史块
    node.classList.add('sealed');
}

/** **开一个新的 live 块**（并重置 live 数据）。 */
function openLiveBlock() {
    const box = el('chatMsgs');
    if (!box) return;
    box.insertAdjacentHTML('beforeend', renderLiveMsg());
    chatState.live = { content: '', reasoning: '', tools: [], done: false };
    scrollToBottom(box, true);   // OK 只写不读，零强制重排（见 scrollToBottom 注释）
}

/** 渲染流式消息（从数据到 DOM，每帧最多一次） */
function paintLive() {
    const live = chatState.live;
    const node = document.getElementById(LIVE_ID);
    if (!node || !live) return;

    // 🔴 **缓存子元素引用（2026-09-25 性能优化）** ——
    //   原来每帧做 4~5 次 `querySelector`；rAF 每 80ms 一次虽不算多，
    //   但配合强制重排会放大开销。⇒ **只在第一次查，之后复用**。
    if (!node._think) {
        node._think = node.querySelector('.think');
        node._tbody = node.querySelector('.think-body');
        node._tools = node.querySelector('.tools');
        node._bubble = node.querySelector('.bubble');
    }
    // 思考过程
    const think = node._think;
    const tbody = node._tbody;
    if (think && tbody) {
        if (live.reasoning) {
            think.hidden = false;
            think.open = true;
            // 🔴 **增量追加（2026-09-24 修性能）** ——
            //   旧写法 `tbody.textContent = live.reasoning` 是【全量替换】= O(n)；
            //   每 80ms 重绘一次 ⇒ **O(n²)**。实测思考动辄几万字（甚至有 82 次重复的极端情况），
            //   ⇒ 「文字一卡一卡的、连打字输入都卡」（用户实测反馈）。
            //   ⇒ 改为【只 append 新增那一段】（O(增量)）；非追加场景退回全量替换。
            {
                const prevR = tbody._rsn || '';
                if (live.reasoning.length > prevR.length && live.reasoning.startsWith(prevR)) {
                    tbody.appendChild(document.createTextNode(live.reasoning.slice(prevR.length)));
                } else if (prevR !== live.reasoning) {
                    tbody.textContent = live.reasoning;
                }
                tbody._rsn = live.reasoning;
            }
        } else {
            think.hidden = true;
        }
    }

    // 工具调用轨迹（每轮动作对用户可见 —— 对标成熟 Agent 的呈现）
    const tools = node._tools;
    if (tools) {
        if (live.tools && live.tools.length) {
            tools.hidden = false;
            // 🔴 **只在工具列表【真的变了】时重建 innerHTML（2026-09-25 性能优化）** ——
            //   原来每帧都 `innerHTML = ...` 重建整串（含 escapeHtml 转义），
            //   而工具列表在两步之间其实是不变的 ⇒ 白做。
            //   用「数量 + 最后一个的完成态」当签名，够用且极廉价。
            const last = live.tools[live.tools.length - 1];
            const sig = live.tools.length + ':' + (last && last.done ? '1' : '0');
            if (tools._sig !== sig) {
                tools._sig = sig;
                tools.innerHTML = live.tools
                    .map((t) => `<span class="tool-chip${t.done ? ' done' : ''}">${t.done ? '✓' : '⋯'} ${escapeHtml(t.label)}</span>`)
                    .join('');
            }
        } else if (!tools.hidden) {
            tools.hidden = true;
            tools._sig = '';
        }
    }

    // 正文
    //
    // **流式期间只用纯文本，不做 markdown 解析** —— 这是「丝滑度」的关键。
    // 旧写法每帧 `innerHTML = renderMarkdown(整段)`：markdown 解析是 O(n)、
    // 流式期间累计就是 O(n²)，3000 字的回答每帧要重解析几百万字符操作 ⇒
    // 表现为「文字一顿一顿、结尾卡住不响应」（用户反馈：与 DSH 等成熟 Agent 差距明显）。
    // 现在：流式期间 `textContent = 原文`（浏览器原生、增量、几乎零成本），
    // 只有 `chatDone` 时才把 `live.final = true` 并做一次完整 markdown 渲染。
    const bubble = node._bubble;   // ✅ 用缓存（见上方注释）
    if (bubble && live.content !== undefined) {
        if (live.final) {
            if (bubble._raw !== live.content) {
                bubble._raw = live.content;
                bubble.innerHTML = renderMarkdown(live.content);
            }
        } else if (bubble._text !== live.content) {
            // 纯文本流式：textContent 替换不触发 HTML 解析，且滚动更平滑
            // 🔴 **增量追加（2026-09-24 修性能）** ——
            //   旧写法 `bubble.textContent = live.content` 是【全量替换】= O(n)，
            //   每 80ms 一次 ⇒ **O(n²)**（回答越长越卡）。
            //   ⇒ 只 append 新增部分；非追加场景全量替换。
            {
                const prevC = bubble._text || '';
                if (live.content.length > prevC.length && live.content.startsWith(prevC)) {
                    bubble.appendChild(document.createTextNode(live.content.slice(prevC.length)));
                } else if (prevC !== live.content) {
                    bubble.textContent = live.content;
                }
                bubble._text = live.content;
            }
        }
        // **工具调用阶段的「中间过程」占位** ——
        // 模型在调工具的那几步只产出思考、不产出正文，此时 `.bubble` 是空的，
        // 但节点仍在（CSS 给它最小高度），表现就是「文本块不断变大、里面什么内容都没有」
        //（用户实测反馈）。这里在正文为空时把当前动作显示出来，让那块地方有信息量：
        //   · 有工具在跑 → 「正在调用 xxx…」
        //   · 只有思考   → 显示最后一段思考（让用户看到模型在想什么）
        if (!live.final && !live.content) {
            const running = (live.tools || []).filter((t) => !t.done).pop();
            const lastThink = (live.reasoning || '').trim().split('\n').filter(Boolean).pop() || '';
            const hint = running ? `正在调用 ${running.label}…`
                : lastThink ? lastThink
                : '正在思考…';
            if (bubble._pending !== hint) {
                bubble._pending = hint;
                bubble.innerHTML = `<div class="bubble-pending">${escapeHtml(hint)}</div>`;
            }
        } else if (bubble._pending) {
            bubble._pending = '';
        }
    }

    const box = el('chatMsgs');
    // 🔴 **修「打字卡」的头号元凶：强制同步布局（2026-09-25）** ——
    //   旧写法 `box.scrollTop = box.scrollHeight` 会【读 scrollHeight】，
    //   而读布局属性会强制浏览器【立刻同步重算整个容器的布局】；
    //   在 rAF 里每帧一次 ⇒ 典型的 layout thrashing。
    //   用户实测：**Agent 输出时打字会卡** —— 因为这次强制重排把输入框的布局也拖累了。
    //   ✅ 改用 `1e9`：赋值一个远超实际高度的值，浏览器会自动夹到最大值，
    //      **全程不读取任何布局属性 ⇒ 零强制重排**。
    //   另外：**只在用户本来就在底部时才自动跟随**，否则不打扰他翻阅历史。
    scrollToBottom(box);
}

/**
 * **滚到底部（不触发强制同步布局）**。
 *
 * 关键点：**绝不读 `scrollHeight` 这类布局属性** —— 读它会强制浏览器同步重排，
 * 在流式输出的 rAF 循环里每帧一次，就会让整个界面（含输入框）卡顿。
 * 用 `scrollTop = 1e9` 让浏览器自己夹到最大滚动位置，**只写不读**。
 *
 * @param {HTMLElement} box 滚动容器
 * @param {boolean} force 为 true 时无条件滚到底（如刚插入自己的发言）
 */
function scrollToBottom(box, force) {
    if (!box) return;
    // ✅ 只查缓存（不读任何布局属性）—— 见函数上方注释
    if (!force && !box._atBottom) return;
    box.scrollTop = 1e9;   // ✅ 只写不读 ⇒ 零强制重排
    box._atBottom = true;
}

/**
 * 给滚动容器挂上「是否在底部」的监听（只需挂一次）。
 * 滚动/窗口变化时更新缓存 —— 那时读布局不影响流式渲染性能。
 */
function attachScrollWatcher(box) {
    if (!box || box._scrollWatched) return;
    box._scrollWatched = true;
    const update = () => {
        // 容差 120px：接近底部就认为要跟随
        box._atBottom = (box.scrollHeight - box.scrollTop - box.clientHeight) < 120;
    };
    box.addEventListener('scroll', update, { passive: true });
    window.addEventListener('resize', update, { passive: true });
    update();
}

let livePaintPending = false;
let livePaintLast = 0;
const LIVE_PAINT_MIN_MS = 80;   // 最快每 80ms 重绘一次

/**
 * 合并渲染：rAF + 时间下限双保险。
 *
 * 为什么需要时间下限：长回答时 renderMarkdown 是 O(n)，
 * 每帧都重解析整段文本就是 O(n²) —— 表现为「回答越长越卡」。
 * 80ms 的下限让重绘频率与文本长度解耦，视觉上仍是「流式涌现」。
 */
function scheduleLivePaint() {
    if (livePaintPending) return;
    livePaintPending = true;
    const run = () => {
        livePaintPending = false;
        const now = performance.now();
        const wait = LIVE_PAINT_MIN_MS - (now - livePaintLast);
        if (wait > 0) { setTimeout(scheduleLivePaint, wait); return; }
        livePaintLast = now;
        try { paintLive(); } catch (e) { console.warn('paintLive 失败', e); }
    };
    requestAnimationFrame(run);
}

/** 把工具名转成对用户友好的说明 */
function toolLabel(name, argsJson) {
    const M = {
        query_libraries: '查询音色库', query_instruments: '查询乐器', get_library_info: '读取库信息',
        list_directory: '浏览目录', read_text_file: '读取文件', search_manual: '检索说明书',
        search_kontakt_doc: '检索 Kontakt 文档', web_search: '联网搜索', web_fetch: '抓取网页',
        run_command: '执行命令', run_script: '运行脚本', remember: '记住', recall: '回忆',
        update_plan: '更新计划', find_audition: '找试听', suggest_cross_library: '建议跨库',
        ui_list_windows: '列出窗口', ui_dump_tree: '读取控件树', ui_find: '查找控件',
        ui_get_state: '读取控件状态', ui_click: '点击界面', ui_type: '输入文本', ui_key: '发送按键',
        ui_scroll: '滚动界面', ui_screenshot: '截图', ui_describe_window: '视觉识别窗口',
        kontakt_ui_state: '读取 Kontakt 界面', kontakt_classic_search: '搜索音色库',
        kontakt_classic_load: '加载音色库', kontakt_set_view: '切换视图', kontakt_search: 'Kontakt 搜索',
        kontakt_switch_ui_mode: '切换界面模式',
    };
    let label = M[name] || name;
    try {
        const a = JSON.parse(argsJson || '{}');
        const hint = a.query || a.keyword || a.name || a.path || a.library || a.url || a.command;
        if (hint) label += '：' + String(hint).slice(0, 40);
    } catch { }
    return label;
}
let streamRafPending = false;


    function renderMsg(role, content, reasoning, image) {
        const isUser = role === 'user';
        const who = isUser ? '你' : 'AI';
        // ⚠ **assistant 消息必须始终产出 `.think` 元素**（哪怕此刻还没有思考内容）。
        //    旧实现在 reasoning 为空时返回空串，导致 sendChat 里
        //    `msgEl.querySelector('.think')` 拿到 null、`chatState._curThink` 为 null，
        //    于是后续所有 chatReasoning 事件都被 `if (chatState._curThink)` 挡掉
        //    —— 这就是「看不到思考过程」的根因（用户实测报告）。
        const think = isUser ? ''
            : `<details class="think"${reasoning ? ' open' : ' hidden'}><summary>思考过程</summary><div class="think-body">${escapeHtml(reasoning || '')}</div></details>`;
        return `<div class="msg ${isUser ? 'user' : 'assistant'}">
            <div class="who">${who}</div>
            ${think}
            <div class="bubble">${renderMarkdown(content)}${image ? '<img class="msg-img" src="' + image + '" alt="附图" />' : ''}</div>
        </div>`;
    }

/** 轻量 Markdown：粗体 / 行内代码 / 标题 / 列表 */
/**
 * 轻量 Markdown 渲染（够用即可，不引入外部库 —— 界面要求完全离线）。
 * 支持：标题、表格、有序/无序列表、引用、分割线、粗体、行内代码。
 * 先整体转义 HTML 再按行解析，避免注入。
 */

/* ── 发送 ── */
async function sendChat() {
    const dbg = (why) => { try { bridge.call('clientLog', { kind: 'sendChat', message: why, source: 'sendChat', stack: '' }); } catch { } };
    // **Agent 正在工作时**：这句话是【插话引导】而不是新回合（对标 DSH 的 steering 交互）。
    if (chatState.streaming) { dbg('流式中 → 作为插话引导'); return steerChat(); }
    const input = el('chatInput');
    if (!input) { dbg('早退：找不到 #chatInput'); return; }
    const q = input.value.trim();
    if (!q) { dbg('早退：输入为空'); return; }
    if (!chatState.sessionId) {
        dbg('早退：chatState.sessionId 为空');
        toast('请先新建或选择一个会话', 'err');
        return;
    }
    dbg('开始发送：' + q.slice(0, 60));
    input.value = '';
    // 状态行：新一轮 → 轮次 +1、步数清零
    aiStatsNewTurn();
    // 🔴 **新一轮开始：清空上一轮的试听卡累积（2026-09-26）** ——
    //   否则跨轮累积、旧卡会一直跟着新对话出现。
    chatState._auditionBatches = [];
    chatState._lastAudition = null;
    const sentImage = chatImage;
    clearChatImage();
    chatState.streaming = true;
    setChatSendMode(true);           // 按钮变成「停止」，用户可随时打断
    // 🔴 **不要再禁用（2026-09-24 修 Bug 8）** —— setChatSendMode(true) 已把按钮设成「停止」且可用；
    //   这一行紧接着又把它 disabled=true ⇒ **「停止」按钮点不动**（用户实测反馈）。
    // el('btnChatSend').disabled = true;

    const box = el('chatMsgs');
    if (box.querySelector('.chat-empty')) box.innerHTML = '';
    box.insertAdjacentHTML('beforeend', renderMsg('user', q, '', sentImage));
    // 状态驱动的流式消息：数据在 chatState.live，DOM 只是一个带 id 的落点
    box.insertAdjacentHTML('beforeend', renderLiveMsg());
    chatState.live = { content: '', reasoning: '', tools: [], done: false };
    scrollToBottom(box, true);   // OK 只写不读，零强制重排（见 scrollToBottom 注释）


       try {
           await bridge.call('chatAsk', { sessionId: chatState.sessionId, branchId: chatState.branchId, question: q, image: sentImage, thinkingEffort: chatState.thinkEffort });
           // ⚠ **这里绝不能判断 chatState.streaming 并收尾**：
           //    chatAsk 是**异步 RPC** —— 它立刻返回 {ok:true}，而 Agent 还在后台跑，
           //    真正的流式内容（思考 / 工具调用 / 每轮回复）全靠事件推送过来。
           //    旧实现在这里调 finishChatStream() 会把 chatState._curBubble 置为 null，
           //    于是**后续所有 chatDelta / chatReasoning 事件都因 _curBubble 为 null 被静默丢弃**
           //    —— 这就是「看不到思考、看不到首轮回复、切会话才看到结果」的根因（用户实测报告）。
           //    正确的收尾时机是 chatDone / chatError 事件。
       } catch (e) {
           // 只有 RPC 本身失败（参数错、桥接异常）才在这里收尾
           if (chatState.live) chatState.live.content = '请求失败：' + e.message;
           paintLive();
           finishChatStream();
           return;
       }
       // 兜底：若 chatDone 长时间没来（推送丢失 / 模型卡死），才主动收尾，避免发送键永久禁用。
    // 兜底：若 chatDone 长时间没来（推送丢失 / 模型卡死），才主动收尾，避免发送键永久禁用。
    clearTimeout(chatState._streamWatchdog);
    chatState._streamWatchdog = setTimeout(() => {
        if (!chatState.streaming) return;
        if (chatState.live && !chatState.live.content)
            chatState.live.content = '（长时间未收到回复。Agent 可能仍在运行，可稍后切换会话查看结果。）';
        paintLive();
        finishChatStream();
    }, 10 * 60 * 1000);
}

/* ══════════ 音色地图（独立工作台）═════════ */

/**
 * 音色地图状态。
 * **设计原则：所有重活都在【后台线程】**（C# 侧 Task.Run），
 * 前端只负责「显示进度」与「画图」—— 这样 86 分钟的分析不会卡死 UI。
 */
let mapState = {
    inited: false,
    running: false,
    // 数据
    points: [],          // [{x,y,clipId,lib,file,cluster}]
    colorBy: 'cluster',
    level: 'sample',        // sample | instrument（采样级 / 乐器级）
    instrumentPoints: [],
    // 视图
    scale: 1, offsetX: 0, offsetY: 0,
    hover: -1,
    // 🔴 **两个概念必须分开**（之前混用同一个字段，导致「点过点之后三个按钮全死」）：
    selected: [],          // 【框选】的多个点索引 —— **永远是数组**
    clicked: -1,           // 【单击】选中的单个点索引 —— 用于高亮与详情
    pollTimer: 0,          // 运行中的状态轮询定时器
    audioFeatTimer: 0,     // 档 1 声学特征提取的轮询定时器
    audioFeatBucket: -1,   // 日志粒度桶（每 10% 记一条）
    // ── 音色筛选（可解释维度滑杆）──
    filterVals: {},        // key -> 0..1 阈值（0 = 不限）
    filterHits: null,      // 命中的 clipId 集合（Set）；null = 未筛
    filterTimer: 0,        // 防抖定时器
    filterStrength: 0.92,  // 未命中点的压暗强度（0=不压暗，1=完全隐藏）
    hitCycle: 0,           // 「定位命中点」的循环索引
    semHits: null,         // 语义命中的 clipId 集合（null = 未筛）
    semTimer: 0,           // 语义输入防抖
    timbreValues: null,    // Map<clipId, [p0..p5]>（按特征着色用；懒加载）
    timbreDimsOrder: [],   // 维度顺序（与后端一致）
    // ── 框选（拖拽矩形选点）──
    sel: null,             // {x0,y0,x1,y1} 屏幕坐标
    selected: [],          // 被框选中的点索引
    playing: false,        // 批量试听进行中
    clusterMeta: [],       // 簇元信息（规模/主要库/代表样本）
    highlightCluster: -1,  // 图例里点中的簇（-1 = 不高亮）
    // 渲染
    raf: 0,
};

/** 进入标签页时：初始化 + 拉一次状态。 */
function mapOnEnter() {
    if (!mapState.inited) { mapInit(); mapState.inited = true; }
    mapRefreshStatus();
}

/** 初始化：绑按钮、绑 Canvas 事件、拉一次覆盖率。 */
function mapInit() {
    const c = el('mapCanvas');
    if (c) {
        c.addEventListener('mousemove', mapOnMove);
        c.addEventListener('mousedown', mapSelStart);
        c.addEventListener('click', mapOnClick);
        c.addEventListener('wheel', mapOnWheel, { passive: false });
        c.addEventListener('mouseleave', () => { mapState.hover = -1; mapScheduleDraw(); });
        // 框选按钮绑定
        if (el('btnSelPlayAll')) el('btnSelPlayAll').addEventListener('click', mapPlaySelection);
        if (el('btnSelTag')) el('btnSelTag').addEventListener('click', mapTagSelection);
        if (el('btnTagOk')) el('btnTagOk').addEventListener('click', mapTagCommit);
        if (el('btnMertOk')) el('btnMertOk').addEventListener('click', () => {
            const th = Math.max(1, Math.min(256, Number(el('mertThreadsInput') ? el('mertThreadsInput').value : 0) || 1));
            const force = !!(el('mertForceChk') && el('mertForceChk').checked);
            const cb = _mertDialogCb;
            closeMertRunDialog();
            if (cb) cb(false, th, force);
        });
        if (el('btnMertCancel')) el('btnMertCancel').addEventListener('click', closeMertRunDialog);
        if (el('mertRunModal')) el('mertRunModal').addEventListener('click', (e2) => {
            if (e2.target.id === 'mertRunModal') closeMertRunDialog();
        });
        if (el('btnCpuOk')) el('btnCpuOk').addEventListener('click', () => {
            const v = Math.max(1, Math.min(256, Number(el('cpuThreadsInput') ? el('cpuThreadsInput').value : 0) || 1));
            const cb = _cpuDialogCb;
            const force = !!(el('cpuForceChk') && el('cpuForceChk').checked);
            closeCpuDialog();
            if (cb) cb(v, force);
        });
        if (el('btnCpuCancel')) el('btnCpuCancel').addEventListener('click', closeCpuDialog);
        if (el('cpuThreadsInput')) el('cpuThreadsInput').addEventListener('keydown', (e2) => {
            if (e2.key === 'Enter' && el('btnCpuOk')) el('btnCpuOk').click();
        });
        if (el('cpuThreadsModal')) el('cpuThreadsModal').addEventListener('click', (e2) => {
            if (e2.target.id === 'cpuThreadsModal') closeCpuDialog();
        });
        if (el('btnTagCancel')) el('btnTagCancel').addEventListener('click', () => { const m2 = el('tagInputModal'); if (m2) m2.hidden = true; });
        if (el('btnInputDialogOk')) el('btnInputDialogOk').addEventListener('click', () => closeInputDialog(true));
        if (el('btnInputDialogCancel')) el('btnInputDialogCancel').addEventListener('click', () => closeInputDialog(false));
        if (el('inputDialogText')) el('inputDialogText').addEventListener('keydown', (e2) => {
            if (e2.key === 'Enter') { e2.preventDefault(); closeInputDialog(true); }
            if (e2.key === 'Escape') { e2.preventDefault(); closeInputDialog(false); }
        });
        if (el('tagInputText')) el('tagInputText').addEventListener('keydown', (e2) => { if (e2.key === 'Enter') mapTagCommit(); });
        if (el('tagInputModal')) el('tagInputModal').addEventListener('click', (e2) => { if (e2.target.id === 'tagInputModal') el('tagInputModal').hidden = true; });
        if (el('btnSelExport')) el('btnSelExport').addEventListener('click', mapExportSelection);
        if (el('btnSelClear')) el('btnSelClear').addEventListener('click', mapClearSelection);
        // **导出合并成一个按钮 → 弹窗选格式**（用户要求）
        if (el('btnMapExport')) el('btnMapExport').addEventListener('click', mapOpenExportModal);
        if (el('btnExportAsPng')) el('btnExportAsPng').addEventListener('click', () => { mapCloseExportModal(); mapExportPng(); });
        if (el('btnExportAsCsv')) el('btnExportAsCsv').addEventListener('click', () => { mapCloseExportModal(); mapExportCsv(); });
        if (el('btnExportCancel')) el('btnExportCancel').addEventListener('click', mapCloseExportModal);
        if (el('mapExportModal')) el('mapExportModal').addEventListener('click', (e2) => { if (e2.target.id === 'mapExportModal') mapCloseExportModal(); });
        new ResizeObserver(() => mapResize()).observe(c.parentElement);
    }
    if (el('btnMapStart')) el('btnMapStart').addEventListener('click', mapStart);
    if (el('btnMapPause')) el('btnMapPause').addEventListener('click', mapPause);
    if (el('btnMapRebuild')) el('btnMapRebuild').addEventListener('click', mapRebuild);
    if (el('btnMapReset')) el('btnMapReset').addEventListener('click', mapResetView);
    if (el('mapColorBy')) el('mapColorBy').addEventListener('change', async (e) => {
        mapState.colorBy = e.target.value;
        // **按可解释特征着色需要先拿数据**（懒加载，只取一次）
        const TIMBRE = ['brightness','highFreq','noisiness','roughness','loudness','punch'];
        if (TIMBRE.includes(mapState.colorBy) && !mapState.timbreValues) {
            await mapLoadTimbreValues();
        }
        mapScheduleDraw();
    });
    if (el('mapLevel')) el('mapLevel').addEventListener('change', (e) => {
        mapState.level = e.target.value;
        mapState.clicked = -1;
        mapApplyPoints();
        mapLog(e.target.value === 'instrument'
            ? '已切到【乐器级】—— 每个点是一个乐器（按目录聚合），更符合「找相似音色」的直觉'
            : '已切到【采样级】—— 每个点是一个音频片段');
    });
    if (el('btnMapInstruments')) el('btnMapInstruments').addEventListener('click', mapBuildInstruments);
    if (el('btnMapExtractAudio')) el('btnMapExtractAudio').addEventListener('click', mapExtractAudioFeatures);
    if (el('btnFilterReset')) el('btnFilterReset').addEventListener('click', mapResetFilter);
    if (el('mapDimStrength')) el('mapDimStrength').addEventListener('input', () => {
        mapState.filterStrength = Number(el('mapDimStrength').value) / 100;
        mapUpdateStrengthLabel();
        mapScheduleDraw();
    });
    // **语义筛选**：输入防抖 + 选项变化即重查
    if (el('mfKeywords')) el('mfKeywords').addEventListener('input', () => mapScheduleSemantic());
    ['mfMatchFile','mfMatchPath','mfMatchLib','mfMatchTag','mfRelaxInst'].forEach((id) => {
        if (el(id)) el(id).addEventListener('change', () => mapRunSemantic());
    });
    if (el('btnFilterPrev')) el('btnFilterPrev').addEventListener('click', () => mapFocusHit(-1));
    if (el('btnFilterNext')) el('btnFilterNext').addEventListener('click', () => mapFocusHit(1));
    if (el('btnFilterZoom')) el('btnFilterZoom').addEventListener('click', mapZoomToHits);
    if (el('btnSimilarTo')) el('btnSimilarTo').addEventListener('click', mapFindSimilarTo);
    // **进页面时生成滑杆**（维度定义来自后端，保证与 TimbreFilter 一致）
    mapBuildFilterSliders();
    if (el('mapDimStrength')) mapState.filterStrength = Number(el('mapDimStrength').value) / 100;
    mapUpdateStrengthLabel();
    if (el('btnMapLogClear')) el('btnMapLogClear').addEventListener('click', () => {
        el('mapLog').innerHTML = '<div class="kb-log-line dim">已清空</div>';
    });
    if (el('btnMapLogFold')) el('btnMapLogFold').addEventListener('click', () => {
        const p2 = document.querySelector('.map-log-panel');
        p2.classList.toggle('folded');
        el('btnMapLogFold').textContent = p2.classList.contains('folded') ? '展开' : '收起';
    });
    mapResize();
    mapScheduleDraw();
    // 进页面时拉一次档 1 特征状态（让用户看到「有没有提取过」）
    bridge.call('audioFeatureStatus', {}).then((r) => mapApplyAudioFeatStatus(r)).catch(() => { });
}

/** 日志一行（带时间戳，与知识库建库日志同风格）。 */
function mapLog(line, kind) {
    const box = el('mapLog');
    if (!box) return;
    const t = new Date().toTimeString().slice(0, 8);
    const cls = kind === 'ok' ? 'ok' : kind === 'err' ? 'err' : 'dim';
    box.insertAdjacentHTML('beforeend', `<div class="kb-log-line ${cls}">[${t}] ${escapeHtml(line)}</div>`);
    scrollToBottom(box, true);   // OK 只写不读，零强制重排（见 scrollToBottom 注释）
    while (box.children.length > 400) box.removeChild(box.firstChild);
}

/** 拉一次分析状态并刷新界面。 */
async function mapRefreshStatus() {
    let r;
    try { r = await bridge.call('audioMapStatus', {}); }
    catch (e) { mapLog('读取分析状态失败：' + (e.message || e), 'err'); return; }
    mapApplyStatus(r);
    // 点集处理已统一在 mapApplyStatus 里做，这里不再重复
}

/** 把状态对象渲染到控制台。 */
function mapApplyStatus(r) {
    if (!r) return;
    mapState.running = !!r.running;

    // 阶段
    const setStg = (id, state, label) => {
        const e2 = el(id); if (!e2) return;
        e2.className = 'map-stage ' + (state || '');
        e2.innerHTML = label + ' <b>' + (state === 'done' ? '✓' : state === 'running' ? '进行中' : state === 'fail' ? '失败' : '—') + '</b>';
    };
    setStg('stgExtract', r.extractState, '① 提取特征');
    setStg('stgUmap', r.phase === 'reduce' ? 'running' : r.umapState, '② 降维');
    setStg('stgCluster', r.phase === 'cluster' ? 'running' : r.clusterState, '③ 聚类');

    // 进度条
    const bar = el('mapBar');
    if (bar) bar.style.width = (r.percent || 0) + '%';

    // 文字
    const pt = el('mapProgressText');
    if (pt) {
        const failTxt = r.failed > 0 ? `　·　失败 ${r.failed} 个（多为格式不支持：.ogg/.aif 无法解码、部分 .nkx 缺注册表信息）` : '';
        if (r.phase === 'reduce') {
            // **降维阶段**：done/total 是「轮数」，必须换一种说法，否则百分比看起来荒谬
            pt.textContent = `正在降维（UMAP）—— 第 ${r.done} / ${r.total} 轮（${(r.percent || 0).toFixed(1)}%）· 已用 ${r.elapsedText || '—'}`;
        } else if (r.phase === 'cluster') {
            pt.textContent = `正在聚类…（已用 ${r.elapsedText || '—'}）`;
        } else if (r.phase === 'extract') {
            pt.textContent = `正在提取 ${r.done} / ${r.total}（${(r.percent || 0).toFixed(1)}%）· 已用 ${r.elapsedText || '—'} · 预计剩余 ${r.etaText || '—'}${failTxt}`;
        } else if (r.mertDone > 0) {
            pt.textContent = `特征已提取 ${r.mertDone} 个${failTxt}　——　可以点「重算地图」生成 2D 音色地图`;
        } else {
            pt.textContent = '尚未开始 —— 点「开始分析」后台提取音色特征';
        }
    }

    // 覆盖率（如实显示边界）
    const cv = el('mapCoverage');
    if (cv && r.coverageText) cv.textContent = r.coverageText;

    // 按钮可用性
    if (el('btnMapStart')) { el('btnMapStart').disabled = !!r.running; el('btnMapStart').textContent = r.done > 0 && r.done < r.total ? '继续分析' : '开始分析'; }
    if (el('btnMapPause')) el('btnMapPause').disabled = !r.running;
    if (el('btnMapRebuild')) el('btnMapRebuild').disabled = !!r.running || !(r.mertDone > 0);
    mapPollSync(!!r.running);

    // 🔴 **点集处理必须放在这里** —— mapApplyStatus 是「轮询」与「切页」共同的落点。
    // 之前只写在 mapRefreshStatus（切页才走），导致轮询拿到的乐器点从没存进 state
    // ⇒ 「乐器地图」跑完地图不刷新，要切走再切回才显示。
    if (r.mapPoints) mapLoadPoints(r.mapPoints);
    if (r.instrumentPoints) mapState.instrumentPoints = r.instrumentPoints;
    if (r.clusterMeta) mapState.clusterMeta = r.clusterMeta;
    mapApplyPoints();
    renderClusterLegend();
}

/* ══════════ MERT 运行方式弹窗（GPU / CPU + 线程数）══════════ */

let _mertDialogCb = null;

/** 打开 MERT 运行方式弹窗。 */
async function openMertRunDialog(cb, stored) {
    const m = el('mertRunModal');
    if (!m) { cb(false, 0); return; }
    let info;
    try { info = await bridge.call('cpuInfo', {}); } catch { info = { cores: 4, suggest: 2 }; }
    const cores = info.cores || 4, suggest = info.suggest || Math.max(1, cores - 2);

    if (el('mertRunHint')) el('mertRunHint').textContent =
        `MERT 要跑神经网络（768 维语义嵌入），比声学特征慢得多（每个约 0.3–0.5 秒）。\n\n` +
        `本机有 ${cores} 个逻辑核，**多核并行能拿到数倍提速** —— 建议留 2 个核给系统。`;
    // **MERT 也要能「重新提取（覆盖）」** —— 已有嵌入时显示勾选项
    const mertForceRow = el('mertForceRow');
    // **stored 由调用方传入** —— 之前写成引用 mapStart 里的局部变量 ⇒ ReferenceError，
    // 导致本函数在这一行就崩掉、弹窗永远不显示（用户报「点开始分析没反应」）。
    if (mertForceRow) mertForceRow.hidden = !(stored > 0);
    const mertForceChk = el('mertForceChk');
    if (mertForceChk) mertForceChk.checked = false;

    const mertSuggest = Math.min(suggest, 12);
    if (el('mertThreadsSuggest')) el('mertThreadsSuggest').textContent = `建议 ${mertSuggest}（共 ${cores} 核 · 实测 8–12 最优）`;
    const ti = el('mertThreadsInput');
    if (ti) { ti.value = String(mertSuggest); ti.max = String(cores); }
    const row = el('mertCpuRow'); if (row) row.style.display = 'flex';
    _mertDialogCb = cb;
    m.hidden = false;
}

function closeMertRunDialog() { const m = el('mertRunModal'); if (m) m.hidden = true; _mertDialogCb = null; }

/** 开始/继续分析。 */
async function mapStart() {
    // **先弹窗问「用 GPU 还是 CPU / 几个线程」**（用户要求）
    // 看已提取多少 MERT 嵌入 —— 有数据时才提供「重新提取（覆盖）」
    let stored = 0;
    try { const st = await bridge.call('audioMapStatus', {}); stored = st.mertCount || st.embedded || 0; } catch { }

    // ⚠️ **回调必须带上 force** —— 否则弹窗里勾了「重新提取」也传不出去
    openMertRunDialog((useGpu, threads, force) => mapStartWith(useGpu, threads, force), stored);
}

/** 弹窗确认后真正启动。 */
async function mapStartWith(useGpu, threads, force) {
    mapLog(`开始音色分析（后台，CPU ${threads} 线程）…`);
    if (el('btnMapStart')) el('btnMapStart').disabled = true;
    try {
        const r = await bridge.call('audioMapStart', { useGpu: !!useGpu, threads: threads || 0, force: !!force });
        mapLog(r && r.message ? r.message : '已启动', r && r.ok ? 'ok' : 'err');
        mapApplyStatus(r);
        if (r && r.running) mapPollSync(true);
    } catch (e) { mapLog('启动失败：' + (e.message || e), 'err'); }
}

/** 暂停分析。 */
async function mapPause() {
    try { const r = await bridge.call('audioMapPause', {}); mapLog(r && r.message ? r.message : '已请求暂停', 'ok'); }
    catch (e) { mapLog('暂停失败：' + (e.message || e), 'err'); }
}

/** 运行中时启动轮询（每 1.5 秒），结束时自动停 —— 避免常驻定时器。 */
function mapPollSync(running) {
    if (running && !mapState.pollTimer) {
        mapState.pollTimer = setInterval(async () => {
            try {
                const r2 = await bridge.call('audioMapStatus', {});
                mapApplyStatus(r2);
                if (!r2.running) { clearInterval(mapState.pollTimer); mapState.pollTimer = 0; mapLog('分析任务已结束：' + (r2.message || ''), 'ok'); }
            } catch { }
        }, 1500);
    } else if (!running && mapState.pollTimer) {
        clearInterval(mapState.pollTimer); mapState.pollTimer = 0;
    }
}

/* ══════════ 音色筛选（6 个可解释维度滑杆）══════════ */

/* ══════════ 语义筛选（方案 A）══════════ */

/* ══════════ 聚类 ∩ 音色筛选（联动）══════════ */

/**
 * **把筛选结果与「右侧选中的簇」求交** —— 实现「先选簇、再在簇内调滑杆」。
 *
 * **为什么需要**：右侧聚类与左侧筛选原本各自独立。用户的实际流程是
 * 「**先粗后细**」：先在右侧点一个簇（如「8Dio Hybrid Tools」），
 * 再拖左侧滑杆（如「打击感 ≥ 80%」）⇒ **期望在簇内筛**，而不是在全库里筛。
 *
 * **⚠️ 注意**：这里只在【选中了簇】时才求交；**没选簇时原样返回**（全库筛）。
 * @param {Set<number>|null} set 筛选结果；null = 无筛选
 * @returns {{set: Set<number>|null, inCluster: boolean, clusterSize: number}}
 */
function mapIntersectCluster(set) {
    const hl = mapState.highlightCluster;
    if (hl < 0) return { set, inCluster: false, clusterSize: 0 };
    // 当前选中簇的点集
    const inC = new Set();
    for (const p of mapState.points) if (p.cluster === hl) inC.add(p.clipId);
    if (!set) return { set: inC, inCluster: true, clusterSize: inC.size };   // 只选簇、没筛选 ⇒ 整簇
    const out = new Set();
    for (const id of set) if (inC.has(id)) out.add(id);
    return { set: out, inCluster: true, clusterSize: inC.size };
}

/** 防抖（输入时不狂发 RPC）。 */
function mapScheduleSemantic() {
    if (mapState.semTimer) clearTimeout(mapState.semTimer);
    mapState.semTimer = setTimeout(() => { mapState.semTimer = 0; mapRunSemantic(); }, 320);
}

/** 跑语义筛选 —— **只读表、毫秒级**。 */
async function mapRunSemantic() {
    const kw = (el('mfKeywords') ? el('mfKeywords').value : '').trim();
    if (!kw) {
        mapState.semHits = null;
        mapScheduleFilter();
        mapLog('语义筛选：已清空');
        return;
    }
    try {
        const r = await bridge.call('semanticFilter', {
            keywords: kw,
            matchFile: !!(el('mfMatchFile') && el('mfMatchFile').checked),
            matchPath: !!(el('mfMatchPath') && el('mfMatchPath').checked),
            matchLibrary: !!(el('mfMatchLib') && el('mfMatchLib').checked),
            matchTag: !!(el('mfMatchTag') && el('mfMatchTag').checked),
            relaxByInstrument: !!(el('mfRelaxInst') && el('mfRelaxInst').checked),
        });
        if (!r || r.ready === false) { mapState.semHits = null; mapLog('语义筛选不可用'); return; }
        mapState.semHits = new Set(r.clipIds || []);
        mapLog(`语义「${kw}」→ 命中 ${r.matched} / ${r.total}` + (r.relaxedLibraries ? `（含 ${r.relaxedLibraries} 个放宽库）` : ''));
    } catch (e) {
        mapLog('语义筛选失败：' + (e.message || e), 'err');
        mapState.semHits = null;
    }
    // **语义变了 → 感觉筛的命中要重新求交**（两者是 AND 关系）
    mapRunFilter();
}

/** 生成滑杆（维度定义来自后端，保证与 TimbreFilter 一致）。 */
async function mapBuildFilterSliders() {
    const box = el('mapFilterSliders');
    if (!box) return;
    let info;
    try { info = await bridge.call('timbreDims', {}); }
    catch { box.innerHTML = '<div class="map-filter-hint">读取维度定义失败</div>'; return; }

    const dims = (info && info.dims) || [];
    box.innerHTML = dims.map((d) => `
        <div class="mf-row" title="${escapeHtml(d.meaning || '')}">
            <div class="mf-head">
                <span class="mf-label">${escapeHtml(d.label)}</span>
                <span class="mf-val off" id="mfv-${d.key}">不限</span>
            </div>
            <input type="range" id="mf-${d.key}" min="0" max="100" value="0" step="5" />
        </div>`).join('') +
        '<div class="mf-result dim" id="mfResult"></div>';

    // 绑定（**输入时防抖**，避免拖动过程中狂发 RPC）
    dims.forEach((d) => {
        const s = el('mf-' + d.key);
        if (s) s.addEventListener('input', () => {
            mapState.filterVals[d.key] = Number(s.value) / 100;
            mapUpdateFilterLabel(d.key);
            mapScheduleFilter();
        });
    });

    // 未提取特征时给出明确提示
    if (info && info.stored === 0) {
        const hint = el('mapFilterHint');
        if (hint) hint.textContent = '还没有提取声学特征 —— 请先点上方「声学特征」按钮（32 维、不用模型，很快）';
    }
}

/** 更新某个维度的数值标签（0 显示「不限」）。 */
function mapUpdateFilterLabel(key) {
    const v = mapState.filterVals[key] || 0;
    const e2 = el('mfv-' + key);
    if (!e2) return;
    if (v <= 0) { e2.textContent = '不限'; e2.className = 'mf-val off'; }
    else { e2.textContent = '≥ ' + Math.round(v * 100) + '%'; e2.className = 'mf-val'; }
}

/** 防抖调度（拖动中不狂发 RPC）。 */
function mapScheduleFilter() {
    if (mapState.filterTimer) clearTimeout(mapState.filterTimer);
    mapState.filterTimer = setTimeout(() => { mapState.filterTimer = 0; mapRunFilter(); }, 260);
}

/** 真正跑筛选（**只读表、毫秒级**）。 */
async function mapRunFilter() {
    const args = {};
    for (const k of Object.keys(mapState.filterVals)) {
        const v = mapState.filterVals[k];
        if (v > 0) args[k] = v;
    }
    const res = el('mfResult');
    if (Object.keys(args).length === 0) {
        // **没拖滑杆时，若填了语义关键词，就只用语义结果**
        mapState.filterHits = mapState.semHits ? new Set(mapState.semHits) : null;
        // **与选中的簇求交**（先选簇、再筛选）
        {
            const r0 = mapIntersectCluster(mapState.filterHits);
            mapState.filterHits = r0.set;
            if (res && r0.inCluster) {
                res.textContent = `簇内 ${mapState.filterHits ? mapState.filterHits.size : r0.clusterSize} 个（簇 ${mapState.highlightCluster}）`;
                res.className = 'mf-result';
            }
        }
        mapUpdateHitCycleLabel();
        if (res) { res.textContent = ''; res.className = 'mf-result dim'; }
        mapScheduleDraw();
        return;
    }
    try {
        const r = await bridge.call('timbreFilter', args);
        if (!r || r.ready === false) {
            mapState.filterHits = null;
            if (res) { res.textContent = (r && r.message) || '还没提取声学特征'; res.className = 'mf-result dim'; }
            mapScheduleDraw();
            return;
        }
        mapState.filterHits = new Set(r.clipIds || []);
        mapState.hitCycle = 0;   // 筛选条件变了 → 循环索引归零
        // **与语义结果求交（AND）** —— 「打击感强 + 尖锐的 + 军鼓」
        if (mapState.semHits) {
            const out = new Set();
            for (const id of mapState.filterHits) if (mapState.semHits.has(id)) out.add(id);
            mapState.filterHits = out;
        }
        // **再与选中的簇求交** —— 实现「先选簇、再在簇内筛」
        const rc = mapIntersectCluster(mapState.filterHits);
        mapState.filterHits = rc.set;
        if (res) {
            const parts = [];
            if (rc.inCluster) parts.push(`簇 ${mapState.highlightCluster}`);
            if (mapState.semHits) parts.push('语义');
            parts.push(r.condition || '感觉');
            res.textContent = `命中 ${mapState.filterHits ? mapState.filterHits.size : 0}（${parts.join(' ∩ ')}）`;
            res.className = 'mf-result';
        }
        const sub = el('mapFilterSub');
        if (sub) sub.textContent = `${r.matched} / ${r.total}`;
        mapUpdateHitCycleLabel();
        mapLog(`音色筛选：${r.condition} → 命中 ${r.matched} / ${r.total}`);
    } catch (e) {
        if (res) { res.textContent = '筛选失败：' + (e.message || e); res.className = 'mf-result dim'; }
    }
    mapScheduleDraw();
}

/** 更新「弱化强度」的两个标签（筛选面板里已移到地图工具条）。 */
function mapUpdateStrengthLabel() {
    const v = mapState.filterStrength;
    const e2 = el('mapDimLabel');
    if (e2) e2.textContent = Math.round(v * 100) + '%';
    const e3 = el('mfStrengthVal');
    if (e3) {
        if (v >= 0.995) { e3.textContent = '只看命中'; e3.className = 'mf-val'; }
        else if (v <= 0.005) { e3.textContent = '不压暗'; e3.className = 'mf-val off'; }
        else { e3.textContent = '未命中 ' + Math.round((1 - v) * 100) + '% 透明'; e3.className = 'mf-val'; }
    }
}
/* ══════════ 以音色搜音色（方案 B）══════════ */

/**
 * **以当前选中的点为基准找相似**（MERT 768 维语义嵌入余弦 KNN）。
 *
 * **为什么需要它**：关键词语义筛（方案 A）对【缩写命名】的库天然失效
 * （实测：HAR_HDF_ / SMP_MTDGTR_ / DIWET_ 这类命名里没有可读词）。
 * **而本功能【完全不依赖名字】** —— 只要有一个「就是它」的样本，
 * 就能顺着 MERT 语义空间找到同类音色（**实测：同目录 0.9420 vs 异类 0.7933**）。
 */
async function mapFindSimilarTo() {
    const idx = mapState.clicked;
    if (idx < 0 || !mapState.points[idx]) {
        toast('先在地图上点一个点作为基准（就是「听起来像这个」的那个）', 'err');
        return;
    }
    const p = mapState.points[idx];
    try {
        const r = await bridge.call('similarToClip', { clipId: p.clipId, topK: 60 });
        if (!r || !r.ready) { toast((r && r.message) || 'MERT 嵌入不可用（需先跑音色分析）', 'err'); return; }
        const ids = r.clipIds || [];
        if (ids.length === 0) { toast('没找到相似样本', 'err'); return; }
        // **把相似结果当成一次「筛选」** —— 复用现有的弱化/定位/计数机制
        mapState.filterHits = new Set(ids);
        mapState.hitCycle = 0;
        mapUpdateHitCycleLabel();
        mapScheduleDraw();
        const res = el('mfResult');
        if (res) { res.textContent = `以「${p.file || p.lib || ''}」找相似 → ${ids.length} 个`; res.className = 'mf-result'; }
        mapLog(`找相似：${p.file || ''} → ${ids.length} 个（MERT 余弦）`);
    } catch (e) { toast('找相似失败：' + (e.message || e), 'err'); }
}

/**
 * **聚焦到第 idx 个命中点**（居中 + 固定放大 + 顺手选中它让右侧详情显示）。
 */
function mapFocusHitAt(idx) {
    const hits = mapState.filterHits;
    if (!hits || hits.size === 0) { toast("当前没有命中任何点 —— 先拖滑杆筛选", "err"); return; }
    const list = mapState.points.filter((p) => hits.has(p.clipId));
    if (list.length === 0) { toast("命中的点不在地图当前粒度上（试试切到「按采样」）", "err"); return; }

    // **循环取模**（负数也能正确回绕）
    const i = ((idx % list.length) + list.length) % list.length;
    mapState.hitCycle = i;
    const p = list[i];

    // **居中 + 固定放大倍数**（以「适配全部点」的 scale 为基准放大约 8 倍）
    const w = el("mapCanvas").clientWidth || 800, h2 = el("mapCanvas").clientHeight || 500;
    const fit = mapState.fitScale || mapState.scale || 1;
    mapState.scale = fit * 8;
    mapState.offsetX = w / 2 - p.x * mapState.scale;
    mapState.offsetY = h2 / 2 - p.y * mapState.scale;

    // **顺手选中** —— 右侧详情会显示文件名 / 播放器 / 相似列表
    mapState.clicked = mapState.points.indexOf(p);
    mapShowDetail(mapState.clicked);

    mapScheduleDraw();
    mapUpdateHitCycleLabel();
    mapLog("命中点 " + (i + 1) + "/" + list.length + "：" + (p.file || p.lib || ""));
}

/** 点「定位命中点」→ 聚焦【第一个】。 */
function mapZoomToHits() { mapFocusHitAt(0); }

/**
 * 点「上一个 / 下一个」→ **相对当前命中点移动一格**。
 *   dir=+1 下一个、dir=-1 上一个（都能循环回绕）。
 *
 * ⚠️ **这里必须用 `hitCycle + dir`** —— 之前写成 `base` / `base - 2` 是错的：
 *    · 「下一个」传 base ⇒ 原地不动（用户报「点了没反应」）
 *    · 「上一个」传 base-2 ⇒ 跳两格（用户报「按一下跳两个数字」）
 *   `mapFocusHitAt` 内部已做取模回绕，这里只管加减一即可。
 */
function mapFocusHit(dir) {
    const hits = mapState.filterHits;
    if (!hits || hits.size === 0) { toast("当前没有命中任何点 —— 先拖滑杆筛选", "err"); return; }
    const n = mapState.points.filter((p) => hits.has(p.clipId)).length;
    if (n === 0) { toast("命中的点不在地图当前粒度上", "err"); return; }
    mapFocusHitAt(mapState.hitCycle + dir);
}

/** 更新「定位命中点 (n/m)」的计数显示。 */
function mapUpdateHitCycleLabel() {
    const e2 = el("btnFilterZoomN");
    if (!e2) return;
    const hits = mapState.filterHits;
    const n = hits ? mapState.points.filter((p) => hits.has(p.clipId)).length : 0;
    e2.textContent = n > 0 ? "(" + (mapState.hitCycle + 1) + "/" + n + ")" : "";
}

/** 重置所有滑杆。 */
function mapResetFilter() {
    mapState.filterVals = {};
    mapState.filterHits = null;
    mapState.hitCycle = 0;
    mapState.semHits = null;
    if (el('mfKeywords')) el('mfKeywords').value = '';
    mapUpdateHitCycleLabel();
    document.querySelectorAll('#mapFilterSliders input[type="range"]').forEach((s) => { s.value = '0'; });
    document.querySelectorAll('#mapFilterSliders .mf-val').forEach((e2) => { e2.textContent = '不限'; e2.className = 'mf-val off'; });
    const res = el('mfResult');
    if (res) { res.textContent = ''; res.className = 'mf-result dim'; }
    const sub = el('mapFilterSub');
    if (sub) sub.textContent = '';
    mapScheduleDraw();
    mapLog('已重置音色筛选');
}

/* ══════════ CPU 核心数选择弹窗（声学特征 / MERT-CPU 共用）══════════ */

let _cpuDialogCb = null;   // 确认后的回调

/**
 * 打开「用几个核心」弹窗。
 * @param {string} title  标题
 * @param {string} hint   说明（会显示总核数与建议）
 * @param {number} suggest 建议值
 * @param {number} cores  总核数
 * @param {(threads:number)=>void} cb 确认回调
 */
async function openCpuDialog(title, hint, suggest, cores, cb, showForce) {
    const m = el('cpuThreadsModal');
    if (!m) { cb(suggest); return; }              // 弹窗不存在就直接用建议值
    if (el('cpuModalTitle')) el('cpuModalTitle').textContent = title;
    if (el('cpuModalHint')) el('cpuModalHint').textContent = hint;
    if (el('cpuSuggest')) el('cpuSuggest').textContent = `建议 ${suggest}（共 ${cores} 核）`;
    const inp = el('cpuThreadsInput');
    if (inp) { inp.value = String(suggest); inp.max = String(cores); }
    // **「重新提取」选项**：只在调用方明确要求时显示（如已提取过特征时）
    const forceRow = el('cpuForceRow');
    if (forceRow) forceRow.hidden = !showForce;
    const forceChk0 = el('cpuForceChk');
    if (forceChk0) forceChk0.checked = false;

    _cpuDialogCb = cb;

    // 快捷按钮：建议值 / 一半 / 跑满
    const quick = el('cpuQuickRow');
    if (quick) {
        const opts = [
            [`建议 ${suggest}`, suggest],
            [`一半 ${Math.max(1, Math.floor(cores / 2))}`, Math.max(1, Math.floor(cores / 2))],
            [`跑满 ${cores}`, cores],
        ];
        quick.innerHTML = opts.map(([label, v]) => `<button class="btn small" data-th="${v}">${label}</button>`).join('');
        quick.querySelectorAll('[data-th]').forEach((b) => b.addEventListener('click', () => {
            if (inp) inp.value = b.dataset.th;
        }));
    }
    m.hidden = false;
    if (inp) setTimeout(() => inp.select(), 30);
}

function closeCpuDialog() { const m = el('cpuThreadsModal'); if (m) m.hidden = true; _cpuDialogCb = null; }

/** 启动档 1 声学特征提取（**后台跑，不阻塞 UI**）。 */
async function mapExtractAudioFeatures() {
    mapLog('【声学特征】按钮已触发 —— 提取 32 维可解释特征（不用模型）');

    // **先问核心数**（用户要求：提示总核数 + 建议留 2 个给系统，但也可以跑满）
    let info;
    try { info = await bridge.call('cpuInfo', {}); } catch { info = { cores: 4, suggest: 2 }; }
    const cores = info.cores || 4, suggest = info.suggest || Math.max(1, cores - 2);

    // 看已提取多少 —— 有数据时才提供「重新提取（覆盖）」
    let stored = 0;
    try { const st = await bridge.call('audioFeatureStatus', {}); stored = st.stored || 0; } catch { }
    openCpuDialog(
        '用几个 CPU 核心跑声学特征提取？',
        `本机有 ${cores} 个逻辑核。声学特征是【纯 CPU 计算、无共享状态】，多核并行能拿 4–8 倍提速。\n\n` +
        `建议留 2 个核给系统（避免界面卡顿），但你也可以填 ${cores} 跑满 —— 那样界面可能会卡。`,
        suggest, cores,
        (threads, force) => mapStartAudioFeature(threads, force),
        stored > 0);
}

/** 弹窗确认后真正启动。 */
async function mapStartAudioFeature(threads, force) {
    mapState.audioFeatBucket = -1;
    mapLog(force
        ? `开始【重新提取】声学特征（${threads} 个线程，会覆盖已有特征）…`
        : `开始提取声学特征（${threads} 个线程）…`);
    try {
        const r = await bridge.call('extractAudioFeatures', { threads, force: !!force });
        mapLog(r && r.message ? r.message : '已启动', r && r.ok ? 'ok' : 'err');
        mapPollAudioFeature(true);
    } catch (e) { mapLog('启动失败：' + (e.message || e), 'err'); }
}

/** 轮询档 1 提取进度（复用音色地图的定时器字段，**只用一个定时器**）。 */
function mapPollAudioFeature(running) {
    if (running && !mapState.audioFeatTimer) {
        mapState.audioFeatTimer = setInterval(async () => {
            try {
                const r = await bridge.call('audioFeatureStatus', {});
                mapApplyAudioFeatStatus(r);
                // **按 10% 粒度写日志**（避免每 1.5 秒刷一条）
                const bucket = Math.floor((r.percent || 0) / 10);
                if (r.running && bucket !== mapState.audioFeatBucket) {
                    mapState.audioFeatBucket = bucket;
                    mapLog(`声学特征 ${r.done} / ${r.total}（${(r.percent || 0).toFixed(0)}%）${r.etaText ? ' · 剩余 ' + r.etaText : ''}`);
                }
                if (!r.running) {
                    clearInterval(mapState.audioFeatTimer); mapState.audioFeatTimer = 0;
                    mapLog('声学特征提取结束：' + (r.message || ''), 'ok');
                    // **提取完立刻刷新筛选可用性提示**
                    mapRefreshStatus();
                }
            } catch { }
        }, 1500);
    } else if (!running && mapState.audioFeatTimer) {
        clearInterval(mapState.audioFeatTimer); mapState.audioFeatTimer = 0;
    }
}

/**
 * 把档 1 提取状态渲染到界面 —— **复用音色地图的进度条、进度文字与日志**（用户要求）。
 * 档 1 与档 2 是两个独立任务，所以进度条显示【当前在跑的那一个】。
 */
function mapApplyAudioFeatStatus(r) {
    const box = el('mapAudioFeatStatus');
    if (!r) return;
    const btn = el('btnMapExtractAudio');

    if (r.running) {
        // **进度条 + 进度文字**（与档 2 共用同一套 UI）
        const bar = el('mapBar'); if (bar) bar.style.width = (r.percent || 0) + '%';
        const pt = el('mapProgressText');
        if (pt) pt.textContent = `正在提取声学特征（32 维）${r.done} / ${r.total}（${(r.percent || 0).toFixed(1)}%）` +
            ` · 已用 ${r.elapsedText || '—'}` + (r.etaText ? ` · 预计剩余 ${r.etaText}` : '') +
            (r.failed > 0 ? ` · 失败 ${r.failed}` : '');
        if (btn) { btn.disabled = true; btn.textContent = '提取中…'; }
        if (box) box.textContent = '（进度见上方进度条与「分析日志」）';
    } else {
        if (btn) { btn.disabled = false; btn.textContent = '声学特征'; }
        if (box) {
            box.textContent = r.stored > 0
                ? `声学特征已提取 ${r.stored} 个（32 维）—— 可用「按可解释特征筛选」（明亮度/噪声感/打击感…）`
                : '尚未提取声学特征 —— 点「声学特征」按钮（32 维、不用模型，比音色地图快）';
        }
        // 结束时把进度条交还给档 2 的状态
        if (r.message) mapRefreshStatus();
    }
}

/** 重算地图（UMAP + 聚类）。 */
async function mapRebuild() {
    const nb = Number(el('mapNeighbors') ? el('mapNeighbors').value : 15) || 15;
    const mt = el('mapMetric') ? el('mapMetric').value : 'cosine';
    mapLog(`重算地图（UMAP n_neighbors=${nb}，度量=${mt}）…`);
    try {
        const r = await bridge.call('audioMapRebuild', {
            neighbors: Number(el('mapNeighbors') ? el('mapNeighbors').value : 15) || 15,
            metric: el('mapMetric') ? el('mapMetric').value : 'cosine',
        });
        mapLog(r && r.message ? r.message : '已启动', r && r.ok ? 'ok' : 'err');
        if (r && r.mapPoints) mapLoadPoints(r.mapPoints);
        // 🔴 **必须把状态应用 + 启动轮询** —— 否则整个降维过程看不到任何进度。
        // （这是之前漏写的两行：mapStart 里有、mapRebuild 里没有，导致「重算地图只有一条日志」。）
        mapApplyStatus(r);
        if (r && r.running) mapPollSync(true);
    } catch (e) { mapLog('重算失败：' + (e.message || e), 'err'); }
}

/** 按当前粒度选择要显示的点集。 */
function mapApplyPoints() {
    mapState.points = mapState.level === 'instrument' ? (mapState.instrumentPoints || []) : (mapState.samplePoints || []);
    const em = el('mapEmpty');
    if (em) {
        const empty = mapState.points.length === 0;
        em.style.display = empty ? 'flex' : 'none';
        if (empty) em.textContent = mapState.level === 'instrument'
            ? '还没有乐器级地图 —— 点「乐器地图」生成（很快，不重新跑模型）'
            : '还没有地图数据 —— 先完成「开始分析」，再点「重算地图」';
    }
    mapFitView();
    mapScheduleDraw();
}

/** 构建乐器级地图（三期）。 */
async function mapBuildInstruments() {
    mapLog('开始构建乐器级地图（聚合已有特征，不重新跑模型）…');
    try {
        const r = await bridge.call('audioMapBuildInstruments', {});
        mapLog(r && r.message ? r.message : '已启动', r && r.ok ? 'ok' : 'err');
        mapApplyStatus(r);
        if (r && r.running) mapPollSync(true);
    } catch (e) { mapLog('构建失败：' + (e.message || e), 'err'); }
}

/** 载入地图点。 */
function mapLoadPoints(pts) {
    mapState.samplePoints = pts || [];
    // 显示哪一套由 mapApplyPoints() 按当前粒度决定（此处不直接改 points）
}

/** 重置视图。 */
function mapResetView() { mapFitView(); mapScheduleDraw(); }

/** 让所有点适配到画布。 */
function mapFitView() {
    const pts = mapState.points;
    if (!pts.length) return;
    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
    for (const p of pts) { if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x; if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y; }
    const w = el('mapCanvas').clientWidth || 800, h = el('mapCanvas').clientHeight || 500;
    const pad = 28;
    const sx = (w - pad * 2) / Math.max(1e-6, maxX - minX);
    const sy = (h - pad * 2) / Math.max(1e-6, maxY - minY);
    mapState.scale = Math.min(sx, sy);
    mapState.fitScale = mapState.scale;   // 记下「适配全部点」的 scale（逐点定位时作放大基准）
    mapState.offsetX = pad - minX * mapState.scale + (w - pad * 2 - (maxX - minX) * mapState.scale) / 2;
    mapState.offsetY = pad - minY * mapState.scale + (h - pad * 2 - (maxY - minY) * mapState.scale) / 2;
}

/** 画布尺寸变化。 */
function mapResize() {
    const c = el('mapCanvas');
    if (!c) return;
    const r = c.parentElement.getBoundingClientRect();
    const dpr = window.devicePixelRatio || 1;
    c.width = Math.max(1, Math.round(r.width * dpr));
    c.height = Math.max(1, Math.round(r.height * dpr));
    const ctx = c.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    mapScheduleDraw();
}

/** 节流重绘（避免 mousemove 每次都全量重画）。 */
function mapScheduleDraw() {
    if (mapState.raf) return;
    mapState.raf = requestAnimationFrame(() => { mapState.raf = 0; mapDraw(); });
}

/** 画散点图。**用 Canvas 而不是 DOM** —— 几万个 DOM 节点会卡死。 */
function mapDraw() {
    const c = el('mapCanvas');
    if (!c) return;
    const ctx = c.getContext('2d');
    const w = c.clientWidth, h = c.clientHeight;
    ctx.clearRect(0, 0, w, h);

    const pts = mapState.points;
    if (!pts.length) return;

    const S = mapState.scale, OX = mapState.offsetX, OY = mapState.offsetY;
    const palette = mapPalette();
    mapSyncColorBar();

    // **被框选中的点**：加亮描边（先画，避免被后面的点盖住）
    if (mapState.selected.length > 0) {
        ctx.strokeStyle = '#7fd6a8'; ctx.lineWidth = 1;
        for (const si of mapState.selected) {
            const q = pts[si]; if (!q) continue;
            const sx = q.x * S + OX, sy = q.y * S + OY;
            if (sx < -5 || sy < -5 || sx > w + 5 || sy > h + 5) continue;
            ctx.beginPath(); ctx.arc(sx, sy, 3.5, 0, 6.2832); ctx.stroke();
        }
    }

    // 逐点画（点太多时用更小的半径）
    const r = pts.length > 8000 ? 1.6 : pts.length > 3000 ? 2.1 : 2.8;
    for (let i = 0; i < pts.length; i++) {
        const p = pts[i];
        const x = p.x * S + OX, y = p.y * S + OY;
        if (x < -5 || y < -5 || x > w + 5 || y > h + 5) continue;
        const hl = mapState.highlightCluster;
        // **筛选未命中也要压暗** —— 让符合「明亮/有打击感」的点凸显出来
        const filtered = mapState.filterHits && !mapState.filterHits.has(p.clipId);
        // ⚠️ **必须先算 dim 再判断** —— 之前把 continue 写在 dim 声明之前，
        //    导致 `dim` 在声明前被使用（TDZ ReferenceError）⇒ 整个绘制循环崩掉 ⇒ 一个点都画不出来。
        const dim = (hl >= 0 && p.cluster !== hl) || filtered;
        // **弱化强度是全局的** —— 未命中筛选的点、以及非高亮簇的点，都用同一个强度
        const dimAlpha = Math.max(0, 1 - (mapState.filterStrength || 0));
        if (dim && dimAlpha <= 0.005) continue;   // 完全弱化 → 直接跳过绘制（省开销）
        ctx.globalAlpha = dim ? dimAlpha : 1;
        ctx.fillStyle = mapPointColor(p, palette);
        ctx.beginPath();
        ctx.arc(x, y, i === mapState.clicked ? r + 2.5 : i === mapState.hover ? r + 1.5 : r, 0, 6.2832);
        ctx.fill();
        if (i === mapState.clicked) { ctx.strokeStyle = '#fff'; ctx.lineWidth = 1.5; ctx.stroke(); }
    }
    ctx.globalAlpha = 1;

    // **拖拽中的矩形**
    const sel = mapState.sel;
    if (sel && sel.dragging) {
        const rx = Math.min(sel.x0, sel.x1), ry = Math.min(sel.y0, sel.y1);
        const rw = Math.abs(sel.x1 - sel.x0), rh = Math.abs(sel.y1 - sel.y0);
        ctx.fillStyle = 'rgba(76,141,255,0.12)';
        ctx.fillRect(rx, ry, rw, rh);
        ctx.strokeStyle = '#4c8dff'; ctx.lineWidth = 1;
        ctx.setLineDash([4, 3]);
        ctx.strokeRect(rx, ry, rw, rh);
        ctx.setLineDash([]);
    }
}

/** 配色：稳定地把任意 key 映射到一组好看的色。 */
const MAP_COLORS = ['#4c8dff', '#7fd6a8', '#e8b04b', '#f09a9a', '#c8b6f0', '#5ad1e6', '#f0a0d0', '#a8d95a', '#ff9d5c', '#8fa8ff', '#6fd0c0', '#d0a0ff'];
function mapPalette() {
    const cache = new Map();
    return (key) => {
        if (key === undefined || key === null || key === '') return '#3a4353';
        const k = String(key);
        if (!cache.has(k)) cache.set(k, MAP_COLORS[cache.size % MAP_COLORS.length]);
        return cache.get(k);
    };
}

/* ══════════ 簇图例（给簇起名 + 代表乐器）══════════ */

/**
 * 渲染簇图例。
 * **命名规则**：用「占比最高的库名」作为簇名（用户看得懂），
 * 后面跟规模与代表样本 —— 比 `cluster 0` 有意义得多。
 */
function renderClusterLegend() {
    const box = el('mapLegend');
    if (!box) return;
    const meta = mapState.clusterMeta || [];
    const panel = el('mapLegendPanel');
    // **乐器级地图没有簇元信息**（后端只对采样级算），此时整块隐藏
    if (mapState.level === 'instrument' || meta.length === 0) {
        box.innerHTML = '';
        if (panel) panel.hidden = true;
        return;
    }
    if (panel) panel.hidden = false;
    const sub = el('mapLegendSub');
    if (sub) {
        const total2 = meta.reduce((a, b) => a + b.count, 0);
        sub.textContent = `（${meta.length} 个簇 / ${total2} 个点）`;
    }

    const palette = mapPalette();
    const total = meta.reduce((a, b) => a + b.count, 0);
    box.innerHTML = meta.map((m) => {
        const pct = total > 0 ? (m.count * 100 / total).toFixed(1) : '0';
        return `<div class="map-legend-item ${mapState.highlightCluster === m.cluster ? 'on' : ''}" data-cl="${m.cluster}"
                     title="簇 ${m.cluster}｜${m.count} 个点（${pct}%）｜主要来自：${escapeHtml(m.topLibrary)}｜代表样本：${escapeHtml(m.repFile)}">
                    <span class="map-legend-dot" style="background:${palette(m.cluster)}"></span>
                    <span class="map-legend-name">${escapeHtml(shortLib(m.topLibrary))}</span>
                    <span class="map-legend-cnt">${m.count}</span>
                    <span class="map-legend-play" data-play="${m.cluster}" title="试听该簇的代表样本">▶</span>
                </div>`;
    }).join('');

    box.querySelectorAll('[data-play]').forEach((b) => b.addEventListener('click', (ev) => {
        ev.stopPropagation();
        mapPlayCluster(Number(b.dataset.play));
    }));
    box.querySelectorAll('[data-cl]').forEach((el2) => el2.addEventListener('click', () => {
        const c = Number(el2.dataset.cl);
        mapState.highlightCluster = mapState.highlightCluster === c ? -1 : c;
    // **选/取消簇后立即重跑筛选** —— 让「簇内筛选」即时生效（否则要等下次拖滑杆）
    mapRunFilter();
        renderClusterLegend();
        mapScheduleDraw();
        const m = meta.find((x) => x.cluster === c);
        if (m) mapLog(`簇 ${c}：${m.count} 个点，主要来自「${m.topLibrary}」，代表样本「${m.repFile}」`);
    }));
}

/** 打开导出弹窗（**合并了原来的两个按钮** —— 用户要求点一次再选格式）。 */
function mapOpenExportModal() {
    const pts = mapState.points;
    if (!pts.length) { toast('还没有地图可导出', 'err'); return; }
    const m = el('mapExportModal');
    const hint = el('mapExportHint');
    if (hint) {
        const lvl = mapState.level === 'instrument' ? '乐器级' : '采样级';
        hint.textContent = `当前是【${lvl}】地图，共 ${pts.length} 个点。\n\n` +
            '· 导出为图片：得到一张 PNG（适合放进文档/分享）\n' +
            '· 导出为表格：得到 CSV（含库名/文件名/聚类/坐标，适合 Excel 分析）';
    }
    if (m) m.hidden = false;
}

function mapCloseExportModal() {
    const m = el('mapExportModal');
    if (m) m.hidden = true;
}

/* ══════════ 地图导出（任务 5）═════════ */

/** **导出当前地图为 PNG** —— 直接取 Canvas 内容。 */
function mapExportPng() {
    const c = el('mapCanvas');
    if (!c || mapState.points.length === 0) { toast('还没有地图可导出', 'err'); return; }
    try {
        // 深色背景 + Canvas 内容合成（Canvas 本身是透明的，直接导会得到黑底透明图）
        const out = document.createElement('canvas');
        out.width = c.width; out.height = c.height;
        const ctx = out.getContext('2d');
        ctx.fillStyle = '#0d1117';
        ctx.fillRect(0, 0, out.width, out.height);
        ctx.drawImage(c, 0, 0);
        const url = out.toDataURL('image/png');
        const a = document.createElement('a');
        a.href = url;
        a.download = '音色地图-' + new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-') + '.png';
        document.body.appendChild(a); a.click(); a.remove();
        mapLog(`已导出 PNG（${out.width}×${out.height}，${mapState.points.length} 个点）`, 'ok');
    } catch (e) { mapLog('导出 PNG 失败：' + (e.message || e), 'err'); }
}

/** **导出全部地图点为 CSV**（不只选区）。 */
async function mapExportCsv() {
    const pts = mapState.points;
    if (!pts.length) { toast('还没有地图可导出', 'err'); return; }
    try {
        const r = await bridge.call('exportMapSelection', {
            rows: pts.map((p) => ({ lib: p.lib, file: p.file, cluster: p.cluster, x: p.x, y: p.y, clipId: p.clipId })),
        });
        if (r && r.ok) { mapLog(`已导出 ${r.rows} 行到 ${r.path}`, 'ok'); toast('已导出 CSV', 'ok'); }
        else mapLog('导出失败：' + ((r && r.message) || '未知'), 'err');
    } catch (e) { mapLog('导出失败：' + (e.message || e), 'err'); }
}

/** 试听某个簇的【代表样本】。 */
async function mapPlayCluster(cluster) {
    const m = (mapState.clusterMeta || []).find((x) => x.cluster === cluster);
    if (!m) return;
    if (mapState.playing) { mapState.playing = false; mapLog('已停止播放'); return; }
    try {
        const r = await bridge.call('audioMapDetail', { clipId: m.repClipId });
        if (!r || !r.audioUrl) { mapLog(`簇 ${cluster} 的代表样本「${m.repFile}」没有可播放的音频`, 'err'); return; }
        mapLog(`▶ 簇 ${cluster} 代表样本：${m.repFile}（来自 ${m.topLibrary}）`, 'ok');
        const audio = new Audio(r.audioUrl);
        mapState.playing = true;
        audio.onended = () => { mapState.playing = false; };
        await audio.play();
    } catch (e) { mapLog('试听失败：' + (e.message || e), 'err'); }
}

/** 库名太长时截断（图例要紧凑）。 */
function shortLib(s) {
    s = String(s || '?');
    return s.length > 18 ? s.slice(0, 17) + '…' : s;
}

/* ══════════ 框选（拖拽矩形批量选点）══════════ */

/** 按下鼠标：记录起点（**不立即进入框选** —— 只有移动超过阈值才算拖拽，否则是普通点击）。 */
function mapSelStart(ev) {
    const [mx, my] = mapLocalPos(ev);
    mapState.sel = { x0: mx, y0: my, x1: mx, y1: my, dragging: false };
    const move = (e2) => {
        const [x, y] = mapLocalPos(e2);
        mapState.sel.x1 = x; mapState.sel.y1 = y;
        const dx = Math.abs(x - mapState.sel.x0), dy = Math.abs(y - mapState.sel.y0);
        if (dx > 4 || dy > 4) mapState.sel.dragging = true;   // 超过阈值才算拖拽
        if (mapState.sel.dragging) mapScheduleDraw();
    };
    const up = () => {
        window.removeEventListener('mousemove', move);
        window.removeEventListener('mouseup', up);
        const s = mapState.sel;
        mapState.sel = null;
        if (s && s.dragging) {
            mapApplySelection(s);
        } else {
            mapScheduleDraw();
        }
    };
    window.addEventListener('mousemove', move);
    window.addEventListener('mouseup', up);
}

/** 把屏幕矩形里的点选出来。 */
function mapApplySelection(rect) {
    const pts = mapState.points;
    const x0 = Math.min(rect.x0, rect.x1), x1 = Math.max(rect.x0, rect.x1);
    const y0 = Math.min(rect.y0, rect.y1), y1 = Math.max(rect.y0, rect.y1);
    const S = mapState.scale, OX = mapState.offsetX, OY = mapState.offsetY;
    const hit = [];
    for (let i = 0; i < pts.length; i++) {
        const sx = pts[i].x * S + OX, sy = pts[i].y * S + OY;
        if (sx >= x0 && sx <= x1 && sy >= y0 && sy <= y1) hit.push(i);
    }
    mapState.selected = hit;
    mapScheduleDraw();
    mapUpdateSelBar();
    if (hit.length > 0) mapLog(`框选了 ${hit.length} 个点（${(x1 - x0).toFixed(0)}×${(y1 - y0).toFixed(0)} 像素区域）`);
}

/** 刷新选区操作条。 */
function mapUpdateSelBar() {
    const bar = el('mapSelBar');
    const cnt = el('mapSelCount');
    const n2 = mapState.selected.length;
    if (bar) bar.hidden = n2 === 0;
    if (cnt) {
        const pts = mapState.points;
        const libs = new Set(mapState.selected.map(i => pts[i] && pts[i].lib).filter(Boolean));
        cnt.textContent = `已选 ${n2} 个` + (libs.size > 1 ? `（跨 ${libs.size} 个库）` : '');
    }
}

function mapClearSelection() {
    mapState.selected = [];
    mapUpdateSelBar();
    mapScheduleDraw();
}

/** 批量试听：**依次播放**（每段约 3 秒后自动切下一段）。 */
async function mapPlaySelection() {
    mapLog('【批量试听】按钮已触发');
    try { await mapPlaySelectionCore(); }
    catch (e) { mapLog('批量试听出错：' + (e.message || e), 'err'); }
}

async function mapPlaySelectionCore() {
    const pts = mapState.points;
    const sel = Array.isArray(mapState.selected) ? mapState.selected : [];
    if (sel.length === 0) { mapLog('没有框选任何点 —— 请先在地图上拖拽框选一片区域', 'err'); toast('先框选一片区域', 'err'); return; }
    const ids = sel.map(i => pts[i] && pts[i].clipId).filter(x => x);
    if (!ids.length) { toast('选中的点没有可播放的片段', 'err'); return; }
    if (mapState.playing) { mapState.playing = false; mapLog('已停止批量试听'); return; }

    mapState.playing = true;
    if (el('btnSelPlayAll')) el('btnSelPlayAll').textContent = '⏸ 停止';
    mapLog(`开始批量试听 ${ids.length} 段（每段约 3 秒）…`);

    const audio = new Audio();
    audio.preload = 'auto';
    let played = 0, skipped = 0, blocked = 0;
    for (let k = 0; k < ids.length && mapState.playing; k++) {
        let r;
        try { r = await bridge.call('audioMapDetail', { clipId: ids[k] }); }
        catch (e) { skipped++; mapLog(`  [${k + 1}/${ids.length}] 查询失败：${e.message || e}`, 'err'); continue; }
        if (!r || !r.audioUrl) {
            skipped++;
            // **如实报告为什么跳过一个** —— 之前静默 continue，用户以为「没效果」
            mapLog(`  [${k + 1}/${ids.length}] 跳过 ${r && r.file ? r.file : ''}（${(r && r.error) || '没有可播放的音频'}`);
            continue;
        }
        mapLog(`  [${k + 1}/${ids.length}] ▶ ${r.file || ''}`);
        audio.src = r.audioUrl;
        try {
            await audio.play();
            played++;
        } catch (e) {
            blocked++;
            mapLog(`      播放被拦：${e.message || e}（点一下页面再试）`, 'err');
        }
        await new Promise(res => setTimeout(res, 3000));
    }
    mapState.playing = false;
    if (el('btnSelPlayAll')) el('btnSelPlayAll').textContent = '▶ 批量试听';
    mapLog(`批量试听结束：播放 ${played} 段，跳过 ${skipped} 段${blocked ? `，被拦 ${blocked} 段` : ''}`, played > 0 ? 'ok' : 'err');
}

/** 批量打标签：弹输入框 → 用真实的 tagsOf + tagsSet 写回（挂在「库」上）。 */
async function mapTagSelection() {
    mapLog('【打标签】按钮已触发');
    const pts = mapState.points;
    const sel = Array.isArray(mapState.selected) ? mapState.selected : [];
    if (sel.length === 0) { mapLog('没有框选任何点 —— 请先在地图上拖拽框选一片区域', 'err'); toast('先框选一片区域', 'err'); return; }
    const names = [...new Set(sel.map(i => pts[i] && pts[i].lib).filter(Boolean))];
    if (!names.length) { toast('选中的点里没有识别到音色库', 'err'); mapLog('选中的点里没有识别到音色库', 'err'); return; }

/* ══════════ 通用输入弹窗（替代 prompt()）══════════ */

let _inputDialogCb = null;

/**
 * **通用文本输入弹窗** —— 替代 `prompt()`。
 *
 * **为什么必须替代**：**WebView2 默认禁用脚本对话框**（`prompt`/`confirm`/`alert`），
 * `prompt()` 会**立刻返回 null** ⇒ 表现为「点了按钮完全没反应」。
 * （本项目里「会话重命名」就踩过这个坑；标签弹窗也是同样原因改的。）
 *
 * @returns {Promise<string|null>} 确定返回文本；取消返回 null
 */
function showInputDialog(title, defaultValue, hint) {
    return new Promise((resolve) => {
        const m = el('inputDialogModal');
        if (!m) { resolve(null); return; }   // 弹窗不存在 ⇒ 安全返回
        if (el('inputDialogTitle')) el('inputDialogTitle').textContent = title || '输入';
        if (el('inputDialogHint')) el('inputDialogHint').textContent = hint || '';
        const inp = el('inputDialogText');
        if (inp) inp.value = defaultValue || '';
        _inputDialogCb = resolve;
        m.hidden = false;
        if (inp) setTimeout(() => { inp.focus(); inp.select(); }, 30);
    });
}

/** 关闭并回传（ok=true 取值，false 取 null）。 */
function closeInputDialog(ok) {
    const m = el('inputDialogModal');
    const inp = el('inputDialogText');
    const v = ok ? (inp ? inp.value.trim() : '') : null;
    if (m) m.hidden = true;
    const cb = _inputDialogCb;
    _inputDialogCb = null;
    if (cb) cb(v);
}

    // **不能用 prompt()** —— WebView2 默认禁用脚本对话框，prompt 会立刻返回 null，
    // 表现为「点了按钮完全没反应」。改用页面内的弹窗。
    const m = el('tagInputModal');
    const hint = el('tagInputHint');
    const inp = el('tagInputText');
    if (hint) {
        // **只列前 5 个**，避免超长文本把弹窗撑开
        const preview = names.slice(0, 5).join('、');
        hint.textContent = `将给 ${names.length} 个音色库打标签（已有该标签的会跳过）：\n${preview}${names.length > 5 ? ` 等 ${names.length} 个` : ''}`;
    }
    if (inp) inp.value = '';
    if (m) m.hidden = false;
    if (inp) setTimeout(() => inp.focus(), 30);

    // 常用标签快捷按钮（省得手打）
    const quick = el('tagQuickRow');
    if (quick && !quick.dataset.done) {
        quick.dataset.done = '1';
        quick.innerHTML = ['明亮', '暗淡', '打击乐', '弦乐', '管乐', '人声', '环境音', '合成器']
            .map((x) => `<button class="btn small" data-quick="${x}">${x}</button>`).join('');
        quick.querySelectorAll('[data-quick]').forEach((b) => b.addEventListener('click', () => {
            if (inp) inp.value = b.dataset.quick;
        }));
    }
}

/** 弹窗确认后真正写入。 */
async function mapTagCommit() {
    const tagName = (el('tagInputText') ? el('tagInputText').value : '').trim();
    if (!tagName) { toast('标签不能为空', 'err'); return; }
    const m = el('tagInputModal');
    if (m) m.hidden = true;

    const pts = mapState.points;
    const sel = Array.isArray(mapState.selected) ? mapState.selected : [];
    const names = [...new Set(sel.map(i => pts[i] && pts[i].lib).filter(Boolean))];
    mapLog(`开始给 ${names.length} 个库写标签「${tagName}」…`);

    let ok = 0, fail = 0, lastErr = '';
    for (const libName of names) {
        try {
            let existing = [];
            try {
                const cur = await bridge.call('tagsOf', { entityType: 'library', entityId: libName });
                existing = (cur && cur.tags) ? cur.tags.map(x => (typeof x === 'string' ? x : x.name)).filter(Boolean) : [];
            } catch (e) { lastErr = 'tagsOf: ' + (e.message || e); }
            if (existing.includes(tagName)) { ok++; continue; }
            const r = await bridge.call('tagsSet', {
                entityType: 'library', entityId: libName, tags: [...existing, tagName],
            });
            if (r && r.ok !== false) ok++; else { fail++; lastErr = (r && (r.error || r.message)) || ''; }
        } catch (e) { fail++; lastErr = (e && e.message) || String(e); }
    }
    mapLog(`打标签「${tagName}」：成功 ${ok} 个库${fail ? `，失败 ${fail}（${lastErr}）` : ''}`, fail ? 'err' : 'ok');
    toast(fail ? `成功 ${ok} 个、失败 ${fail} 个` : `已给 ${ok} 个库打上标签「${tagName}」`, fail ? 'err' : 'ok');
}

/** 导出选区为 CSV。 */
async function mapExportSelection() {
    mapLog('【导出 CSV】按钮已触发');
    try { await mapExportSelectionCore(); }
    catch (e) { mapLog('导出 CSV 出错：' + (e.message || e), 'err'); }
}

async function mapExportSelectionCore() {
    const pts = mapState.points;
    const sel = Array.isArray(mapState.selected) ? mapState.selected : [];
    if (sel.length === 0) { mapLog('没有框选任何点 —— 请先在地图上拖拽框选一片区域', 'err'); toast('先框选一片区域', 'err'); return; }
    const rows = sel.map(i => pts[i]).filter(Boolean);
    if (!rows.length) { mapLog('选中的点已失效（可能地图重算过），请重新框选', 'err'); return; }
    try {
        const r = await bridge.call('exportMapSelection', {
            rows: rows.map(p => ({ lib: p.lib, file: p.file, cluster: p.cluster, x: p.x, y: p.y, clipId: p.clipId })),
        });
        if (r && r.ok) { mapLog(`已导出 ${rows.length} 行到 ${r.path}`, 'ok'); toast('已导出 CSV', 'ok'); }
        else mapLog('导出失败：' + ((r && r.message) || '未知'), 'err');
    } catch (e) { mapLog('导出失败：' + (e.message || e), 'err'); }
}

/** **懒加载**：地图上那些点的 6 维可解释百分位（按特征着色用）。 */
async function mapLoadTimbreValues() {
    try {
        const r = await bridge.call('timbreValues', {});
        if (!r || r.ready === false) {
            mapLog((r && r.message) || '还没有提取声学特征 —— 请先点「声学特征」按钮', 'err');
            return;
        }
        const m = new Map();
        for (const row of (r.values || [])) m.set(row[0], [row[1], row[2], row[3], row[4], row[5], row[6]]);
        mapState.timbreValues = m;
        mapState.timbreDimsOrder = r.dims || [];
        mapLog(`已加载 ${m.size} 个点的可解释特征（用于按特征着色）`);
    } catch (e) { mapLog('加载可解释特征失败：' + (e.message || e), 'err'); }
}

/** 可解释特征着色的色带：蓝 → 青 → 绿 → 黄 → 红（低 → 高）。 */
function timbreColor(v) {
    // v: 0..1
    const stops = [[0.0, 43,108,176], [0.25, 56,178,172], [0.5, 104,211,145], [0.75, 246,224,94], [1.0, 245,101,101]];
    let a = stops[0], b = stops[stops.length - 1];
    for (let i = 0; i < stops.length - 1; i++) {
        if (v >= stops[i][0] && v <= stops[i + 1][0]) { a = stops[i]; b = stops[i + 1]; break; }
    }
    const span = (b[0] - a[0]) || 1;
    const k = Math.min(1, Math.max(0, (v - a[0]) / span));
    const r = Math.round(a[1] + (b[1] - a[1]) * k);
    const g = Math.round(a[2] + (b[2] - a[2]) * k);
    const bl = Math.round(a[3] + (b[3] - a[3]) * k);
    return `rgb(${r},${g},${bl})`;
}

/** 当前着色是否为「按可解释特征」。 */
function mapIsTimbreColor() {
    return ['brightness','highFreq','noisiness','roughness','loudness','punch'].includes(mapState.colorBy);
}

/** 取某个点在当前着色模式下的颜色。 */
function mapPointColor(p, palette) {
    if (!mapIsTimbreColor()) return palette(mapState.colorBy === 'cluster' ? p.cluster : p.lib);
    const arr = mapState.timbreValues ? mapState.timbreValues.get(p.clipId) : null;
    if (!arr) return '#3a4353';                       // 没有声学特征的点画灰
    const idx = mapState.timbreDimsOrder.indexOf(mapState.colorBy);
    const v = idx >= 0 ? (arr[idx] ?? 0.5) : 0.5;
    return timbreColor(v);
}

/** 同步色带显隐与名称（只有按可解释特征着色时才显示）。 */
function mapSyncColorBar() {
    const bar = el('mapColorBar');
    if (!bar) return;
    const isT = mapIsTimbreColor();
    bar.hidden = !isT;
    if (!isT) return;
    const names = { brightness:'明亮度', highFreq:'高频含量', noisiness:'噪声感', roughness:'粗糙度', loudness:'响度', punch:'打击感' };
    const nm = el('mapColorBarName');
    if (nm) nm.textContent = (names[mapState.colorBy] || '') + ' 低';
}

/** 屏幕坐标 → 点索引（网格索引，避免 O(n) 扫描）。 */
function mapHitTest(mx, my) {
    const pts = mapState.points;
    if (!pts.length) return -1;
    const S = mapState.scale, OX = mapState.offsetX, OY = mapState.offsetY;
    let best = -1, bestD = 12 * 12;
    for (let i = 0; i < pts.length; i++) {
        const dx = pts[i].x * S + OX - mx, dy = pts[i].y * S + OY - my;
        const d = dx * dx + dy * dy;
        if (d < bestD) { bestD = d; best = i; }
    }
    return best;
}

function mapLocalPos(ev) {
    const r = el('mapCanvas').getBoundingClientRect();
    return [ev.clientX - r.left, ev.clientY - r.top];
}

function mapOnMove(ev) {
    const [mx, my] = mapLocalPos(ev);
    const i = mapHitTest(mx, my);
    if (i !== mapState.hover) {
        mapState.hover = i;
        el('mapCanvas').style.cursor = i >= 0 ? 'pointer' : 'crosshair';
        if (i >= 0) el('mapCanvas').title = mapState.points[i].file || '';
        mapScheduleDraw();
    }
}

async function mapOnClick(ev) {
    const [mx, my] = mapLocalPos(ev);
    const i = mapHitTest(mx, my);
    mapState.clicked = i;          // **只设 clicked，不碰 selected**（selected 是框选数组）
    mapScheduleDraw();
    if (i >= 0) await mapShowDetail(i);
}

function mapOnWheel(ev) {
    ev.preventDefault();
    const [mx, my] = mapLocalPos(ev);
    const f = ev.deltaY < 0 ? 1.15 : 1 / 1.15;
    const S = mapState.scale, OX = mapState.offsetX, OY = mapState.offsetY;
    mapState.offsetX = mx - (mx - OX) * f;
    mapState.offsetY = my - (my - OY) * f;
    mapState.scale = S * f;
    mapScheduleDraw();
}

/** 显示选中点的详情 + 相似列表。 */
async function mapShowDetail(i) {
    const p = mapState.points[i];
    const box = el('mapDetailBody');
    if (!box || !p) return;
    box.innerHTML = `<div class="hint-row">正在查询…</div>`;
    let r;
    try { r = await bridge.call('audioMapDetail', { clipId: p.clipId }); }
    catch (e) { box.innerHTML = '<div class="hint-row">查询失败：' + escapeHtml(e.message || e) + '</div>'; return; }

    const sims = (r && r.similar) || [];
    box.innerHTML = `
        <div class="map-detail-title">${escapeHtml(r.file || p.file || '')}</div>
        <div class="map-detail-meta">
            音色库：<b>${escapeHtml(r.library || p.lib || '')}</b><br>
            聚类：<b>${escapeHtml(String(r.cluster ?? p.cluster ?? '—'))}</b>
        </div>
        <div style="margin-top:10px">
            ${r && r.audioUrl
                ? '<audio controls preload="none" style="width:100%" src="' + escapeHtml(r.audioUrl) + '"></audio>'
                : '<div class="hint-row">这个片段没有可播放的音频（可能是 Kontakt 专有格式）</div>'}
        </div>
        <div class="map-sim-head">最相似的 ${sims.length} 个</div>
        ${sims.length ? sims.map((s, k) => `
            <div class="map-sim-item" data-sim="${k}">
                <span class="sc">${(s.score * 100).toFixed(1)}%</span>
                <span class="nm" title="${escapeHtml(s.file)}">${escapeHtml(s.file)}</span>
            </div>`).join('') : '<div class="hint-row">没有相似项</div>'}`;

    box.querySelectorAll('[data-sim]').forEach((n) => n.addEventListener('click', async () => {
        const s = sims[Number(n.dataset.sim)];
        if (!s) return;
        // 在地图上定位并选中那个点
        const idx = mapState.points.findIndex((q) => q.clipId === s.clipId);
        if (idx >= 0) { mapState.clicked = idx; mapScheduleDraw(); }
        if (s.audioUrl) toast('已在地图上定位；试听请在右侧详情里播放', 'ok');
    }));
}

/* ══════════ 主动建议（概览页）—— 轮换海报 ══════════ */

/** 当前建议列表与轮换状态。 */
let suggState = { items: [], index: 0, timer: null, paused: false };

/** 拉取体检建议并渲染成轮换海报。 */
async function loadSuggestions(includeSlow) {
    const stage = el('suggStage');
    if (!stage) return;
    stage.innerHTML = '<div class="hint-row">' + (includeSlow ? '正在体检（含重复大文件扫描，稍慢）…' : '正在体检…') + '</div>';
    const dots = el('suggDots'); if (dots) dots.innerHTML = '';

    let r;
    try { r = await bridge.call('suggestions', { includeSlow: !!includeSlow }); }
    catch (e) { stage.innerHTML = '<div class="hint-row">体检失败：' + escapeHtml(e.message || e) + '</div>'; return; }

    suggState.items = r.items || [];
    suggState.index = 0;
    renderSuggSlide();
    startSuggRotation();
}

/** 渲染当前这一条。 */
function renderSuggSlide() {
    const stage = el('suggStage');
    if (!stage) return;
    const items = suggState.items;

    if (!items.length) {
        stage.innerHTML = '<div class="sugg-empty">✅ 体检通过 —— 当前没有发现需要处理的事项。</div>';
        const d = el('suggDots'); if (d) d.innerHTML = '';
        setSuggNav(false);
        return;
    }

    const s = items[suggState.index];
    stage.innerHTML = `
        <div class="sugg-slide ${s.level === 'high' ? 'high' : ''}">
            <span class="sugg-dot"></span>
            <div class="sugg-body">
                <div class="sugg-title">${escapeHtml(s.title)}</div>
                <div class="sugg-detail">${s.detail}</div>
                ${Array.isArray(s.samples) && s.samples.length
                    ? `<div class="sugg-samples">涉及：${s.samples.map(escapeHtml).join('、')}${s.samples.length >= 8 ? ' …' : ''}</div>`
                    : ''}
            </div>
        </div>`;
    // **海报上不放按钮**（用户要求）—— 点整张卡片弹窗看完整内容，弹窗里再给「去处理」
    stage.onclick = () => openSuggModal(suggState.index);

    // 指示点
    const d = el('suggDots');
    if (d) {
        d.innerHTML = items.map((_, k) =>
            `<span class="dot ${k === suggState.index ? 'on' : ''}" data-dot="${k}"></span>`).join('')
            + `<span class="cnt">${suggState.index + 1} / ${items.length}</span>`;
        d.querySelectorAll('[data-dot]').forEach((b) => b.addEventListener('click', () => {
            suggState.index = Number(b.dataset.dot);
            renderSuggSlide();
            startSuggRotation();      // 手动切换后重新计时
        }));
    }
    setSuggNav(items.length > 1);
}

/** 只有一条时禁用左右箭头。 */
function setSuggNav(enabled) {
    ['btnSuggPrev', 'btnSuggNext'].forEach((id) => {
        const b = el(id);
        if (b) b.disabled = !enabled;
    });
}

/**
 * 建议详情弹窗：显示**完整**提示内容 + 「去处理」直达按钮。
 * 海报本身只有 84px 高、会裁掉长文，所以完整内容在这里看。
 */
function openSuggModal(index) {
    const items = suggState.items;
    if (!items.length) return;
    const s = items[index] || items[0];

    const mask = document.createElement('div');
    mask.className = 'modal-mask';
    mask.innerHTML = `
        <div class="modal" style="width:min(680px,92vw)">
            <h3 style="display:flex;align-items:center;gap:8px">
                <span>💡 建议详情</span>
                <span class="filters" style="margin-left:auto">
                    <button class="btn small" data-m="close">✕</button>
                </span>
            </h3>
            <div class="sugg-modal-body">
                <div class="sm-title">${escapeHtml(s.title)}
                    <span class="sm-level ${s.level === 'high' ? 'high' : ''}">${s.level === 'high' ? '该处理' : '可优化'}</span>
                </div>
                <div class="sm-detail">${escapeHtml(s.detail || '')}</div>
                ${Array.isArray(s.samples) && s.samples.length
                    ? `<div class="sm-samples"><b>涉及 ${s.samples.length} 个：</b><br>${s.samples.map(escapeHtml).join('、')}${s.samples.length >= 8 ? ' …' : ''}</div>`
                    : ''}
            </div>
            <div class="filters" style="justify-content:space-between;padding-top:10px">
                <span class="meta">${index + 1} / ${items.length}</span>
                <span class="filters">
                    <button class="btn" data-m="close">关闭</button>
                    ${s.action ? `<button class="btn primary" data-m="go">${escapeHtml(s.actionLabel || '去处理')}</button>` : ''}
                </span>
            </div>
        </div>`;
    document.body.appendChild(mask);

    mask.addEventListener('click', (ev) => {
        if (ev.target === mask) { mask.remove(); return; }
        const b = ev.target.closest('[data-m]');
        if (!b) return;
        if (b.dataset.m === 'close') { mask.remove(); return; }
        if (b.dataset.m === 'go') { mask.remove(); runSuggestion(s); }
    });
}

/** 切到上一条 / 下一条（循环）。 */
function suggStep(delta) {
    const n = suggState.items.length;
    if (n <= 1) return;
    suggState.index = (suggState.index + delta + n) % n;
    renderSuggSlide();
    startSuggRotation();
}

/**
 * 启动 5 秒自动轮换。
 * 悬停时暂停（用户正在读某一条时不该被翻走），移开后恢复并重新计时。
 */
function startSuggRotation() {
    stopSuggRotation();
    const panel = el('suggestPanel');
    if (panel && !panel.dataset.hoverBound) {
        panel.dataset.hoverBound = '1';
        panel.addEventListener('mouseenter', () => { suggState.paused = true; });
        panel.addEventListener('mouseleave', () => { suggState.paused = false; startSuggRotation(); });
    }
    if (suggState.items.length <= 1) return;
    suggState.timer = setTimeout(function tick() {
        if (!suggState.paused) {
            const n = suggState.items.length;
            suggState.index = (suggState.index + 1) % n;
            renderSuggSlide();
        }
        suggState.timer = setTimeout(tick, 5000);
    }, 5000);
}

function stopSuggRotation() {
    if (suggState.timer) { clearTimeout(suggState.timer); suggState.timer = null; }
}

/** 执行一条建议的动作（按 action 名分发到已有 RPC / 页面）。 */
async function runSuggestion(s) {
    if (!s || !s.action) return;
    switch (s.action) {
        case 'kbBuildAll':
            switchTab('chat');
            setTimeout(() => {
                const b = document.querySelector('[data-cst="kb"]');
                if (b) b.click();
                const bb = el('btnKbBuildAll');
                if (bb) bb.click();
            }, 260);
            break;
        case 'find_duplicates':
            switchTab('maintain');
            toast('已跳到「维护」页，请点「扫描重复大文件」查看详情', 'ok');
            break;
        case 'registerLibraries':
            switchTab('libraries');
            toast('已跳到「音色库列表」，勾选要入库的库后点「入库」', 'ok');
            break;
        default:
            toast('这条建议没有可直接执行的动作', 'err');
    }
}

/* ══════════ 停止正在进行的 AI 回合 ══════════ */

/**
 * 点「停止」：请求后端取消当前回合。
 * 后端会 Cancel 该分支的 CancellationTokenSource，流式生成与 Agent 工具循环都会中止。
 * 界面这边立即把按钮恢复成「发送」，避免用户以为还卡着。
 */
/**
 * **插话引导（steering）** —— Agent 正在工作时补一句引导。
 * 不新建回合、不打断，而是交给后端排队；Agent 循环会在下一个步骤边界取走并据此调整方向。
 */
async function steerChat() {
    const input = el('chatInput');
    if (!input) return;
    const q = input.value.trim();
    if (!q) return;
    input.value = '';
    try {
        const r = await bridge.call('chatSteer', {
            branchId: chatState.branchId || 0,
            text: q,
        });
        if (r && r.ok === false) { toast(r.message || '插话失败', 'err'); return; }
        toast('已插入引导，Agent 会在下一步看到', 'ok');
        // 立刻把用户这句显示在对话里（后端也存了，重载后一致）
        // 🔴 **插话要插在【步骤之间的空档】（2026-09-24 重做）** ——
        //   用户的关键观察：**DSH 的每个动作是原子化的**，插话插在步骤间隙，
        //   所以下一步立刻能采纳；而旧实现只 appendChild 到对话【最末尾】，
        //   看起来像「末尾留言」而不是「半路插入」。
        //   ⇒ 正确做法：**冻结当前步的块 → 插入插话块 → 开新块**，
        //     这样插话在时间线上就落在【它被发出时的那一步之后、下一步之前】✓
        sealLiveBlock();
        const box = el('chatMsgs');
        if (box) {
            const d = document.createElement('div');
            d.className = 'msg user steer';
            d.innerHTML = '<div class="who">你（插话引导）</div><div class="bubble">' + escapeHtml(q) + '</div>';
            box.appendChild(d);
            scrollToBottom(box, true);   // OK 只写不读，零强制重排（见 scrollToBottom 注释）
        }
        // **开新块** —— 后续思考/正文渲染到插话之后（视觉上「Agent 收到这句、继续干」）
        openLiveBlock();
    } catch (e) {
        toast('插话失败：' + (e && e.message ? e.message : e), 'err');
    }
}

async function stopChat() {
    const btn = el('btnChatSend');
    if (btn) { btn.disabled = true; btn.textContent = '停止中…'; }
    try {
        const r = await bridge.call('chatStop', {
            branchId: chatState.branchId || 0,
            sessionId: chatState.sessionId || 0,
        });
        toast(r && r.message ? r.message : '已请求停止', 'ok');
    } catch (e) {
        toast('停止失败：' + (e && e.message ? e.message : e), 'err');
    }
    // 兜底：即使后端没及时回，也把界面收尾（避免按钮永久停在「停止中…」）
    setTimeout(() => {
        if (chatState.streaming) {
            chatState.streaming = false;
            const b = el('btnChatSend');
            if (b) { b.disabled = false; b.textContent = '发送'; b.classList.remove('danger'); b.onclick = null; }
        }
    }, 2500);
}

/** 按当前是否在流式生成，切换发送/停止按钮的外观与行为。 */
function setChatSendMode(streaming) {
    const btn = el('btnChatSend');
    if (!btn) return;
    if (streaming) {
        btn.textContent = '停止';
        btn.classList.add('danger');
        btn.disabled = false;
        btn.dataset.mode = 'stop';
    } else {
        btn.textContent = '发送';
        btn.classList.remove('danger');
        btn.disabled = false;
        btn.dataset.mode = 'send';
    }
}

function finishChatStream() {
    clearTimeout(chatState._streamWatchdog);
    chatState.streaming = false;
    setChatSendMode(false);          // 按钮恢复成「发送」
    el('btnChatSend').disabled = false;

    // **回合结束后从服务端重载一次消息** ——
    // 旧实现只保留 DOM，不重新拉取；若这一轮里服务端状态变了
    //（例如工具加载了音源、新增了试听卡、或分支被切换），界面就会「停在上一步」，
    // 用户实测「切到别的会话再切回来才正常」——就是因为只有重开会话才会重载。
    // 这里主动重载，保证 DOM 与服务端一致；失败也不影响收尾。
    if (chatState.sessionId) {
        bridge.call('chatSessionOpen', { sessionId: chatState.sessionId })
            .then((r) => {
                if (r && r.ok && r.activeBranchId === chatState.branchId) {
                    // 🔴 **服务端数据没变时【不重建 DOM】（2026-09-26 修三个 BUG）**
                    //   实测症状：① 「输出最终答案后，原本渲染的播放器没了」
                    //             ② 「回复完页面还是滚动到最上面」
                    //             ③ 「连 markdown 格式都没正常显示」（表格显示成原始 | 记号）
                    //   **同一根因**：renderChatMessages() 会 box.innerHTML = … 重建整个消息区 ⇒
                    //     · 客户端临时渲染的播放器卡（不在服务端消息里）被抹掉；
                    //     · scrollTop 归零，且 .msg 有 content-visibility: auto，重建后高度是估算值；
                    //     · 而【最终 markdown 渲染】原本依赖这条重建路径 ⇒ 一旦跳过就没人做了（症状③）。
                    //   ✅ 修法：① 签名比对，数据没变就不重建；
                    //          ② 跳过重建时【显式补一次 markdown 渲染】（补上症状③）；
                    //          ③ 真重建时等一帧再滚（修症状②，因 content-visibility 高度是估算值）。
                    const sig = (r.messages || []).map((m) =>
                        (m.role || '') + '\u0001' + String(m.content || '').length + '\u0001' +
                        String(m.reasoning || '').length).join('\u0002');
                    if (chatState._renderSig !== sig) {
                        const box2 = el('chatMsgs');
                        const wasAtBottom = box2 ? box2._atBottom !== false : true;
                        chatState._renderSig = sig;
                        renderChatMessages(r.messages || []);
                        if (wasAtBottom) requestAnimationFrame(() => scrollToBottom(box2, true));
                    } else {
                        // ✅ **跳过重建时补一次 markdown 渲染** —— 见上方症状③
                        try {
                            const bubbles = document.querySelectorAll('#chatMsgs .msg.assistant .bubble');
                            const last = bubbles[bubbles.length - 1];
                            if (last && last._raw && typeof renderMarkdown === 'function') {
                                last.innerHTML = renderMarkdown(last._raw);
                            }
                        } catch (e) { }
                    }
    syncAiStats();   // 会话级统计（从库里数，重启不归零）
                    chatState.branches = r.branches || chatState.branches;
                }
            })
            .catch(() => { });
    syncAiStats();   // 回合结束：校准轮次/步数
    }
}

/** 跨库切换询问卡 */
function showCrossSwitchPrompt(reason) {
    const box = el('chatMsgs');
    if (box.querySelector('.chat-switch')) return;
    const card = document.createElement('div');
    card.className = 'chat-switch';
    card.innerHTML = `
        <div class="q">你问的问题已经<b>超出该知识库的覆盖范畴</b>，请问是否切换至「跨库问答模式」？<br>\n            ${reason ? '<span class="meta">AI 判断：' + escapeHtml(reason) + '</span><br>' : ''}
            <span class="meta">切换后会在本会话下新建一个跨库分支，可检索全部已建库的知识库。</span></div>
        <div class="filters">
            <button class="btn small primary" id="csYes">切换至跨库问答</button>
            <button class="btn small" id="csNo">继续单库问答</button>
        </div>`;
    box.appendChild(card);
    scrollToBottom(box, true);   // OK 只写不读，零强制重排（见 scrollToBottom 注释）
    card.querySelector('#csYes').addEventListener('click', async () => {
        card.remove();
        const bid = await createCrossBranch(false);
        if (bid) toast('已新建跨库分支，请重新提问（或直接发送，AI 将检索全部知识库）', 'ok');
    });
    card.querySelector('#csNo').addEventListener('click', () => card.remove());
}

/* ── 事件绑定 ── */




/* ══════════ 多模态：截图提问 ══════════ */
let chatImage = '';        // data URL

/** 点击「上传图片」：打开文件选择器，选中的图片挂到输入区 */
function pickChatImage() {
    const inp = el('chatFilePick');
    if (!inp) return;
    inp.value = '';          // 允许重复选同一个文件
    inp.click();
}

/** 把一张图挂到输入区（上传 / 粘贴共用） */
function attachImage(dataUrl, bytes) {
    chatImage = dataUrl;
    el('chatAttachImg').src = dataUrl;
    el('chatAttachInfo').textContent = bytes ? formatBytes(bytes) : '';
    el('chatAttach').hidden = false;
}

async function captureForChat() {
    const btn = el('btnChatShot');
    btn.disabled = true;
    try {
        const r = await bridge.call('captureWindow', {});
        if (!r.ok) { toast('截图失败：' + (r.message || ''), 'err'); return; }
        if (r.multimodal === false) {
            toast('当前模型未勾选「多模态能力」，附图可能无法理解（可到设置里开启）', 'err');
        }
        chatImage = 'data:image/jpeg;base64,' + r.base64;
        el('chatAttachImg').src = chatImage;
        el('chatAttachInfo').textContent = r.width + '×' + r.height + ' · ' + formatBytes(r.bytes);
        el('chatAttach').hidden = false;
    } catch (e) { toast('截图失败：' + e.message, 'err'); }
    finally { btn.disabled = false; }
}

function clearChatImage() {
    chatImage = '';
    el('chatAttach').hidden = true;
    el('chatAttachImg').src = '';
}


/** 代码执行确认：展示**完整脚本正文**（预览改动） */
/* ══════════ Agent 行动确认（行内，位于输入框上方）══════════
   设计说明（用户要求）：不再用居中弹窗，改成**输入框上方的行内确认条** ——
   这是当前 Agent 类产品的常见交互，视线不用离开对话流。

   另外修了一个真 bug：旧实现里 `answerShell()` **从未绑定到任何按钮**，
   所以「允许执行 / 拒绝」点了没反应（用户实测报告）。
*/

/** 显示行内确认条 */
/** 显示行内确认条（性能：避免强制同步布局） */
function showAgentConfirm(opts) {
    const bar = el('agentConfirm');
    if (!bar) return;
    el('agentConfirmTitle').textContent = opts.title || '需要你确认';
    el('agentConfirmReason').textContent = opts.reason || '';
    const body = el('agentConfirmBody');
    body.hidden = !opts.body;
    // 脚本正文放到下一帧再写：避免与流式渲染抢同一帧（长脚本时尤其明显）
    requestAnimationFrame(() => { body.textContent = opts.body || ''; });
    el('agentConfirmAllow').textContent = opts.okText || '允许执行';
    el('agentConfirmDeny').textContent = opts.cancelText || '拒绝';
    bar.hidden = false;
    // ⚠ **不要用 scrollIntoView** —— 它会强制整份文档同步重排，
    //    与正在进行的流式渲染抢同一帧，表现为「提权弹窗出现时界面短暂无响应」（用户实测）。
    //    确认条固定在输入框上方、本来就始终可见，无需滚动定位。
    //    焦点也放到下一帧，避免和渲染同帧竞争。
    requestAnimationFrame(() => {
        try { el('agentConfirmAllow').focus({ preventScroll: true }); } catch { }
    });
}

/** 收起确认条 */
function hideAgentConfirm() {
    const bar = el('agentConfirm');
    if (bar) bar.hidden = true;
}

/** 运行脚本确认 */
function showScriptConfirm(language, code) {
    shellPendingCmd = '';
    showAgentConfirm({
        title: '需要你确认才能运行这段脚本',
        reason: '语言：' + (language === 'javascript' ? 'JavaScript (node)' : 'PowerShell') +
                ' · 脚本写在临时目录、执行完立即删除；请先看清内容再决定。',
        body: code || '',
        okText: '允许执行',
        cancelText: '拒绝',
    });
}

/** 界面操作确认 */
function showUiConfirm(description) {
    shellPendingCmd = '';
    showAgentConfirm({
        title: '需要你确认才能操作界面',
        reason: 'Agent 想在外部程序（如 Kontakt）里执行这个操作，只有你允许后才会真正点击/输入。',
        body: description || '',
        okText: '允许',
        cancelText: '拒绝',
    });
}

/** Shell 命令确认 */
function showShellConfirm(command, reason) {
    shellPendingCmd = command || '';
    showAgentConfirm({
        title: '需要你确认才能执行这条命令',
        reason: reason || '命令会以当前用户权限运行；请先看清内容再决定。',
        body: shellPendingCmd,
        okText: '允许执行',
        cancelText: '拒绝',
    });
}

async function answerShell(approve) {
    hideAgentConfirm();
    try { await bridge.call('shellDecision', { approve }); }
    catch (e) { toast('回传失败：' + e.message, 'err'); }
    if (approve) toast('已允许执行，命令正在运行…', 'ok');
    else toast('已拒绝该命令', 'ok');
}

/* ══════════ 进度条：完成后延时自动隐藏 ══════════
   —— 之前进度条跑完就停在顶部不消失。现在统一走 finishProgress()：
      先把进度条补满、写入结果文案，再在 5 秒后自动收起。
      期间若又有新任务开始，会取消这个定时器。 */
let progressHideTimer = null;

function cancelProgressHide() {
    if (progressHideTimer) { clearTimeout(progressHideTimer); progressHideTimer = null; }
}

function finishProgress(text, hideAfterMs) {
    // 兜底：即便定时器被后续事件取消，也在 N 秒后强制隐藏一次
    setTimeout(() => { const w = el('progressWrap'); if (w) w.hidden = true; }, (hideAfterMs === undefined ? 5000 : hideAfterMs) + 400);
    const wrap = el('progressWrap');
    if (!wrap) return;
    if (text) el('progressText').textContent = text;
    el('progressFill').style.transform = 'scaleX(1)';
    cancelProgressHide();
    progressHideTimer = setTimeout(() => {
        wrap.hidden = true;
        progressHideTimer = null;
    }, hideAfterMs === undefined ? 5000 : hideAfterMs);
}

function showProgress(p) {
    cancelProgressHide();
    el('progressWrap').hidden = false;
    let pct = 0;
    if (p.phase === '统计文件') {
        pct = p.librariesTotal ? (p.librariesDone / p.librariesTotal) * 45 : 0;
    } else if (p.phase === '读取乐器名与版本') {
        pct = 45 + (p.filesIndexed && state.dashboard?.instrumentCount
            ? Math.min(1, p.filesIndexed / state.dashboard.instrumentCount) * 55 : 55);
    } else pct = 100;
    // **防御**：pct 可能是 NaN（除零/字段缺失），toFixed 对 NaN 安全，但 undefined 会抛；
    // 这里统一兜底成数字，避免「进度条一出现就抛异常、整条消息链中断」。
    if (typeof pct !== 'number' || !isFinite(pct)) pct = 100;
    el('progressFill').style.transform = 'scaleX(' + (Math.min(100, pct) / 100).toFixed(4) + ')';
    const rootPart = p.currentRoot ? p.currentRoot + ' · ' : '';
    el('progressText').textContent =
        (p.phase || '') + ' · ' + rootPart + (p.currentLibrary || '') +
        ' · 已处理 ' + formatNumber(p.filesIndexed) + (p.elapsed ? ' · ' + p.elapsed : '');
    el('scanState').textContent = (p.phase || '') + '…';
}

async function startScan() {
    if (state.scanning) return;
    try {
        const res = await bridge.call('scan', {});
        if (!res.started) { toast('无法开始扫描：' + (res.reason || '未知原因'), 'err'); return; }
        setScanning(true);
        cancelProgressHide();
        el('progressWrap').hidden = false;
        el('progressFill').style.transform = 'scaleX(0)';
        el('progressText').textContent = `正在扫描 ${res.roots} 个路径…`;
    } catch (e) { toast('扫描失败：' + e.message, 'err'); }
}

/* ══════════ 动作 ══════════ */
async function doOpenPath(path) {
    try { await bridge.call('openPath', { path }); }
    catch (e) { toast('打开失败：' + e.message, 'err'); }
}

async function doTestLibrary(id) {
    try {
        const res = await bridge.call('testLibrary', { id });
        toast(res.message, res.ok ? 'ok' : 'err');
    } catch (e) { toast('调用 Kontakt 失败：' + e.message, 'err'); }
}

/* ══════════ 数据加载 ══════════ */
async function loadConfig() {
    state.config = await bridge.call('config');
    el('rootPath').textContent = state.config.roots?.length
        ? `音色库路径：${state.config.roots.map((r) => r.path).join('  |  ')}`
        : `音色库路径：未配置`;

    const banner = el('adminBanner');
    if (!state.config.isAdmin) {
        banner.hidden = false;
        banner.className = 'banner warn';
        banner.innerHTML = `当前<b>不是管理员权限</b>运行，入库（写注册表）操作会失败。请关闭本工具后右键「以管理员身份运行」。`;
    } else {
        banner.hidden = true;
    }
    return state.config;
}

async function refresh() {
    const [dashboard, libraries, registration] = await Promise.all([
        bridge.call('dashboard'),
        bridge.call('libraries'),
        bridge.call('registration'),
    ]);
    state.dashboard = dashboard;
    state.libraries = libraries;
    renderDashboard(dashboard);
    renderTable();
    renderRegistration(registration);   // 概览页的入库指标条
}

async function loadSettings() {
    const [cfg, roots] = await Promise.all([loadConfig(), bridge.call('roots')]);
    renderRoots(roots);
    renderKontaktCurrent();
    await loadCandidates();
    const rep = await bridge.call('registration');
    renderRegistration(rep);
    await loadCompat();
    await loadAiSettings();
}

async function loadCompat() {
    const c = await bridge.call('compat');
    renderCompat(c);
}

/* ══════════ 乐器中心 ══════════ */
function debounce(fn, ms) {
    let timer;
    return (...args) => { clearTimeout(timer); timer = setTimeout(() => fn(...args), ms); };
}

const INST_ROW_H = 34;

/** 分页大小由容器实际高度决定：一页正好填满，不需要滚动条 */
function computeInstPageSize() {
    const list = el('instList');
    const h = list ? list.clientHeight : 0;
    if (h < 120) return 12;                       // 标签页尚未显示时的兜底
    return Math.max(5, Math.floor(h / INST_ROW_H));
}

/** 左侧分类树：分类可折叠，默认收起 */
async function loadInstrumentTree() {
    state.instTreeLoaded = true;
    let tree = [];
    try {
        tree = await bridge.call('instrumentTree');
    } catch (e) {
        el('instTree').innerHTML = `<div class="hint-row">加载失败：${escapeHtml(e.message)}</div>`;
        return;
    }

    const byCat = new Map();
    for (const t of tree) {
        if (!byCat.has(t.category)) byCat.set(t.category, []);
        byCat.get(t.category).push(t);
    }
    const grand = tree.reduce((s, t) => s + t.count, 0);

    let html = `<div class="tree-item active" data-lib="0">
        <span class="label">全部音色库</span><span class="n">${formatNumber(grand)}</span></div>`;
    // 「我的收藏」：独立入口，点击只看收藏的乐器（用户要求）
    html += `<div class="tree-item mine" data-mine="1" title="只看已收藏的乐器">
        <span class="label">★ 我的收藏</span>
        <span class="n" id="favCount">${formatNumber((state.favSet || new Set()).size)}</span></div>`;

    for (const [cat, libs] of [...byCat.entries()].sort((a, b) => a[0].localeCompare(b[0], 'zh'))) {
        const catTotal = libs.reduce((s, t) => s + t.count, 0);
        html += `<div class="tree-cat" data-cat="${escapeHtml(cat)}">
            <span class="chev">▶</span>
            <span class="cat-name">${escapeHtml(cat)}</span>
            <span class="cat-n">${libs.length} 库 · ${formatNumber(catTotal)}</span>
        </div>
        <div class="tree-libs" data-cat-libs="${escapeHtml(cat)}" hidden>
            ${libs.map((l) => `<div class="tree-item" data-lib="${l.libraryId}" title="${escapeHtml(l.library)}">
                <span class="label">${escapeHtml(l.library)}</span><span class="n">${formatNumber(l.count)}</span></div>`).join('')}
        </div>`;
    }

    const box = el('instTree');
    box.innerHTML = html;

    // 分类折叠开关
    box.querySelectorAll('.tree-cat').forEach((catRow) => catRow.addEventListener('click', () => {
        const name = catRow.dataset.cat;
        const libs = box.querySelector(`[data-cat-libs="${CSS.escape(name)}"]`);
        const open = catRow.classList.toggle('open');
        if (libs) libs.hidden = !open;
    }));

    // 选择库
    // 「我的收藏」入口
    const mineEl = el('instTree').querySelector('[data-mine]');
    if (mineEl) mineEl.addEventListener('click', () => {
        state.instFavOnly = true;
        state.instLibraryId = 0;
        state.instPage = 0;
        el('instTree').querySelectorAll('.tree-item').forEach((x) => x.classList.toggle('active', x === mineEl));
        loadInstruments();
    });
    box.querySelectorAll('[data-lib]').forEach((node) => node.addEventListener('click', () => {
            state.instFavOnly = false;   // 点具体库时退出「我的收藏」模式
        state.instLibraryId = Number(node.dataset.lib);
        state.instPage = 0;
        box.querySelectorAll('.tree-item').forEach((n) => n.classList.toggle('active', n === node));
        loadInstruments();
    }));

    loadInstruments();
}

async function loadInstruments() {
    if (!state.favSet || state.favSet.size === 0) await loadFavorites();
    const size = computeInstPageSize();
    state.instPageSize = size;
    const offset = state.instPage * size;

    const query = el('instSearch').value.trim();
    const kind = el('instKind').value;
    // 「我的收藏」筛选：state.instFavOnly 或 类型选了 __fav__
    const favOnly = state.instFavOnly || kind === '__fav__';
    const kindReal = kind === '__fav__' ? '' : kind;
    const sort = el('instSort').value;
    const articulation = el('instArt').value;

    let res;
    try {
        res = await bridge.call('instruments', {
            query, libraryId: state.instLibraryId, kind: kindReal, sort, articulation, favoriteOnly: favOnly, limit: size, offset,
        });
    } catch (e) {
        el('instFoot').textContent = '检索失败：' + e.message;
        return;
    }

    state.instItems = res.items;
    state.instTotal = res.total;
    state.instSelected = null;
    el('instDetail').innerHTML = '<div class="hint-row">从左侧选择一个乐器查看详情</div>';
    renderInstrumentList(query);
}

/** 加载收藏集合（乐器 id） */
async function loadFavorites() {
    try {
        const r = await bridge.call('favorites', {});
        state.favSet = new Set(r.ids || []);
    } catch { state.favSet = new Set(); }
}

/** 切换收藏并就地更新界面（不整表重绘，避免闪烁与丢焦点） */
async function toggleFavorite(instrumentId, starEl) {
    try {
        const r = await bridge.call('toggleFavorite', { instrumentId });
        if (!r.ok) { toast(r.message || '操作失败', 'err'); return; }
        if (r.favorite) state.favSet.add(instrumentId); else state.favSet.delete(instrumentId);
        if (starEl) {
            starEl.classList.toggle('on', r.favorite);
            starEl.textContent = r.favorite ? '★' : '☆';
            starEl.title = r.favorite ? '取消收藏' : '加入收藏';
        }
        toast(r.favorite ? '已加入收藏' : '已取消收藏', 'ok');
    } catch (e) { toast('操作失败：' + e.message, 'err'); }
}

function renderInstrumentList(query) {
    const box = el('instList');
    const size = state.instPageSize || 12;
    const totalPages = Math.max(1, Math.ceil(state.instTotal / size));

    if (!state.instItems.length) {
        box.innerHTML = `<div class="empty" style="height:100%">
            <h2>没有匹配的乐器</h2>
            <p>${query ? '试试更短的关键词，或清空搜索框' : '该库下没有 NKI 乐器'}</p></div>`;
        el('instFoot').textContent = '0 个结果';
        el('instPages').innerHTML = '';
        return;
    }

    box.innerHTML = state.instItems.map((i, idx) => `
        <div class="inst-row ${state.instSelected === i.id ? 'active' : ''}" data-idx="${idx}">
        <span class="i-fav ${state.favSet && state.favSet.has(i.id) ? 'on' : ''}" data-fav="${i.id}" title="${state.favSet && state.favSet.has(i.id) ? '取消收藏' : '加入收藏'}">${state.favSet && state.favSet.has(i.id) ? '★' : '☆'}</span>
            <span class="i-name" title="${escapeHtml(i.name)}">${escapeHtml(i.name)}</span>
            <span class="i-lib" title="${escapeHtml(i.libraryName)}">${escapeHtml(i.libraryName)}</span>
            <span class="i-size">${formatBytes(i.sizeBytes)}</span>
            <span class="i-art" title="演奏法：${escapeHtml(i.articulation || '未分类')}">${escapeHtml(i.articulation || '-')}</span>
        </div>`).join('');

        // 收藏星标：阻止冒泡，避免触发行选中
        box.querySelectorAll('[data-fav]').forEach((s) => s.addEventListener('click', (ev) => {
            ev.stopPropagation();
            toggleFavorite(Number(s.dataset.fav), s);
        }));
    box.querySelectorAll('.inst-row').forEach((row) => row.addEventListener('click', () => {
        state.instSelected = state.instItems[Number(row.dataset.idx)].id;
        box.querySelectorAll('.inst-row').forEach((r) => r.classList.toggle('active', r === row));
        renderInstrumentDetail(state.instItems[Number(row.dataset.idx)]);
    }));

    const from = state.instPage * size + 1;
    const to = from + state.instItems.length - 1;
    el('instFoot').textContent =
        `第 ${state.instPage + 1} / ${totalPages} 页 · 显示 ${from}-${to} / 共 ${formatNumber(state.instTotal)} 个` +
        (query ? ` · 「${query}」` : '');

    renderPager(totalPages);
}

function renderPager(totalPages) {
    const box = el('instPages');
    if (totalPages <= 1) { box.innerHTML = ''; return; }

    const cur = state.instPage;
    const btn = (label, page, opts = {}) =>
        `<button class="pager-btn ${opts.active ? 'active' : ''}" data-page="${page}" ${opts.disabled ? 'disabled' : ''}
            ${opts.title ? `title="${opts.title}"` : ''}>${label}</button>`;

    // 页码窗口：首末 + 当前附近，其余用省略号
    const nums = new Set([0, totalPages - 1, cur]);
    for (let d = 1; d <= 2; d++) {
        if (cur - d >= 0) nums.add(cur - d);
        if (cur + d < totalPages) nums.add(cur + d);
    }
    const sorted = [...nums].sort((a, b) => a - b);

    let html = btn('«', 0, { disabled: cur === 0, title: '第一页' });
    html += btn('‹', cur - 1, { disabled: cur === 0, title: '上一页' });

    let prev = -1;
    for (const n of sorted) {
        if (prev >= 0 && n - prev > 1) html += '<span class="pager-dots">…</span>';
        html += btn(String(n + 1), n, { active: n === cur });
        prev = n;
    }

    html += btn('›', cur + 1, { disabled: cur >= totalPages - 1, title: '下一页' });
    html += btn('»', totalPages - 1, { disabled: cur >= totalPages - 1, title: '最后一页' });
    box.innerHTML = html;

    box.querySelectorAll('[data-page]').forEach((b) => b.addEventListener('click', () => {
        const page = Number(b.dataset.page);
        if (page === state.instPage || page < 0 || page >= totalPages) return;
        state.instPage = page;
        loadInstruments();
    }));
}

function renderInstrumentDetail(i) {
    const rows = [
        ['名称', escapeHtml(i.name)],
        ['音色库', escapeHtml(i.libraryName)],
        ['类型', i.kind === 'nkm' ? 'NKM 多轨合奏' : 'NKI 乐器'],
        ['演奏法', i.articulation ? escapeHtml(i.articulation) : '<span style="color:#6f7787">未分类</span>'],
        ['大小', formatBytes(i.sizeBytes)],
        ['引擎版本', i.engineVersion ? escapeHtml(i.engineVersion) : '<span style="color:#6f7787">未标注</span>'],
        ['名称来源', i.source === 'header' ? '读取自文件头' : escapeHtml(i.source || '未知')],
        ['相对路径', `<span class="mono" style="font-size:11px">${escapeHtml(i.relPath)}</span>`],
    ].map(([k, v]) => `<div class="detail-row"><span class="k">${k}</span><span class="v">${v}</span></div>`).join('');

    el('instDetail').innerHTML = `
        <div class="detail-title">${escapeHtml(i.name)}</div>
        ${rows}
        <div class="detail-actions">
            <button class="btn small" id="instOpen">用默认程序打开</button>
            <button class="btn small" id="instReveal">在资源管理器中定位</button>
            <button class="btn small" id="instCopy">复制完整路径</button>
            <button class="btn small" id="instAsk">🤖 问这个库</button>
        </div>
        <div class="audition-box" id="auditionBox">
            <div class="audition-head">
                <span>🎧 试听</span>
                <button class="btn small" id="auditionPick">随机抓几个采样</button>
            </div>
            <div class="audition-body" id="auditionBody">
                <div class="hint-row">从该库的采样中随机挑几个直接播放，快速判断音色是否符合需要。</div>
            </div>
        </div>`;

    el('instOpen').addEventListener('click', async () => {
        const r = await bridge.call('openInstrument', { id: i.id });
        toast(r.message, r.ok ? 'ok' : 'err');
    });
    el('instReveal').addEventListener('click', async () => {
        const r = await bridge.call('revealInstrument', { id: i.id });
        toast(r.message, r.ok ? 'ok' : 'err');
    });
    el('instCopy').addEventListener('click', async () => {
        const r = await bridge.call('copyText', { text: i.fullPath });
        toast(r.message, r.ok ? 'ok' : 'err');
    });
    el('instAsk').addEventListener('click', () => openChatTab(i.libraryId));
    el('auditionPick').addEventListener('click', () => pickAuditionClips(i.libraryId, i.name));
}

/* ══════════ 试听播放器（单例 + 竞态安全）══════════
   设计要点：
     · 全局**单例** audio 元素，不随列表重渲染而重建；
     · **令牌（token）** 机制：每次切换播放都递增令牌，在途的 play() 承诺
       若发现令牌已过期就直接忽略 —— 这消除了
       "The play() request was interrupted by a new load request" 这类误报
       （该错误是切换音频时的正常现象，不是失败）；
     · **按 audio.error.code 分类报错**：
       1=ABORTED 忽略（切换/停止导致）、2=网络/读取、3=解码、4=格式不支持。 */
const audition = {
    el: null,
    token: 0,
    key: '',
    onState: null,

    ensure() {
        if (this.el) return this.el;
        const a = new Audio();
        a.preload = 'none';

        a.addEventListener('ended', () => { this.key = ''; this.notify(); });

        a.addEventListener('error', () => {
            const code = a.error ? a.error.code : 0;
            // 代码 1 = MEDIA_ERR_ABORTED：加载被新请求取代，属正常，静默忽略
            if (code === 1) return;
            this.key = '';
            this.notify();
            const msg = code === 2 ? '读取失败：文件可能被占用或路径不可访问'
                : code === 3 ? '解码失败：文件可能已损坏'
                    : code === 4 ? '浏览器内核不支持该音频格式（如 AIFF）'
                        : '播放失败：未知错误';
            toast(msg, 'err');
        });

        this.el = a;
        return a;
    },

    notify() { if (this.onState) this.onState(this.key); },

    /** 停止并让在途请求失效 */
    stop() {
        this.token++;
        this.key = '';
        const a = this.el;
        if (a) { a.pause(); a.removeAttribute('src'); try { a.load(); } catch { } }
        this.notify();
    },

    /** 播放 / 再次点击停止 */
    async toggle(key, url) {
        const a = this.ensure();

        if (this.key === key && !a.paused) { this.stop(); return; }

        const my = ++this.token;          // 本次请求的令牌
        a.pause();
        a.removeAttribute('src');         // 先清空，避免与上一次加载相互打断
        a.src = url;
        this.key = key;
        this.notify();

        try {
            await a.play();
            if (my !== this.token) return;          // 已被更新的请求取代 → 忽略
        } catch (e) {
            if (my !== this.token) return;          // 同上：被新加载请求打断，属正常
            this.key = '';
            this.notify();
            const name = e && e.name;
            const msg = name === 'NotAllowedError' ? '浏览器暂未允许自动播放，请再点一次播放按钮'
                : name === 'AbortError' ? '播放被中断，请重试'
                    : '播放失败：' + (e && e.message ? e.message : e);
            toast(msg, 'err');
        }
    },
};

/* ══════════ 试听：从库里随机抓几条可播放音频 ══════════ */
async function pickAuditionClips(libraryId, matchName) {
    const body = el('auditionBody');
    if (!body) return;

    audition.stop();                       // 换一批之前先停掉在播的
    body.innerHTML = '<div class="hint-row">正在从采样库中随机挑选…</div>';

    let res;
    try {
        res = await bridge.call('audioClips', { libraryId, limit: 6, matchName: matchName || '' });
    } catch (e) {
        body.innerHTML = `<div class="hint-row">读取失败：${escapeHtml(e.message)}</div>`;
        return;
    }

    if (!res.ok) { body.innerHTML = `<div class="hint-row">${escapeHtml(res.message)}</div>`; return; }

    if (!res.items || !res.items.length) {
        body.innerHTML = `<div class="hint-row">${escapeHtml(res.note || '该库没有可直接播放的采样。')}</div>`;
        return;
    }

    const rows = res.items.map((c, i) => `
        <div class="audition-row" data-i="${i}">
            <button class="audition-play" data-i="${i}" title="播放 / 停止">▶</button>
            <canvas class="audition-wave" data-wave="${i}" width="176" height="26" title="波形（点击可播放）"></canvas>
            <span class="a-name" title="${escapeHtml(c.relPath)}">${escapeHtml(c.name)}</span>
            <span class="a-kind ${c.kind}">${c.kind === 'demo' ? '演示' : c.kind === 'ncw' ? 'NCW' : '采样'}</span>
            <span class="a-size">${formatBytes(c.sizeBytes)}</span>
        </div>`).join('');

    body.innerHTML = `
        <div class="audition-meta">
            可试听 ${res.total} 条（演示音频 ${res.demoCount} · 直接采样 ${res.sampleCount} · NCW 压缩采样 ${res.ncwCount || 0}）· 本次随机取 ${res.items.length} 条
        </div>
        ${rows}
        <div class="audition-note">提示：WAV / OGG / MP3 直接播放；<b>.ncw（NI 专有压缩采样）由本工具本地解码为 WAV 后播放</b>（每次约 10~40 ms，取前 6 秒）。.nkx 单块归档暂不支持。</div>`;

    // 播放状态 → 行高亮与按钮图标
    audition.onState = (key) => {
        body.querySelectorAll('.audition-row').forEach((r) =>
            r.classList.toggle('playing', r.dataset.i === key));
        body.querySelectorAll('.audition-play').forEach((b) =>
            b.textContent = b.dataset.i === key ? '■' : '▶');
    };

    drawWaveforms(res.items, body);   // 后台依次解码画波形

    body.querySelectorAll('.audition-row').forEach((row) => {
        row.addEventListener('click', (ev) => {
            ev.stopPropagation();
            const i = Number(row.dataset.i);
            const clip = res.items[i];
            audition.toggle(String(i), clip.url);
        });
    });
}

/* ══════════ 维护：健康检查 / 杂质清理 / 导出 ══════════ */
const HEALTH_LABEL = {
    junk: ['杂质文件', 'warn'],
    'no-manual': ['无说明书', 'dim'],
    'unknown-version': ['版本未知', 'dim'],
    'no-cover': ['无封面', 'dim'],
};

async function loadMaintain() {
    loadHealth();
    loadJunk();
    loadSnapshots();
}

/* ══════════ 入库快照与回滚 ══════════ */
async function loadSnapshots() {
    const box = el('snapList');
    box.innerHTML = '<div class="hint-row">读取中…</div>';
    let list = [];
    try { list = await bridge.call('snapshotList'); }
    catch (e) { box.innerHTML = `<div class="hint-row">读取失败：${escapeHtml(e.message)}</div>`; return; }

    if (!list.length) { box.innerHTML = '<div class="hint-row">还没有快照。建议在批量入库前先创建一份。</div>'; return; }

    box.innerHTML = list.map((s, i) => `
        <div class="snap-row">
            <div class="snap-main">
                <span class="snap-time">${escapeHtml(s.createdAt)}</span>
                <span class="snap-reason">${escapeHtml(s.reason || '-')}</span>
                <span class="snap-meta">${s.productCount} 个产品${s.hasPortable ? ' · 含便携版配置' : ''} · ${formatBytes(s.sizeBytes)}</span>
            </div>
            <div class="snap-actions">
                <button class="btn small" data-restore="${i}">回滚到此</button>
                <button class="btn small" data-del="${i}">删除</button>
            </div>
        </div>`).join('');

    box.querySelectorAll('[data-restore]').forEach((b) => b.addEventListener('click', async () => {
        const s = list[Number(b.dataset.restore)];
        const yes = await confirmModal('回滚入库状态',
            `将把注册表与便携版配置还原到 <b>${escapeHtml(s.createdAt)}</b> 的状态（${s.productCount} 个产品）。<br><br>` +
            `回滚前会为当前状态<b>自动创建一份快照</b>，所以这一步可以撤销。<br><br>` +
            `⚠ 需要 Kontakt 已完全关闭；需要管理员权限。`);
        if (!yes) return;
        try {
            const res = await bridge.call('snapshotRestore', { path: s.path });
            let msg = res.message;
            if (res.details && res.details.length) msg += '\n' + res.details.join('\n');
            toast(msg, res.ok ? 'ok' : 'err');
            await loadSnapshots();
            await refresh();
        } catch (e) { toast('回滚失败：' + e.message, 'err'); }
    }));

    box.querySelectorAll('[data-del]').forEach((b) => b.addEventListener('click', async () => {
        const s = list[Number(b.dataset.del)];
        const yes = await confirmModal('删除快照', `确定删除 <b>${escapeHtml(s.createdAt)}</b> 的快照？此操作不可恢复。`);
        if (!yes) return;
        const res = await bridge.call('snapshotDelete', { path: s.path });
        toast(res.message, res.ok ? 'ok' : 'err');
        await loadSnapshots();
    }));
}

async function doSnapshotCapture() {
    try {
        const res = await bridge.call('snapshotCapture', { reason: '手动快照' });
        toast(res.message, res.ok ? 'ok' : 'err');
        await loadSnapshots();
    } catch (e) { toast('创建快照失败：' + e.message, 'err'); }
}

async function loadHealth() {
    const box = el('healthBox');
    box.innerHTML = '<div class="hint-row">检查中…</div>';
    let issues = [];
    try { issues = await bridge.call('healthIssues'); }
    catch (e) { box.innerHTML = `<div class="hint-row">检查失败：${escapeHtml(e.message)}</div>`; return; }

    if (!issues.length) { box.innerHTML = '<div class="hint-row">✅ 未发现问题</div>'; return; }

    const groups = new Map();
    for (const it of issues) {
        if (!groups.has(it.kind)) groups.set(it.kind, []);
        groups.get(it.kind).push(it);
    }

    box.innerHTML = [...groups.entries()].map(([kind, list]) => {
        const [label, cls] = HEALTH_LABEL[kind] || [kind, 'dim'];
        const size = list.reduce((s, x) => s + (x.sizeBytes || 0), 0);
        return `<details class="health-group">
            <summary><span class="hg-label ${cls}">${escapeHtml(label)}</span>
                <span class="hg-count">${list.length} 个库${size ? ` · ${formatBytes(size)}` : ''}</span></summary>
            <div class="hg-list">${list.slice(0, 60).map((x) => `
                <div class="hg-row" data-lib="${x.libraryId}">
                    <span class="hg-name">${escapeHtml(x.libraryName)}</span>
                    <span class="hg-detail">${escapeHtml(x.detail)}</span>
                </div>`).join('')}${list.length > 60 ? `<div class="hg-row">…另有 ${list.length - 60} 个</div>` : ''}</div>
        </details>`;
    }).join('');

    box.querySelectorAll('[data-lib]').forEach((row) => row.addEventListener('click', () => {
        const lib = state.libraries.find((l) => l.id === Number(row.dataset.lib));
        if (!lib) return;
        state.filterText = lib.name;
        state.filterCat = ''; state.filterReg = '';
        el('filterText').value = lib.name; el('filterCat').value = ''; el('filterReg').value = '';
        switchTab('libraries');
        renderTable();
    }));
}

let junkItems = [];

function baseName(p) { return (p || '').split('\\').pop(); }
function extOf(p) { const b = baseName(p); const i = b.lastIndexOf('.'); return i > 0 ? b.slice(i + 1).toLowerCase() : ''; }

let junkPage = 0;
const JUNK_PAGE_SIZE = 10;

async function loadJunk() {
    const body = el('junkBody');
    body.innerHTML = '<tr><td colspan="5" class="hint-row">正在读取…</td></tr>';
    try {
        const res = await bridge.call('junkList', {});
        junkItems = res.items || [];
        el('junkSummary').textContent = res.total
            ? res.total + ' 个文件 · ' + formatBytes(res.totalBytes)
            : '没有杂质文件';
        junkPage = 0;
        renderJunkPage();
    } catch (e) {
        body.innerHTML = '<tr><td colspan="5" class="hint-row">读取失败：' + escapeHtml(e.message) + '</td></tr>';
    }
}

function renderJunkPage() {
    const body = el('junkBody');
    const total = junkItems.length;
    const pages = Math.max(1, Math.ceil(total / JUNK_PAGE_SIZE));
    if (junkPage >= pages) junkPage = pages - 1;
    if (junkPage < 0) junkPage = 0;

    const from = junkPage * JUNK_PAGE_SIZE;
    const rows = junkItems.slice(from, from + JUNK_PAGE_SIZE);

    body.innerHTML = rows.length
        ? rows.map((j) => `
            <tr>
                <td><input type="checkbox" class="junk-check" data-path="${escapeHtml(j.fullPath || '')}" /></td>
                <td title="${escapeHtml(j.fullPath || j.relPath || '')}">${escapeHtml(baseName(j.relPath))}</td>
                <td>${escapeHtml(j.libraryName)}</td>
                <td>${escapeHtml(extOf(j.relPath))}</td>
                <td class="num">${formatBytes(j.sizeBytes)}</td>
            </tr>`).join('')
        : '<tr><td colspan="5" class="hint-row">✅ 没有杂质文件</td></tr>';

    const info = el('junkPageInfo');
    if (info) {
        info.textContent = total
            ? `第 ${junkPage + 1} / ${pages} 页 · 共 ${total} 个文件（本页 ${rows.length} 个）`
            : '—';
    }
    const bf = el('btnJunkFirst'), bp = el('btnJunkPrev'), bn = el('btnJunkNext'), bl = el('btnJunkLast');
    if (bf) bf.disabled = junkPage === 0;
    if (bp) bp.disabled = junkPage === 0;
    if (bn) bn.disabled = junkPage >= pages - 1;
    if (bl) bl.disabled = junkPage >= pages - 1;
}

function selectedJunkIds() {
    const ids = [];
    document.querySelectorAll('[data-junk]').forEach((cb) => {
        if (cb.checked) ids.push(junkItems[Number(cb.dataset.junk)].id);
    });
    return ids;
}

async function doJunkDelete(recycle) {
    const ids = selectedJunkIds();
    if (!ids.length) { toast('请先勾选要清理的文件', 'err'); return; }

    const bytes = junkItems.filter((j) => ids.includes(j.id)).reduce((s, j) => s + j.sizeBytes, 0);
    const yes = await confirmModal(recycle ? '送回收站' : '永久删除',
        `将处理 <b>${ids.length}</b> 个杂质文件，释放约 <b>${formatBytes(bytes)}</b>。<br><br>` +
        (recycle ? '文件会送进系统回收站，<b>可以撤销</b>。' : '⚠ 永久删除，<b>不可恢复</b>。') +
        '<br><br>工具只会删除杂质文件，不会碰 .nki / .nkm / 采样 / 说明书。');
    if (!yes) return;

    try {
        const res = await bridge.call('junkDelete', { ids, recycle });
        toast(res.message, res.ok ? 'ok' : 'err');
        if (res.errors && res.errors.length) console.warn(res.errors);
        await loadJunk();
        await refresh();
    } catch (e) { toast('清理失败：' + e.message, 'err'); }
}

async function doExport(format) {
    try {
        const res = await bridge.call('exportLibraries', { format });
        toast(res.message, res.ok ? 'ok' : '');
    } catch (e) { toast('导出失败：' + e.message, 'err'); }
}


/* ══════════ 波形预览 ══════════
   用 Web Audio API 在浏览器端解码（WAV/OGG/MP3 都支持），
   取声道 0 的峰值画柱状波形 —— 一眼分辨短促打击 vs 长音/循环。 */
let __audioCtx = null;
function getAudioCtx() {
    if (!__audioCtx) {
        const Ctx = window.AudioContext || window.webkitAudioContext;
        __audioCtx = new Ctx();
    }
    return __audioCtx;
}

/** 计算 N 段峰值（每段取绝对值最大） */
function computePeaks(audioBuffer, buckets) {
    const data = audioBuffer.getChannelData(0);
    const step = Math.max(1, Math.floor(data.length / buckets));
    const peaks = new Float32Array(buckets);
    for (let b = 0; b < buckets; b++) {
        let max = 0;
        const start = b * step;
        const end = Math.min(data.length, start + step);
        for (let i = start; i < end; i++) {
            const v = data[i] < 0 ? -data[i] : data[i];
            if (v > max) max = v;
        }
        peaks[b] = max;
    }
    return peaks;
}

function paintWave(canvas, peaks, color) {
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    const w = canvas.width, h = canvas.height;
    const dpr = window.devicePixelRatio || 1;
    canvas.width = w * dpr; canvas.height = h * dpr;
    canvas.style.width = w + 'px'; canvas.style.height = h + 'px';
    ctx.scale(dpr, dpr);
    ctx.clearRect(0, 0, w, h);

    const mid = h / 2;
    const n = peaks.length;
    const barW = Math.max(1, w / n - 0.6);
    ctx.fillStyle = color || '#4c8dff';
    for (let i = 0; i < n; i++) {
        const amp = Math.min(1, peaks[i]);
        const bh = Math.max(1, amp * (h - 4));
        const x = (i / n) * w;
        ctx.fillRect(x, mid - bh / 2, barW, bh);
    }
}

/** 后台依次解码并绘制波形（失败画一条静音线） */
async function drawWaveforms(items, body) {
    let ctx;
    try { ctx = getAudioCtx(); } catch { return; }
    for (let i = 0; i < items.length; i++) {
        const canvas = body.querySelector(`[data-wave="${i}"]`);
        if (!canvas) continue;
        try {
            const r = await fetch(items[i].url);
            const buf = await r.arrayBuffer();
            const audio = await ctx.decodeAudioData(buf);
            paintWave(canvas, computePeaks(audio, 64));
        } catch {
            const c2 = canvas.getContext('2d');
            c2.clearRect(0, 0, canvas.width, canvas.height);
            c2.fillStyle = '#39414f';
            c2.fillRect(0, canvas.height / 2 - 1, canvas.width, 2);
        }
    }
}
/* ══════════ 封面编辑器 ══════════
   在 canvas 上按 905×99（Kontakt 官方横幅尺寸）合成封面：
   图片可拖动/缩放构图，文字可拖动定位，虚线框标出文字安全区。
   导出 PNG dataURL 交给后端保存。 */
const cover = {
    libraryId: 0,
    libraryName: '',
    img: null,          // Image 对象
    scale: 1,           // 图片缩放（相对「填满画布」的基准）
    offsetX: 0,         // 图片平移
    offsetY: 0,
    baseScale: 1,       // 填满画布所需的基础缩放
    // 多行文字：每行独立字号（参考 NI 原版封面：左对齐 + 纵向居中）
    lines: [],          // [{ text, size }]
    color: '#ffffff',
    stroke: '#000000',
    // 参考 NI 原版封面：文字横向靠左、纵向居中（905×99 横幅）
    align: 'left',      // left | center
    lineGap: 4,
    textY: 50,          // 文字块垂直中心（画布高 99 → 居中）
    textX: 28,          // 左对齐起点（原版封面左边距 20~30px）
    dragging: null,     // 'image' | 'text'
    dragStart: null,
    ready: false,
};

const COVER_W = 905, COVER_H = 99;
/** Kontakt 封面的文字安全区（官方横幅文字多集中在左侧偏下） */
const TEXT_SAFE = { x: 24, y: 12, w: 857, h: 75 };

function ceEl(id) { return document.getElementById(id); }

async function openCoverEditor(libraryId) {
    const lib = state.libraries.find((l) => l.id === libraryId);
    if (!lib) return;

    cover.libraryId = libraryId;
    cover.libraryName = lib.name;
    ceEl('ceLibName').textContent = lib.name;
    ceEl('coverEditor').hidden = false;
    ceEl('ceHint').innerHTML = lib.coverFile && lib.coverFile.startsWith('custom-')
        ? '该库当前使用的是<b>你自定义的封面</b>。'
        : (lib.hasNicnt
            ? '该库带 .nicnt：保存后<b>本工具内生效</b>；如需写入 Kontakt 官方容器需另做（有风险，暂未实现）。'
            : '该库无 .nicnt（Kontakt 本身不显示封面），保存后<b>在本工具内生效</b>。');
    ceEl('ceStageHint').textContent = '先点「选择图片」载入一张图；拖动可调整构图，滚轮缩放';

    // 若已有封面，载入作为底图
    cover.img = null;
    cover.ready = false;
    if (lib.coverFile) {
        const im = new Image();
        im.onload = () => { cover.img = im; fitCover(); drawCover(); cover.ready = true; };
        im.onerror = () => drawCover();
        im.src = `https://kontakt-covers/${lib.coverFile}`;
    }
    drawCover();
}

function closeCoverEditor() {
    ceEl('coverEditor').hidden = true;
    cover.img = null;
    cover.ready = false;
}

/** 按「填满画布」计算基础缩放 */
function fitCover() {
    if (!cover.img) return;
    const iw = cover.img.naturalWidth, ih = cover.img.naturalHeight;
    cover.baseScale = Math.max(COVER_W / iw, COVER_H / ih);
    cover.scale = 1;
    cover.offsetX = (COVER_W - iw * cover.baseScale) / 2;
    cover.offsetY = (COVER_H - ih * cover.baseScale) / 2;
    ceEl('ceZoom').value = 100;
    ceEl('ceZoomVal').textContent = '100%';
}

function drawCover(forExport) {
    const cv = ceEl('ceCanvas');
    if (!cv) return;
    const ctx = cv.getContext('2d');
    ctx.clearRect(0, 0, COVER_W, COVER_H);

    // 底：深色（无图时的占位）
    ctx.fillStyle = '#1c212a';
    ctx.fillRect(0, 0, COVER_W, COVER_H);

    // 图片
    if (cover.img) {
        const s = cover.baseScale * cover.scale;
        const w = cover.img.naturalWidth * s, h = cover.img.naturalHeight * s;
        ctx.drawImage(cover.img, cover.offsetX, cover.offsetY, w, h);
        // 轻微暗角，保证文字可读
        const g = ctx.createLinearGradient(0, 0, 0, COVER_H);
        g.addColorStop(0, 'rgba(0,0,0,0.12)');
        g.addColorStop(0.55, 'rgba(0,0,0,0.05)');
        g.addColorStop(1, 'rgba(0,0,0,0.45)');
        ctx.fillStyle = g;
        ctx.fillRect(0, 0, COVER_W, COVER_H);
    }

    // 文字安全区（虚线）—— **仅编辑预览时画，导出时绝不画进输出图**
    if (!forExport) {
        ctx.save();
        ctx.setLineDash([6, 5]);
        ctx.strokeStyle = 'rgba(120,170,255,0.55)';
        ctx.lineWidth = 1;
        ctx.strokeRect(TEXT_SAFE.x + 0.5, TEXT_SAFE.y + 0.5, TEXT_SAFE.w - 1, TEXT_SAFE.h - 1);
        ctx.restore();
    }

    // 文字
    // 文字（多行，每行独立字号）
    //   居中模式：整块纵向居中于 textY
    //   左对齐模式：整块中线位于「从下往上 1/3」处（= 距顶 2/3），可再叠加 textY 的微调
    const lines2 = cover.lines.filter((l) => l.text && l.text.length);
    if (lines2.length) {
        const gap = cover.lineGap;
        const totalH = lines2.reduce((s, l) => s + l.size, 0) + gap * (lines2.length - 1);
        const leftMode = cover.align !== 'center';
        let y = leftMode
            ? COVER_H * (2 / 3) - totalH / 2
            : cover.textY - totalH / 2;
        if (leftMode) y += (cover.textY - COVER_H / 2);

        ctx.save();
        ctx.textBaseline = 'top';
        ctx.textAlign = cover.align === 'center' ? 'center' : 'left';
        let maxW = 0;
        for (const l of lines2) {
            ctx.font = `600 ${l.size}px "Microsoft YaHei UI", "Segoe UI", system-ui, sans-serif`;
            if (cover.stroke) {
                ctx.lineWidth = Math.max(2, l.size / 12);
                ctx.strokeStyle = cover.stroke;
                ctx.lineJoin = 'round';
                ctx.strokeText(l.text, cover.textX, y);
            }
            ctx.fillStyle = cover.color;
            ctx.fillText(l.text, cover.textX, y);
            maxW = Math.max(maxW, ctx.measureText(l.text).width);
            y += l.size + gap;
        }
        ctx.restore();

        // 拖动时给文字块加提示框
        if (cover.dragging === 'text') {
            ctx.save();
            ctx.setLineDash([4, 4]);
            ctx.strokeStyle = 'rgba(255,255,255,0.7)';
            const bx = cover.align === 'center' ? cover.textX - maxW / 2 : cover.textX;
            ctx.strokeRect(bx - 6, cover.textY - totalH / 2 - 4, maxW + 12, totalH + 8);
            ctx.restore();
        }
    }
}

/** 画布坐标换算（canvas 显示尺寸可能与内部分辨率不同） */
function cePos(ev) {
    const cv = ceEl('ceCanvas');
    const r = cv.getBoundingClientRect();
    return {
        x: (ev.clientX - r.left) * (COVER_W / r.width),
        y: (ev.clientY - r.top) * (COVER_H / r.height),
    };
}

function hitText(p) {
    const ls = cover.lines.filter((l) => l.text && l.text.length);
    if (!ls.length) return false;
    const ctx = ceEl('ceCanvas').getContext('2d');
    let maxW = 0;
    for (const l of ls) {
        ctx.save();
        ctx.font = `600 ${l.size}px "Microsoft YaHei UI", "Segoe UI", system-ui, sans-serif`;
        maxW = Math.max(maxW, ctx.measureText(l.text).width);
        ctx.restore();
    }
    const totalH = ls.reduce((s, l) => s + l.size, 0) + cover.lineGap * (ls.length - 1);
    const x0 = cover.align === 'center' ? cover.textX - maxW / 2 : cover.textX;
    return p.x >= x0 - 8 && p.x <= x0 + maxW + 8 &&
        p.y >= cover.textY - totalH / 2 - 8 && p.y <= cover.textY + totalH / 2 + 8;
}

function bindCoverEditor() {

/** 按文本域内容生成「每行字号」滑杆（行数变化时重建，尽量保留已有字号） */
function renderLineSizes() {
    const box = ceEl('ceLineSizes');
    const rows = cover.lines;
    box.innerHTML = rows.map((l, i) => `
        <div class="ce-line">
            <span class="n">${i + 1}</span>
            <input type="range" min="12" max="80" value="${l.size}" data-line="${i}" />
            <span class="v">${l.size}</span>
        </div>`).join('');
    box.querySelectorAll('input[data-line]').forEach((r) => r.addEventListener('input', () => {
        const i = Number(r.dataset.line);
        cover.lines[i].size = Number(r.value);
        r.nextElementSibling.textContent = r.value;
        drawCover();
    }));
}

/** 同步文本域 → 行数组（保留已设置的字号，新行用默认字号） */
function syncLinesFromText() {
    const txt = ceEl('ceText').value.replace(/\r/g, '');
    const raw = txt.split('\n');
    const old = cover.lines;
    cover.lines = raw.map((t, i) => ({
        text: t,
        size: (old[i] && old[i].size) ? old[i].size : (i === 0 ? 20 : 26),
    }));
    renderLineSizes();
    drawCover();
}
    ceEl('ceClose').addEventListener('click', closeCoverEditor);

    ceEl('cePick').addEventListener('click', async () => {
        try {
            const res = await bridge.call('pickImage');
            if (!res.ok) { if (res.message !== '已取消') toast(res.message, 'err'); return; }
            const im = new Image();
            im.onload = () => {
                cover.img = im;
                cover.ready = true;
                fitCover();
                if (!cover.text) {
                    // 首次载入图片时，默认填入库名（截断），并放在底部居中
                    const nm = cover.libraryName;
                    ceEl('ceText').value = nm.length > 22 ? nm.slice(0, 22) : nm;
                    cover.textX = 28;
                    cover.textY = COVER_H / 2;
                    syncLinesFromText();
                    cover.textX = 300;   // 靠左
                    cover.textY = COVER_H / 2;   // 纵向居中
                }
                drawCover();
                ceEl('ceStageHint').textContent = `${res.name} · 拖动调整构图，滚轮缩放`;
            };
            im.onerror = () => toast('图片解码失败', 'err');
            im.src = res.dataUrl;
        } catch (e) { toast('选择图片失败：' + e.message, 'err'); }
    });

    // 缩放
    ceEl('ceZoom').addEventListener('input', () => {
        const z = Number(ceEl('ceZoom').value);
        ceEl('ceZoomVal').textContent = z + '%';
        // 以画布中心为锚点缩放
        const cx = COVER_W / 2, cy = COVER_H / 2;
        const old = cover.scale;
        cover.scale = z / 100;
        const k = cover.scale / old;
        cover.offsetX = cx - (cx - cover.offsetX) * k;
        cover.offsetY = cy - (cy - cover.offsetY) * k;
        drawCover();
    });

    ceEl('ceFit').addEventListener('click', () => { fitCover(); drawCover(); });
    ceEl('ceReset').addEventListener('click', () => {
        fitCover();
        ceEl('ceText').value = '';
        syncLinesFromText();
        cover.textX = 300; cover.textY = COVER_H / 2;
        drawCover();
    });

    // 文字属性
    ceEl('ceText').addEventListener('input', syncLinesFromText);
    ceEl('ceLineGap').addEventListener('input', () => {
        cover.lineGap = Number(ceEl('ceLineGap').value);
        ceEl('ceLineGapVal').textContent = cover.lineGap;
        drawCover();
    });
    ceEl('ceColor').addEventListener('input', () => { cover.color = ceEl('ceColor').value; drawCover(); });
    ceEl('ceStroke').addEventListener('input', () => { cover.stroke = ceEl('ceStroke').value; drawCover(); });
    ceEl('ceAlignLeft').addEventListener('click', () => { cover.align = 'left'; cover.textX = 28; drawCover(); });
    ceEl('ceAlignCenter').addEventListener('click', () => { cover.align = 'center'; cover.textX = COVER_W / 2; drawCover(); });
    ceEl('ceCenterV').addEventListener('click', () => { cover.textY = COVER_H / 2; drawCover(); });

    // 拖动：命中文字则移动文字，否则平移图片
    const cv = ceEl('ceCanvas');
    cv.addEventListener('mousedown', (ev) => {
        const p = cePos(ev);
        cover.dragging = hitText(p) ? 'text' : 'image';
        cover.dragStart = { p, offsetX: cover.offsetX, offsetY: cover.offsetY, textX: cover.textX, textY: cover.textY };
        cv.classList.add('dragging');
        drawCover();
    });
    window.addEventListener('mousemove', (ev) => {
        if (!cover.dragging || !cover.dragStart) return;
        const p = cePos(ev);
        const dx = p.x - cover.dragStart.p.x, dy = p.y - cover.dragStart.p.y;
        if (cover.dragging === 'text') {
            cover.textX = Math.max(0, Math.min(COVER_W, cover.dragStart.textX + dx));
            cover.textY = Math.max(0, Math.min(COVER_H, cover.dragStart.textY + dy));
        } else {
            cover.offsetX = cover.dragStart.offsetX + dx;
            cover.offsetY = cover.dragStart.offsetY + dy;
        }
        drawCover();
    });
    window.addEventListener('mouseup', () => {
        if (!cover.dragging) return;
        cover.dragging = null;
        cover.dragStart = null;
        cv.classList.remove('dragging');
        drawCover();
    });

    // 滚轮缩放
    cv.addEventListener('wheel', (ev) => {
        ev.preventDefault();
        const z = Math.max(100, Math.min(400, Number(ceEl('ceZoom').value) + (ev.deltaY < 0 ? 5 : -5)));
        ceEl('ceZoom').value = z;
        ceEl('ceZoom').dispatchEvent(new Event('input'));
    }, { passive: false });

    // 保存
    ceEl('ceSave').addEventListener('click', async () => {
        if (!cover.img) { toast('请先选择一张图片', 'err'); return; }
        try {
            // 导出模式重绘一次：不画虚线安全框（虚线仅用于编辑时定位）
            drawCover(true);
            const png = ceEl('ceCanvas').toDataURL('image/png');
            drawCover(false);
            const res = await bridge.call('saveCover', { libraryId: cover.libraryId, png });
            toast(res.message, res.ok ? 'ok' : 'err');
            if (res.ok) { closeCoverEditor(); await refresh(); }
        } catch (e) { toast('保存失败：' + e.message, 'err'); }
    });
}

/* ══════════ 重复内容检测 ══════════ */
async function doDupSimilar() {
    const box = el('dupResult');
    box.innerHTML = '<div class="hint-row">正在比对库关系…</div>';
    let list = [];
    try { list = await bridge.call('dupSimilar'); }
    catch (err) { box.innerHTML = '<div class="hint-row">失败：' + escapeHtml(err.message) + '</div>'; return; }

    const bad = list.filter((s2) => s2.relation !== 'Overlap');
    if (!bad.length) {
        box.innerHTML = '<div class="hint-row">✅ 未发现需要处理的库关系（没有「完全重复」「完整包含」「版本升级」的情况）</div>';
        return;
    }

    const label = { Identical: '完全重复', Superset: '完整包含', Upgrade: '版本升级' };
    window.__dupList = bad;

    box.innerHTML = bad.map((s2, idx) => {
        const delId = s2.deleteId;
        // 两个库的「侧栏」：显示路径 + 注册状态 + 操作按钮
        const side = (isA) => {
            const name = isA ? s2.nameA : s2.nameB;
            const path = isA ? s2.pathA : s2.pathB;
            const reg = isA ? s2.regA : s2.regB;
            const id = isA ? s2.libraryA : s2.libraryB;
            const isDelete = delId && id === delId;
            const isKeep = delId && id === s2.keepId;
            const cls = isDelete ? 'remove' : isKeep ? 'keep' : '';
            const badge = isDelete ? '<span class="dup-badge del">✗ 建议删除</span>'
                : isKeep ? '<span class="dup-badge keep">✓ 建议保留</span>' : '';
            const regBadge = reg ? '<span class="dup-badge reg">📌 注册表当前指向</span>' : '';
            return `
                <div class="dup-side ${cls}">
                    <div class="dup-side-head">
                        ${badge}${regBadge}
                        <b>${isA ? 'A' : 'B'}：${escapeHtml(name)}</b>
                    </div>
                    <div class="dup-path" title="${escapeHtml(path || '')}">${escapeHtml(path || '（无路径）')}</div>
                    <div class="dup-side-act">
                        <button class="btn dup-del ${isDelete ? 'primary' : ''}" data-dup-del="${id}" data-dup-idx="${idx}">删除此库</button>
                        <button class="btn" data-dup-reg="${id}" data-dup-idx="${idx}" ${reg ? 'disabled' : ''}>转为注册</button>
                    </div>
                </div>`;
        };

        return `
        <div class="dup-card">
            <div class="dup-card-head">
                <span class="dup-tag">${escapeHtml(label[s2.relation] || s2.relation)}</span>
                <span class="dup-pair">${escapeHtml(s2.nameA)} <span class="sub">⇄</span> ${escapeHtml(s2.nameB)}</span>
                <span class="dup-jac">Jaccard ${s2.jaccard}</span>
            </div>
            <div class="dup-meta">
                A: ${s2.totalA} 乐器 · ${formatBytes(s2.sizeA)} · 需 Kontakt ${escapeHtml(s2.versionA || '未知')}
                &nbsp;|&nbsp;
                B: ${s2.totalB} 乐器 · ${formatBytes(s2.sizeB)} · 需 Kontakt ${escapeHtml(s2.versionB || '未知')}
                <br>共享 ${s2.sharedInstruments} 个同名乐器 · 覆盖率 A→B ${(s2.coverA * 100).toFixed(0)}% / B→A ${(s2.coverB * 100).toFixed(0)}%
                ${s2.regPath ? '<br>注册表路径：<code>' + escapeHtml(s2.regPath) + '</code>' : '<br>注册表路径：（两库都未注册）'}
            </div>
            <div class="dup-advice">
                ${escapeHtml(s2.recommendation).replace(/\*\*(.+?)\*\*/g, '<b>$1</b>')}
            </div>
            ${delId ? '<div class="dup-sides">' + side(true) + side(false) + '</div>'
                    : '<div class="dup-sides">' + side(true) + side(false) + '<div class="hint-row">该组为版本升级关系，工具不给出删除建议 —— 请自行确认旧版不再需要。</div></div>'}
            <div class="dup-diff-bar"><button class="btn" data-dup-diff="${idx}">查看差异（A 比 B 多什么 / B 比 A 多什么）</button></div>
            ${s2.needTransferFirst ? '<div class="dup-warn">⚠ 建议删除的那个正是**注册表当前指向**的库。直接删除会让 Kontakt 找不到库 —— 请先点它的「转为注册」把注册信息转到要保留的库，再删除。</div>' : ''}
        </div>`;
    }).join('');

    // 绑定：删除
    box.querySelectorAll('[data-dup-del]').forEach((b) => b.addEventListener('click', () => dupDelete(Number(b.dataset.dupDel), Number(b.dataset.dupIdx))));
    // 绑定：转移注册
    box.querySelectorAll('[data-dup-reg]').forEach((b) => b.addEventListener('click', () => dupTransferReg(Number(b.dataset.dupReg), Number(b.dataset.dupIdx))));
    // 查看差异
    box.querySelectorAll('[data-dup-diff]').forEach((b) => b.addEventListener('click', () => showDupDiff(Number(b.dataset.dupDiff))));
}

/** 删除某个库（带分级确认：删「建议删除」的确认一次；删「建议保留/注册中」的先警告引导转注册） */
async function dupDelete(libId, idx) {
    const s2 = (window.__dupList || [])[idx];
    if (!s2) return;
    const isA = libId === s2.libraryA;
    const name = isA ? s2.nameA : s2.nameB;
    const path = isA ? s2.pathA : s2.pathB;
    const reg = isA ? s2.regA : s2.regB;
    const isRecommended = s2.deleteId && libId === s2.deleteId;
    const keepName = isA ? s2.nameB : s2.nameA;
    const keepId = isA ? s2.libraryB : s2.libraryA;

    // ① 删的是「建议保留」的那个（尤其是注册中的）→ 先警告，引导先转注册
    if (!isRecommended) {
        const msg = reg
            ? `「${name}」是**注册表当前指向**的库。\n\n直接删除会导致 Kontakt 找不到这个库。\n建议：先点「转为注册」把注册信息转到「${keepName}」，再删除本库。`
            : `「${name}」是建议**保留**的那个（另一个更全或与它重复）。\n\n删除它可能不是最优选择。`;
        const go = await confirmModal('⚠ 不建议删除这个库', msg.replace(/\n/g, '<br>'), { okText: '仍要删除', cancelText: '取消' });
        if (!go) return;
        if (reg) {
            const transfer = await confirmModal('先转移注册信息？', `要把注册信息转到「${keepName}」吗？\n（转移后再删除本库，Kontakt 就不会找不到库）`.replace(/\n/g, '<br>'), { okText: '先转移注册', cancelText: '不转移，直接删' });
            if (transfer) { await dupTransferReg(libId, idx); return; }
        }
    }

    // ② 最终确认（用户要求：点击删除后提醒一次再执行）
    const ok = await confirmModal('确认删除音色库', 
        `即将删除：\n<b>${escapeHtml(name)}</b>\n<code>${escapeHtml(path || '')}</code>\n\n` +
        `文件会被放入**回收站**（可从回收站恢复），索引记录会被移除。\n` +
        `预计可回收 ${formatBytes(isA ? s2.sizeA : s2.sizeB)}。`,
        { okText: '确认删除', cancelText: '取消' });
    if (!ok) return;

    let r;
    try { r = await bridge.call('dupDeleteLibrary', { libraryId: libId }); }
    catch (e) { toast('删除失败：' + e.message, 'err'); return; }

    if (!r.ok) {
        if (r.needTransfer) {
            const go2 = await confirmModal('需要先转移注册信息', r.message + '<br><br>要现在把注册信息转到「' + escapeHtml(keepName) + '」吗？', { okText: '转移注册', cancelText: '取消' });
            if (go2) await dupTransferReg(libId, idx);
        } else toast(r.message || '删除失败', 'err');
        return;
    }
    toast(r.message, 'ok');
    await doDupSimilar();
}

/** 把某库的注册信息转移到同组的另一个库 */
/** 查看两个库的文件差异（用户要求：体积差几 MB 时要知道差在哪） */
/** 查看两个库的文件差异（用户要求：体积差几 MB 时要知道差在哪）
 *  **必须走 showModal** —— 弹窗的按钮处理器是在 showModal 的 Promise 里绑定的，
 *  直接改 el('modal').hidden 会导致「关不掉」（实测踩到）。
 */
async function showDupDiff(idx) {
    const s2 = (window.__dupList || [])[idx];
    if (!s2) return;

    // 先用 showModal 打开（这样「关闭」按钮才有处理器），拿到 Promise
    const p = showModal('库文件差异', '<div class="hint-row">正在对比两个库的文件清单…（大库可能要几秒）</div>',
        { okText: '关闭', showCancel: false });

    let d;
    try { d = await bridge.call('dupDiff', { libraryIdA: s2.libraryA, libraryIdB: s2.libraryB }); }
    catch (e) { el('modalBody').innerHTML = '<div class="hint-row">对比失败：' + escapeHtml(e.message) + '</div>'; return p; }
    if (!d.ok) { el('modalBody').innerHTML = '<div class="hint-row">' + escapeHtml(d.message || '对比失败') + '</div>'; return p; }

    const list = (arr, title, color) => arr.length === 0
        ? `<div class="diff-sec"><div class="diff-head ${color}">${title}：无</div></div>`
        : `<div class="diff-sec">
             <div class="diff-head ${color}">${title}（${arr.length} 个${d.truncated ? '，仅显示前 200' : ''}）</div>
             <div class="diff-list">${arr.map((f) => `<div class="diff-row"><span class="diff-path" title="${escapeHtml(f.path)}">${escapeHtml(f.path)}</span><span class="diff-size">${formatBytes(f.bytes)}</span></div>`).join('')}</div>
           </div>`;

    el('modalBody').innerHTML = `
        <div class="hint-row" style="padding:0 0 10px">
            <b>A</b>：${escapeHtml(d.nameA)} <span class="sub">(${d.totalA} 个文件 · ${formatBytes(d.sizeA)})</span><br>
            <code>${escapeHtml(d.pathA || '')}</code><br><br>
            <b>B</b>：${escapeHtml(d.nameB)} <span class="sub">(${d.totalB} 个文件 · ${formatBytes(d.sizeB)})</span><br>
            <code>${escapeHtml(d.pathB || '')}</code>
        </div>
        <div class="diff-sum">
            只在 A 有：<b>${d.onlyACount}</b> 个 · <b>${formatBytes(d.onlyABytes)}</b>
            &nbsp;|&nbsp;
            只在 B 有：<b>${d.onlyBCount}</b> 个 · <b>${formatBytes(d.onlyBBytes)}</b>
        </div>
        ${list(d.onlyA || [], '只在 A 有的文件', 'a')}
        ${list(d.onlyB || [], '只在 B 有的文件', 'b')}`;

    return p;
}

/** 把某个库设为「注册方」（即让 Kontakt 注册表指向它）。
 *  clickedId = 用户点的那个库（**目标**，要成为注册方的）
 *  源 = 本组里当前已注册的那个（若有）
 */
async function dupTransferReg(clickedId, idx) {
    const s2 = (window.__dupList || [])[idx];
    if (!s2) return;

    // **方向修正（原 BUG）**：用户点的是「目标」，不是「源」。
    // 之前把点击的那个当成源，导致弹窗写「从 B 转到 A」——完全反了。
    const targetIsA = clickedId === s2.libraryA;
    const targetName = targetIsA ? s2.nameA : s2.nameB;
    const targetPath = targetIsA ? s2.pathA : s2.pathB;

    const sourceIsA = s2.regA;                       // 当前注册方是 A 吗
    const hasSource = s2.regA || s2.regB;
    const sourceId = sourceIsA ? s2.libraryA : s2.libraryB;
    const sourceName = sourceIsA ? s2.nameA : s2.nameB;

    if (!hasSource) {
        // 本组两个库都没注册 → 不是「转移」，而是直接注册目标库
        const okReg = await confirmModal('注册这个库',
            `本组的两个库当前**都未注册**。\n\n要把「<b>${escapeHtml(targetName)}</b>」注册到 Kontakt 吗？\n` +
            `注册表路径将设为：\n<code>${escapeHtml(targetPath || '')}</code>\n\n` +
            `（需要该库自带 .nicnt；若没有产品键则无法自动注册，需在 Kontakt 里手动入库）`.replace(/\n/g, '<br>'),
            { okText: '确认注册', cancelText: '取消' });
        if (!okReg) return;
        let r0;
        try { r0 = await bridge.call('dupRegisterLibrary', { libraryId: clickedId }); }
        catch (e) { toast('注册失败：' + e.message, 'err'); return; }
        toast(r0.message || (r0.ok ? '已注册' : '注册失败'), r0.ok ? 'ok' : 'err');
        if (r0.ok) await doDupSimilar();
        return;
    }

    const ok = await confirmModal('转移注册信息',
        `把注册信息从「<b>${escapeHtml(sourceName)}</b>」转移到「<b>${escapeHtml(targetName)}</b>」？\n\n` +
        `注册表里的路径将改为：\n<code>${escapeHtml(targetPath || '')}</code>\n\n` +
        `（这会修改 Kontakt 的注册表项，属可逆操作）`.replace(/\n/g, '<br>'),
        { okText: '确认转移', cancelText: '取消' });
    if (!ok) return;

    let r;
    try { r = await bridge.call('dupTransferReg', { fromLibraryId: sourceId, toLibraryId: clickedId }); }
    catch (e) { toast('转移失败：' + e.message, 'err'); return; }
    if (!r.ok) { toast(r.message || '转移失败', 'err'); return; }
    toast(r.message, 'ok');
    await doDupSimilar();
}

async function doDupScan() {
    const box = el('dupResult');
    box.innerHTML = '<div class="hint-row">正在扫描重复大文件…</div>';
    cancelProgressHide();
    el('progressWrap').hidden = false;
    el('progressFill').style.transform = 'scaleX(0)';
    el('progressText').textContent = '正在启动…';
    try {
        const res = await bridge.call('dupScan', { minMb: 5 });
        if (!res.started) { box.innerHTML = `<div class="hint-row">${escapeHtml(res.reason || '无法启动')}</div>`; }
    } catch (e) { box.innerHTML = `<div class="hint-row">失败：${escapeHtml(e.message)}</div>`; }
}

function renderDupResult(msg) {
    const box = el('dupResult');
    const gs = msg.groups || [];
    const head = `<div class="dup-row"><span class="dup-name"><b>重复组 ${msg.groupCount} 个</b></span>
        <span class="dup-badge">可回收</span>
        <span class="dup-size">${formatBytes(msg.reclaimBytes)}</span></div>`;
    if (!gs.length) { box.innerHTML = head + '<div class="hint-row">✅ 没有内容重复的大文件</div>'; return; }

    box.innerHTML = head + gs.map((g) => `
        <div class="dup-row">
            <span class="dup-name">${formatBytes(g.sizeBytes)} × ${g.files.length} 份</span>
            <span class="dup-badge">可回收 ${formatBytes(g.reclaimBytes)}</span>
            <span class="dup-size"></span>
        </div>
        ${g.files.slice(0, 3).map((f) => `<div class="dup-file">[${escapeHtml(f.library)}] ${escapeHtml(f.path.split('\\').pop())}</div>`).join('')}`).join('');
}

/* ══════════ 事件 ══════════ */

/* 问问AI 状态行统计（轮次 / 步数 / token 速度 / 会话 token / 检索命中率） */
/** **真正的「知识库检索」类工具** —— 只有这些才计入「检索命中率」。
    其它工具（query_libraries / library_stats / audition / read_text_file …）不碰知识库，不计入。 */
const RETRIEVAL_TOOLS = new Set([
    'search_manual', 'search_kontakt_doc', 'kontakt_classic_search', 'kontakt_search',
]);
const aiStats = { turns: 0, step: 0, pt: 0, ct: 0, lastSpeed: 0, searches: 0, hit: 0 };

function renderAiStatus() {
    const set = (id, html, cls) => {
        const e = document.getElementById(id);
        if (!e) return;
        e.innerHTML = html;
        e.className = 'ai-st' + (cls ? ' ' + cls : '');
    };
    set('aiStTurn', `轮次 <b>${aiStats.turns}</b>`);
    set('aiStStep', `步数 <b>${aiStats.step}</b>`);
    set('aiStSpeed', `速度 <b>${aiStats.lastSpeed > 0 ? aiStats.lastSpeed.toFixed(1) + ' tok/s' : '—'}</b>`);
    set('aiStTokens', `本会话 token <b>${(aiStats.pt + aiStats.ct).toLocaleString()}</b>`);
    // **命中率要自我解释**：`searches === 0` 表示「这一轮根本没做知识库检索」
    //（模型凭自身理解直接答了，或用的是查索引类工具），不是「检索全失败」。
    // 原来显示一个 `—`，用户无法区分这两种情况（实测被误认为坏了）。
    const rate = aiStats.searches > 0 ? (aiStats.hit / aiStats.searches) * 100 : -1;
    set('aiStHit',
        rate < 0
            ? `检索命中率 <b style="color:#6b7688">本轮无检索</b>`
            : `检索命中率 <b>${rate.toFixed(0)}%</b> <span style="color:#5b6675">(${aiStats.hit}/${aiStats.searches})</span>`,
        rate < 0 ? '' : rate >= 60 ? 'hot' : rate >= 30 ? 'warn' : 'bad');
}

/**
 * **从服务端同步会话级统计**（轮次 / 步数）。
 * 这两个数由 `chatStats` 从库里数出来（chat_messages 的 user 条数、agent_trace 的调用条数），
 * 因此**重启应用也不会归零** —— 旧实现用内存里的计数器，一重启就全变 0（用户反馈）。
 */
async function syncAiStats() {
    if (!chatState.sessionId) return;
    try {
        const r = await bridge.call('chatStats', { sessionId: chatState.sessionId });
        if (r && r.ok) {
            aiStats.turns = r.turns;
            aiStats.step = r.steps;
            renderAiStatus();
        }
    } catch { }
}

/** 新一轮提问：速度清零、并乐观 +1 轮次（随后由 syncAiStats 校准）。 */
function aiStatsNewTurn() {
    aiStats.turns++;
    aiStats.lastSpeed = 0;
    renderAiStatus();
    syncAiStats();
}

function bindEvents() {
    // 标签导航
    document.querySelectorAll('.tab').forEach((b) =>
        b.addEventListener('click', () => switchTab(b.dataset.tab)));
    el('btnSettings').addEventListener('click', () => switchTab('settings'));
    wirePermissionTier();   // 右上角权限档位（2026-09-26）
    el('btnBack').addEventListener('click', async () => {
        switchTab('home');
        await refresh();
    });

    // 维护页
    el('btnHealth').addEventListener('click', loadHealth);
    el('btnHealthFixJunk').addEventListener('click', async () => {
        const yes = await confirmModal('一键清理杂质', '把健康检查列出的**全部杂质文件**删除？<br><br>默认<b>删到回收站</b>（可恢复），不会动任何乐器/采样文件。', { okText: '清理', cancelText: '取消' });
        if (!yes) return;
        toast('正在清理…', 'ok');
        try { const r = await bridge.call('healthFixJunk', {}); toast(r.message, 'ok'); } catch (e) { toast('失败：' + e.message, 'err'); }
        loadHealth();
    });
    el('btnHealthFixCover').addEventListener('click', async () => {
        toast('正在补全封面…（库多时需几十秒）', 'ok');
        try { const r = await bridge.call('healthFixCovers', {}); toast(r.message, 'ok'); } catch (e) { toast('失败：' + e.message, 'err'); }
        loadHealth();
    });
    el('btnSnapReload').addEventListener('click', loadSnapshots);
    el('btnSnapCapture').addEventListener('click', doSnapshotCapture);
    el('btnDupSimilar').addEventListener('click', doDupSimilar);
    /* ── 维护页：孤儿注册表 ── */
    if (el('btnOrphanScan')) el('btnOrphanScan').addEventListener('click', doOrphanScan);
    if (el('btnOrphanCleanAll')) el('btnOrphanCleanAll').addEventListener('click', doOrphanCleanAll);
    if (el('btnOrphanRestore')) el('btnOrphanRestore').addEventListener('click', doOrphanRestore);
    /* ── 维护页：Agent 调用轨迹 ── */
    if (el('btnTraceLoad')) el('btnTraceLoad').addEventListener('click', doTraceLoad);
    if (el('btnTraceStats')) el('btnTraceStats').addEventListener('click', doTraceStatsToggle);
    if (el('btnTraceClear')) el('btnTraceClear').addEventListener('click', doTraceClear);
    // ── 问问AI：截图与粘贴 ──
    el('btnKbBuildAll').addEventListener('click', startKbBatch);
    if (el('btnSuggPrev')) el('btnSuggPrev').addEventListener('click', () => suggStep(-1));
    if (el('btnSuggNext')) el('btnSuggNext').addEventListener('click', () => suggStep(1));
    // （维护页的「提取音色特征」按钮已移除 —— 统一到音色地图页，复用那边的进度条与日志）
    if (el('btnSuggRefresh')) el('btnSuggRefresh').addEventListener('click', () => loadSuggestions(false));
    if (el('btnSuggSlow')) el('btnSuggSlow').addEventListener('click', () => loadSuggestions(true));
    if (el('btnKbLogClear')) el('btnKbLogClear').addEventListener('click', () => {
        const b = el('kbLog'); if (b) { b.innerHTML = ''; b.dataset.started = ''; }
    });
    if (el('btnKbLogFold')) el('btnKbLogFold').addEventListener('click', (ev) => {
        const b = el('kbLog'); if (!b) return;
        const wasHidden = b.hidden; b.hidden = !wasHidden;
        ev.target.textContent = wasHidden ? '收起' : '展开';
    });
    el('btnKbBatchCancel').addEventListener('click', async () => {
        const r = await bridge.call('kbBatchCancel', {});
        toast(r.message || '已请求取消', 'ok');
    });
    // Agent 行内确认条（修 bug：旧实现的 answerShell 从未绑定到按钮，点了没反应）
    el('agentConfirmAllow').addEventListener('click', () => answerShell(true));
    el('agentConfirmDeny').addEventListener('click', () => answerShell(false));
    el('agentConfirmX').addEventListener('click', () => answerShell(false));
    el('btnChatShot').addEventListener('click', pickChatImage);
    el('chatFilePick').addEventListener('change', (ev) => {
        const f = ev.target.files && ev.target.files[0];
        if (!f) return;
        if (!f.type.startsWith('image/')) { toast('请选择图片文件', 'err'); return; }
        if (f.size > 6 * 1024 * 1024) { toast('图片过大（上限 6MB）', 'err'); return; }
        const fr = new FileReader();
        fr.onload = () => { attachImage(fr.result, f.size); el('chatAttachInfo').textContent = f.name + ' · ' + formatBytes(f.size); };
        fr.readAsDataURL(f);
    });
    el('chatAttachDel').addEventListener('click', clearChatImage);
    // 支持直接粘贴图片（Ctrl+V）
    document.addEventListener('paste', (ev) => {
        if (el('tab-chat').hidden) return;
        const items = ev.clipboardData && ev.clipboardData.items;
        if (!items) return;
        for (const it of items) {
            if (it.type && it.type.startsWith('image/')) {
                const f = it.getAsFile();
                if (!f) continue;
                const fr = new FileReader();
                fr.onload = () => { attachImage(fr.result, f.size); };
                fr.readAsDataURL(f);
                ev.preventDefault();
                break;
            }
        }
    });

    // ── 问问AI ──
    // 输入框随内容自动长高（上限由 CSS max-height 控制）
    function autoGrowChatInput() {
        const t = el('chatInput');
        t.style.height = 'auto';
        t.style.height = Math.min(t.scrollHeight, 132) + 'px';
    }
    el('chatInput').addEventListener('input', autoGrowChatInput);
atPathSetup();   // 🔴 @路径补全（2026-09-27）

/**
 * 🔴 **`@` 路径补全**（2026-09-27，用户要求的第 2 项）。
 *
 * 交互：在输入框打 `@` 后继续打字 ⇒ 弹出候选（库根 + 子目录）；
 *   ↑/↓ 选择、Enter/Tab 插入、Esc 关闭、点击也可插入。
 * ⚠️ 只列【白名单内的目录】—— 后端 `pathCandidates` 已限制在音色库根之下。
 */
const atState = { open: false, items: [], sel: 0, start: -1, end: -1, req: 0 };

function atMenuEl() {
    let m = el('atMenu');
    if (m) return m;
    // 🔴 **挂到 body 上、用 fixed 定位**（2026-09-27）——
    //   挂在输入框容器里会被 \`contain: paint\` 裁剪（用户实测「候选被挡住了」）。
    m = document.createElement('div');
    m.id = 'atMenu'; m.className = 'at-menu'; m.hidden = true;
    document.body.appendChild(m);
    return m;
}

/** 把菜单定位到输入框【上方】。 */
function atPosition() {
    const m = el('atMenu');
    const input = el('chatInput');
    if (!m || !input) return;
    const r = input.getBoundingClientRect();
    m.style.left = Math.max(8, r.left) + 'px';
    m.style.width = Math.min(640, Math.max(320, r.width)) + 'px';
    // 输入框上沿往上贴；菜单自身高度由内容决定
    m.style.top = 'auto';
    m.style.bottom = (window.innerHeight - r.top + 8) + 'px';
}

function atClose() {
    atState.open = false; atState.items = []; atState.sel = 0;
    const m = el('atMenu'); if (m) m.hidden = true;
}

/** 找出光标前最近的一个 @token；返回 null 表示不在补全上下文。 */
function atToken(input) {
    const pos = input.selectionStart;
    const before = input.value.slice(0, pos);
    const at = before.lastIndexOf("@");
    if (at < 0) return null;
    // @ 与光标之间不能有换行
    const seg = before.slice(at + 1);
    if (seg.includes("\n")) return null;
    return { start: at, prefix: seg, end: pos };
}

function atRender() {
    const m = atMenuEl();
    if (!atState.items.length) { atClose(); return; }
    m.innerHTML = atState.items.map((it, i) =>
        `<div class="at-item${i === atState.sel ? " sel" : ""}" data-i="${i}">` +
        `<span class="k">${it.kind === "library" ? "库" : "目录"}</span>` +
        `<span class="n">${escapeHtml(it.name || "")}</span>` +
        `<span class="p">${escapeHtml(it.path || "")}</span></div>`).join("");
    atPosition();
    m.hidden = false;
    atState.open = true;
}

async function atQuery(prefix) {
    const my = ++atState.req;
    try {
        const r = await bridge.call('pathCandidates', { prefix });
        if (my !== atState.req) return;   // 过期响应丢弃
        atState.items = (r && r.items) || [];
        atState.sel = 0;
        atRender();
    } catch { atClose(); }
}

function atInsert(path) {
    const input = el("chatInput");
    if (!input || atState.start < 0) return;
    const v = input.value;
    // 路径含空格 ⇒ 用引号包住，避免被当成多个参数
    const txt = /\s/.test(path) ? `@"${path}"` : `@${path}`;
    input.value = v.slice(0, atState.start) + txt + v.slice(atState.end);
    const caret = atState.start + txt.length;
    input.setSelectionRange(caret, caret);
    input.focus();
    atClose();
    autoGrowChatInput();
}

function atKeydown(e) {
    if (!atState.open) return false;
    if (e.key === "ArrowDown") { atState.sel = Math.min(atState.sel + 1, atState.items.length - 1); atRender(); return true; }
    if (e.key === "ArrowUp") { atState.sel = Math.max(atState.sel - 1, 0); atRender(); return true; }
    if (e.key === "Enter" || e.key === "Tab") {
        const it = atState.items[atState.sel];
        if (it) { atInsert(it.path); return true; }
    }
    if (e.key === "Escape") { atClose(); return true; }
    return false;
}

function atPathSetup() {
    const input = el("chatInput");
    if (!input || input.dataset.atWired) return;
    input.dataset.atWired = "1";
    input.addEventListener("input", () => {
        const tk = atToken(input);
        if (!tk) { atClose(); return; }
        atState.start = tk.start; atState.end = tk.end;
        atQuery(tk.prefix);
    });
    input.addEventListener("blur", () => setTimeout(atClose, 180));
    // 窗口尺寸变化时菜单会错位 ⇒ 重算（2026-09-27）
    window.addEventListener('resize', () => { if (atState.open) atPosition(); });
    const m = atMenuEl();
    m.addEventListener('mousedown', (ev) => {
        const it = ev.target.closest('.at-item');
        if (!it) return;
        ev.preventDefault();
        const idx = Number(it.dataset.i);
        const item = atState.items[idx];
        if (item) atInsert(item.path);
    });
}

    el('chatThink').addEventListener('change', () => {
        const v = el('chatThink').value;
        el('chatThink').title = v === 'auto' ? '不指定，由模型自控'
            : v === 'low' ? '省 token，复杂问题可能不够'
            : v === 'high' ? '推理更充分，更慢更贵' : '中等推理投入';
        toast('思考强度：' + el('chatThink').selectedOptions[0].textContent, 'ok');
    });
    el('btnChatSend').addEventListener('click', () => {
        // 流式生成中点一下 = 停止；否则 = 发送（用户要求「发送后变成停止按钮」）
        if (el('btnChatSend').dataset.mode === 'stop') stopChat();
        else sendChat();
    });
    el('chatInput').addEventListener('keydown', (e) => {
        if (atKeydown(e)) return;   // 🔴 @补全菜单优先（2026-09-27）
        if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendChat(); }
    });
    el('btnChatNewCross').addEventListener('click', () => createChatSession(0, 'cross'));
    el('btnChatRename').addEventListener('click', async () => {
        const t = await showInputDialog('重命名会话', chatState.session ? chatState.session.title : '', '输入新的会话名称');
        if (t === null) return;
        await bridge.call('chatSessionRename', { sessionId: chatState.sessionId, title: t });
        await openChatSession(chatState.sessionId);
    });
    el('btnChatDelete').addEventListener('click', async () => {
        const ok = await showModal('删除会话', '确定删除该会话？其下所有分支与消息都会一并删除，不可恢复。',
            { okText: '删除', cancelText: '取消' });
        if (!ok) return;
        await bridge.call('chatSessionDelete', { sessionId: chatState.sessionId });
        chatState.sessionId = 0; chatState.session = null; chatState.branches = [];
        el('chatMsgs').innerHTML = '<div class="chat-empty"><div class="chat-empty-title">问问 AI</div></div>';
        el('chatTitle').textContent = '未选择会话';
        el('btnChatRename').hidden = true; el('btnChatDelete').hidden = true;
        el('chatBranches').innerHTML = ''; el('chatModeTag').hidden = true;
        await loadChatSessions();
    });
    document.querySelectorAll('.chat-side-tabs .cst').forEach((b) => b.addEventListener('click', () => {
        document.querySelectorAll('.chat-side-tabs .cst').forEach((x) => x.classList.toggle('active', x === b));
        el('cstManuals').hidden = b.dataset.cst !== 'manuals';
        el('cstKb').hidden = b.dataset.cst !== 'kb';
        el('cstMem').hidden = b.dataset.cst !== 'mem';
        if (b.dataset.cst === 'mem') loadChatMemories();
    }));
    el('btnDupScan').addEventListener('click', doDupScan);
    bindCoverEditor();
    el('btnJunkReload').addEventListener('click', loadJunk);
    el('btnJunkFirst').addEventListener('click', () => { junkPage = 0; renderJunkPage(); });
    el('btnJunkPrev').addEventListener('click', () => { junkPage--; renderJunkPage(); });
    el('btnJunkNext').addEventListener('click', () => { junkPage++; renderJunkPage(); });
    el('btnJunkLast').addEventListener('click', () => { junkPage = 1e9; renderJunkPage(); });
    el('btnJunkRecycle').addEventListener('click', () => doJunkDelete(true));
    el('btnJunkDelete').addEventListener('click', () => doJunkDelete(false));
    el('btnExportCsv').addEventListener('click', () => doExport('csv'));
    el('btnExportMd').addEventListener('click', () => doExport('md'));

    // 乐器中心
    el('instSearch').addEventListener('input', debounce(() => { state.instPage = 0; loadInstruments(); }, 220));
    el('instKind').addEventListener('change', () => { state.instPage = 0; state.instFavOnly = false; loadInstruments(); });
    el('instSort').addEventListener('change', () => { state.instPage = 0; loadInstruments(); });
    el('instArt').addEventListener('change', () => { state.instPage = 0; loadInstruments(); });

    el('btnAddRoot').addEventListener('click', doAddRoot);
/** 按新规则重扫全部库的说明书清单（剔除 license/readme/changelog 等非手册文档）。 */
async function doRescanManuals() {
    const btn = el('btnRescanManuals');
    if (btn) { btn.disabled = true; btn.textContent = '重扫中…'; }
    try {
        const r = await bridge.call('rescanManuals', {});
        const lines = [r.message || '完成'];
        if (Array.isArray(r.changed) && r.changed.length) {
            lines.push('');
            lines.push('变化最大的库（前 20）：');
            r.changed.slice(0, 20).forEach((x) => lines.push(`  ${x.name}：${x.before} → ${x.after}`));
        }
        // **进度条必须收尾** —— 重扫过程中推的是 scanProgress（会显示进度条），
        // 但旧实现从不调 finishProgress，于是那条进度条永远留在界面上（用户反馈）。
        finishProgress(`重扫完成：${r.libs} 个库，净减 ${(r.removed || 0) - (r.added || 0)} 条`, 3000);
        showModal('重扫说明书清单', `<pre style="white-space:pre-wrap;max-height:50vh;overflow:auto">${escapeHtml(lines.join('\n'))}</pre>`);
        toast(`已重扫 ${r.libs} 个库，净减 ${(r.removed || 0) - (r.added || 0)} 条`, 'ok');
    } catch (e) {
        finishProgress('重扫失败', 3000);
        toast('重扫失败：' + (e && e.message ? e.message : e), 'err');
    } finally {
        if (btn) { btn.disabled = false; btn.textContent = '🧹 重扫说明书清单'; }
        // **无条件兜底**：重扫期间每条 scanProgress 都会 cancelProgressHide() 取消隐藏定时器，
        // 所以只靠 finishProgress 可能被"迟到的进度事件"再次取消 —— 这里挂一个不受它影响的定时器，
        // 3 秒后强制隐藏（用户实测进度条不消失）。
        setTimeout(() => { const w = el('progressWrap'); if (w) w.hidden = true; }, 3000);
    }
}
    el('btnScan').addEventListener('click', startScan);
    if (el('btnRescanManuals')) el('btnRescanManuals').addEventListener('click', doRescanManuals);

    el('btnDetectKontakt').addEventListener('click', async () => {
        await loadCandidates();
        const n = state.candidates.length, ig = state.ignored.length;
        toast(`检测到 ${n} 个可用程序` + (ig ? `，已忽略 ${ig} 个安装包/非程序文件` : ''),
            n ? 'ok' : 'err');
    });
    el('btnAddKontakt').addEventListener('click', async () => {
        const pick = await bridge.call('pickKontaktExe', {});
        if (!pick.picked) return;
        const res = await bridge.call('addKontaktCandidate', { path: pick.path });
        toast(res.message, res.ok ? 'ok' : 'err');
        applyCandidatesResult(res);
    });
    el('btnLaunchKontakt').addEventListener('click', async () => {
        const res = await bridge.call('launchKontakt', {});
        toast(res.message, res.ok ? 'ok' : 'err');
    });
    el('btnClearKontakt').addEventListener('click', async () => {
        const res = await bridge.call('clearKontaktExe');
        toast(res.message, 'ok');
        await loadConfig();
        renderKontaktCurrent();
        await loadCompat();
        await refresh();
    });

    el('btnRefreshReg').addEventListener('click', async () => {
        renderRegistration(await bridge.call('registration'));
        toast('入库状态已刷新', 'ok');
    });

    
// ── 修复注册表视图错位（Kontakt 8 是 64 位程序，只读 HKLM 64 位视图）──
    
el('btnFixRegView').addEventListener('click', async () => {
    
    try {
    
        const r = await bridge.call('registrationViewIssues', {});
    
        if (r.count === 0) { toast('没有视图错位的库，无需修复', 'ok'); return; }
    
        const ok = await confirmModal('修复注册表视图错位',
    
            '发现 <b>' + r.count + '</b> 个库的注册项<b>只在 32 位视图 / HKCU 里</b>。<br><br>' +
    
            'Kontakt 8 是 64 位程序，<b>只读 64 位视图</b>，所以这些库在 Kontakt 里会报「Library not found」。<br><br>' +
    
            '把它们提升到 64 位视图即可修复（其中 ' + r.fixable + ' 个目录真实存在）。',
    
            { okText: '执行修复', cancelText: '取消' });
    
        if (!ok) return;
    
        const p = await bridge.call('registrationPromote', {});
    
        toast(p.message, p.ok ? 'ok' : 'err');
    
        renderRegistration(await bridge.call('registration'));
    
    } catch (e) { toast('修复失败：' + e.message, 'err'); }
    
});

    
// ── 给没有 .nicnt 的库入库（不依赖 .nicnt 元数据）──
    
el('btnRegisterNoNicnt').addEventListener('click', async () => {
    
    try {
    
        const r = await bridge.call('registerWithoutNicnt', {});
    
        toast(r.message, r.ok ? 'ok' : 'err');
    
        renderRegistration(await bridge.call('registration'));
    
    } catch (e) { toast('入库失败：' + e.message, 'err'); }
    
});

/* ══════════ 维护页：Agent 调用轨迹 ══════════ */

/** 加载并渲染轨迹记录表 + 时间概览。 */
async function doTraceLoad() {
    const box = el('traceList');
    if (box) box.innerHTML = '<div class="hint-row">正在加载…</div>';
    let r;
    try { r = await bridge.call('agentTraces', { limit: 500 }); }
    catch (e) { if (box) box.innerHTML = `<div class="hint-row">加载失败：${escapeHtml(e.message)}</div>`; return; }
    const items = (r.items || []).slice().reverse();   // 后端按时间倒序，这里正序显示
    window.__traceItems = items;

    if (!items.length) {
        if (box) box.innerHTML = '<div class="hint-row">暂无轨迹（问一次 AI 后再来看）</div>';
        const ov0 = el('traceOverview'); if (ov0) ov0.innerHTML = '';
        return;
    }

    // ── 时间概览：按 created_at 投影每条记录的开始与耗时（DSH 的 Overview 思路）──
    const ts = items.map((x) => Date.parse((x.createdAt || '').replace(' ', 'T')));
    const valid = ts.filter((t) => !isNaN(t));
    const ov = el('traceOverview');
    if (ov) {
        if (!valid.length) { ov.innerHTML = ''; }
        else {
            const t0 = Math.min(...valid), t1 = Math.max(...valid);
            const span = Math.max(1, t1 - t0);
            ov.innerHTML = items.map((x, i) => {
                const t = ts[i];
                if (isNaN(t)) return '';
                const left = ((t - t0) / span) * 100;
                const w = Math.max(0.3, (Math.max(1, x.ms || 1) / span) * 100);
                const cls = x.ok ? 'trace-bar' : 'trace-bar fail';
                const tip = `${x.tool}  ${x.ms}ms  ${x.ok ? '成功' : '失败'}`;
                return `<div class="${cls}" style="left:${left.toFixed(3)}%;width:${w.toFixed(3)}%" title="${escapeHtml(tip)}" data-trace="${i}"></div>`;
            }).join('');
            ov.querySelectorAll('[data-trace]').forEach((b) =>
                b.addEventListener('click', () => toggleTraceDetail(Number(b.dataset.trace))));
        }
    }

    // ── 记录表 ──
    if (box) {
        box.innerHTML = items.map((x, i) => `
            <div class="trace-row ${x.ok ? '' : 'fail'}" data-tracerow="${i}">
                <span class="trace-step">#${x.step}</span>
                <span class="trace-tool" title="${escapeHtml(x.tool)}">${escapeHtml(x.tool)}</span>
                <span class="trace-args" title="${escapeHtml(x.args || '')}">${escapeHtml(x.args || '')}</span>
                <span class="trace-ms">${x.ms} ms</span>
                <span class="trace-tok" title="本次模型请求的 token：提示 ${x.pt||0} / 补全 ${x.ct||0}（其中思考 ${x.rt||0}）">${(x.pt||0)+(x.ct||0)} tok</span>
                <span class="trace-chars">${(x.chars || 0).toLocaleString()} 字</span>
                <span class="trace-ok">${x.ok ? '✅' : '❌'}</span>
                <span class="trace-time">${escapeHtml(x.createdAt || '')}</span>
            </div>`).join('');
        box.querySelectorAll('[data-tracerow]').forEach((b) =>
            b.addEventListener('click', () => toggleTraceDetail(Number(b.dataset.tracerow))));
    }
    toast(`已加载 ${items.length} 条轨迹`, 'ok');
}

/** 点行展开/收起该条的完整参数与错误。 */
function toggleTraceDetail(i) {
    const x = (window.__traceItems || [])[i];
    if (!x) return;
    const row = document.querySelector(`[data-tracerow="${i}"]`);
    if (!row) return;
    const next = row.nextElementSibling;
    if (next && next.classList.contains('trace-detail')) { next.remove(); return; }
    const d = document.createElement('div');
    d.className = 'trace-detail';
    d.innerHTML =
        `工具：<code>${escapeHtml(x.tool)}</code>　步骤 #${x.step}　会话 ${x.sessionId}　耗时 ${x.ms} ms　返回 ${(x.chars || 0).toLocaleString()} 字<br>` +
        `时间：${escapeHtml(x.createdAt || '')}<br>` +
        `Token：提示 <b>${x.pt||0}</b> / 补全 <b>${x.ct||0}</b>（其中思考 ${x.rt||0}）　合计 <b>${(x.pt||0)+(x.ct||0)}</b><br>` +
        `参数：<code>${escapeHtml(x.args || '（空）')}</code>` +
        (x.error ? `<br><span style="color:#ff9a9a">错误：${escapeHtml(x.error)}</span>` : '');
    row.after(d);
}

/**
 * **工具统计开关**：打开显示统计页（隐藏列表），再点关闭恢复列表。
 * 用户要求：两个视图切换，而不是把统计塞在列表上方（那样会把列表挤扁）。
 */
async function doTraceStatsToggle() {
    const stats = el('traceStats');
    const list = el('traceList');
    const ov = el('traceOverview');
    const btn = el('btnTraceStats');
    if (!stats || !list) return;
    const opening = stats.hidden !== false;   // 当前不可见 → 这次是打开
    if (opening) {
        await doTraceStats();                 // 先算好再显示
        stats.hidden = false;
        list.hidden = true;
        if (ov) ov.hidden = true;
        if (btn) { btn.textContent = '返回列表'; btn.classList.add('primary'); }
    } else {
        stats.hidden = true;
        list.hidden = false;
        if (ov) ov.hidden = false;
        if (btn) { btn.textContent = '工具统计'; btn.classList.remove('primary'); }
    }
}
/** 工具维度统计：调用次数 / 失败次数 / 平均耗时 / 平均返回长度。 */
async function doTraceStats() {
    let r;
    try { r = await bridge.call('agentTraceStats'); }
    catch (e) { toast('统计失败：' + e.message, 'err'); return; }
    const tools = (r.tools || []).slice().sort((a, b) => b.calls - a.calls);
    const box = el('traceStats');
    if (!box) return;
    const totalCalls = tools.reduce((s, t) => s + t.calls, 0);
    const totalFails = tools.reduce((s, t) => s + t.fails, 0);
    const rate = totalCalls ? (((totalCalls - totalFails) / totalCalls) * 100).toFixed(1) : '—';
    box.innerHTML =
        `<div class="trace-stat">总调用 <b>${totalCalls}</b></div>` +
        `<div class="trace-stat">失败 <b>${totalFails}</b></div>` +
        `<div class="trace-stat">成功率 <b>${rate}%</b></div>` +
        `<div class="trace-stat">涉及工具 <b>${tools.length}</b></div>` +
        (window.__traceItems ? `<div class="trace-stat">累计 token <b>${(window.__traceItems.reduce((s,x)=>s+(x.pt||0)+(x.ct||0),0)).toLocaleString()}</b></div>` : '') +
        tools.map((t) =>
            `<div class="trace-stat" title="平均耗时 ${t.avgMs} ms / 平均返回 ${t.avgChars} 字">` +
            `${escapeHtml(t.tool)} <b>${t.calls}</b>${t.fails ? ` <span style="color:#ff9a9a">(${t.fails} 失败)</span>` : ''}` +
            ` <span style="color:#6b7688">${t.avgMs}ms</span></div>`).join('');
}

/** 清空轨迹（需确认）。 */
async function doTraceClear() {
    const yes = await confirmModal('清空调用轨迹',
        `删除 <b>agent_trace</b> 表里的全部记录？<br><br>` +
        `<span style="color:#8b93a3">只删轨迹，不影响会话与知识库。</span>`,
        { okText: '清空', cancelText: '取消' });
    if (!yes) return;
    let r;
    try { r = await bridge.call('agentTraceClear'); }
    catch (e) { toast('清空失败：' + e.message, 'err'); return; }
    toast(`已清空 ${r.cleared} 条轨迹`, 'ok');
    const st = el('traceStats'); if (st) st.innerHTML = '';
    doTraceLoad();
}

/* ══════════ 维护页：孤儿注册表 ══════════ */

/** 扫描孤儿注册表（ContentDir 非空但目录不存在），渲染成列表。 */
async function doOrphanScan() {
    const box = el('orphanList');
    if (box) box.innerHTML = '<div class="hint-row">正在扫描…</div>';
    let r;
    try { r = await bridge.call('orphanPreview'); }
    catch (e) { if (box) box.innerHTML = `<div class="hint-row">扫描失败：${escapeHtml(e.message)}</div>`; return; }
    window.__orphanItems = r.items || [];
    if (box) {
        if (!r.count) { box.innerHTML = '<div class="hint-row">✅ 未发现孤儿注册表项</div>'; return; }
        box.innerHTML = r.items.map((x, i) => `
            <div class="orphan-row">
                <span class="orphan-badge">孤儿</span>
                <span class="orphan-name" title="${escapeHtml(x.key)}">${escapeHtml(x.key)}</span>
                <span class="orphan-dir" title="${escapeHtml(x.contentDir)}">${escapeHtml(x.contentDir)}</span>
                <span class="orphan-view">${escapeHtml(x.view)}</span>
                <span class="orphan-ev" title="判定为 Kontakt 库的依据">${escapeHtml(x.evidence || '')}</span>
                <button class="btn small danger" data-orphandel="${i}">删除</button>
            </div>`).join('');
        box.querySelectorAll('[data-orphandel]').forEach((b) =>
            b.addEventListener('click', () => doOrphanDeleteOne(Number(b.dataset.orphandel))));
    }
    toast(`发现 ${r.count} 个孤儿注册表项`, 'ok');
}

/** 删除单个孤儿项（同样先备份）。 */
async function doOrphanDeleteOne(idx) {
    const item = (window.__orphanItems || [])[idx];
    if (!item) return;
    const yes = await confirmModal('删除单个孤儿注册表项',
        `<b>${escapeHtml(item.key)}</b><br>` +
        `<span style="color:#8b93a3">${escapeHtml(item.view)} · ${escapeHtml(item.contentDir)}</span><br><br>` +
        `该目录不存在。删除前会自动备份（含 Content 条目 / SC XML / NA 记录），可一键还原。`,
        { okText: '删除', cancelText: '取消' });
    if (!yes) return;
    let r;
    try { r = await bridge.call('orphanClean', { view: item.view, key: item.key }); }
    catch (e) { toast('删除失败：' + e.message, 'err'); return; }
    toast(r.message, r.ok ? 'ok' : 'err');
    doOrphanScan();
    renderRegistration(await bridge.call('registration'));
}

/** 一键清理全部孤儿项（先预览确认，再备份删除）。 */
async function doOrphanCleanAll() {
    let pv;
    try { pv = await bridge.call('orphanPreview'); }
    catch (e) { toast('扫描失败：' + e.message, 'err'); return; }
    if (!pv.count) { toast('✅ 未发现孤儿注册表项', 'ok'); return; }
    const list = pv.items.slice(0, 15).map((x) => `· [${x.view}] ${x.key}`).join('<br>');
    const more = pv.count > 15 ? `<br>…另有 ${pv.count - 15} 项` : '';
    const yes = await confirmModal('一键清理孤儿注册表',
        `发现 <b>${pv.count}</b> 个孤儿注册表项（ContentDir 指向的目录不存在）：<br><br>` +
        `${list}${more}<br><br>` +
        `删除前会把三视图的值 + <code>Content\\k2lib*</code> 条目 + SC XML + NA 记录全部备份到 ` +
        `<code>data\\orphan-backups\\&lt;时间戳&gt;\\</code>，之后可一键还原。`,
        { okText: '备份并清理', cancelText: '取消' });
    if (!yes) return;
    let r;
    try { r = await bridge.call('orphanClean', {}); }
    catch (e) { toast('清理失败：' + e.message, 'err'); return; }
    toast(r.message, r.ok ? 'ok' : 'err');
    doOrphanScan();
    renderRegistration(await bridge.call('registration'));
}

/** 一键还原最近一次孤儿清理备份。 */

async function doOrphanRestore() {
    const yes = await confirmModal('还原孤儿注册表',
        `从<b>最近一次</b>「清理孤儿注册表」的备份还原？<br><br>` +
        `<span style="color:#8b93a3">会写回三视图的注册项、<code>Content\\k2lib*</code> 条目、SC XML 与 NA 记录。</span>`,
        { okText: '还原', cancelText: '取消' });
    if (!yes) return;
    let r;
    try { r = await bridge.call('orphanRestore'); }
    catch (e) { toast('还原失败：' + e.message, 'err'); return; }
    toast(r.message, r.ok ? 'ok' : 'err');
    doOrphanScan();
    renderRegistration(await bridge.call('registration'));
}


    el('btnRegisterMissing').addEventListener('click', async () => {
        const rep = state.registration;
        if (!rep) return;
        const targets = rep.items.filter((l) =>
            ['missing', 'path-mismatch', 'incomplete', 'pending-manager'].includes(l.regStatus));
        if (!targets.length) { toast('没有需要入库的音色库', 'ok'); return; }

        const portableNote = rep.portableRoot
            ? `<br><b>⑤ 便携版库列表</b>：自动写入 <code>UserData\\Settings.cfg</code> 与 <code>LibraryHints.xml</code>` +
              `<br><span style="color:#f0c07a">（写入前自动备份，写后校验，失败自动还原；需 Kontakt 完全关闭）</span>`
            : '';

        const yes = await confirmModal('一键入库',
            `将对 <b>${targets.length}</b> 个音色库执行完整入库：<br>` +
            `① HKLM 注册表（64 位）② HKLM（32 位）③ HKCU 用户项<br>` +
            `④ Service Center 记录 <code>&lt;键名&gt;.xml</code>${portableNote}<br><br>` +
            `写入内容全部来自库自带的 <code>.nicnt</code> 元数据，不修改音色文件、不伪造授权字段。`);
        if (!yes) return;
        await doRegister(targets.map((l) => l.id));
    });

    el('btnBatchFinalize').addEventListener('click', doBatchFinalize);
    el('btnTagRefresh').addEventListener('click', renderTagManager);
    el('btnRefreshCompat').addEventListener('click', async () => {
        await loadCompat();
        toast('已重新检查版本兼容性', 'ok');
    });

    // ── AI 助手 ──
    el('btnSaveAi').addEventListener('click', saveAiSettings);
    bindLanguageSelects();
    el('btnTestAi').addEventListener('click', testAiConnection);
    el('btnAiClose').addEventListener('click', () => { el('aiPanel').hidden = true; });
    el('btnKbBuild').addEventListener('click', buildKb);
    el('btnAiClear').addEventListener('click', () => {
        ai.messages = [];
        renderAiMessages();
        toast('已清空当前对话', 'ok');
    });
    el('btnKbDelete').addEventListener('click', async () => {
        const yes = await confirmModal('删除知识库缓存',
            `确定删除「${escapeHtml(ai.libraryName)}」的知识库缓存？<br>` +
            `仅删除工具生成的知识库与页面图，不会动说明书原文件。下次可重新建立。`);
        if (!yes) return;
        const res = await bridge.call('kbDelete', { libraryId: ai.libraryId });
        toast(res.message, res.ok ? 'ok' : 'err');
        await refreshAiKb();
        renderAiMessages();
    });
    el('btnAsk').addEventListener('click', askQuestion);
    el('aiInput').addEventListener('keydown', (e) => {
        if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); askQuestion(); }
    });

    el('filterText').addEventListener('input', (e) => { state.filterText = e.target.value; renderTable(); });
    el('filterCat').addEventListener('change', (e) => { state.filterCat = e.target.value; renderTable(); });
    el('libTagFilter').addEventListener('change', (e) => { state.filterTag = e.target.value; renderTable(); });
    el('filterReg').addEventListener('change', (e) => { state.filterReg = e.target.value; renderTable(); });
    el('sortBy').addEventListener('change', (e) => {
        state.sortBy = e.target.value;
        state.sortDir = ['name', 'category', 'required'].includes(state.sortBy) ? 1 : -1;
        renderTable();
    });
    document.querySelectorAll('th.sortable').forEach((th) => {
        th.addEventListener('click', () => {
            const key = th.dataset.sort;
            if (state.sortBy === key) state.sortDir *= -1;
            else { state.sortBy = key; state.sortDir = ['name', 'category', 'required'].includes(key) ? 1 : -1; }
            el('sortBy').value = ['name', 'size', 'nki', 'junk', 'required'].includes(key) ? key : 'name';
            renderTable();
        });
    });

    bridge.onMessage((msg) => {
        if (msg.type === 'scanProgress') {
            showProgress(msg.progress);
        } else if (msg.type === 'scanDone') {
            // 扫描完成：5 秒后自动收起进度条（下面原有逻辑继续执行）
            const s = msg.summary;
            el('progressFill').style.transform = 'scaleX(1)';
            el('progressText').textContent =
                `完成：${s.roots} 个路径 / ${s.libraries} 个库 / ${formatNumber(s.instruments)} 个乐器 / ${formatBytes(s.bytes)} · 耗时 ${s.seconds}s` +
                (s.errors ? ` · ${s.errors} 个错误` : '');
            finishProgress();   // 5 秒后自动收起进度条
            setScanning(false);
            toast(`扫描完成：${s.libraries} 个音色库`, 'ok');
            refresh().catch(console.error);
            if (state.view === 'settings') {
                bridge.call('roots').then(renderRoots).catch(console.error);
                bridge.call('registration').then(renderRegistration).catch(console.error);
                loadCompat().catch(console.error);
            }
        } else if (msg.type === 'scanError') {
            finishProgress(null, 8000);
            el('progressText').textContent = '扫描失败：' + msg.error;
            setScanning(false);
            toast('扫描失败：' + msg.error, 'err');
        } else if (msg.type === 'kbProgress') {
            // **日志窗口先记** —— 不受 `ai.libraryId` 守卫限制。
            // 旧实现三个 kb 事件都带 `if (msg.libraryId !== ai.libraryId) return;`，
            // 它们只服务旧的 #aiPanel 浮层；从「知识库」面板建库时根本不匹配，
            // 于是「后台跑、没有任何进度提示」（用户实测反馈）。
            kbLogLine('progress', msg);
            if (msg.libraryId !== ai.libraryId) return;
            el('aiProgress').hidden = false;
            const pct = msg.total > 0 ? (msg.current / msg.total) * 100 : 0;
            el('aiProgressFill').style.transform = `scaleX(${(pct / 100).toFixed(4)})`;
            el('aiProgressText').textContent = `${msg.phase} ${msg.current}/${msg.total} ${msg.message || ''}`;
        } else if (msg.type === 'kbDone') {
        kbLogLine('done', msg);
        // **建库完成后必须刷新两个列表** —— 旧实现只刷新旧浮层的 refreshAiKb，
        // 且被 ai.libraryId 守卫挡住 ⇒ 建完库后「知识库」和「说明书」列表仍显示「未建库」
        //（用户实测反馈）。这里在守卫【之前】无条件刷新，任何来源的建库都能生效。
        loadChatKbs();
        loadChatManuals();
        if (chatState.kbs === null) chatState.kbs = null;
            if (msg.libraryId !== ai.libraryId) return;
            const s = msg.summary;
            el('aiProgressFill').style.transform = 'scaleX(1)';
            el('aiProgressText').textContent =
                `完成：${s.pages} 页 / ${s.chunks} 块 / ${formatNumber(s.chars)} 字符` +
                (s.visionPages ? ` / 视觉解析 ${s.visionPages} 页` : '');
            toast(`知识库构建完成（${s.pages} 页）`, 'ok');
            refreshAiKb().then(renderAiMessages);
        } else if (msg.type === 'kbError') {
            kbLogLine('error', msg);
            if (msg.libraryId !== ai.libraryId) return;
            el('aiProgressText').textContent = '构建失败：' + msg.error;
            toast('知识库构建失败：' + msg.error, 'err');
            refreshAiKb();
            } else if (msg.kind === 'usage') {
                // 模型请求的 token 用量 → 速度 = 补全 token / 耗时
                aiStats.step = Math.max(aiStats.step, (msg.step || 0) + 1);
                aiStats.pt += msg.pt || 0;
                aiStats.ct += msg.ct || 0;
                if (msg.ms > 0 && (msg.ct || 0) > 0) aiStats.lastSpeed = (msg.ct * 1000) / msg.ms;
                renderAiStatus();
        } else if (msg.type === 'aiEvent') {
            if (msg.libraryId !== ai.libraryId || !ai.current) return;
            const m = ai.current;
            if (msg.kind === 'delta') {
                m.content += msg.text || '';
                scheduleAiRender();
            } else if (msg.kind === 'reasoning') {
                m.reasoning = (m.reasoning || '') + (msg.text || '');
                scheduleAiRender();
            } else if (msg.kind === 'tool') {
                let label = msg.toolName;
                if (msg.toolName === 'search_manual') {
                    let q = '';
                    try { q = JSON.parse(msg.toolArgs || '{}').query || ''; } catch { q = msg.toolArgs || ''; }
                    label = `检索手册：${q}`;
                } else if (msg.toolName === 'get_library_info') {
                    label = '读取音色库信息';
                }
                m.tools = m.tools || [];
                m.tools.push({ label, args: msg.toolArgs, done: false });
                scheduleAiRender();
            } else if (msg.kind === 'toolresult') {
                const pending = (m.tools || []).filter((t) => !t.done);
                if (pending.length) pending[pending.length - 1].done = true;
                // 检索命中率：只要有一次检索带回了知识块（pages>0）就算命中
                if (pending.length) {
                    aiStats.searches++;
                    if ((msg.pages || 0) > 0) aiStats.hit++;
                }
                renderAiStatus();
                scheduleAiRender();
            }
        } else if (msg.type === 'chatPlan') {
            renderPlanCard(msg);
        } else if (msg.type === 'kbBatch') {
            onKbBatch(msg);
        } else if (msg.type === 'uiConfirm') {
            showUiConfirm(msg.description);
        } else if (msg.type === 'scriptConfirm') {
            showScriptConfirm(msg.language, msg.code);
        } else if (msg.type === 'shellConfirm') {
            // 🔴 修复（2026-09-26）：原先 showShellConfirm 被错放进 chatAudition 分支、而这里空了。
            //   ⚠️ 且曾因【整行被压成一行】导致行注释 // 吞掉了后面的 chatAudition 分支
            //   ⇒ 表现为「后端推了 chatAudition、前端却完全不渲染播放器」（已实测确认）。
            showShellConfirm(msg.command, msg.reason);
        } else if (msg.type === 'chatAudition') {
            // 试听卡渲染（客户端临时控件，重建后由 renderChatMessages 补回）
            renderAudition(msg.items);
        } else if (msg.type === 'chatStep') {
            // **步骤边界** ⇒ 冻结当前块、开新块（对齐 DSH 的「每步一块」）
            sealLiveBlock();
            openLiveBlock();
        } else if (msg.type === 'chatDelta') {
                // 状态驱动：只改数据，渲染交给 rAF 合并
                if (chatState.live) { chatState.live.content += (msg.text || ''); scheduleLivePaint(); }
            } else if (msg.type === 'chatReasoning') {
                if (chatState.live) { chatState.live.reasoning += (msg.text || ''); scheduleLivePaint(); }
            } else if (msg.type === 'chatTool') {
                if (chatState.live) {
                    chatState.live.tools.push({ label: toolLabel(msg.name, msg.args), done: false });
                    scheduleLivePaint();
                }
            } else if (msg.type === 'chatToolResult') {
                if (chatState.live) {
                    const pending = chatState.live.tools.filter((t) => !t.done);
                    if (pending.length) pending[pending.length - 1].done = true;
                    scheduleLivePaint();
                }
                // 状态行：检索命中率 —— **只统计真正的「知识库检索」类工具**。
                // 原来把每个工具结果都算成一次检索，导致 `query_libraries`（查索引，不碰知识库）
                // 也被计入，于是 pages 恒为 0、命中率永远显示 0%（实测误判）。
                if (msg.name && RETRIEVAL_TOOLS.has(msg.name)) {
                    aiStats.searches++;
                    if ((msg.pages || 0) > 0) aiStats.hit++;
                    renderAiStatus();
                }
            } else if (msg.type === 'chatUsage') {
                // 状态行：token 用量与速度（问问AI 标签页走 chatUsage，不是 aiEvent）
                aiStats.step = Math.max(aiStats.step, (msg.step || 0) + 1);
                aiStats.pt += msg.pt || 0;
                aiStats.ct += msg.ct || 0;
                if (msg.ms > 0 && (msg.ct || 0) > 0) aiStats.lastSpeed = (msg.ct * 1000) / msg.ms;
                renderAiStatus();
            } else if (msg.type === 'chatStep') {
                if (msg.step !== undefined) aiStats.step = Math.max(aiStats.step, msg.step + 1);
                renderAiStatus();
            } else if (msg.type === 'chatDone') {
                // **错误卡片**：失败时不要只塞一段文字，要显示「错误码 + 解读 + 建议」，并留在对话里可回看。
                if (msg.error && msg.errorCode) {
                    try {
                        const node = document.getElementById(LIVE_ID);
                        if (node) {
                            const b = node.querySelector('.bubble');
                            if (b) {
                                b.innerHTML =
                                    `<div class="ai-error-card">` +
                                    `<div class="ai-error-code">${escapeHtml(msg.errorCode)}</div>` +
                                    `<div class="ai-error-hint">${escapeHtml(msg.errorHint || '')}</div>` +
                                    `<details class="ai-error-detail"><summary>完整说明</summary>` +
                                    `<pre>${escapeHtml(msg.content || '')}</pre></details>` +
                                    `</div>`;
                                b._raw = msg.content || '';
                            }
                        }
                        toast('AI 请求失败：' + msg.errorCode, 'err');
                        bridge.call('clientLog', {
                            kind: 'aiFail', message: msg.errorCode, source: 'chatDone', stack: (msg.errorHint || ''),
                        });
                    } catch { }
                }
                // 状态驱动：**以 chatDone 的 content 为准**（它是权威的完整答案）。
                // ⚠ 渲染必须包在 try/catch 里，收尾必须放 finally ——
                //    旧写法若 paintLive() 抛异常，finishChatStream() 就永远不执行，
                //    chatState.streaming 卡在 true、发送按钮永久禁用（用户实测报告）。
                try {
                    if (chatState.live) {
                        if (msg.content) chatState.live.content = msg.content;
                        if (msg.reasoning) chatState.live.reasoning = msg.reasoning;
                        if (Array.isArray(chatState.live.tools)) chatState.live.tools.forEach((t) => { t.done = true; });
                        // **先立刻用纯文本收尾**（用户马上看到完整文字，不等 markdown）
                        paintLive();
                        // **再延后一帧做 markdown 富文本渲染** ——
                        // 3000+ 字带表格的回答，markdown 解析是重活；同步做会让窗口「卡一下」。
                        // 放到 rAF 里，先让纯文本上屏、UI 保持可交互，再做富文本替换。
                        const liveRef = chatState.live;
                        liveRef.final = true;
                        requestAnimationFrame(() => {
                            try { paintLive(); } catch { }
                        });
                    }
                } catch (e) {
                    // **绝不静默吞掉**：渲染失败也要让用户看见，并把数据保下来。
                    // 旧写法只 console.warn（用户完全看不到），随后 chatState.live = null 把数据丢掉，
                    // 表现就是「AI 气泡空白、但数据库里其实有完整回答」（实测踩过）。
                    try {
                        bridge.call('clientLog', {
                            kind: 'render',
                            message: 'chatDone 渲染失败：' + (e && e.message ? e.message : e),
                            source: 'chatDone',
                            stack: (e && e.stack) ? String(e.stack).slice(0, 1200) : '',
                        });
                    } catch { }
                    try {
                        // 降级：直接把原文塞进气泡（不经 markdown 渲染），至少用户能看到内容
                        const node = document.getElementById(LIVE_ID);
                        const bubble = node && node.querySelector('.bubble');
                        if (bubble && msg.content) {
                            bubble.textContent = msg.content;
                            bubble._raw = msg.content;
                        }
                        toast('回答渲染出错，已降级为纯文本显示（详见 bridge.log）', 'err');
                    } catch { }
                } finally {
                    chatState.live = null;      // 数据交回数据库，DOM 保留
                    finishChatStream();         // **无论如何都要收尾**
                }
                if (msg.crossSuggest) showCrossSwitchPrompt(msg.crossReason || "");
                } else if (msg.type === 'dupProgress') {
            cancelProgressHide();
            el('progressWrap').hidden = false;
            const pct = msg.total > 0 ? (msg.current / msg.total) : 0;
            el('progressFill').style.transform = `scaleX(${Math.min(1, pct).toFixed(4)})`;
            el('progressText').textContent = msg.phase + ' · ' + msg.message;
        } else if (msg.type === 'dupDone') {
            el('progressFill').style.transform = 'scaleX(1)';
            el('progressText').textContent = `重复检测完成：${msg.groupCount} 组 / 可回收 ${formatBytes(msg.reclaimBytes)}`;
            renderDupResult(msg);
            finishProgress();   // 5 秒后自动收起进度条
        } else if (msg.type === 'dupError') {
            el('progressText').textContent = '重复检测失败：' + msg.error;
            toast('重复检测失败：' + msg.error, 'err');
            finishProgress(null, 8000);
        } else if (msg.type === 'moveProgress') {
            cancelProgressHide();
            el('progressWrap').hidden = false;
            el('progressFill').style.transform = `scaleX(${((msg.percent || 0) / 100).toFixed(4)})`;
            el('progressText').textContent =
                `${msg.phase} · ${msg.message}` + (msg.speedMBps ? ` · ${msg.speedMBps} MB/s` : '');
        } else if (msg.type === 'moveDone') {
            finishProgress();
            el('progressFill').style.transform = 'scaleX(1)';
            el('progressText').textContent = msg.message;
            showMoveResult(msg);
        } else if (msg.type === 'moveError') {
            el('progressText').textContent = '移动失败：' + msg.error;
            toast('移动失败：' + msg.error, 'err');
        }
    });
}

/* ══════════ 启动 ══════════ */
(async function init() {
    if (!bridge.supported) {
        document.body.innerHTML = '<div class="empty"><h2>请通过 KontaktLibManager.exe 启动本界面</h2><p>当前不在 WebView2 宿主中。</p></div>';
        return;
    }
    bindEvents();
    try {
        await loadConfig();
        await refresh();
        switchTab('home');
    } catch (e) {
        el('rootPath').textContent = '初始化失败：' + e.message;
        toast('初始化失败：' + e.message, 'err');
    }
})();
