import React, { useEffect, useMemo, useState } from 'react';
import { createRoot } from 'react-dom/client';
import Alert from 'antd/es/alert';
import Button from 'antd/es/button';
import Card from 'antd/es/card';
import ConfigProvider from 'antd/es/config-provider';
import theme from 'antd/es/theme';
import Empty from 'antd/es/empty';
import Input from 'antd/es/input';
import Layout from 'antd/es/layout';
import Modal from 'antd/es/modal';
import Popconfirm from 'antd/es/popconfirm';
import Select from 'antd/es/select';
import Slider from 'antd/es/slider';
import Skeleton from 'antd/es/skeleton';
import Switch from 'antd/es/switch';
import Tag from 'antd/es/tag';
import Tooltip from 'antd/es/tooltip';
import message from 'antd/es/message';
import {
  AppstoreOutlined, ArrowRightOutlined, CheckCircleFilled, CloudOutlined,
  DeleteOutlined, EditOutlined, ExperimentOutlined, HomeOutlined, MenuFoldOutlined, MenuUnfoldOutlined,
  MoonOutlined, PlusOutlined, PoweroffOutlined, ReloadOutlined, SettingOutlined, SunOutlined, WifiOutlined,
} from '@ant-design/icons';
import Automations from './Automations.jsx';
import './style.css';
import './theme.css';

const { Sider, Content } = Layout;
const colors = ['#5269e9', '#925ff0', '#ef8847', '#12ad88', '#e75d91', '#339acb'];

async function api(path, options = {}) {
  const response = await fetch(`/api${path}`, {
    ...options,
    headers: { 'Content-Type': 'application/json', ...options.headers },
  });
  if (!response.ok) {
    let detail;
    try { detail = (await response.json()).error; } catch { /* no JSON body */ }
    throw new Error(detail || `Request failed (${response.status})`);
  }
  return response.status === 204 || response.status === 202 ? null : response.json();
}

