import React, { useState } from 'react';
import Button from 'antd/es/button';
import Card from 'antd/es/card';
import Empty from 'antd/es/empty';
import Input from 'antd/es/input';
import InputNumber from 'antd/es/input-number';
import Modal from 'antd/es/modal';
import Popconfirm from 'antd/es/popconfirm';
import Select from 'antd/es/select';
import Switch from 'antd/es/switch';
import Tag from 'antd/es/tag';
import {
  ClockCircleOutlined, DeleteOutlined, EditOutlined, ExperimentOutlined,
  PlusOutlined, ThunderboltOutlined,
} from '@ant-design/icons';
import './automations.css';
import './timers.css';

const weekdays = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
const operators = ['>', '>=', '<', '<=', '==', '!='].map(value => ({ value, label: value }));
const timeText = minute => `${String(Math.floor(minute / 60)).padStart(2, '0')}:${String(minute % 60).padStart(2, '0')}`;
const minuteOf = time => { const [h, m] = time.split(':').map(Number); return h * 60 + m; };

export default function Automations({ data, request }) {
  const [draft, setDraft] = useState(null);
  const sensorNodes = data.nodes.filter(node => node.kind === 'sensor');
  const actuatorNodes = data.nodes.filter(node => node.kind === 'actuator' && node.outputs.length);
  const availableTargets = actuatorNodes.flatMap(node => node.outputs.map(output => ({ node, output })));
  const usedTarget = (nodeId, index, currentType, currentId) =>
    data.rules.some(rule => rule.actuatorNodeId === nodeId && rule.outputIndex === index && !(currentType === 'rule' && rule.id === currentId)) ||
    data.schedules.some(schedule => schedule.actuatorNodeId === nodeId && schedule.outputIndex === index && !(currentType === 'schedule' && schedule.id === currentId)) ||
    data.timers.some(timer => timer.completedAtUtc === null && timer.actuatorNodeId === nodeId && timer.outputIndex === index);
  const freeTargets = availableTargets.filter(({ node, output }) => !usedTarget(node.id, output.index));

  const openRule = rule => {
    const sensor = sensorNodes[0];
    const target = freeTargets[0];
    setDraft(rule ? { type: 'rule', ...rule } : {
      type: 'rule', name: '', sensorNodeId: sensor?.id ?? null, sensorKey: sensor?.sensors[0]?.key ?? '',
      operator: '>', threshold: sensor?.sensors[0]?.value ?? 0, actuatorNodeId: target?.node.id ?? null,
      outputIndex: target?.output.index ?? null, actionState: true, elseState: false, enabled: true,
    });
  };
  const openSchedule = schedule => {
    const target = freeTargets[0];
    setDraft(schedule ? { type: 'schedule', ...schedule } : {
      type: 'schedule', name: '', actuatorNodeId: target?.node.id ?? null, outputIndex: target?.output.index ?? null,
      startMinute: 8 * 60, endMinute: 16 * 60, daysMask: 127, enabled: true,
    });
  };
  const openTimer = () => {
    const target = freeTargets[0];
    setDraft({ type: 'timer', name: '', actuatorNodeId: target?.node.id ?? null,
      outputIndex: target?.output.index ?? null, durationMinutes: 30, actionState: true });
  };
  const sensorChannels = sensorNodes.find(node => node.id === draft?.sensorNodeId)?.sensors || [];
  const targetOptions = availableTargets.map(({ node, output }) => ({
    value: `${node.id}:${output.index}`, label: `${node.name} · Output ${output.index + 1} (GPIO ${output.gpio})`,
    disabled: usedTarget(node.id, output.index, draft?.type, draft?.id),
  }));
  const chooseTarget = value => {
    const [actuatorNodeId, outputIndex] = value.split(':').map(Number);
    setDraft({ ...draft, actuatorNodeId, outputIndex });
  };
  const set = (key, value) => setDraft(current => ({ ...current, [key]: value }));
  const canSave = draft && draft.name.trim() && draft.actuatorNodeId !== null && draft.outputIndex !== null &&
    !usedTarget(draft.actuatorNodeId, draft.outputIndex, draft.type, draft.id) &&
    (draft.type === 'rule' ? draft.sensorNodeId !== null && draft.sensorKey && Number.isFinite(draft.threshold) :
      draft.type === 'schedule' ? draft.daysMask > 0 && Number.isInteger(draft.startMinute) && Number.isInteger(draft.endMinute) &&
      draft.startMinute !== draft.endMinute : Number.isInteger(draft.durationMinutes) && draft.durationMinutes >= 1 && draft.durationMinutes <= 1440);

  const save = async () => {
    if (!draft) return;
    const { type, id, ...body } = draft;
    const path = type === 'rule' ? 'rules' : type === 'schedule' ? 'schedules' : 'timers';
    const okay = await request(`/${path}${id ? `/${id}` : ''}`, id ? 'PUT' : 'POST', body,
      id ? `${type === 'rule' ? 'Rule' : 'Schedule'} updated` : `${type === 'rule' ? 'Rule' : type === 'schedule' ? 'Schedule' : 'Timer'} created`);
    if (okay) setDraft(null);
  };

  const toggle = (type, item, enabled) => request(`/${type === 'rule' ? 'rules' : 'schedules'}/${item.id}`, 'PUT',
    { ...item, enabled }, enabled ? 'Automation enabled' : 'Automation paused');
  const remove = (type, item) => request(`/${type === 'rule' ? 'rules' : 'schedules'}/${item.id}`, 'DELETE',
    undefined, 'Automation removed');
  const cancelTimer = item => request(`/timers/${item.id}`, 'DELETE', undefined,
    item.completedAtUtc || !item.startedAtUtc ? 'Timer removed' : 'Timer ending action queued');
  const nodeName = id => data.nodes.find(node => node.id === id)?.name || 'Removed node';
  const dayText = mask => mask === 127 ? 'Every day' : weekdays.filter((_, i) => mask & (1 << i)).join(', ');

  return <>
    <div className="automation-intro"><div><strong>Make your home respond automatically</strong><p>Sensor rules react when readings cross a threshold. Schedules keep outputs on during a daily time window.</p></div><Tag color="blue"><ClockCircleOutlined /> {data.timeZone}</Tag></div>

    <div className="section-heading"><div><h2>Sensor rules</h2><p>When a reading matches, set an actuator output</p></div><Button type="primary" icon={<PlusOutlined />} disabled={!sensorNodes.some(node => node.sensors.length) || !freeTargets.length} onClick={() => openRule(null)}>Add rule</Button></div>
    {!data.rules.length ? <div className="empty-card"><Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No sensor rules yet. Connect a sensor and actuator to create one." /></div> : <div className="automation-grid">{data.rules.map(rule => <Card className="automation-card" key={rule.id}>
      <div className="auto-card-head"><span className="auto-icon sensor"><ExperimentOutlined /></span><div className="auto-actions"><Switch size="small" checked={rule.enabled} onChange={value => toggle('rule', rule, value)} /><Button type="text" size="small" icon={<EditOutlined />} onClick={() => openRule(rule)} /><Popconfirm title="Delete this rule?" onConfirm={() => remove('rule', rule)}><Button type="text" size="small" danger icon={<DeleteOutlined />} /></Popconfirm></div></div>
      <h3>{rule.name}</h3><p className="auto-description">If <b>{nodeName(rule.sensorNodeId)} · {rule.sensorKey}</b> {rule.operator} <b>{rule.threshold}</b>, turn <b>{nodeName(rule.actuatorNodeId)} · Output {rule.outputIndex + 1}</b> {rule.actionState ? 'on' : 'off'}.</p>
      <div className="auto-footer"><span>Otherwise: {rule.elseState === null ? 'leave unchanged' : rule.elseState ? 'turn on' : 'turn off'}</span><Tag color={rule.enabled ? 'success' : 'default'}>{rule.enabled ? 'Active' : 'Paused'}</Tag></div>
    </Card>)}</div>}

    <div className="section-heading"><div><h2>Time schedules</h2><p>Maintain output state during a daily time window</p></div><Button type="primary" icon={<PlusOutlined />} disabled={!freeTargets.length} onClick={() => openSchedule(null)}>Add schedule</Button></div>
    {!data.schedules.length ? <div className="empty-card"><Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No schedules yet. Connect an actuator to add a time window." /></div> : <div className="automation-grid">{data.schedules.map(schedule => <Card className="automation-card" key={schedule.id}>
      <div className="auto-card-head"><span className="auto-icon clock"><ClockCircleOutlined /></span><div className="auto-actions"><Switch size="small" checked={schedule.enabled} onChange={value => toggle('schedule', schedule, value)} /><Button type="text" size="small" icon={<EditOutlined />} onClick={() => openSchedule(schedule)} /><Popconfirm title="Delete this schedule?" onConfirm={() => remove('schedule', schedule)}><Button type="text" size="small" danger icon={<DeleteOutlined />} /></Popconfirm></div></div>
      <h3>{schedule.name}</h3><p className="auto-description"><b>{nodeName(schedule.actuatorNodeId)} · Output {schedule.outputIndex + 1}</b> on from <b>{timeText(schedule.startMinute)}</b> to <b>{timeText(schedule.endMinute)}</b>, off outside that window.</p>
      <div className="auto-footer"><span>{dayText(schedule.daysMask)}</span><Tag color={schedule.enabled ? 'success' : 'default'}>{schedule.enabled ? 'Active' : 'Paused'}</Tag></div>
    </Card>)}</div>}

    <div className="section-heading"><div><h2>Countdown timers</h2><p>Run an output for a set duration, then reverse it</p></div><Button type="primary" icon={<PlusOutlined />} disabled={!freeTargets.length} onClick={openTimer}>Add timer</Button></div>
    {!data.timers.length ? <div className="empty-card"><Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No timers yet. Try turning an output on for 30 minutes." /></div> : <div className="automation-grid">{data.timers.map(timer => <Card className="automation-card" key={timer.id}>
      <div className="auto-card-head"><span className="auto-icon timer"><ThunderboltOutlined /></span><div className="auto-actions"><Popconfirm title={timer.completedAtUtc ? 'Remove this timer?' : 'End this timer?'} onConfirm={() => cancelTimer(timer)}><Button type="text" size="small" danger icon={<DeleteOutlined />} /></Popconfirm></div></div>
      <h3>{timer.name}</h3><p className="auto-description"><b>{nodeName(timer.actuatorNodeId)} · Output {timer.outputIndex + 1}</b> {timer.actionState ? 'on' : 'off'} for <b>{timer.durationMinutes} minutes</b>, then {timer.actionState ? 'off' : 'on'}.</p>
      <div className="auto-footer"><span>{timer.completedAtUtc ? 'Finished' : !timer.startedAtUtc ? 'Waiting for node' : new Date(timer.endsAtUtc) <= new Date() ? 'Ending when node is available' : `${Math.ceil((new Date(timer.endsAtUtc) - Date.now()) / 60000)} min remaining`}</span><Tag color={timer.completedAtUtc ? 'default' : 'processing'}>{timer.completedAtUtc ? 'Done' : !timer.startedAtUtc ? 'Pending' : new Date(timer.endsAtUtc) <= new Date() ? 'Ending' : 'Running'}</Tag></div>
    </Card>)}</div>}

    <p className="automation-note"><ThunderboltOutlined /> One output can have one active automation: sensor rule, time schedule, or timer. Time windows follow {data.timeZone}.</p>

    <Modal title={draft?.type === 'rule' ? (draft.id ? 'Edit sensor rule' : 'Add sensor rule') : draft?.type === 'schedule' ? (draft.id ? 'Edit schedule' : 'Add schedule') : 'Add countdown timer'} open={!!draft} onCancel={() => setDraft(null)} onOk={save} okText="Save automation" okButtonProps={{ disabled: !canSave }} destroyOnHidden>
      {draft?.type === 'rule' && <div className="editor-form"><label>Name</label><Input size="large" placeholder="e.g. Fan when hot" value={draft.name} maxLength={80} onChange={e => set('name', e.target.value)} />
        <label>Sensor node</label><Select size="large" value={draft.sensorNodeId} options={sensorNodes.map(node => ({ value: node.id, label: node.name }))} onChange={id => { const node = sensorNodes.find(x => x.id === id); setDraft({ ...draft, sensorNodeId: id, sensorKey: node?.sensors[0]?.key ?? '' }); }} />
        <label>Reading</label><Select size="large" value={draft.sensorKey || undefined} options={sensorChannels.map(sensor => ({ value: sensor.key, label: `${sensor.key} · now ${sensor.value} ${sensor.unit}` }))} onChange={value => set('sensorKey', value)} />
        <label>Condition</label><div className="form-inline"><Select value={draft.operator} options={operators} onChange={value => set('operator', value)} /><InputNumber value={draft.threshold} onChange={value => set('threshold', value)} placeholder="Threshold" /></div>
        <label>Actuator output</label><Select size="large" value={draft.actuatorNodeId !== null && draft.outputIndex !== null ? `${draft.actuatorNodeId}:${draft.outputIndex}` : undefined} options={targetOptions} onChange={chooseTarget} />
        <label>When condition matches</label><Select size="large" value={draft.actionState} options={[{ value: true, label: 'Turn on' }, { value: false, label: 'Turn off' }]} onChange={value => set('actionState', value)} />
        <label>When condition stops matching</label><Select size="large" value={draft.elseState} options={[{ value: null, label: 'Leave unchanged' }, { value: true, label: 'Turn on' }, { value: false, label: 'Turn off' }]} onChange={value => set('elseState', value)} />
      </div>}
      {draft?.type === 'schedule' && <div className="editor-form"><label>Name</label><Input size="large" placeholder="e.g. Daytime lights" value={draft.name} maxLength={80} onChange={e => set('name', e.target.value)} />
        <label>Actuator output</label><Select size="large" value={draft.actuatorNodeId !== null && draft.outputIndex !== null ? `${draft.actuatorNodeId}:${draft.outputIndex}` : undefined} options={targetOptions} onChange={chooseTarget} />
        <label>Time window ({data.timeZone})</label><div className="form-inline"><Input size="large" type="time" value={timeText(draft.startMinute)} onChange={e => set('startMinute', minuteOf(e.target.value))} /><span>to</span><Input size="large" type="time" value={timeText(draft.endMinute)} onChange={e => set('endMinute', minuteOf(e.target.value))} /></div>
        <label>Days</label><div className="weekday-picker">{weekdays.map((day, index) => <button key={day} className={draft.daysMask & (1 << index) ? 'selected' : ''} onClick={() => set('daysMask', draft.daysMask ^ (1 << index))}>{day}</button>)}</div>
      </div>}
      {draft?.type === 'timer' && <div className="editor-form"><label>Name</label><Input size="large" placeholder="e.g. Porch light for 30 minutes" value={draft.name} maxLength={80} onChange={e => set('name', e.target.value)} />
        <label>Actuator output</label><Select size="large" value={draft.actuatorNodeId !== null && draft.outputIndex !== null ? `${draft.actuatorNodeId}:${draft.outputIndex}` : undefined} options={targetOptions} onChange={chooseTarget} />
        <label>Duration (minutes)</label><InputNumber size="large" min={1} max={1440} precision={0} value={draft.durationMinutes} onChange={value => set('durationMinutes', value)} style={{ width: '100%' }} />
        <label>Start action</label><Select size="large" value={draft.actionState} options={[{ value: true, label: 'Turn on, then turn off' }, { value: false, label: 'Turn off, then turn on' }]} onChange={value => set('actionState', value)} />
      </div>}
    </Modal>
  </>;
}
