// ============================================================================
// dsh-plugin/client.js —— NX Copilot 插件 Client 半部分
// 在 cordis_run 卡片内（tool.view.cordis / key: self）渲染桥接状态面板：
// 连接地址与令牌输入、连接按钮、NX 会话信息、最近错误，每 5 秒自动刷新。
// ============================================================================

const S = {
  card: {
    border: '1px solid var(--dsh-border, rgba(128,128,128,.35))',
    borderRadius: 8,
    padding: '8px 10px',
    margin: '4px 0',
    fontFamily: 'inherit',
  },
  title: { fontWeight: 600, fontSize: 13, marginBottom: 6 },
  row: { display: 'flex', alignItems: 'center', gap: 6, marginBottom: 6, flexWrap: 'wrap' },
  input: {
    flex: 1,
    minWidth: 160,
    padding: '4px 8px',
    borderRadius: 6,
    border: '1px solid rgba(128,128,128,.4)',
    background: 'transparent',
    color: 'inherit',
    fontSize: 12,
  },
  btn: {
    padding: '4px 12px',
    borderRadius: 6,
    border: '1px solid rgba(128,128,128,.4)',
    background: 'transparent',
    color: 'inherit',
    fontSize: 12,
    cursor: 'pointer',
  },
  info: { fontSize: 12, opacity: 0.85, marginTop: 4 },
};

function NxPanel({ timer }) {
  const [url, setUrl] = React.useState('http://127.0.0.1:8123');
  const [token, setToken] = React.useState('');
  const [status, setStatus] = React.useState({ connected: false, session: null });
  const [busy, setBusy] = React.useState(false);

  const refresh = React.useCallback(() => {
    host.call('nx.status')
      .then((s) => setStatus(s || {}))
      .catch((e) => setStatus({ connected: false, session: null, lastError: String(e) }));
  }, []);

  React.useEffect(() => {
    refresh();
    return timer.interval(refresh, 5000);
  }, [refresh]);

  const connect = () => {
    setBusy(true);
    host.call('nx.connect', { url, token })
      .then((r) => setStatus(r || {}))
      .catch((e) => setStatus({ connected: false, session: null, lastError: String(e) }))
      .finally(() => setBusy(false));
  };

  const session = status.session;
  const workPart = session && session.workPart;

  return React.createElement(
    'div',
    { style: S.card },
    React.createElement('div', { style: S.title }, 'NX Copilot 桥接状态'),
    React.createElement(
      'div',
      { style: S.row },
      React.createElement('input', {
        style: S.input,
        value: url,
        onChange: (e) => setUrl(e.target.value),
        placeholder: 'http://<nx-host>:8123',
      }),
      React.createElement('input', {
        style: Object.assign({}, S.input, { width: 110 }),
        value: token,
        onChange: (e) => setToken(e.target.value),
        placeholder: 'token',
        type: 'password',
      }),
      React.createElement(
        'button',
        { style: S.btn, onClick: connect, disabled: busy },
        busy ? '连接中…' : '连接',
      ),
    ),
    React.createElement(
      'div',
      { style: S.row },
      React.createElement(
        'span',
        {
          style: {
            display: 'inline-block',
            width: 10,
            height: 10,
            borderRadius: 5,
            marginRight: 6,
            background: status.ok === false || status.lastError
              ? '#e5484d'
              : status.connected
                ? '#30a46c'
                : '#8d8d8d',
          },
        },
      ),
      React.createElement(
        'span',
        { style: { fontSize: 12 } },
        status.connected
          ? '已连接 · 请求数 ' + (status.count || 0)
          : status.ok === false
            ? '连接失败：' + (status.error || '未知错误')
            : '未连接',
      ),
    ),
    session
      ? React.createElement(
          'div',
          { style: S.info },
          'NX ' + (session.nxVersion || '?') +
            ' · ' + ((workPart && workPart.name) || '无打开部件') +
            (workPart && workPart.units ? ' · ' + workPart.units : ''),
        )
      : null,
    status.lastOp
      ? React.createElement('div', { style: S.info }, '最近操作: ' + status.lastOp)
      : null,
  );
}

return {
  inject: ['timer'],
  apply(ctx) {
    const slots = ctx.get('slots');
    if (slots === undefined) return;
    const timer = ctx.timer;

    slots.inject('tool.view.cordis', () => slots.register(
      { name: 'tool.view.cordis', key: 'self' },
      (props) => React.createElement(NxPanel, { timer }),
    ));
  },
};