function App() {
  const [data, setData] = useState({ areas: [], nodes: [], rules: [], schedules: [], timers: [], timeZone: 'Asia/Karachi', broker: { connected: false } });
  const [loading, setLoading] = useState(true);
  const [fetchError, setFetchError] = useState('');
  const [view, setView] = useState('overview');
  const [areaFilter, setAreaFilter] = useState('all');
  const [kindFilter, setKindFilter] = useState('all');
  const [mobile, setMobile] = useState(() => window.matchMedia('(max-width: 760px)').matches);
  const [collapsed, setCollapsed] = useState(() => window.matchMedia('(max-width: 760px)').matches);
  const [dark, setDark] = useState(() => {
    const saved = localStorage.getItem('homi-theme');
    return saved ? saved === 'dark' : window.matchMedia('(prefers-color-scheme: dark)').matches;
  });
  const [editor, setEditor] = useState(null);
  const [busy, setBusy] = useState('');
  const [toast, contextHolder] = message.useMessage();

  const refresh = async (quiet = false) => {
    try {
      const snapshot = await api('/dashboard');
      setData(snapshot);
      setFetchError('');
    } catch (error) {
      setFetchError(error.message);
      if (!quiet) toast.error(error.message);
    } finally { setLoading(false); }
  };

  useEffect(() => {
    refresh();
    const timer = setInterval(() => refresh(true), 3000);
    return () => clearInterval(timer);
  }, []);

  useEffect(() => {
    localStorage.setItem('homi-theme', dark ? 'dark' : 'light');
    document.documentElement.style.colorScheme = dark ? 'dark' : 'light';
  }, [dark]);

  useEffect(() => {
    const media = window.matchMedia('(max-width: 760px)');
    const update = event => { setMobile(event.matches); setCollapsed(event.matches); };
    media.addEventListener('change', update);
    return () => media.removeEventListener('change', update);
  }, []);

  const request = async (path, method, body, success) => {
    try {
      await api(path, { method, body: body === undefined ? undefined : JSON.stringify(body) });
      toast.success(success);
      setEditor(null);
      await refresh(true);
      return true;
    } catch (error) { toast.error(error.message); return false; }
  };

  const commandOutput = async (node, output, command, mode = false) => {
    const key = `${node.id}:${output.index}`;
    setBusy(key);
    try {
      await api(`/nodes/${node.id}/outputs/${output.index}/${mode ? 'mode' : 'command'}`, {
        method: 'POST', body: JSON.stringify(command),
      });
      setTimeout(() => refresh(true), 700);
    } catch (error) { toast.error(error.message); }
    finally { setBusy(''); }
  };

  const filteredNodes = useMemo(() => data.nodes.filter(node =>
    (areaFilter === 'all' || node.areaId === areaFilter) &&
    (kindFilter === 'all' || node.kind === kindFilter)), [data.nodes, areaFilter, kindFilter]);
  const onlineCount = data.nodes.filter(node => node.isOnline).length;
  const activeOutputs = data.nodes.flatMap(node => node.outputs).filter(output => output.isOn).length;
  const automatedTargets = new Set([
    ...data.rules.filter(item => item.enabled).map(item => `${item.actuatorNodeId}:${item.outputIndex}`),
    ...data.schedules.filter(item => item.enabled).map(item => `${item.actuatorNodeId}:${item.outputIndex}`),
    ...data.timers.filter(item => !item.completedAtUtc).map(item => `${item.actuatorNodeId}:${item.outputIndex}`),
  ]);

  const openArea = (area = null) => setEditor({ type: 'area', id: area?.id, name: area?.name || '', color: area?.color || colors[0] });
  const openNode = (node = null) => setEditor({ type: 'node', id: node?.id, deviceId: node?.deviceId || '', name: node?.name || '', areaId: node?.areaId ?? null, kind: node?.kind || 'actuator' });
  const saveEditor = () => {
    if (editor.type === 'area') {
      request(editor.id ? `/areas/${editor.id}` : '/areas', editor.id ? 'PUT' : 'POST',
        { name: editor.name, color: editor.color }, editor.id ? 'Area updated' : 'Area created');
    } else {
      request(editor.id ? `/nodes/${editor.id}` : '/nodes', editor.id ? 'PUT' : 'POST',
        editor.id ? { name: editor.name, areaId: editor.areaId, kind: editor.kind } :
          { deviceId: editor.deviceId, name: editor.name, areaId: editor.areaId, kind: editor.kind },
        editor.id ? 'Node updated' : 'Node added');
    }
  };

  const navigate = (next) => { setView(next); setAreaFilter('all'); setKindFilter('all'); if (mobile) setCollapsed(true); };
  const viewTitle = { overview: 'Overview', areas: 'Areas', nodes: 'Nodes', automations: 'Automations' }[view];

  return <ConfigProvider theme={{ algorithm: dark ? theme.darkAlgorithm : theme.defaultAlgorithm, token: {
    colorPrimary: dark ? '#9aacff' : '#5269df', borderRadius: 12,
    colorText: dark ? '#eaf0fb' : '#1b2943',
    colorTextSecondary: dark ? '#abbad0' : '#687a96',
    colorBgBase: dark ? '#131f31' : '#ffffff',
    fontFamily: 'Inter, ui-sans-serif, system-ui, sans-serif',
  } }}>
    {contextHolder}
    <Layout className={`shell${dark ? ' app-dark' : ''}`}>
      <Sider width={246} collapsedWidth={76} collapsed={collapsed} className="sidebar" theme={dark ? 'dark' : 'light'}>
        <div className="brand" onClick={() => navigate('overview')}>
          <div className="brand-mark"><HomeOutlined /></div>
          {!collapsed && <div><strong>homi.</strong><small>CONTROL CENTER</small></div>}
        </div>
        <div className="nav-caption">{collapsed ? '•' : 'WORKSPACE'}</div>
        <nav className="nav">
          <button className={view === 'overview' ? 'nav-item active' : 'nav-item'} onClick={() => navigate('overview')}><AppstoreOutlined /><span>Overview</span></button>
          <button className={view === 'areas' ? 'nav-item active' : 'nav-item'} onClick={() => navigate('areas')}><HomeOutlined /><span>Areas</span></button>
          <button className={view === 'nodes' ? 'nav-item active' : 'nav-item'} onClick={() => navigate('nodes')}><WifiOutlined /><span>Nodes</span></button>
          <button className={view === 'automations' ? 'nav-item active' : 'nav-item'} onClick={() => navigate('automations')}><ExperimentOutlined /><span>Automations</span></button>
        </nav>
        {!collapsed && <div className="sidebar-bottom">
          <div className="sidebar-note"><span className="note-icon"><CloudOutlined /></span><strong>Your home, in sync.</strong><p>ESP32 nodes connect over Wi‑Fi and report through your MQTT broker.</p></div>
          <div className="sidebar-footer"><span className={data.broker.connected ? 'status-dot on' : 'status-dot'} /> Broker {data.broker.connected ? 'connected' : 'offline'}</div>
        </div>}
      </Sider>
      <div className={`sidebar-backdrop${mobile && !collapsed ? ' visible' : ''}`} onClick={() => setCollapsed(true)} />
      <Layout>
        <header className="topbar">
          <div className="topbar-left"><Button type="text" aria-label={collapsed ? 'Open sidebar' : 'Collapse sidebar'} icon={collapsed ? <MenuUnfoldOutlined /> : <MenuFoldOutlined />} onClick={() => setCollapsed(!collapsed)} /><span className="topbar-divider" /><span className="topbar-path">Home <ArrowRightOutlined /> <strong>{viewTitle}</strong></span></div>
          <div className="topbar-right"><span className={`topbar-live${data.broker.connected ? ' connected' : ''}`}><span className={data.broker.connected ? 'status-dot on' : 'status-dot'} />{data.broker.connected ? 'System online' : 'Broker offline'}</span><span className="today">{new Intl.DateTimeFormat('en', { weekday: 'long', month: 'short', day: 'numeric' }).format(new Date())}</span><Tooltip title={dark ? 'Switch to light theme' : 'Switch to dark theme'}><Button shape="circle" aria-label={dark ? 'Switch to light theme' : 'Switch to dark theme'} icon={dark ? <SunOutlined /> : <MoonOutlined />} onClick={() => setDark(value => !value)} /></Tooltip><Tooltip title="Refresh"><Button shape="circle" icon={<ReloadOutlined />} onClick={() => refresh()} /></Tooltip><span className="avatar">H</span></div>
        </header>
        <Content className="page">
          {fetchError && <Alert type="error" showIcon message="Cannot reach the server" description={fetchError} className="error-alert" />}
          {loading ? <div className="loading"><Skeleton active paragraph={{ rows: 9 }} /></div> : <>
            <section className="page-heading">
              <div><div className="eyebrow">YOUR SPACE, SIMPLIFIED</div><h1>{view === 'overview' ? 'Good to see you.' : view === 'areas' ? 'Your areas' : view === 'nodes' ? 'Connected nodes' : 'Automations'}</h1><p>{view === 'overview' ? 'A clear view of everything happening at home.' : view === 'areas' ? 'Organize nodes by the spaces they belong to.' : view === 'nodes' ? 'Manage your ESP32 sensors and actuators.' : 'Let sensor readings and time schedules control your home.'}</p></div>
              {view !== 'automations' && <Button type="primary" size="large" icon={<PlusOutlined />} onClick={() => view === 'nodes' ? openNode() : openArea()}>{view === 'nodes' ? 'Add node' : 'Add area'}</Button>}
            </section>

            {view === 'overview' && <>
              <div className="stats-grid">
                <StatCard icon={<HomeOutlined />} tone="blue" label="Areas" value={data.areas.length} detail="Spaces in your home" />
                <StatCard icon={<WifiOutlined />} tone="green" label="Online nodes" value={`${onlineCount} / ${data.nodes.length}`} detail="Connected ESP32 devices" />
                <StatCard icon={<PoweroffOutlined />} tone="orange" label="Outputs on" value={activeOutputs} detail="Currently active" />
                <StatCard icon={<CloudOutlined />} tone={data.broker.connected ? 'purple' : 'gray'} label="MQTT broker" value={data.broker.connected ? 'Connected' : 'Offline'} detail={data.broker.connected ? 'Messages flowing' : 'Waiting to reconnect'} />
              </div>
              <div className="section-heading"><div><h2>Areas</h2><p>Browse your home by room</p></div><Button type="link" onClick={() => navigate('areas')}>View all <ArrowRightOutlined /></Button></div>
              <AreaGrid areas={data.areas} nodes={data.nodes} onSelect={id => { setAreaFilter(id); setView('nodes'); }} onAdd={() => openArea()} />
              <div className="section-heading nodes-heading"><div><h2>All nodes</h2><p>Live status from your ESP32 devices</p></div><Button type="link" onClick={() => navigate('nodes')}>Manage nodes <ArrowRightOutlined /></Button></div>
              <NodeGrid nodes={data.nodes} areas={data.areas} busy={busy} automated={automatedTargets} onCommand={commandOutput} onEdit={openNode} />
            </>}

            {view === 'areas' && <>
              <div className="section-heading"><div><h2>Spaces</h2><p>{data.areas.length} areas configured</p></div></div>
              <AreaGrid areas={data.areas} nodes={data.nodes} onSelect={id => { setAreaFilter(id); setView('nodes'); }} onEdit={openArea} onDelete={area => request(`/areas/${area.id}`, 'DELETE', undefined, 'Area removed')} onAdd={() => openArea()} />
            </>}

            {view === 'nodes' && <>
              <div className="filter-row"><div className="filter-tabs"><button className={areaFilter === 'all' ? 'selected' : ''} onClick={() => setAreaFilter('all')}>All areas <b>{data.nodes.length}</b></button>{data.areas.map(area => <button key={area.id} className={areaFilter === area.id ? 'selected' : ''} onClick={() => setAreaFilter(area.id)}>{area.name} <b>{data.nodes.filter(node => node.areaId === area.id).length}</b></button>)}</div><div className="filter-tabs kind-tabs"><button className={kindFilter === 'all' ? 'selected' : ''} onClick={() => setKindFilter('all')}>All types</button><button className={kindFilter === 'sensor' ? 'selected' : ''} onClick={() => setKindFilter('sensor')}>Sensors</button><button className={kindFilter === 'actuator' ? 'selected' : ''} onClick={() => setKindFilter('actuator')}>Actuators</button></div></div>
              <NodeGrid nodes={filteredNodes} areas={data.areas} busy={busy} automated={automatedTargets} onCommand={commandOutput} onEdit={openNode} onDelete={node => request(`/nodes/${node.id}`, 'DELETE', undefined, 'Node removed')} />
              <p className="discovery-hint"><WifiOutlined /> Nodes are discovered automatically when they publish MQTT status or state. You can also add one by device ID.</p>
            </>}
            {view === 'automations' && <Automations data={data} request={request} />}
          </>}
        </Content>
      </Layout>
    </Layout>

    <Modal title={editor?.type === 'area' ? (editor.id ? 'Edit area' : 'Add an area') : (editor?.id ? 'Edit node' : 'Add a node')} open={!!editor} onCancel={() => setEditor(null)} onOk={saveEditor} okText="Save" destroyOnHidden>
      {editor?.type === 'area' && <div className="editor-form"><label>Area name</label><Input size="large" placeholder="e.g. Living room" value={editor.name} maxLength={60} onChange={e => setEditor({ ...editor, name: e.target.value })} onPressEnter={saveEditor} /><label>Color</label><div className="color-picker">{colors.map(color => <button key={color} className={editor.color === color ? 'chosen' : ''} style={{ background: color }} onClick={() => setEditor({ ...editor, color })}>{editor.color === color && <CheckCircleFilled />}</button>)}</div></div>}
      {editor?.type === 'node' && <div className="editor-form"><label>Device ID</label><Input size="large" placeholder="esp32-aabbccddeeff" value={editor.deviceId} disabled={!!editor.id} maxLength={64} onChange={e => setEditor({ ...editor, deviceId: e.target.value })} /><label>Display name</label><Input size="large" placeholder="e.g. Living room lights" value={editor.name} maxLength={80} onChange={e => setEditor({ ...editor, name: e.target.value })} /><label>Node type</label><Select size="large" value={editor.kind} disabled={!!editor.id && !!(data.nodes.find(node => node.id === editor.id)?.outputs.length || data.nodes.find(node => node.id === editor.id)?.sensors.length)} onChange={kind => setEditor({ ...editor, kind })} options={[{ value: 'sensor', label: 'Sensor' }, { value: 'actuator', label: 'Actuator' }]} /><label>Area</label><Select size="large" value={editor.areaId} onChange={value => setEditor({ ...editor, areaId: value })} options={[{ value: null, label: 'Unassigned' }, ...data.areas.map(a => ({ value: a.id, label: a.name }))]} /></div>}
    </Modal>
  </ConfigProvider>;
}

