'use strict';
const $ = (selector, root = document) => root.querySelector(selector);
const app = $('#app');
const modal = $('#modal');
const state = { user: null, csrf: '', categories: [], modules: [], category: '', selected: null, tab: 'overview',
  filters: { q: '', language: '', environment: '', tag: '' }, dirty: false, busy: false };
const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const prettyDate = value => value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '尚未发布';
const bytes = value => value < 1024 ? `${value} B` : value < 1048576 ? `${(value / 1024).toFixed(1)} KiB` : `${(value / 1048576).toFixed(1)} MiB`;
function notify(message, error = false) {
  const element = $('#toast'); element.textContent = message; element.className = error ? 'error' : ''; element.hidden = false;
  clearTimeout(notify.timer); notify.timer = setTimeout(() => { element.hidden = true; }, error ? 8000 : 4000);
}
async function api(path, options = {}) {
  const headers = new Headers(options.headers);
  const method = options.method || 'GET';
  if (!['GET','HEAD'].includes(method) && state.csrf) headers.set('X-CSRF-Token', state.csrf);
  let body = options.body;
  if (body !== undefined && !(body instanceof FormData)) { headers.set('Content-Type', 'application/json'); body = JSON.stringify(body); }
  const response = await fetch(`/api${path}`, { ...options, method, headers, body, credentials: 'same-origin' });
  if (!response.ok) {
    const data = await response.json().catch(() => ({}));
    if (response.status === 401 && state.user) { state.user = null; state.selected = null; state.dirty = false; await session(); renderAuth(); }
    throw new Error(data.error || ({ 401:'请登录后继续。',403:'没有执行此操作的权限。',404:'内容不存在或无权访问。',429:'操作过于频繁，请稍后再试。' }[response.status] || `请求失败（${response.status}）。`));
  }
  return response.status === 204 ? null : response.json();
}
async function session() {
  const data = await api('/session'); state.user = data.user; state.csrf = data.csrfToken; state.needsSetup = data.needsSetup;
  state.personalMode = data.personalMode; state.version = data.version;
}
async function run(work) {
  if (state.busy) return;
  state.busy = true; app.setAttribute('aria-busy', 'true');
  const buttons = [...document.querySelectorAll('button:not([data-action="close-modal"])')];
  const enabled = buttons.filter(button => !button.disabled); enabled.forEach(button => { button.disabled = true; });
  modal.querySelector('.modal-error')?.remove();
  try { await work(); } catch (error) {
    const message = error.message || '操作失败。'; notify(message, true);
    if (modal.open) { const alert = document.createElement('div'); alert.className = 'auth-error modal-error'; alert.setAttribute('role','alert'); alert.textContent = message; modal.querySelector('.modal-body')?.prepend(alert); }
  }
  finally { state.busy = false; app.removeAttribute('aria-busy'); enabled.forEach(button => { if (button.isConnected) button.disabled = false; }); }
}
function allowDiscard() { return !state.dirty || confirm('有尚未保存的模块信息。放弃这些修改并继续？'); }
function showModal(title, body) {
  modal.innerHTML = `<header class="modal-head"><h2 id="modal-title">${esc(title)}</h2><button type="button" data-action="close-modal" aria-label="关闭">×</button></header><div class="modal-body">${body}</div>`;
  if (!modal.open) modal.showModal();
}
function categoryOptions(current = '', includeEmpty = true) {
  let html = includeEmpty ? '<option value="">未分类</option>' : '';
  function walk(parent, depth) {
    state.categories.filter(x => (x.parentId || '') === parent).forEach(x => {
      html += `<option value="${esc(x.id)}" ${x.id === current ? 'selected' : ''}>${'　'.repeat(depth)}${esc(x.name)}</option>`; walk(x.id, depth + 1);
    });
  }
  walk('', 0); return html;
}
function categoryLabel(id) { return state.categories.find(x => x.id === id)?.name || '未分类'; }
function renderAuth() {
  const setup = state.needsSetup;
  app.innerHTML = `<div class="auth-page"><section class="auth-art"><div class="brand"><span class="brand-mark">{ }</span><div>模块仓库<div class="brand-sub">MODULE REPOSITORY</div></div></div><div><div class="eyebrow">BUILD ON WHAT WORKS</div><div class="diagram">CODE → MODULE → NEXT PROJECT</div><h1>验证过的功能，<br>留给下一次开发。</h1><p>把代码、接口和使用经验整理成模块。让每个新项目，都从已有的积累开始。</p></div><small>模块管理 · 最新内容发布</small></section><section class="auth-side"><div class="auth-card"><div class="eyebrow">${setup ? 'FIRST-TIME SETUP' : 'YOUR WORKSPACE'}</div><h2>${setup ? '创建管理员' : '欢迎回来'}</h2><p>${setup ? '设置账号即可进入，支持中文账号，密码可以留空或使用简单数字。' : '当前已启用账号模式，请输入账号。'}</p><form data-form="${setup ? 'setup' : 'login'}"><div class="field"><label for="auth-username">账号</label><input id="auth-username" name="username" autocomplete="username" maxlength="80" required placeholder="输入账号"></div>${setup ? '<div class="field"><label for="auth-display">显示名称</label><input id="auth-display" name="displayName" maxlength="80" required placeholder="你的名称"></div>' : ''}<div class="field"><label for="auth-password">密码（可留空）</label><input id="auth-password" name="password" type="password" autocomplete="${setup ? 'new-password' : 'current-password'}" maxlength="256" placeholder="可留空，也可以用简单密码"></div><button class="primary" type="submit">${setup ? '创建并进入仓库' : '登录仓库'} →</button></form></div></section></div>`;
}
function sidebar() {
  let nav = `<button class="nav-item ${!state.category ? 'active' : ''}" data-action="category" data-id=""><span class="dot">▦</span>全部模块</button>`;
  function walk(parent, depth) {
    for (const category of state.categories.filter(x => (x.parentId || '') === parent)) {
      nav += `<button class="nav-item ${state.category === category.id ? 'active' : ''}" data-action="category" data-id="${esc(category.id)}"><span class="dot">${'　'.repeat(depth)}⌑</span>${esc(category.name)}</button>`; walk(category.id, depth + 1);
    }
  }
  walk('', 0);
  const accountLinks = state.personalMode ? '' : `${state.user.isAdmin ? '<button class="nav-item" data-action="users">♙　账号管理</button>' : ''}<button class="nav-item" data-action="tokens">⌘　访问令牌</button>`;
  return `<aside class="sidebar"><div class="brand"><span class="brand-mark">{ }</span><div>模块仓库<div class="brand-sub">MODULE REPOSITORY</div></div></div><div><div class="nav-label">LIBRARY / 模块分类</div><nav class="category-nav" aria-label="模块分类">${nav}</nav></div><div class="sidebar-bottom">${state.user.isAdmin ? '<button class="nav-item" data-action="categories">⌑　分类管理</button>' : ''}${accountLinks}<div class="identity"><span class="avatar">${esc(state.user.displayName.slice(0,1))}</span><div><div class="identity-name">${state.personalMode ? '自用模式' : esc(state.user.displayName)}</div><small>${state.personalMode ? '直接管理 · 无需登录' : state.user.isAdmin ? '管理员' : esc(state.user.username)}</small></div></div>${state.personalMode ? '' : '<button class="nav-item" data-action="logout">退出登录 ↗</button>'}</div></aside>`;
}
function render() {
  if (!state.user) return renderAuth();
  app.innerHTML = `<div class="layout">${sidebar()}<main class="main"><div class="topline"><div class="eyebrow">WORKSPACE / ${state.selected ? 'MODULE DETAIL' : 'LIBRARY'}</div><span class="version">v${esc(state.version)} · ${state.personalMode ? '自用模式' : '管理与发布'}</span></div>${state.selected ? detail() : listing()}</main></div>`;
}
function listing() {
  return `<header class="page-header"><div><h1>${state.category ? esc(categoryLabel(state.category)) : '让已有功能，再用一次。'}</h1><p>统一整理代码、文档与适用环境，找到下一个项目可以直接使用的模块。</p></div><div class="actions"><button data-action="refresh">刷新</button><button class="primary" data-action="new-module">＋ 新建模块</button></div></header><form class="filters" data-form="search"><label class="screen-reader" for="search-q">搜索功能</label><input id="search-q" name="q" value="${esc(state.filters.q)}" placeholder="搜索名称、功能或文档…"><label class="screen-reader" for="search-language">语言</label><input id="search-language" name="language" value="${esc(state.filters.language)}" placeholder="开发语言，如 C#"><label class="screen-reader" for="search-environment">应用环境</label><input id="search-environment" name="environment" value="${esc(state.filters.environment)}" placeholder="环境，如 Unity"><label class="screen-reader" for="search-tag">标签</label><input id="search-tag" name="tag" value="${esc(state.filters.tag)}" placeholder="标签"><button type="submit">检索</button></form><div class="results-meta"><span>${state.modules.length} 个可访问模块</span><span>按最近更新排列</span></div>${state.modules.length ? `<div class="module-grid">${state.modules.map(card).join('')}</div>` : '<section class="empty"><div class="empty-icon">{ + }</div><h2>从第一个可复用功能开始</h2><p>这里会显示你有权访问的模块。创建新模块，或调整筛选条件。</p><button class="primary" data-action="new-module">创建模块</button></section>'}`;
}
function card(module) {
  const m = module.metadata;
  return `<article class="module-card"><div class="card-top"><span class="module-symbol">${esc(m.language.slice(0,2))}</span><span class="badge ${!module.enabled ? 'off' : !module.revision ? 'draft' : ''}">${!module.enabled ? '已停用' : module.publishedVersion ? esc(module.publishedVersion) : '未发布草稿'}</span></div><button class="module-title" data-action="open-module" data-id="${esc(module.id)}">${esc(m.name)}</button><p class="card-summary">${esc(m.summary)}</p><div class="tags"><span class="tag">${esc(m.language)}</span><span class="tag">${esc(m.environment)}</span>${m.tags.slice(0,3).map(t => `<span class="tag">${esc(t)}</span>`).join('')}</div><div class="card-footer"><span>${esc(categoryLabel(m.categoryId))}</span><span>${module.canManage ? '可维护 · ' : ''}${module.revision ? `发布 #${module.revision}` : '仅维护者可见'}</span></div></article>`;
}
function detail() {
  const module = state.selected, published = module.publication, m = published?.metadata || module.draft;
  const download = published && module.enabled ? `<a class="download" href="/api/modules/${encodeURIComponent(module.id)}/download?revision=${published.revision}">↓ 下载最新内容</a>` : '';
  const tabs = [['overview','发布内容'], ...(module.canManage ? [['edit','编辑草稿'],['files','草稿文件'],['publish','发布更新']] : []), ...(module.canGrant && !state.personalMode ? [['grants','访问权限']] : [])];
  let content = '';
  if (state.tab === 'overview') content = overview(module);
  else if (state.tab === 'edit') content = metadataForm('edit-module', module.draft);
  else if (state.tab === 'files') content = filesTab(module);
  else if (state.tab === 'publish') content = publishTab(module);
  else if (state.tab === 'grants') content = grantsTab(module);
  return `<button class="back" data-action="back">← 返回模块列表</button><header class="page-header"><div><div class="inline"><h1>${esc(m.name)}</h1><span class="badge ${!module.enabled ? 'off' : !published ? 'draft' : ''}">${!module.enabled ? '已停用' : published ? esc(published.version) : '未发布'}</span></div><p>${esc(m.summary)}</p><div class="detail-id"><code>${esc(module.id)}</code></div></div><div class="actions">${download}<button data-action="refresh-detail">刷新</button></div></header><div class="detail-layout"><section class="panel"><nav class="tabs" aria-label="模块详情">${tabs.map(([id,label]) => `<button class="tab ${state.tab === id ? 'active' : ''}" data-action="tab" data-tab="${id}">${label}</button>`).join('')}</nav>${content}</section><aside class="panel detail-aside"><h3>模块信息</h3><dl class="detail-meta"><dt>所有者</dt><dd>${esc(module.owner.displayName)}<br><small>@${esc(module.owner.username)}</small></dd><dt>分类</dt><dd>${esc(categoryLabel(m.categoryId))}</dd><dt>开发语言</dt><dd>${esc(m.language)}</dd><dt>应用环境</dt><dd>${esc(m.environment)}</dd><dt>最近发布</dt><dd>${prettyDate(published?.publishedAt)}</dd><dt>标签</dt><dd><div class="tags">${m.tags.map(t => `<span class="tag">${esc(t)}</span>`).join('') || '—'}</div></dd></dl><div class="note">仓库提供当前最新内容。项目中的历史、差异和恢复由项目 Git 管理。</div></aside></div>`;
}
function markdown(text) {
  // Treat raw HTML and URLs as text; only render headings, lists, paragraphs and code fences.
  let html = '', paragraph = [], code = [], inCode = false, inList = false;
  const flush = () => { if (paragraph.length) { html += `<p>${esc(paragraph.join('\n'))}</p>`; paragraph = []; } if (inList) { html += '</ul>'; inList = false; } };
  for (const line of String(text || '').split('\n')) {
    if (/^\s*```/.test(line)) {
      if (inCode) { html += `<pre>${esc(code.join('\n'))}</pre>`; code = []; inCode = false; }
      else { flush(); inCode = true; }
      continue;
    }
    if (inCode) { code.push(line); continue; }
    const heading = line.match(/^(#{1,3})\s+(.+)$/);
    if (heading) { flush(); html += `<h${heading[1].length}>${esc(heading[2])}</h${heading[1].length}>`; }
    else if (/^[-*] /.test(line)) { if (!inList) { flush(); html += '<ul>'; inList = true; } html += `<li>${esc(line.slice(2))}</li>`; }
    else if (!line.trim()) flush();
    else { if (inList) flush(); paragraph.push(line); }
  }
  if (inCode) html += `<pre>${esc(code.join('\n'))}</pre>`;
  flush(); return html;
}
function overview(module) {
  const p = module.publication;
  if (!p) return '<div class="empty"><div class="empty-icon">{ … }</div><h2>这个模块还没有发布</h2><p>完善草稿说明和文件后，在“发布更新”中发布第一个版本。</p></div>';
  const m = p.metadata;
  return `<div class="section"><div class="section-title">本次更新 · ${esc(p.version)} / #${p.revision}</div><div class="note">${esc(p.changeNotes)}</div></div><div class="section document">${markdown(m.documentation)}</div>${[['依赖与安装要求',m.dependencies],['已知限制',m.limitations],['验证记录',m.validation],['来源',m.source],['使用许可',m.license]].filter(([,v])=>v).map(([title,value]) => `<section class="section"><h3>${title}</h3><div class="document">${markdown(value)}</div></section>`).join('')}<section class="section"><h3>当前发布文件</h3>${fileTable(module.publishedFiles, false)}</section>`;
}
function metadataForm(form, m = {}) {
  const input = (name,label,placeholder='',required=false) => `<div class="field"><label for="meta-${name}">${label}${required ? ' *' : ''}</label><input id="meta-${name}" name="${name}" value="${esc(m[name])}" placeholder="${esc(placeholder)}" ${required ? 'required' : ''} maxlength="${name === 'summary' ? 500 : name === 'environment' ? 300 : name === 'source' || name === 'license' ? 1000 : 100}"></div>`;
  return `<form data-form="${form}" ${form === 'edit-module' ? 'id="draft-form"' : ''}><div class="form-grid">${form === 'new-module' ? '<div class="field full"><label for="module-id">模块 ID *</label><input id="module-id" name="id" required pattern="[a-z0-9][a-z0-9._-]{1,79}" placeholder="例如 common.file-download"><small>稳定标识，创建后不变。</small></div>' : ''}${input('name','模块名称','例如 支持断点续传的文件下载',true)}<div class="field"><label for="meta-category">主分类</label><select id="meta-category" name="categoryId">${categoryOptions(m.categoryId)}</select></div><div class="field full"><label for="meta-summary">功能摘要 *</label><input id="meta-summary" name="summary" value="${esc(m.summary)}" maxlength="500" required placeholder="这个模块解决什么问题？"></div>${input('language','开发语言','例如 C#',true)}${input('environment','应用环境与适用条件','例如 .NET 10 / Windows；Android 未验证',true)}<div class="field full"><label for="meta-tags">标签</label><input id="meta-tags" name="tags" value="${esc((m.tags || []).join(', '))}" placeholder="用逗号分隔，如 网络, 下载, 进度"></div><div class="field full"><label for="meta-documentation">功能、API 与使用示例（Markdown）</label><textarea id="meta-documentation" name="documentation" rows="12" maxlength="100000" placeholder="说明适用条件、接口、初始化／释放方式，并给出最小使用示例。">${esc(m.documentation)}</textarea><small>发布前必须填写。也可以引用随包上传的详细文档文件。</small></div>${['dependencies','limitations','validation'].map((name,i)=>`<div class="field full"><label for="meta-${name}">${['依赖与安装要求','已知限制','验证方式与环境'][i]}</label><textarea id="meta-${name}" name="${name}" rows="2" maxlength="10000">${esc(m[name] || (name === 'validation' ? '未验证' : ''))}</textarea></div>`).join('')}${input('source','来源地址或作者')}${input('license','使用许可说明')}</div><div class="form-footer"><button class="primary" type="submit">${form === 'new-module' ? '创建模块草稿' : '保存草稿'}</button><small>${form === 'new-module' ? '模块创建后即可编辑、上传和发布。' : `当前草稿修订 #${state.selected?.draftRevision}；保存不影响已发布内容。`}</small></div></form>`;
}
function fileTable(files, draft) {
  if (!files.length) return '<p class="muted">还没有文件。</p>';
  return `<div class="table-wrap"><table><thead><tr><th>相对路径</th><th>大小</th><th>操作</th></tr></thead><tbody>${files.map(file => `<tr><td class="path mono">${esc(file.path)}</td><td>${bytes(file.size)}</td><td><div class="actions"><button data-action="preview" data-kind="${draft ? 'draft' : 'published'}" data-path="${esc(file.path)}">预览</button>${draft ? `<button class="danger" data-action="delete-file" data-path="${esc(file.path)}">移除</button>` : ''}</div></td></tr>`).join('')}</tbody></table></div>`;
}
function filesTab(module) {
  return `<div class="note">上传只更新草稿。选择“替换全部”可移除旧文件，再发布最新内容。模块最多 500 个文件，未压缩总大小不超过 32 MiB。</div><form class="upload-box" data-form="upload"><h3>上传文件</h3><div class="stack"><label for="upload-files" class="screen-reader">选择文件或 ZIP</label><input id="upload-files" name="files" type="file" multiple required><div class="inline"><label for="upload-format">内容格式</label><select id="upload-format" name="format"><option value="files">普通文件（原样保存）</option><option value="zip">解包一个 ZIP，保留相对目录</option></select></div><label class="check"><input type="checkbox" name="replace">替换全部草稿文件（未勾选时按路径新增或覆盖）</label><div><button type="submit" class="primary">上传到草稿</button> <button type="button" data-action="snippet">＋ 输入代码</button></div></div></form><h3>草稿文件 · ${module.draftFiles.length}</h3>${fileTable(module.draftFiles,true)}`;
}
function publishTab(module) {
  return `<div class="note">发布将代码、文档和文件清单一起切换为最新内容。旧版不再提供下载；当前使用者的项目文件不会被远程修改。</div><div class="section"><h3>待发布草稿</h3><p class="muted">${esc(module.draft.name)} · ${module.draftFiles.length} 个文件 · ${bytes(module.draftFiles.reduce((sum,f)=>sum+f.size,0))}</p><p><small>上次发布：${esc(module.publication?.version || '无')}；当前草稿修订 #${module.draftRevision}</small></p></div><form data-form="publish"><div class="form-grid"><div class="field"><label for="publish-version">新版本号 *</label><input id="publish-version" name="version" required maxlength="60" placeholder="例如 1.0.0"></div><div class="field full"><label for="publish-notes">本次更新说明 *</label><textarea id="publish-notes" name="changeNotes" rows="5" maxlength="10000" required placeholder="说明功能变化、API 变化和使用方需要注意的内容。"></textarea></div></div><div class="form-footer"><button type="submit" class="primary" ${!module.enabled ? 'disabled' : ''}>发布最新内容</button></div></form>`;
}
function grantsTab(module) {
  return `<div class="note">使用者可读取和下载已发布内容；维护者还可以修改草稿并发布。所有者与管理员始终具有管理权限。</div><form data-form="grant"><div class="form-grid"><div class="field"><label for="grant-username">账号</label><input id="grant-username" name="username" required placeholder="输入已有账号"></div><div class="field"><label for="grant-role">权限</label><select id="grant-role" name="role"><option value="reader">使用者：读取与下载</option><option value="maintainer">维护者：编辑与发布</option></select></div></div><div class="form-footer"><button type="submit" class="primary">保存授权</button></div></form><hr class="section-divider"><h3>已授权账号</h3><div id="grant-list">正在读取…</div><hr class="section-divider"><h3>模块可用状态</h3><p class="muted">停用后使用者无法检索或下载；已经下载到项目的文件保留。</p><button class="${module.enabled ? 'danger' : 'primary'}" data-action="toggle-module">${module.enabled ? '停用模块' : '重新启用'}</button>`;
}
async function loadList() {
  const query = new URLSearchParams(Object.entries({ ...state.filters, categoryId:state.category }).filter(([,v])=>v));
  state.modules = await api(`/modules?${query}`);
}
async function loadLibrary() { state.categories = await api('/categories'); await loadList(); render(); }
async function selectModule(id, tab = 'overview') {
  const data = await api(`/modules/${encodeURIComponent(id)}`); state.selected = data; state.tab = tab; state.dirty = false;
  if (tab !== 'overview' && !data.canManage) state.tab = 'overview';
  render(); if (state.tab === 'grants') await loadGrants();
}
async function loadGrants() {
  const grants = await api(`/modules/${encodeURIComponent(state.selected.id)}/grants`);
  if (!$('#grant-list')) return;
  $('#grant-list').innerHTML = grants.length ? `<div class="table-wrap"><table><thead><tr><th>账号</th><th>角色</th><th>操作</th></tr></thead><tbody>${grants.map(g => `<tr><td>${esc(g.displayName)} <small>@${esc(g.username)}${!g.enabled ? ' · 已停用' : ''}</small></td><td>${g.role === 'maintainer' ? '维护者' : '使用者'}</td><td><button class="danger" data-action="revoke-grant" data-username="${esc(g.username)}">撤销</button></td></tr>`).join('')}</tbody></table></div>` : '<p class="muted">尚未向其他账号授权。</p>';
}
async function categoriesModal() {
  state.categories = await api('/categories');
  showModal('分类管理', `<form data-form="category"><div class="form-grid"><div class="field"><label for="category-name">分类名称</label><input id="category-name" name="name" required maxlength="80"></div><div class="field"><label for="category-parent">上级分类</label><select id="category-parent" name="parentId">${categoryOptions()}</select></div></div><div class="form-footer"><button class="primary" type="submit">添加分类</button></div></form><hr class="section-divider"><div class="table-wrap"><table><thead><tr><th>分类</th><th>上级</th><th>操作</th></tr></thead><tbody>${state.categories.map(c => `<tr><td>${esc(c.name)}</td><td>${c.parentId ? esc(categoryLabel(c.parentId)) : '顶级分类'}</td><td><div class="actions"><button data-action="edit-category" data-id="${esc(c.id)}">编辑</button><button class="danger" data-action="delete-category" data-id="${esc(c.id)}">删除</button></div></td></tr>`).join('')}</tbody></table></div>`);
}
async function usersModal() {
  const users = await api('/users'); state.users = users;
  showModal('账号管理', `<form data-form="create-user"><div class="form-grid"><div class="field"><label for="user-name">账号</label><input id="user-name" name="username" required maxlength="80" autocomplete="off"></div><div class="field"><label for="user-display">显示名称</label><input id="user-display" name="displayName" required maxlength="80"></div><div class="field full"><label for="user-password">初始密码</label><input id="user-password" name="password" type="password" maxlength="256" autocomplete="new-password"></div></div><label class="check"><input name="isAdmin" type="checkbox">授予管理员权限</label><button class="primary" type="submit">创建账号</button></form><hr class="section-divider"><div class="table-wrap"><table><thead><tr><th>账号</th><th>角色</th><th>状态</th><th>操作</th></tr></thead><tbody>${users.map(u=>`<tr><td>${esc(u.displayName)} <small>@${esc(u.username)}</small></td><td>${u.isAdmin ? '管理员' : '普通用户'}</td><td>${u.enabled ? '启用' : '停用'}</td><td><button data-action="edit-user" data-id="${esc(u.id)}">编辑</button></td></tr>`).join('')}</tbody></table></div>`);
}
async function tokensModal() {
  const tokens = await api('/tokens');
  showModal('只读访问令牌', `<div class="note">用于工具读取你有权访问的模块。令牌不能上传、发布或修改授权。停用账号或撤销模块授权会影响后续访问。</div><form data-form="token"><label for="token-name">令牌名称</label><div class="inline"><input id="token-name" name="name" required maxlength="80" placeholder="例如 开发电脑"><button class="primary" type="submit">创建令牌</button></div></form><hr class="section-divider"><table><thead><tr><th>名称</th><th>创建时间</th><th>操作</th></tr></thead><tbody>${tokens.map(t=>`<tr><td>${esc(t.name)}</td><td>${prettyDate(t.createdAt)}</td><td><button class="danger" data-action="revoke-token" data-id="${esc(t.id)}">撤销</button></td></tr>`).join('')}</tbody></table>`);
}
function metadataFrom(form) {
  const data = Object.fromEntries(new FormData(form));
  data.tags = data.tags.split(/[,，]/).map(x=>x.trim()).filter(Boolean);
  data.categoryId ||= null; delete data.id; return data;
}
document.addEventListener('submit', event => {
  const form = event.target.closest('form[data-form]'); if (!form) return; event.preventDefault();
  run(async () => {
    const data = Object.fromEntries(new FormData(form)); const action = form.dataset.form;
    const id = encodeURIComponent(state.selected?.id || '');
    if (action === 'setup' || action === 'login') {
      await api(`/${action}`, { method:'POST',body:data }); form.reset(); await session(); await loadLibrary();
    } else if (action === 'search') { state.filters = data; await loadList(); render(); }
    else if (action === 'new-module') {
      const created = await api('/modules', {method:'POST',body:{id:data.id,metadata:metadataFrom(form)}});
      modal.close(); await loadList(); await selectModule(created.id,'files'); notify('模块草稿已创建，可以上传文件。');
    } else if (action === 'edit-module') {
      await api(`/modules/${id}/draft`,{method:'PUT',body:{metadata:metadataFrom(form),expectedDraftRevision:state.selected.draftRevision}});
      await selectModule(state.selected.id,'edit'); notify('草稿已保存，已发布内容保持可用。');
    } else if (action === 'upload') {
      const body = new FormData(form); body.set('expectedDraftRevision', state.selected.draftRevision); body.set('mode',data.replace ? 'replace' : 'merge');
      await api(`/modules/${id}/files`,{method:'POST',body}); await selectModule(state.selected.id,'files'); notify('文件已上传到草稿。');
    } else if (action === 'snippet') {
      await api(`/modules/${id}/snippet`,{method:'POST',body:{...data,expectedDraftRevision:state.selected.draftRevision}});
      modal.close(); await selectModule(state.selected.id,'files'); notify('代码已保存到草稿文件。');
    } else if (action === 'publish') {
      await api(`/modules/${id}/publish`,{method:'POST',body:{...data,expectedDraftRevision:state.selected.draftRevision}});
      await selectModule(state.selected.id); await loadList(); notify('最新内容已发布。');
    } else if (action === 'grant') {
      await api(`/modules/${id}/grants`,{method:'PUT',body:data}); form.reset(); await loadGrants(); notify('模块授权已保存。');
    } else if (action === 'category' || action === 'edit-category') {
      const editId = form.dataset.id;
      await api(editId ? `/categories/${editId}` : '/categories',{method:editId ? 'PUT' : 'POST',body:{name:data.name,parentId:data.parentId || null}});
      await categoriesModal(); await loadLibrary(); notify('分类已保存。');
    } else if (action === 'create-user') {
      await api('/users',{method:'POST',body:{...data,isAdmin:!!data.isAdmin}}); form.reset(); await usersModal(); notify('账号已创建。');
    } else if (action === 'edit-user') {
      await api(`/users/${form.dataset.id}`,{method:'PUT',body:{displayName:data.displayName,enabled:!!data.enabled,isAdmin:!!data.isAdmin,newPassword:data.newPassword || null}});
      form.reset(); await session(); if (state.user) { await usersModal(); render(); } else { modal.close(); renderAuth(); } notify('账号已更新，旧登录会话已失效。');
    } else if (action === 'token') {
      const result = await api('/tokens',{method:'POST',body:data});
      showModal('保存访问令牌',`<div class="note warn">令牌只显示这一次。请保存到本机安全设置中，不要写入项目或分享。</div><pre class="secret">${esc(result.secret)}</pre><div class="form-footer"><button data-action="tokens">已保存，返回令牌列表</button></div>`);
    }
  });
});
document.addEventListener('click', event => {
  const button = event.target.closest('[data-action]'); if (!button) return;
  const action = button.dataset.action;
  if (action === 'close-modal') { modal.close(); return; }
  run(async () => {
    const id = encodeURIComponent(state.selected?.id || '');
    if (['new-module','categories','users','tokens'].includes(action) && state.dirty) {
      if (!allowDiscard()) return; state.dirty = false; render();
    }
    if (action === 'new-module') showModal('新建模块',metadataForm('new-module'));
    else if (action === 'category' || action === 'back') {
      if (!allowDiscard()) return; state.dirty = false; state.selected = null; if (action === 'category') state.category = button.dataset.id; await loadList(); render();
    } else if (action === 'open-module') { if (allowDiscard()) await selectModule(button.dataset.id); }
    else if (action === 'refresh') await loadLibrary();
    else if (action === 'refresh-detail') { if (allowDiscard()) await selectModule(state.selected.id,state.tab); }
    else if (action === 'tab') { if (!allowDiscard()) return; state.dirty = false; state.tab = button.dataset.tab; render(); if (state.tab === 'grants') await loadGrants(); }
    else if (action === 'logout') {
      if (!allowDiscard()) return; await api('/logout',{method:'POST'}); modal.close(); state.selected = null; state.dirty = false; await session(); renderAuth();
    } else if (action === 'preview') {
      const preview = await api(`/modules/${id}/preview?${new URLSearchParams({kind:button.dataset.kind,path:button.dataset.path})}`);
      showModal(preview.path,`<pre>${esc(preview.text)}</pre>`);
    } else if (action === 'delete-file') {
      if (!confirm(`从草稿移除 ${button.dataset.path}？已发布内容暂不改变。`)) return;
      await api(`/modules/${id}/files`,{method:'DELETE',body:{path:button.dataset.path,expectedDraftRevision:state.selected.draftRevision}});
      await selectModule(state.selected.id,'files'); notify('文件已从草稿移除。');
    } else if (action === 'snippet') showModal('输入代码',`<form data-form="snippet"><div class="field"><label for="snippet-path">模块内相对路径</label><input id="snippet-path" name="path" required placeholder="例如 src/FileDownload.cs"></div><div class="field"><label for="snippet-content">代码内容</label><textarea class="mono" id="snippet-content" name="content" rows="15" required spellcheck="false"></textarea></div><div class="form-footer"><button class="primary" type="submit">保存到草稿</button></div></form>`);
    else if (action === 'revoke-grant') {
      await api(`/modules/${id}/grants`,{method:'PUT',body:{username:button.dataset.username,role:'none'}}); await loadGrants(); notify('授权已撤销。');
    } else if (action === 'toggle-module') {
      await api(`/modules/${id}/enabled`,{method:'PUT',body:{enabled:!state.selected.enabled}}); await selectModule(state.selected.id,'grants');
    } else if (action === 'categories') await categoriesModal();
    else if (action === 'edit-category') {
      const c = state.categories.find(x=>x.id === button.dataset.id);
      showModal('编辑分类',`<form data-form="edit-category" data-id="${esc(c.id)}"><div class="field"><label for="edit-category-name">名称</label><input id="edit-category-name" name="name" required value="${esc(c.name)}" maxlength="80"></div><div class="field"><label for="edit-category-parent">上级分类</label><select id="edit-category-parent" name="parentId">${categoryOptions(c.parentId)}</select></div><div class="form-footer"><button class="primary" type="submit">保存</button><button type="button" data-action="categories">返回</button></div></form>`);
    } else if (action === 'delete-category') {
      if (!confirm('删除此分类？有子分类或被模块使用的分类不能删除。')) return;
      await api(`/categories/${button.dataset.id}`,{method:'DELETE'}); await categoriesModal(); await loadLibrary();
    } else if (action === 'users') await usersModal();
    else if (action === 'edit-user') {
      const user = state.users.find(x=>x.id === button.dataset.id);
      showModal(`编辑账号 · ${user.username}`,`<form data-form="edit-user" data-id="${esc(user.id)}"><div class="field"><label for="edit-user-display">显示名称</label><input id="edit-user-display" name="displayName" required maxlength="80" value="${esc(user.displayName)}"></div><label class="check"><input type="checkbox" name="enabled" ${user.enabled ? 'checked' : ''}>启用账号</label><label class="check"><input type="checkbox" name="isAdmin" ${user.isAdmin ? 'checked' : ''}>管理员</label><div class="field"><label for="edit-user-password">重置密码（留空则保留）</label><input id="edit-user-password" name="newPassword" type="password" maxlength="256" autocomplete="new-password"></div><div class="form-footer"><button class="primary" type="submit">保存账号</button><button type="button" data-action="users">返回</button></div><small>修改账号会使其旧登录会话失效。</small></form>`);
    } else if (action === 'tokens') await tokensModal();
    else if (action === 'revoke-token') { await api(`/tokens/${button.dataset.id}`,{method:'DELETE'}); await tokensModal(); notify('访问令牌已撤销。'); }
  });
});
document.addEventListener('input', event => { if (event.target.closest('#draft-form')) state.dirty = true; });
window.addEventListener('beforeunload', event => { if (state.dirty) { event.preventDefault(); event.returnValue = ''; } });
modal.addEventListener('close', () => { modal.replaceChildren(); });
run(async () => { await session(); if (state.user) await loadLibrary(); else renderAuth(); });