function StatCard({ icon, tone, label, value, detail }) {
  return <div className={`stat-card tone-${tone}`}><div className={`stat-icon ${tone}`}>{icon}</div><div className="stat-label">{label}</div><div className="stat-value">{value}</div><div className="stat-detail">{detail}</div></div>;
}

function AreaGrid({ areas, nodes, onSelect, onEdit, onDelete, onAdd }) {
  return <div className="area-grid">{areas.map(area => <Card key={area.id} className="area-card" style={{ '--area-color': area.color }} hoverable onClick={() => onSelect(area.id)}>
    <div className="area-top"><div className="area-icon"><HomeOutlined /></div>{onEdit && <div className="card-actions" onClick={e => e.stopPropagation()}><Tooltip title="Edit"><Button size="small" type="text" icon={<EditOutlined />} onClick={() => onEdit(area)} /></Tooltip><Popconfirm title="Remove area?" description="Its nodes will become unassigned." onConfirm={() => onDelete(area)}><Button size="small" type="text" danger icon={<DeleteOutlined />} /></Popconfirm></div>}</div>
    <h3>{area.name}</h3><div className="area-footer"><span>{nodes.filter(node => node.areaId === area.id).length} nodes</span><ArrowRightOutlined /></div>
  </Card>)}<button className="add-area-card" onClick={onAdd}><span><PlusOutlined /></span><strong>Add a space</strong><small>Make room for more</small></button></div>;
}

function PwmSlider({ node, output, disabled, onCommand }) {
  const [level, setLevel] = useState(output.level);
  useEffect(() => setLevel(output.level), [output.level]);
  return <div className="pwm-slider"><Slider min={0} max={100} value={level} disabled={disabled}
    onChange={setLevel} onChangeComplete={value => { if (value !== output.level) onCommand(node, output, { level: value }); }} />
    <span>{level}%</span></div>;
}

function NodeGrid({ nodes, areas, busy, automated, onCommand, onEdit, onDelete }) {
  if (!nodes.length) return <div className="empty-card"><Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No nodes here yet. Power on an ESP32 or add one by device ID." /></div>;
  return <div className="node-grid">{nodes.map(node => <Card key={node.id} className="node-card">
    <div className="node-top"><div className={node.kind === 'sensor' ? 'node-symbol sensor' : 'node-symbol'}>{node.kind === 'sensor' ? <ExperimentOutlined /> : <WifiOutlined />}</div><div className="node-menu"><Tag color={node.isOnline ? 'success' : 'default'}><span className={node.isOnline ? 'status-dot on' : 'status-dot'} />{node.isOnline ? 'Online' : 'Offline'}</Tag><Tooltip title="Edit node"><Button size="small" type="text" icon={<SettingOutlined />} onClick={() => onEdit(node)} /></Tooltip>{onDelete && <Popconfirm title="Remove node?" onConfirm={() => onDelete(node)}><Button size="small" type="text" danger icon={<DeleteOutlined />} /></Popconfirm>}</div></div>
    <h3>{node.name}</h3><p className="node-id">{node.deviceId}</p><div className="node-meta"><span>{areas.find(area => area.id === node.areaId)?.name || 'Unassigned'}</span><span>{node.kind === 'sensor' ? 'Sensor' : 'Actuator'}</span><span>{node.kind === 'sensor' ? `${node.sensors.length} readings` : `${node.outputs.length} outputs`}</span></div>
    <div className="output-list">{node.kind === 'sensor' ? (node.sensors.length ? node.sensors.map(sensor => <div className="output-row" key={sensor.key}><div className="output-identity"><span className="bulb sensor"><ExperimentOutlined /></span><div><strong>{sensor.key}</strong><small>Latest reading</small></div></div><strong className="sensor-value">{sensor.value} <small>{sensor.unit}</small></strong></div>) : <div className="no-outputs">Waiting for sensor readings</div>) : (node.outputs.length ? node.outputs.map(output => {
      const key = `${node.id}:${output.index}`;
      const disabled = !node.isOnline || automated.has(key) || busy === key;
      const pwm = output.mode === 'pwm';
      return <div className={pwm ? 'output-row pwm-output' : 'output-row'} key={output.index}>
        <div className="output-identity"><span className={output.isOn ? 'bulb on' : 'bulb'}><PoweroffOutlined /></span><div><strong>Output {output.index + 1}</strong><small>GPIO {output.gpio}{automated.has(key) ? ' · Automated' : ''}</small></div></div>
        <div className="output-controls"><Tooltip title={automated.has(key) ? 'Pause automation to change mode' : 'Output mode'}><Select size="small" aria-label={`Output ${output.index + 1} mode`} value={output.mode || 'digital'} disabled={disabled} options={[{ value: 'digital', label: 'On/off' }, { value: 'pwm', label: 'PWM' }]} onChange={mode => onCommand(node, output, { mode }, true)} /></Tooltip>{!pwm && <Tooltip title={automated.has(key) ? 'Managed in Automations' : ''}><Switch checked={output.isOn} disabled={disabled} loading={busy === key} onChange={state => onCommand(node, output, { state })} /></Tooltip>}</div>
        {pwm && <PwmSlider node={node} output={output} disabled={disabled} onCommand={onCommand} />}
      </div>;
    }) : <div className="no-outputs">Waiting for output state from device</div>)}</div>
  </Card>)}</div>;
}

createRoot(document.getElementById('root')).render(<App />);
